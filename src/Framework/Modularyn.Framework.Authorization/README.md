# Authorization

`Modularyn.Framework.Authorization.Abstractions` holds the contracts other
modules consume: permission and host-operation definitions, the decision, the
resource reference, resource visibility for lists, the one evaluator
`IPermissionEvaluator`, and the audit seam for authority changes. It references
only the modularity, security and tenancy abstractions, so domain and
application contract projects can reference it.

`Modularyn.Framework.Authorization` implements them over the PostgreSQL
persistence adapter and SEC-04's tenancy runtime: the evaluator, the authority
store, permission and host-authority administration, tenancy's host-authority
seam, the HTTP and worker entry points and the startup report of unguarded
paths. It uses the `Microsoft.AspNetCore.App` shared framework and adds no
external dependency identity.

## Composition

```csharp
var authorization = AuthorizationModule.Create(persistenceOptions);
var registry = new AuthorizationRegistry(manifests,
    [.. AuthorizationModule.Permissions(version), .. SettingsModule.Permissions(version), .. itemPermissions],
    [.. AuthorizationModule.HostOperations(version), .. TenantDirectoryModule.HostOperations(version),
     .. IdentityHostOperations.Definitions(version), .. SettingsModule.HostOperations(version)]);

services.AddModularynTenancy(directory);
services.AddModularynIdentity(configuration);
services.AddModularynAuthorization(configuration, authorization, registry);

app.MapPut("/t/{tenant}/items/{code}", UpdateItem)
    .RequireTenantMember()
    .RequirePermission("item.update");
```

Add the module that `AuthorizationModule.Create` returns to the host's one
module registry, and `AuthorizationModule.Manifest` and the registry to the
composition, so the migration coordinator applies the schema
`modularyn_authorization` and the activation gate checks the definitions.
Register tenancy first; registration is refused without it, and a second
registration is refused.

Identity supplies `IPrincipalFreshness` and `ITenantMembership`; while either
is absent, every current-profile decision and every authority change is
refused. Audit (SEC-07) supplies `IAuthorityAudit`; while it is absent, every
authority change is refused.

Options come from `Modularyn:Authorization`, bind strictly, are validated before
any hosted service starts and change only on restart. A refusal names the
setting, never its value.

| Setting | Default | Range |
|---|---|---|
| `FreshnessBound` | 30 seconds | 1 second to 5 minutes |
| `HostOperators:{i}:Kind`, `:Subject` | none | a user or service actor, each named once |
| `HostOperators:{i}:Operations` | none | at least one host operation a composed module declares |

## Definitions

A module declares each permission as a `PermissionDefinition`: a stable
identifier of lowercase ASCII segments, such as `item.read`, its freshness
profile and a version. A host operation is a `HostOperationDefinition` around
SEC-04's `HostOperation`, which names every entity a host scope granted for it
may reach, as `HostReach(moduleId, table)`. The module lists each one in its
manifest's definitions, as `permission.<name>` or `host-operation.<id>` with the
definition's version.

`AuthorizationRegistry` holds exactly one definition for each declared asset,
bound by owner, name and version, and each permission and operation has one
owner. As a composition validator it refuses activation with `MDC310` while a
manifest and the definitions disagree, or while an operation reaches a module
that is not composed. Only what the composition declares exists: an undeclared
permission confers nothing (`MAZ003`) and cannot be granted (`MAZ022`), even
where a stored grant of it survives from an earlier composition.

The module declares two permissions of its own, both of the current profile:
`modularyn.authorization.manage`, which administers roles and grants within a
tenant, and `modularyn.authorization.share`, which lets a resource's owner share
it. It declares two host operations: `modularyn.authorization.host-authority`,
which grants and revokes host authority, and
`modularyn.authorization.tenant-owners`, which names a tenant's owners.

## Authority records

| Record | Scope | Holds |
|---|---|---|
| Tenant authority | Tenant | The tenant's authority version and the lock every change takes |
| Role | Tenant | The permissions its members hold and its assignment ceiling |
| Role member | Tenant | A user's assignment to a role |
| Permission grant | Tenant | A permission granted directly to a user |
| Resource grant | Tenant | A permission on one resource, granted to a user, optionally until an expiry |
| Tenant owner | Host | A user who holds every declared permission in one tenant, with no ceiling |
| Host-authority grant | Host | An actor's stored authority for one host operation |

Every record names who made it and when. Times come from `AuthorizationClock`,
the one place the package reads the host's `TimeProvider`, floored to whole
microseconds; a resource grant's expiry is floored there too, so an expiry
decides the same before and after a database round trip. Grants are made only
to users who are current members of the tenant; a service identity has exactly
its declared scope and is never granted anything.

No authority record is written in an owner-bound unit. Tenant-level records
change only through `AuthorityAdministration`, in a tenant unit of the actor's
own validated context. Tenant owners and host-authority grants change only
through `HostAuthorityAdministration`, in an active host scope granted for that
record's own host operation, which tenancy establishes only under host
authority and after host-scope audit. Each change is recorded through
`IAuthorityAudit` before it commits; when audit is absent or fails, the change is
refused with `MAZ024` and nothing changes.

## Ceilings and serialization

An administrator holds `modularyn.authorization.manage` and may assign a
permission only when it is within the ceiling of the administrator's roles and
held by the administrator: no one grants beyond what they hold. A tenant owner
holds everything with no ceiling. A role change needs every permission the role
carries, before and after, to be assignable, and so do assigning and
unassigning it. A resource's owner may share it with `modularyn.authorization.share`
and the permission shared; an administrator may grant on any resource of the
tenant within the ceiling. Whoever could make a grant may revoke it.

Authority changes only by its holder acting as itself: from the actor's own
undelegated interactive session in its own tenant context, never under a
delegation and never by a service identity (`MAZ020`). Each change runs in one
transaction:

1. It locks the tenant's authority row by writing it first, creating it on the
   tenant's first change, and rotates the tenant's authority version.
2. It rechecks the actor's session and membership now.
3. It reads the actor's authority as it stands under that lock and checks the
   change against it (`MAZ020`, `MAZ021`, `MAZ025`).
4. It records the change in audit and commits.

Concurrent changes in a tenant therefore serialize on its row, and none can
exceed a ceiling that another change reduced first. A change that loses the race
reads again and retries, up to four attempts, then refuses with `MAZ023`. A
change that would change nothing is not made and is not recorded.

## The evaluator

`IPermissionEvaluator.AuthorizeAsync` is the one decision. It reads no
authority from the client: the context comes from tenancy's validated
resolution, the principal from the unit's server-side holder and the resource
from the module's own tenant-filtered read. It checks, in order, and refuses at
the first failure:

| Check | Refusal |
|---|---|
| The unit has an active tenant-scope context | `MAZ002` |
| There is an authenticated principal | `MAZ001` |
| A composed module declares the permission | `MAZ003` |
| The principal is the actor the context was established for | `MAZ009` |
| The execution's ceiling covers the permission: a delegation's scope, tenant and window, or a service identity's scope | `MAZ004` |
| For the current profile, the principal's session, delegation or service identity is current | `MAZ006` |
| The effective subject, and for the current profile the actor, is a current member | `MAZ010` |
| The subject holds the permission in the tenant | `MAZ005` |
| A named resource is in the context's tenant, and the subject owns it or holds a current grant on it | `MAZ007` |

Anything unavailable on the way, such as the store, a seam or the cache's fill,
refuses with the transient `MAZ008` and logs event 8300. An interactive user's
authority is its effective subject's, which is the delegation's subject under a
delegation. A service identity acts for the producer of propagated work, the user
that the revalidated context names, within its own scope, or as itself in a
context it is the actor of, with exactly its scope.

The stored propagated reference therefore decides whose authority a worker
exercises. Only server code may produce one, with
`PropagatedTenantContext.Capture` from the validated context in the producing
unit, as the framework README's propagation contract says. The reference has no
integrity tag and can be run more than once, each run decided afresh at the
current profile. Binding it to its audited producing unit is SEC-07's, and the
adversarial cases are SEC-08's.

`VisibleAsync` answers which resources of one type the subject may see for a
permission, for lists, counts, previews and exports: those it owns and those it
holds a current grant on, and none when the tenant-level decision refuses.
`ResourceVisibility.Apply` composes that filter into the module's query, with
the values bound as parameters, so hidden rows affect neither content nor
totals. `DescribeAsync` reports a principal's effective permissions, for a
bootstrap or current-user view; it describes, and each operation still asks for
its own decision. UI visibility is presentation only.

## Revocation and freshness

Each permission declares a profile; a request may also require the current one.

- **Bounded.** The decision may rest on the subject's authority as read at most
  the declared bound B ago. The snapshot is cached in SEC-04's `TenantCache`
  under a principal-scoped entry keyed by the tenant, the context's principal
  and the effective subject, so no principal receives another's authority. It
  expires at B measured from the start of its fill, which runs in its own
  revalidated unit through `TenantWorkRunner` and gives up at B. Every committed
  change in a tenant makes its entries unreachable at once in the process that
  made it; another process sees it within B. A restart starts with an empty
  cache.
- **Current.** Every decision reads the subject's authority from the store and
  rechecks the principal and the memberships now, so a revoked grant,
  membership or session stops the operation at its next check. Authority and
  settings administration, sharing and every decision a worker makes use it.
- **Cache outage.** When the cache fails, the decision is taken from the store
  and nothing is cached (event 8301). When an invalidation fails, the cache is
  distrusted and every decision comes from the store until B has passed since
  the failure, by when every entry built before the change has expired (event
  8302). When the store fails, the decision refuses.

Resource grants are read from the store at every decision, so a revoked or
expired share stops at once in every process.

## Entry points

The same decision applies at every entry point, and none relies on
interception alone.

- **HTTP endpoints.** `RequirePermission(permission)`, with
  `RequireTenantMember()`, wraps the endpoint's request delegate. The endpoint
  runs only after the evaluator permits, whatever the pipeline contains: the
  order is the ingress, routing, authentication, tenancy's context, the
  permission decision, and then the handler, which validates its input, opens
  its unit of work and commits. A refusal answers with the decision's code as
  `{"code": "MAZ005"}` and `Cache-Control: no-store`: 401 for `MAZ001` and
  `MAZ006`, 404 for `MAZ007`, 503 for `MAZ008` and 403 otherwise. A declared
  endpoint reached without a tenant context or an evaluator answers 500 and logs
  event 8303, so a misplaced pipeline fails closed.
- **Policies.** `PermissionRequirement.Policy(permission)` lets ASP.NET Core's
  authorization middleware ask the same evaluator. A policy holds only where the
  middleware intercepts the request, so an endpoint protected by it alone is
  reported.
- **Workers and continuations.** `AuthorizedWorkRunner.RunAsync` runs a job,
  message or file step through `TenantWorkRunner`, which revalidates the
  propagated tenant and its producer, and then decides the permission at the
  current profile, for the running principal against the producer's current
  authority, before any work. A captured decision is never replayed: a producer
  whose grant or membership was revoked after it enqueued the work is refused,
  `Denied` with the decision's code, or `Deferred` while authorization data is
  unavailable. Work that runs in steps asks again before each step with
  `RequireCurrent`.
- **Use cases.** A service that changes protected state asks the evaluator
  itself before it opens a unit, as settings management does.
- **The caller's own principal.** `RequireAuthenticatedPrincipal()` declares an
  endpoint that describes or acts on the caller's own principal only, such as
  identity's current-user view, and needs no tenant context or operation
  permission. It wraps the request delegate the same way: the endpoint runs only
  when the request's server-side principal holder has a principal, and otherwise
  answers 401 with `MAZ001`. An explicit anonymous marker does not lift it.

`AuthorizationEntryPoints.Describe` classifies every routed endpoint as
`Permission`, `HostAuthority`, `Authenticated`, `Anonymous`, `MemberOnly`,
`InterceptionOnly` or `Undeclared`; an endpoint that requires both a principal
and tenant membership is `MemberOnly`. Once the host has started, the package
logs each endpoint that relies on interception alone (event 8304), declares
nothing (event 8305), requires tenant membership only (event 8306) or requires
an authenticated principal only (event 8307).

## Host authority

The package implements tenancy's `IHostAuthority` under the elevated policy. An
actor holds host authority for an operation only when a composed module
declares the operation and either the deployment's `HostOperators` or a stored
host-authority grant names the actor for it. The answer carries the operation's
declared reach, so the host scope that tenancy then establishes reaches only
those entities: the persistence adapter refuses a cross-tenant read, a named
tenant's update or a host-scoped write outside them with `MPS043`. Stored grants
are read afresh for every host scope, so a revocation applies to the next one.

`HostOperators` are the first host operators, deployment authority as trusted
as the configuration that names them. Every further grant is stored: in a host
scope for `modularyn.authorization.host-authority`, an actor grants or revokes
only operations it holds itself. A tenant's owners are named in a host scope for
`modularyn.authorization.tenant-owners`, for an active tenant only. Each change
is audited before it commits and invalidates the affected cache entries: a
host-authority change, the host-scope entries of that operation and actor; a
tenant owner change, the tenant's entries.

## Refusal codes and events

| Code | Meaning |
|---|---|
| `MAZ001` | No authenticated principal |
| `MAZ002` | No active tenant-scope context |
| `MAZ003` | The permission is not declared by a composed module |
| `MAZ004` | The execution's ceiling excludes the permission |
| `MAZ005` | The subject does not hold the permission |
| `MAZ006` | The principal's session, delegation or subject is no longer current |
| `MAZ007` | The resource is not the tenant's, or not the subject's to use; answered as missing |
| `MAZ008` | Authorization data or a seam is unavailable; transient |
| `MAZ009` | The principal is not the context's actor |
| `MAZ010` | The subject is not a current member of the tenant |
| `MAZ020` | An authority change needs the administration permission from the actor's own interactive principal, or the record's host operation |
| `MAZ021` | An authority change reaches beyond the actor's ceiling or holdings |
| `MAZ022` | An authority change names an undeclared permission or host operation |
| `MAZ023` | Concurrent changes kept committing first |
| `MAZ024` | The change could not be recorded in audit, so it was not made |
| `MAZ025` | The change names something that does not exist or is not eligible |

A refusal carries no grant, role or resource detail. Events 8300 to 8302 are
warnings for unavailable data, a failed cache and a failed invalidation; 8303 is
an error for a declared endpoint that ran unguarded; 8304 and 8305 are the
startup warnings for unguarded paths, and 8306 and 8307 those for endpoints that
declare no operation permission.

## Threat-model controls

For each control of boundary TB-G02-05, and for the SEC-05 parts of controls on
other boundaries, this records what the package delivers, the tests that
exercise it and what remains, with the work order it waits on. Test classes are
in `Modularyn.Framework.Authorization.Tests` unless another project is named,
with `Identity` for `Modularyn.Modules.Identity` and `Framework` for
`Modularyn.Framework`. Tests with the trait `Requires=PostgreSQL` run against
PostgreSQL through the database script, over the real tenancy runtime, identity,
authorization, settings and an items fixture module, and those named
`_Contract` and `_Isolation` also run a real Kestrel server behind the ingress.
The others are unit tests. Only the request's authentication, in the tests that
do not sign in through identity, and SEC-07's audit are test doubles. SEC-08 maps
every control to its evidence.

- **TM-G02-019, partly.** Delivered: module-declared stable permission
  identifiers; deny by default; one evaluator behind every entry point, HTTP,
  policy, worker, continuation and use case, refusing whenever its data is
  unavailable; a declared endpoint that never runs without the decision; no
  authority from the client; identity's administrative writes gated by host
  operations or the account owner's own session. Tests: `DecisionTests`
  (`TM_G02_019_*`), `G02AC003Tests.AC_003_P01_*` and `Identity.Application.Tests`
  `AdministrationTests.TM_G02_019_*`. Remaining: the declared endpoint inventory,
  which requires a declaration on every endpoint and refuses anonymous access by
  omission (CODE-06, with TM-G02-011); the durable job, event and file handlers
  running through `AuthorizedWorkRunner` (CODE-05, CODE-07); adversarial
  evidence (SEC-08).
- **TM-G02-020, partly.** Delivered: the resource resolved by its module within
  the effective tenant, then authorized for its tenant, its owner or a current
  grant, read from the store at every decision, and refused as missing. Tests:
  `DecisionTests.TM_G02_020_*`, `AuthorityTests.TM_G02_020_*` and
  `G02AC003Tests.AC_003_P01_*_Isolation`. Remaining: file versions and download
  grants adopting it (SEC-06, CODE-07); adversarial evidence for AC-003-N01 and
  AC-004 (SEC-08).
- **TM-G02-021, partly.** Delivered: `ResourceVisibility` composed into list,
  count and export queries as parameters, so hidden rows affect neither content
  nor totals, and nothing visible when the decision refuses. Tests:
  `AuthorityTests.TM_G02_021_*` and `G02AC003Tests.AC_003_B01_*`. Remaining:
  bounded paging, sorting and filtering on declared fields (CODE-06); file
  previews (SEC-06); adversarial evidence for AC-003-N01 (SEC-08).
- **TM-G02-022, partly.** Delivered: assignment ceilings, so no grant, role or
  share reaches beyond what the grantor holds within its ceiling; every change
  checked against the grantor's authority read under the tenant's lock in the
  change's own transaction; concurrent changes serialized; no authority change
  under a delegation or by a service identity; changes refused without audit.
  Tests: `AuthorityTests.TM_G02_022_*`. Remaining: the audit store, with records
  in the change's transaction (SEC-07, TM-G02-051); adversarial evidence for
  AC-003-N02 (SEC-08).
- **TM-G02-023, partly.** Delivered: explicit entry points that wrap the
  endpoint, the worker and the use case, in the order stated under Entry points;
  the startup report of endpoints that rely on interception alone or declare
  nothing; a test that a declared permission holds where no authorization
  middleware runs. Tests: `DecisionTests` (`TM_G02_019_*`, `TM_G02_023_*`) and
  `G02AC003Tests.AC_003_P01_*_Contract`. Remaining: the endpoint conventions
  that place validation and transactions in the documented order for every
  endpoint (CODE-06) and for every handler (CODE-05); audit in that order
  (SEC-07).
- **TM-G02-024, partly.** Delivered: permissions, feature flags, settings and
  composition kept apart. A feature or setting never grants authority; a
  permission of a module the composition excludes can be neither granted nor
  exercised; tenant and user overrides of settings and features need their own
  permission, decided at the current profile for the caller. Tests:
  `AuthorityTests.TM_G02_024_*` and `Framework.Settings.Tests`
  `ManagementTests.TM_G02_024_*`. Remaining: entitlements and plans, which
  attach at the feature gate (G-03); an excluded module's endpoints absent from
  the declared inventory (CODE-06); AC-003-N03's evidence (SEC-08).

Carried into SEC-05 from other boundaries:

- **TM-G02-017, SEC-05's part.** Delivered: the bounded and current profiles,
  with the bound B validated at startup; revocation at once in the process that
  made it and within B elsewhere; current-profile operations stopped at their
  next check; decisions across a cache outage and a restart; sessions revoked by
  their owner or an identity administrator only. Tests:
  `G02AC003Tests.AC_003_B01_*`, `AC_003_R01_*` and `AC_003_R02_*`,
  `DecisionTests.TM_G02_033_*` and `Identity.Application.Tests`
  `AdministrationTests.TM_G02_017_*`. Remaining: a cache shared across
  processes, which the single-node profile does not need; host and container
  evidence (CODE-10 on CI-01).
- **TM-G02-028, SEC-05's part.** Delivered: the elevated policy, host
  authority only for declared operations from the deployment's operators or
  stored grants, granted only by an actor who holds the operation; each
  operation bound to the entities it may reach, enforced by the persistence
  adapter; tenant owners and host-authority grants changed only in their own
  audited host scope; identity's tenant memberships written only in a host scope
  that reaches them. Tests: `AuthorityTests.TM_G02_028_*`,
  `Framework.Tenancy.Tests` `HostScopeTests.TM_G02_028_*` and
  `Identity.Persistence.EfCore.Tests` `CredentialStoreTests.TM_G02_028_*`.
  Remaining: the audit store (SEC-07, TM-G02-051); adversarial evidence for
  AC-004-N03 (SEC-08).
- **TM-G02-033, SEC-05's part.** Delivered: authority cached only under
  principal-scoped entries, invalidated on every change; host-scope entries
  keyed by their grant's operation and invalidated with it. Tests:
  `DecisionTests.TM_G02_033_*` and `Framework.Tenancy.Tests`
  `TenantCacheTests.TM_G02_033_*`. Remaining: adversarial evidence for AC-004-B02
  (SEC-08).
- **TM-G02-002 and TM-G02-048.** Permission and host-operation definitions must
  match the composed manifests before activation, and the options validate
  before startup. Tests: `DecisionTests` (`TM_G02_002_*`, `TM_G02_048_*`).

`G02AC003Tests` holds the eleven methods the G-02 evidence plan assigns to
SEC-05: AC-003-B01, a revoked bounded permission stopping at B in a process
that never heard of the change and at once in the one that made it, and an
export that stops at its next check after its permission or its sessions are
revoked; AC-003-P01, one grant applied alike at the HTTP endpoint, a declared
integration endpoint, a policy, an application service, a worker entry point and
an asynchronous continuation, with the endpoint inventory compared against the
declared permissions, and only in the grant's tenant; AC-003-R01, a bulk update
in batches that stops at its next check after its permission is revoked, and
the job queued for the remainder refused when it revalidates its producer, in
another tenant's interleaved work and after a restart; AC-003-R02, decisions
that follow the current grants during a cache outage, after it and after a
restart. The advertised .NET and TypeScript clients are CODE-06's. A test clock
moves time explicitly, so the boundaries are decided at the instants the tests
name.

These tests do not establish container, host or browser evidence, and test
source does not establish execution results.
