# Identity module

The identity module authenticates local accounts, keeps server-side sessions,
enforces the freshness contract of FND-017 and runs background work under
declared service identities (ARC-006). Its projects:

- `Modularyn.Modules.Identity.Domain`: the password, session, throttle and
  delegation policies and rules, the stored forms of credentials, and the store
  ports. It references only `Modularyn.Framework.Security.Abstractions`.
- `Modularyn.Modules.Identity.Application.Contracts`: the public contracts, such
  as `ISignInService`, `ISessionService`, `IAccountService` and
  `IDelegationService`.
- `Modularyn.Modules.Identity.Application`: the use cases, the deployment
  options and their validation.
- `Modularyn.Modules.Identity.Persistence.EfCore`: the module's PostgreSQL
  context, schema `identity` and migration on CODE-03's persistence, ASP.NET Core
  Identity's EF Core user store and password hasher, and the other stores.
- `Modularyn.Modules.Identity.HttpApi`: session authentication and the sign-in,
  sign-out, antiforgery and current-user endpoints.

`Modularyn.Framework.Security.Abstractions` holds what other modules consume: the
`ExecutionPrincipal`, its delegated authority and service scope, the per-unit
principal holder and its accessor, service identity scopes, the freshness recheck, `SecretText` and the
credential-provider seam for generated clients. It uses only the .NET shared
framework.

## Composition

```csharp
var identity = IdentityPersistence.CreateModule(persistenceOptions);
services.AddModularynIdentity(configuration);
services.AddModularynIdentityPostgreSql(identity);
services.AddModularynIdentityHttp();
services.AddModularynServiceIdentity("svc-items", ["item.reindex"]);

app.UseModularynIngress();
app.UseRouting();
app.UseModularynTenancy();
app.UseAuthorization();
app.MapModularynIdentityEndpoints();
```

Add the identity module to the host's one module registry and selection, so the
migration coordinator applies its migration and the activation gate checks it.
`UseModularynIngress` places authentication after its transport checks and
before antiforgery validation, because antiforgery tokens are bound to the
authenticated user, and `UseModularynIngress` refuses a pipeline that already
called `UseAuthentication` or `UseAuthorization`. A host with tenancy registers it
with `AddModularynTenancy` and places `UseModularynTenancy` after routing and
before authorization, as shown. Startup is refused while a store is missing,
while the ingress policy is not registered, or after the session scheme was
replaced.

Options come from `Modularyn:Identity` and are validated before startup; a
refusal names the setting and its range, never its value.

| Setting | Default | Range |
|---|---|---|
| `Passwords:MinimumLength` | 15 | 15 to 128 characters; at most 256 are accepted |
| `Passwords:HashIterations` | 210,000 | 210,000 to 5,000,000 |
| `Sessions:IdleTimeout` | 20 minutes | 1 minute to 12 hours |
| `Sessions:AbsoluteLifetime` | 8 hours | 15 minutes to 24 hours, and at least the idle timeout |
| `Throttling:SourceThreshold` | 20 | 3 to 1,000 failures |
| `Throttling:SourceWindow`, `Throttling:SourceBlock` | 15 minutes each | 1 minute to 1 day |
| `Throttling:AccountFreeAttempts` | 5 | 1 to 50 |
| `Throttling:AccountBaseDelay` | 1 second | 1 second to 5 minutes |
| `Throttling:AccountMaximumDelay` | 15 minutes | the base delay to 1 hour |
| `Throttling:AccountResetAfter` | 1 day | the delay cap to 30 days |
| `Delegation:MaximumLifetime` | 8 hours | 1 minute to 7 days |
| `CredentialKeys` | none | versioned secret references to 32-byte keys |

## Accounts and passwords

Accounts are ASP.NET Core Identity users in the module's own schema, through
`UserOnlyStore`. Only the user name, its normalized form, the password verifier,
the security stamp, the concurrency stamp and a disabled flag are mapped; the
e-mail, phone, two-factor and lockout columns are not, because those journeys
belong to G-03. A user name is 1 to 256 ASCII letters, digits and `-._@+`, so its
upper-case comparison form is the same with or without the globalization
libraries; sign-in refuses any other name before a lookup. Verifiers are ASP.NET Core Identity's version 3 format,
PBKDF2-HMAC-SHA512 with a 128-bit salt, with the iteration count recorded in
each verifier. A successful sign-in rewrites a verifier below the configured
count; a wrong password changes nothing.

There is no lockout. After the free attempts, each failure for an account
requires a delay that doubles up to the cap, so failures by others slow the owner
but never lock the owner out (TH-G02-076). Each client source is also blocked
for the block period once it reaches its threshold within the window; a source
is an IPv4 address or an IPv6 /64 network, as judged by the ingress's declared
proxies. Password sign-in, password change, one-time codes and recovery codes
all pass through one verification gate. It locks the source and subject
counters in PostgreSQL across the verification, refuses without verifying while
a limit applies, and records the outcome before it releases them. Each host
admits one attempt per counter at a time and refuses the others at once with a
one-second retry, so parallel attempts cannot each hold a connection.
Throttle rows hold SHA-256 hashes of their keys, never a user name or address.

An unknown account costs one verification against a verifier made when the
account directory is created, and unknown, disabled and wrong-password attempts
receive the same result, the same response and the same throttling.

## Sessions

A successful sign-in issues a new session with a new 256-bit token and ends any
session the browser presented. PostgreSQL keeps the session record with only
the SHA-256 of its token. The cookie is `__Host-Modularyn-Session`: Secure,
HttpOnly, SameSite=Strict, path `/`, no domain and no expiry. A duplicated or
malformed cookie is refused without a store lookup.

Every request validates its session against the store: the session has not
ended, its account exists and is enabled, its security stamp is current, and
both its idle and absolute limits are ahead. Each limit is exclusive, and a
tightened absolute lifetime shortens sessions already issued. A failed
validation ends the record with its reason and deletes the cookie. Sign-out,
revocation, a password change, disabling the account and a security-state change
therefore take effect at the next request; that is the declared freshness bound
for sessions and the claims they carry. `IPrincipalFreshness` repeats the check
inside a running request for a security-sensitive operation.

## Actors, delegation and service identities

`ExecutionPrincipal` distinguishes the actor, the effective subject and the
identity kind. A user may delegate to another user, from the user's own
undelegated session, a set of permissions for a stated purpose, optionally
limited to one tenant, for a window no longer than the declared maximum. Either
party may revoke it. A request names the grant in the `Modularyn-Delegation`
header; the grant resolves only for its actor's own session while the grant is
active and the subject is enabled, and a request whose grant does not resolve is
not authenticated at all. A delegated principal's ceiling is the grant's scope,
tenant and window, and its claims name both the actor and the subject.

A service identity is declared once, with its exact permissions, through
`AddModularynServiceIdentity`. A service identity is never delegated to and never
uses a session.

No principal is ambient. As with SEC-04's tenant context, every unit of work holds
its own principal in its own service scope, in an `ExecutionPrincipalScope`, and
`IExecutionPrincipalAccessor` reads it from there. A unit's principal is
established once, can never be replaced, and ends with the unit.

- **Requests.** The session handler establishes the principal in the request's
  scope, and sign-out ends it for the rest of the request.
- **Service identities.** `IServiceIdentityScopes.Begin` starts a unit in a new
  service scope with the service identity as its principal. The work resolves its
  services from `ServiceIdentityScope.Services`. The code that began the unit
  keeps its own principal, and neither sees the other's. Disposing the unit ends
  its principal and its scope.
- **Everything else has no principal.** Work that is not given a unit's services,
  such as a task started without them or a new scope, has none. So does later
  work on a reused worker.

`ServiceIdentityScopeTests` and `ExecutionPrincipalScopeTests` show concurrent
units seeing only their own principal, work outside a unit seeing none, the
principal ending with its unit, and refusal to replace one.

## Stored forms of other credentials

Only the stored forms exist; enrolment and recovery journeys are G-03's.

- One-time-password seeds are encrypted with AES-256-GCM under a key derived with
  HKDF-SHA256 from a versioned 32-byte key in the deployment's secret store. The
  associated data binds the account and the key version, and earlier versions
  stay readable while they are configured. Without a usable key a seed is
  neither stored nor verified.
- Recovery codes have 80 bits and are stored only as PBKDF2-SHA256 verifiers
  with a per-code salt. Each is consumed once, under a concurrency token, and a
  new set replaces the old one.
- Passkey records carry an HMAC-SHA256 tag over a length-prefixed form of the
  account, credential identifier, relying party, public key and algorithm, under
  a separately derived key. The tag is checked before anything in the record is
  used.

## Client credentials

`IClientCredentialProvider` is the seam that the generated clients of CODE-06
will use. `OriginBoundCredentialProvider` supplies a credential only for a
request to one of its configured exact HTTPS origins and consults its source for
no other origin. `SecretText`, and a `ClientCredential` through its value,
format and serialize only as a redaction, and `SecretText` refuses to be read
from JSON. The module issues no tokens, and registers no provider.

## Tenancy

Every identity record is host-scoped: accounts, credentials, sessions, the
verification throttle, delegations and tenant memberships. They describe global
principals, which exist before any tenant context does. Tenancy resolves a tenant
only after it has the request's authenticated actor and that actor's current
membership, so sign-in must write these records, and tenancy must read them,
before any tenant exists. An account's sessions and credentials also span every
tenant it belongs to. Keeping them tenant-scoped would let a tenant unit change
records that belong to every tenant. A delegation's tenant is a restriction on
the grant, which its ceiling and its tenant claim enforce, not the row's scope.

Identity supplies two of tenancy's seams:

- `ITenantActorResolver`, in the HTTP adapter: the account behind the request's
  validated session. A delegated request resolves to its actor, never to the
  subject it acts for, so tenant membership and host authority are always the
  actor's own.
- `ITenantMembership`, in the application: whether an enabled user account
  currently holds a membership record for the tenant, read afresh for each
  request. A service identity is never a member.

The session handler adds the `modularyn.tenant` claim only for a delegated request
whose grant names a tenant. Tenancy requires every candidate to name the same
tenant, so such a request reaches that tenant only. An undelegated session
carries no tenant claim; its tenant comes from the request's domain, route or
header and the membership check.

Identity changes its records only in owner-bound units of work, under the scopes
in `IdentityOwnerScopes`, whether or not the request resolved a tenant, and reads
them in unbound units. This is consistent with TM-G02-028, which keeps host
scope from ever being applied implicitly:

- **No host scope.** An owner-bound unit is confined to identity's own schema
  and tables. It reaches no tenant-scoped row, and it can neither read across
  tenants nor update a named tenant's row; those still need host authority and
  an audit record.
- **The module's own use cases.** The writes act on the caller's own principal or
  record an anonymous attempt. Sign-in issues a session and counts failures.
  Validation touches or ends a session. A password change needs the current
  password. A delegation is issued only by its subject, from the subject's own
  session, and revoked only by its actor or subject. None of them takes authority
  from a tenant context or grants any.
- **Nothing from the tenant.** A request's tenant context is never bound to
  identity's units, so it neither widens nor narrows them, and a tenant
  administrator's membership confers nothing in identity.
- **Administrative acts.** Disabling an account, a security-state change and
  membership administration have no endpoint and no permission check of their
  own. The permission that gates them is SEC-05's.

`TenancyIntegrationTests` (`Identity.HttpApi.Tests`) resolves tenants through
these seams with SEC-04's directory on PostgreSQL. A security-state change made
from a tenant endpoint rotates the stamp and ends the member's sessions. The
owner-bound unit's own tests are `OwnerBoundUnitTests` (`Framework.Tenancy.Tests`)
and `HostWriteTests` and `OwnerScopeTests` (`Framework.Persistence.EfCore.Tests`).

## Threat-model controls

For each control of boundary TB-G02-04, this records what the identity module
delivers, the tests that exercise it and what remains, with the work order it
waits on. Test classes are in the test project of the package named, with
`Identity` for `Modularyn.Modules.Identity`. Tests with the trait
`Requires=PostgreSQL` run against PostgreSQL through the database script; those
in `Identity.HttpApi.Tests`, and AC-003-B02's isolation test, also run the HTTP
adapter on Kestrel over loopback HTTPS. The others are unit tests, over
in-memory stores where they need stores. SEC-08 maps every control to its
evidence.

- **TM-G02-015, partly.** Delivered: per-source blocks and per-account delays
  without lockout, shared by every credential and code verification; counters
  held locked in PostgreSQL across a verification, so concurrent attempts from
  several hosts never verify more than the limits allow; uniform results,
  responses, throttling and comparable timing for unknown, disabled and
  wrong-password attempts;
  malformed sign-in input refused before any verification. Tests:
  `Identity.Domain.Tests` `ThrottleTests`, `Identity.Application.Tests`
  `SignInTests` and `OptionsTests`, `Identity.Persistence.EfCore.Tests`
  `SessionAndLedgerTests` and `Identity.HttpApi.Tests` `SignInEndpointTests` and
  `ClientSourceTests`, all `TM_G02_015_*`. Remaining: an operator alert for a
  sustained attack, which no G-02 work order covers; the one-time-code and
  recovery journeys and their endpoints (G-03); AC-002's API-contract evidence
  (SEC-08). Throttle rows accumulate until a retention job removes them
  (CODE-05).
- **TM-G02-016, done for G-02.** Delivered: a new session and token at every
  sign-in, ending the one presented; only the token's hash stored; the
  `__Host-` cookie; no authentication over plain HTTP or for an unconfigured
  host; idle and absolute limits at their edges, with the reason recorded.
  Tests: `Identity.Domain.Tests` `SessionRulesTests`, `Identity.Application.Tests`
  `FreshnessTests`, `Identity.Persistence.EfCore.Tests` `SessionAndLedgerTests`
  and `Identity.HttpApi.Tests` `SessionCookieTests`, all `TM_G02_016_*`.
  Remaining: adversarial evidence (SEC-08). Ended sessions accumulate until a
  retention job removes them (CODE-05).
- **TM-G02-017, partly.** Delivered: sessions, account state and delegations
  read from the store on every request, so a revocation, password change,
  disabled account or security-state change ends a session at the next request;
  a recheck inside a running request through `IPrincipalFreshness`. Tests:
  `Identity.Domain.Tests` `SessionRulesTests`, `Identity.Application.Tests`
  `FreshnessTests` and `Identity.HttpApi.Tests` `RevocationTests`, all
  `TM_G02_017_*`, and `G02AC003Tests.AC_003_B02_*`. Remaining: permissions and
  their versions (SEC-05), which must call `SecurityStateChangedAsync` or
  recheck their own grants; the isolation and real-provider evidence for AC-003
  beyond AC-003-B02 (SEC-08).
- **TM-G02-018, partly.** Delivered: actor, effective subject and identity kind
  on every principal; delegation between users only, explicit, scoped, limited
  to a tenant when the grant names one, expiring and revocable, exercised only
  from the actor's own session; service identities with exactly their declared
  scope, never borrowing an interactive session. Tests:
  `Framework.Security.Abstractions.Tests` `ExecutionPrincipalTests` and
  `ExecutionPrincipalScopeTests`, `Identity.Domain.Tests` `DelegationGrantTests`,
  `Identity.Application.Tests` `ServiceIdentityScopeTests`, all `TM_G02_018_*`,
  and `G02AC003Tests.AC_003_B02_*`. Remaining: delegation and
  impersonation issued by an administrator, which need a permission (SEC-05);
  audit records of issue, use and revocation (SEC-07); jobs and handlers
  running through `IServiceIdentityScopes` (CODE-05, with TM-G02-041).
- **TM-G02-055, done.** Delivered: salted PBKDF2-HMAC-SHA512 verifiers with
  their parameters recorded, at no fewer than 210,000 iterations; upgrade at
  sign-in; no credential, session token or verifier in a log, a response or an
  exception. Tests: `Identity.Persistence.EfCore.Tests` `PasswordVerifierTests`
  and `Identity.HttpApi.Tests` `LeakTests`, all `TM_G02_055_*`.
- **TM-G02-056, partly.** Delivered: the stored form, with versioned keys from
  the secret store, rotation and refusal without a usable key. Tests:
  `Identity.Domain.Tests` `CredentialFormTests`, `Identity.Application.Tests`
  `CredentialFormServiceTests` and `Identity.Persistence.EfCore.Tests`
  `CredentialStoreTests`, all `TM_G02_056_*`. Remaining: enrolment and
  verification journeys (G-03); evidence that a restored database is unusable
  without the keys (CODE-10 on CI-01, with OPS-004).
- **TM-G02-057, partly.** Delivered: the stored form, single consumption under
  concurrency and replacement of a set. Tests: the same three classes,
  `TM_G02_057_*`. Remaining: the recovery journey and its endpoints (G-03).
- **TM-G02-058, partly.** Delivered: the integrity-tagged stored form, checked
  before use. Tests: the same three classes, `TM_G02_058_*`. Remaining: passkey
  registration and sign-in (G-03); audit of credential changes (SEC-07).

`G02AC003Tests.AC_003_B02_DelegationHonorsScopeExpiryAndActorIdentity_Unit` and
`_Isolation` cover the reference case AC-003-B02: a delegated read inside its
scope succeeds at one second before expiry and records the actor, the effective
subject and their kinds; an update outside the scope, the read after expiry and
a revoked grant are refused; another session cannot present the grant; and a
job started from the delegated request runs as its service identity with only
that identity's scope. A fixture stands in for authorization's grants, which
are SEC-05's. The unit test uses in-memory stores; the isolation test runs
through the HTTP adapter on Kestrel against PostgreSQL. Tests named
`TM_G02_004_*`, `TM_G02_025_*` and `TM_G02_048_*` cover the identity module's
schema confinement, its membership records and tenancy seams, and its option
validation.

TM-G02-013, on boundary TB-G02-03, is partly delivered here: the credential
provider seam, bound to exact configured origins, and the redaction of its
values, tested by `Framework.Security.Abstractions.Tests` `ClientCredentialTests`
(`TM_G02_013_*`). Remaining: generated and dynamic clients that ask the provider
for every request and redirect target and never forward ambient credentials
(CODE-06).

These tests do not establish container, host or browser evidence, and test
source does not establish execution results.
