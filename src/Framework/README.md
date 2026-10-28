# Framework composition, hosting, observability, configuration, ingress, tenancy and authorization

`Modularyn.Framework.Modularity.Abstractions` contains immutable manifests,
SemVer values, explicit version intervals, composition validators and lifecycle
contracts. It uses only the .NET shared framework and can be referenced by
domain and application contract projects under the existing dependency rules.

`Modularyn.Framework.Modularity` validates an exact selection, verifies it
against the release's deployment lock and produces a dependency-ordered
activation plan. It does no installation or module discovery.
`Modularyn.Framework.Hosting` connects that plan to the standard .NET host used
by ASP.NET Core, using a `Microsoft.AspNetCore.App` framework reference.
`Modularyn.Framework.Observability` supplies structured logging, W3C correlation,
traces and metrics through the shared frameworks. Hosting references this package;
the modularity contracts remain independent of logging and HTTP adapters.
`Modularyn.Framework.Configuration` binds and validates deployment options,
resolves secret references and declares the data-protection key ring.
`Modularyn.Framework.Ingress` applies the HTTP ingress policy. Configuration and
Ingress use only the `Microsoft.AspNetCore.App` shared framework.
`Modularyn.Framework.Tenancy.Abstractions` holds the tenancy contracts and uses
only the .NET shared framework, so domain and application contract projects can
reference it. `Modularyn.Framework.Tenancy` resolves and keeps tenant context,
over the PostgreSQL persistence adapter and the ingress policy.
`Modularyn.Framework.Security.Abstractions` holds the execution principal, its
delegated authority and service scope, the per-unit principal holder and its
accessor, service identity scopes, the freshness recheck, `SecretText` and the client credential seam. It
uses only the .NET shared framework, and the identity module implements it; see
[the identity module](../Modules/Modularyn.Modules.Identity.Application/README.md).
`Modularyn.Framework.Authorization.Abstractions` and
`Modularyn.Framework.Authorization` hold the permission definitions, the one
evaluator, permission and host-authority administration and the entry points;
see [authorization](Modularyn.Framework.Authorization/README.md).

## Selection and validation

Module, contract, capability, provider and asset names use ordinal,
case-sensitive identity. A manifest retains its exact SemVer 2.0.0 version,
including build metadata. Ranges compare SemVer precedence: metadata does not
affect membership, and prereleases within the explicit bounds are included.
Both finite interval edges explicitly declare whether they are inclusive.
An omitted edge is unbounded and exclusive. `VersionRange.Any` is unbounded.

Required dependencies must be selected. Optional dependencies may be absent;
when selected, their versions and graph edges are validated in the same way as
required dependencies. The environment supplies the selected .NET runtime and
Modularyn Framework versions. Requirements for contracts, capabilities and
providers resolve against the selected manifests. Every compatible provider of
a requirement precedes its consumer. A module can satisfy its own provision
requirement without introducing a dependency on itself. Definitions, migrations,
background handlers and health checks are ownership declarations; composition
does not execute them.

Two different modules cannot provide the same provision if either declaration
marks it exclusive. Routes conflict when their methods overlap and their
templates identify the same route. Comparisons ignore case, trailing slashes
and parameter names while retaining parameter constraints, defaults, optional
markers and catch-all shape. The method `*` overlaps every method. This checks
declared route collisions; an application's endpoint adapters remain responsible
for interpreting route constraints and endpoint selection.

Validation runs in three phases. `ICompositionTrustValidator` implementations
run first, then the built-in structural checks, and both always run, so a
selection that is untrusted and structurally invalid reports both. Trust comes
first because structural diagnostics describe the manifests, and a manifest
can itself be the altered part: a widened range or a substituted version is
reported as a departure from the lock rather than only as a graph error whose
suggested correction would chase the altered declaration. Other
`ICompositionValidator` implementations run only after trust and structure both
pass, so they can assume a trusted, well-formed selection. Within each group,
validators run in ascending `Order`, with registration order breaking ties.
Every returned diagnostic refuses the entire selection. Validator failures and
cancellation also prevent module construction.

| Code | Composition refusal |
|---|---|
| MDC001 | Missing required module |
| MDC002 | Complete dependency cycle |
| MDC003 | Incompatible selected dependency version |
| MDC004 | Multiple providers of an exclusive provision |
| MDC005 | Conflicting routes |
| MDC006 | Duplicate module selection |
| MDC007 | Missing required contract, capability or provider |
| MDC008 | Incompatible contract, capability or provider versions |
| MDC009 | Incompatible .NET runtime |
| MDC010 | Incompatible Modularyn Framework |
| MDC011 | Duplicate named declaration in a manifest |

Diagnostics include the affected module identifiers, the unsatisfied constraint
or conflicting declaration, and a corrective action. A refused result has no
activation plan. Manifests, selections, plans and report snapshots cannot be
changed through their public collections.

## Deployment trust

`DeploymentLockValidator` is the trust validator for a release. It reads the
deployment lock manifest on every validation and refuses the selection unless
each selected module is listed at its exact version, including build metadata,
with the same manifest digest and migrations, every listed module is selected,
and every package file the lock lists for a module exists under the content
root as a regular file, reached through no link below the content root, with
the recorded SHA-256 hash.
A later activation attempt therefore observes the files as they are then.

`ModuleManifestDigest` is the SHA-256 of a manifest's canonical form: every
declaration in declaration order, with exact version text. A widened dependency
range, an added route or a changed exclusive flag changes the digest. The lock
is format `modularyn.deployment-lock`, `formatVersion` 1:

| Field | Contract |
|---|---|
| `format`, `formatVersion` | Exactly `modularyn.deployment-lock` and the integer `1` |
| `modules` | Nonempty; each `id` occurs once |
| `modules[].version` | The exact SemVer 2.0.0 version selected |
| `modules[].manifestSha256` | `ModuleManifestDigest` of the reviewed manifest, 64 lowercase hexadecimal digits |
| `modules[].migrations` | The manifest's migrations in order, as `name` and `version` |
| `modules[].files` | Nonempty `path` and `sha256` pairs; a path shared by modules has one spelling and hash |

Paths are relative, separated by `/`, and made of segments of ASCII letters,
digits, `.`, `_`, `+` and `-` that neither start nor end with `.`, so no segment
is `.`, `..`, rooted, a drive, a stream or a reserved device name. The parser
refuses unknown or missing fields, duplicate properties, comments, trailing
commas, other format versions and locks larger than 16 MiB, and names the
failing location without echoing values. `LockedModule.Describe` and
`DeploymentLock.ToJson` produce the canonical form for release tooling.

| Code | Trust refusal |
|---|---|
| MDC200 | The lock is missing, unreadable or too large; every selected module is named |
| MDC201 | The lock is not a valid format 1 lock |
| MDC202 | A selected module is not listed |
| MDC203 | A listed module is not selected |
| MDC204 | A selected version differs from the locked version |
| MDC205 | A manifest differs from its locked digest |
| MDC206 | Declared migrations differ from the locked migrations |
| MDC210 | A locked package file is missing |
| MDC211 | A locked package file's content hash differs |
| MDC212 | A locked package file is a link, is reached through a link or is not a regular file |
| MDC213 | A locked package file cannot be read |

Trust diagnostics name modules, the lock's file name and paths relative to the
content root. They never contain file system locations, exception text or file
content. `services.AddModularynDeploymentTrust(lockPath, contentRoot)` registers
the validator once; the content root defaults to the application base
directory. The host refuses activation with MDC108 when no trust validator is
registered, unless `ModuleHostOptions.AllowUnverifiedComposition` is set, which
is for development and test hosts only.

The lock binds module manifests and the files it lists. It does not bind the
host application's own assemblies, files the lock omits, or composition
switches, which this composer does not model. Verification runs when the host
composes, after the runtime has loaded any module assembly the application
references: it refuses to construct or activate modules from an altered
package, but it cannot stop code that runs when such an assembly loads, such as
a module initializer. Verifying artifacts before the process starts belongs to
deployment provenance. The lock's own integrity depends on the deployment
pipeline: someone who can replace both a package and the lock acts as a trusted
deployer, as the threat model assumes.

## Host lifecycle

Register the complete selection once with `services.AddModularynModules`,
along with any `ICompositionValidator` implementations. Register a `TimeProvider`
to replace the default `TimeProvider.System`. `ModuleRegistration.Create<TModule>`
uses standard .NET constructor injection; an explicit factory can also be used.
Factories must create fresh instances and must not acquire resources that cannot
be released through the returned instance. Services resolved by a factory follow
the application's service lifetimes; module factories run against the host's
service provider.
The service provider disposes registered diagnostic sources. When constructing
`ModuleHost` directly, dispose it after completing shutdown to release its sources.

The host validates first, then constructs, initializes and starts each module
in dependency order. A dependency reaches readiness before its dependent begins
initialization. Traversal visits module and dependency identifiers in ordinal
order, so discovery order does not affect the plan. The host is ready only after
every selected module is ready. Repeated or concurrent start
calls on a ready host have no further initialization effects. Lifecycle
transitions are serialized; callbacks must not reenter the same host's lifecycle.

Shutdown calls `StopAsync` in reverse order on every owned instance, including
an instance whose initialization only partially completed. An ordinary startup
failure records the failing module and stage, marks unstarted modules unavailable,
and completes cleanup before returning the failure. Startup cancellation is
propagated as cancellation and uses an independent cleanup deadline so acquired
resources can still be released. The default deadline is thirty seconds and
uses the injected clock. Lifecycle callbacks must cooperate with cancellation and
return asynchronous tasks without blocking the calling thread. The host bounds
its wait even when an asynchronous shutdown callback ignores cancellation. That
callback remains owned and visible until cleanup completes; it cannot be forcibly
terminated. A retry observes the same pending task before another cleanup call
can run on that instance.

Shutdown failure or cancellation is reported for each affected module; cleanup
continues for the remaining instances. Unreleased instances stay visible in
`RetainedInstances`, and a new start is refused until `StopAsync` successfully
cleans them up. A subsequent activation revalidates the identical selection and
creates new instances. Each module owns idempotence of its durable initialization
effects across attempts; the host does not infer or reverse application data
changes. The recovery tests exercise this contract with durable filesystem
effects and detect resource remnants with exclusive file creation.

Each callback receives the same `ModuleContext` for its activation, containing
the manifest, clock and startup trace context. Cancellation flows explicitly
through composition and lifecycle contracts. Reports include per-module states,
attempt-numbered lifecycle events, timestamps from `TimeProvider`, and diagnostics.
MDC100 through MDC103 identify construction, initialization, start and shutdown
failures; MDC104 identifies startup cancellation and MDC105 incomplete validation.
MDC106 identifies work remaining after the drain deadline; MDC107 identifies
missing or undeclared health-check bindings; MDC108 identifies a host with no
trust validator.

## Correlation and diagnostic sources

`AddModularynModules` includes `AddModularynObservability`. The latter can also
register diagnostics independently. It installs no logging provider or exporter;
applications use ordinary `ILogger` providers. Every emitted event carries
`CorrelationId` and `SpanId` as structured fields, including when scope rendering
is disabled. Enabling scopes also carries `CorrelationId`, `TraceId` and `SpanId`
into logs written by code inside the operation.
`ModularynDiagnostics.SourceName` and `MeterName` identify the standard
`ActivitySource` and `Meter` export seams. A separately installed exporter can
subscribe by name and attach a logging provider without changing lifecycle code.

`BeginOperation` accepts an explicit W3C parent or uses the ambient W3C context.
It creates correlation context even when tracing is unsampled. Dispose the scope
in its creating execution context; call `Succeed` or `Cancel` to record that
outcome. An unfinished scope records failure. A unit of work uses its trace id
as its correlation id. Nested operations retain that id and receive their own
span ids. The activation context flows through module callbacks and lifecycle
events; shutdown continues the activation trace. Identifiers are diagnostic data
and carry no authority.

Lifecycle logs and trace events contain the module id, module version, stage
and outcome. They never receive configuration, request bodies or exceptions.
The existing composition and lifecycle report remains the explicit inspection
surface for startup and cleanup diagnostics. `IDiagnosticRedactor` is an optional
extension point applied to module metadata before logging or tracing; returning
null omits that field. This package defines no redaction policy.

The meter emits `modularyn.operations`, `modularyn.operation.duration` in seconds,
and `modularyn.module.lifecycle.events`. Duration uses `TimeProvider` timestamps.
Only the finite `operation` and `outcome` vocabularies label measurements.
Measurements occur inside the active activity so an exporter can correlate
exemplars without placing correlation identifiers in metric labels.

## Health, request admission and draining

Supply a `ModuleHealthCheckRegistration` for each manifest health-check asset.
Names match ordinally within the module. Missing or undeclared bindings refuse
startup before factories execute. `Create<TCheck>` resolves a registered check
from the health invocation's dependency injection scope; factories can also be
supplied explicitly. These bindings become ASP.NET Core health checks.

`MapModularynHealthChecks` maps `/health/live` and `/health/ready` by default.
Liveness indicates that the host can answer a probe; it is independent of module
readiness. Readiness requires a ready module host, open work admission and healthy
declared checks. Degraded or unhealthy readiness returns HTTP 503; healthy status
returns HTTP 200. Each response contains only `Healthy`, `Degraded` or `Unhealthy`.
The adapter discards check descriptions, exceptions and data before returning
results to the health service, including its logging. Check implementations remain
responsible for any logging they perform themselves.

Place `UseModularynRequests` after routing and before module endpoints. It keeps
health probes reachable while normal requests require a `ModuleWorkGate` lease.
Each accepted request retains its lease until the pipeline completes. The
middleware uses ASP.NET Core's W3C context and records no headers, URLs or bodies.
Background entry points use the same gate's `TryEnter` and retain the returned
lease until their work finishes. This gate applies only to consumers that use it.

The application-stopping notification and the hosted lifecycle's stopping phase
close admission before module shutdown. Existing leases drain before modules
stop. The standard host's `HostOptions.ShutdownTimeout` and the caller's
cancellation bound draining and cleanup; the module host also applies that
timeout through its injected clock. At the deadline, remaining leases receive
cancellation, cleanup is attempted for every owned module, and the report names
retained instances and the outstanding work count. Startup cannot resume while
old leases or instances remain. Modules own durable checkpoints and resource
release; the gate does not implement job or lease-provider semantics.

## Deployment options and secrets

`services.AddModularynDeploymentOptions<TOptions>(configuration, sectionPath)`
binds one section and validates it with data annotations and any registered
`IValidateOptions<TOptions>` before the host starts a hosted service, so an
invalid section refuses startup before module activation. Binding is strict:
a key the options do not declare, or a value that cannot be converted, fails
validation. Binder messages can quote the configured value, so the failure
names only the section, key path and type. Binding reads a copy of the section
taken at first use, which is startup, and no change-token source is registered,
so neither `IOptions`, `IOptionsMonitor` nor `IOptionsSnapshot` observes a later
reload: deployment options change only on restart. Application and tenant
settings, and their reload, are outside this contract.

A secret setting is a `SecretReference` property, configured as
`secret:<name>`. A name has ASCII letters, digits, `.`, `_` and `-`, at most 128
characters, and neither starts nor ends with `.`. A literal value bound to such
a property is discarded during conversion, never retained, and refused at
startup with the property's key path. `ISecretStore` resolves references;
`services.AddModularynSecrets(configuration)` registers `FileSecretStore` over
the fully qualified, existing directory in `Modularyn:Secrets:Directory`, where
the deployment mounts one file per secret. A link is followed only when its
final target stays inside that directory. Empty files and files over 64 KiB are
refused, and the bytes are returned exactly. A `SecretResolutionException`
names the reference only and carries no inner exception. `SecretValue` formats,
logs, debugs and serializes as `[secret]`, cannot be deserialized, reveals its
value only through `RevealBytes` and `RevealString`, and clears its bytes on
disposal.

These types keep secrets out of what the framework itself formats, logs and
reports. They cannot stop module code from logging a value it revealed, or from
placing one in an exception message, which a lifecycle diagnostic would then
include. Masked settings that accept only replacement belong to the settings
contract, and repository secret scanning runs in CI.

## Data-protection key ring

`services.AddModularynDataProtection(configuration)` adds ASP.NET Core data
protection with a declared key ring in `Modularyn:DataProtection`:
`ApplicationName`, the boundary that may read protected payloads; `KeyLifetime`,
from 7 to 90 days with 90 by default; and `Directory`, a fully qualified,
existing directory that is not a link and has owner-only access. Startup is
refused while any of these is missing or invalid, so keys are never generated
in an undeclared or shared location. The application name, lifetime and store
are applied after other data-protection configuration. Startup is also refused,
before any hosted service starts, when the effective key store is not the one
the declaration created or the effective key lifetime or application
discriminator differs from it, as after a later post-configuration.

The directory store reads only regular `.xml` files, never follows links,
prohibits DTDs, writes each key with mode 0600 on Unix through a temporary file
moved into place, and refuses to read or write while the directory has group or
other access. On Windows, owner-only means a protected access list whose allow
rules name only the current account, LocalSystem or Administrators.
`KeyRingDirectory.Create` makes such a directory with mode 0700 or that access
list. Keys are stored unencrypted at rest; the directory's access control is the
protection, and the deployment may add a key encryptor.

ASP.NET Core rotates keys: it creates the next key before the current one
expires, and older keys keep unprotecting their payloads. `IKeyManager.RevokeKey`
or `RevokeAllKeys` revokes keys; the revocation is stored at once, a running host
honors it when its cached key ring refreshes, and a restarted host honors it
immediately. Operators back the directory up separately from data exports, with
access limited to security operations. A store in another provider is supplied
through the `keyStore` argument, in which case `Directory` must be absent. The
PostgreSQL key store depends on the persistence package and is not part of this
package.

## HTTP ingress

`services.AddModularynIngress(configuration)` reads `Modularyn:Ingress` and
`app.UseModularynIngress()` applies it first in the pipeline:

1. Forwarded headers are applied only when the immediate peer is one of the
   exact `Proxies` addresses, processing `ForwardLimit` entries from the nearest,
   and only forwarded hosts among the public origins' hosts. With no proxies,
   none are applied. Every forwarded header that was not applied is removed, as
   are client-supplied `X-Original-*` headers. Forwarded headers from a peer that
   is not a declared proxy are logged as a warning (event 8200), once per peer.
   That peer may be an undeclared reverse proxy, which would present every client
   behind it as one address to per-source limits.
2. Host filtering accepts only the hosts of `PublicOrigins` and refuses an empty
   host, with 400 and no detail.
3. Plain HTTP is redirected with 308 to `HttpsPort` on the same configured host,
   with no body, before any later processing, so it carries no content or cookies.
4. Every HTTPS response for a configured host carries Strict-Transport-Security
   with `HstsMaxAge`, from one day to two years; `HstsIncludeSubDomains` and
   `HstsPreload` are deployment choices, and preload requires both subdomains and
   at least a year. Responses that Kestrel produces itself, such as 431, and the
   400 for an unconfigured host do not carry it.
5. A declared body larger than `Limits:MaxRequestBodyBytes` is refused with 413
   before any handler, and an undeclared one is capped at the same size. Kestrel's
   header, header-count, request-line and header-timeout limits, the form limits
   and the JSON nesting depth come from `Limits`.
6. Cross-origin access is granted only to exactly listed HTTPS `CrossOrigin:Origins`,
   with credentials, the exact origin value and the listed methods and headers.
   Other origins, including `null`, receive no grant and their preflights are
   refused with 403. Every response from this step on varies by `Origin`, and
   cross-origin headers set later in the pipeline are replaced or removed. With no
   listed origins the application is same-origin only.
7. When an authentication scheme is registered, authentication runs here: after
   the transport, host and cross-origin checks, so no credential is read from a
   plain HTTP request or for an unconfigured host, and before antiforgery
   validation, so antiforgery tokens are validated against the authenticated
   user. A pipeline that called `UseAuthentication` or `UseAuthorization` before
   the ingress is refused. Place `UseAuthorization` after the ingress, routing
   and any tenancy middleware. Startup is also refused when authorization
   services are registered but `UseAuthorization` is never called after the
   ingress, since WebApplication would otherwise add authorization ahead of the
   whole pipeline, before authentication.
8. Every request with a method other than GET, HEAD, OPTIONS or TRACE that carries
   cookies needs a valid antiforgery token, in the `RequestVerificationToken`
   header or the form. A request without cookies is exempt only when its endpoint
   disables antiforgery, as an endpoint for bearer-token clients does. The
   antiforgery cookie is `__Host-Modularyn-Antiforgery`, Secure, HttpOnly and
   SameSite=Strict.

The options are validated before startup: public and cross-origin origins are
exact HTTPS origins with ASCII hosts and no wildcard, path, query or user
information; proxies are canonical single addresses; methods and headers are
single names, never `*`. After other configuration has run, startup is also
refused if the effective host filtering, forwarded-header trust, HTTPS and HSTS,
antiforgery cookie, Kestrel, form or JSON options differ from the policy, or if
the key ring was not declared with `AddModularynDataProtection`, because
antiforgery tokens are data-protected. `PublicOrigins` builds absolute links from
configuration rather than from request headers and accepts a redirect only to a
local path or a configured origin.

Endpoints must not change state on safe methods; the ingress cannot observe
that. Operation-specific input contracts, paging and query bounds, public error
contracts, session cookies, which the identity module issues, and tenant
resolution are not part of the ingress.

## Tenancy

`Modularyn.Framework.Tenancy.Abstractions` contains `ITenantScoped` and
`IHostScoped`, the validated `TenantContext`, the canonical tenant identifier,
the propagated-context format and four seams. Tenancy implements none of the
seams; while one is absent or fails, every operation that needs it is refused:

| Seam | Answers | Supplied by |
|---|---|---|
| `ITenantActorResolver` | The actor of an authenticated principal | Identity (SEC-03) |
| `ITenantMembership` | Whether an actor is currently a member of a tenant | Identity (SEC-03); roles within a membership are authorization's (SEC-05) |
| `IHostAuthority` | Whether an actor currently holds host authority for one operation, and the operation's declared reach | Authorization (SEC-05) |
| `IHostScopeAudit` | Records a host-scope grant before the scope exists | Audit (SEC-07) |

The claim type `modularyn.tenant` is the tenant candidate that a principal may
carry. Identity issues it only for a delegated request whose grant names a
tenant, so that request reaches only that tenant; see
[the identity module](../Modules/Modularyn.Modules.Identity.Application/README.md#tenancy).

### Directory

`TenantDirectoryModule.Create` returns the directory's PostgreSQL module, with
its own schema, context, migration and manifest. Add it to the host's one module
registry and selection, so the migration coordinator applies its migration and
the activation gate checks it. A tenant has an identifier, a name and a state;
each domain names exactly one tenant. `TenantDirectoryAdministration` changes the
directory only in a host scope granted for `modularyn.tenancy.directory`, and
accepts a domain only when it is the host of a configured public origin.
Resolution reads the directory afresh for every unit, so a suspension or a
removed domain takes effect at once.

### Resolution

`services.AddModularynTenancy(directoryModule)` registers tenancy, and
`app.UseModularynTenancy()` applies it after the ingress, routing and
authentication and before authorization and the endpoints. An endpoint declares
`RequireTenantMember()` or `RequireHostAuthority(operation, reason)`; an endpoint
without either gets no tenant context, whatever its request carries.

The candidates are the host of the configured public origin that matches the
validated request host, through SEC-02's `PublicOrigins` and never from a raw
Host or forwarded header; the `tenant` route value; the `X-Modularyn-Tenant`
header; and the principal's `modularyn.tenant` claims, which resolution adds on
every path. A tenant identifier is accepted only in its canonical form, a
lowercase hyphenated GUID that is not empty; a variant in letter case, braces,
whitespace, encoding or separators is refused, never normalized. Each source
may give one value, every source must name the same tenant, a tenant domain
must be that tenant's, the directory must hold the tenant as active, and the
actor must be a current member. Only then does a context exist.

Malformed or ambiguous candidates answer 400 and an unauthenticated principal
401, both decided before any lookup. Every other refusal of a member endpoint
answers 404 with no body: an unknown, suspended or other tenant, conflicting or
missing candidates, a principal that is not a member, a request off the
configured origins and an unavailable seam look alike.
A host endpoint answers 403 when host authority is missing or the request names
a tenant, and 503 when audit or a seam is unavailable; an unavailable directory
answers 503. The refusal codes `MTN001` to `MTN013` are logged with no request
value: refusals at information level, and an unavailable directory, seam or
audit store as a warning, even where the response stays 404, so that a member
endpoint's outage reveals nothing about which tenants exist.

Each declaration also wraps the endpoint's request delegate. A declared endpoint
runs only when its request has an established, active context of the declared
kind, and for a host endpoint the declared operation. Otherwise it answers 500
with no body and logs an error. A pipeline that places `UseModularynTenancy`
before routing, or omits it, therefore fails closed instead of running declared
endpoints without a context.

### Execution context

`TenantExecution` is a scoped service, so each request, job and message has its
own in its own service scope. Its context is established once and cannot change.
It ends when the request leaves the tenancy middleware or the scope ends, on
every path, exceptions and cancellation included. Ending it also ends the
`TenantContext` itself, so a leaked reference to it, or a unit of work bound to
it, can no longer reach tenant data. No ambient state carries the context:
there is no `AsyncLocal`, and work that is not given the context explicitly,
such as a task started without it or a new scope, has none.

### Host scope

Host scope exists only after `IHostAuthority` grants the operation and
`IHostScopeAudit` has recorded the grant, with the unit, actor, operation,
reason, source, time and correlation identifier. A request that names a tenant
in its route or header, or arrives on a tenant's domain, never gets host scope,
and a tenant administrator's membership never implies it. A host-scope unit
reads tenant data only through `AcrossTenants`, which still requires the scope
to be active at each execution, and changes none of it except a named tenant's
row through `ModuleSql.UpdateForTenantAsync`. Host-scoped data, such as the
directory, changes only in a host scope, in the persistence adapter's named
`DeploymentScope`, which product code outside the adapter does not open, or in
the owning module's owner-bound unit, which writes only that module's own
host-scoped records and reaches no tenant data; a unit of work with none of them
can only read it.

A host grant carries its operation, which names every entity the scope may
reach as a module and table. `IHostAuthority` returns the operation with its
reach, and tenancy refuses a grant for any other operation (`MTN009`). The
persistence adapter then refuses, with `MPS043`, a cross-tenant read, a named
tenant's update or a host-scoped write in that scope outside the reach; an
operation such as identity's account administration, whose records the owning
module writes in its own owner-bound unit, reaches no entity in host scope. The
elevated policy that grants operations is
[authorization's](Modularyn.Framework.Authorization/README.md#host-authority).

### Propagation contract for events, jobs and files

CODE-05 (events and jobs) and CODE-07 (Files) carry tenant context this way:

1. **Produce in the tenant's unit.** Write the outbox event, job or
   file-processing row as an `ITenantScoped` entity in the producing tenant's
   unit, so its tenant comes from that unit and the row shares its transaction,
   and store `PropagatedTenantContext.Capture(context).ToJson()` beside the
   payload. The format holds the scope, the tenant, the actor's kind and subject,
   the host operation and reason in host scope, the producing unit and the time:
   references only, never a credential, token, request object or payload value.
   The stored reference selects the authority the work runs with: a service
   identity that runs the item acts for the actor it names. So only server code
   produces one, with `Capture` from the validated context inside the producing
   unit; never build one, or accept one, from a request, a payload or another
   row. It carries no integrity tag yet, and revalidation checks the tenant and
   the actor's membership, not that the named actor produced the item. CODE-05's
   stores take over the capture, SEC-07 binds the producing unit to its audit
   record, and SEC-08 tests forged and replayed references.
2. **Dispatch from host scope.** A dispatcher lists due work across tenants in an
   audited host scope through `AcrossTenants`. It cannot change those rows, except
   to record a disposition on one named tenant's row with `UpdateForTenantAsync`.
3. **Revalidate on receipt.** Run each item with `TenantWorkRunner.RunAsync`. It
   parses the stored reference strictly, revalidates it in a new service scope
   (the tenant still active and the actor still a member, or, in host scope, host
   authority again with a new audit record) and passes the reconstructed context
   explicitly. Begin the item's units with that context, so its effect and its
   completion share one transaction. Never act for a tenant that the payload names.
4. **Record the disposition.** `Completed` ran the work. `Denied` is terminal, for
   a malformed reference, an unknown or suspended tenant, an actor who is no longer
   a member, or revoked host authority: record it and do not retry. `Deferred` is
   transient, for an unavailable directory, seam or audit store: retry under the
   bounded policy. An exception from the work itself propagates to that policy.
5. **Files.** File metadata is tenant-scoped; storage locations derive from the
   validated context's tenant and server-issued identities, never from a supplied
   name or tenant; and scanning and validation steps propagate as jobs do.

### Tenant cache

`TenantCache` builds each key from an active context and a declared
`TenantCacheEntry`: the tenant or host scope, the owning module, its schema
version, the principal for a principal-scoped entry, the entry name and its
parts, each length-prefixed, so entries that differ in one dimension never share
a key. A host-scope entry is keyed by its grant's operation as well, so a
scope granted for one operation never reads another's entries, and
`InvalidateHost` and `InvalidateHostPrincipal` make an operation's entries, or
one actor's, unreachable at once. An authorization-sensitive entry must be
principal-scoped. Each entry declares a staleness limit of at most one day and
expires at it; `InvalidateTenant` and `InvalidatePrincipal` make earlier entries
unreachable at once. A fill that overruns its limit fails with
`TenantCacheStaleException` and is not cached. Concurrent misses for one key
share a single build, which a waiter's cancellation does not cancel, and a
failed build is not cached. The store is the
process's own memory cache: it has no separate outage, and it coordinates
nothing across processes, which the single-node profile does not need.

## Threat-model controls

For each control of boundaries TB-G02-01, TB-G02-02, TB-G02-06, TB-G02-08 and
TB-G02-11, and for the tenant part of TM-G02-029, this records what these
packages deliver, the tests that exercise it and what remains, with the work
order it waits on. Tests are in the test project of the package named. The
SEC-02 controls' tests are unit and local host tests. The tenancy tests with the
trait `Requires=PostgreSQL` run against PostgreSQL, through CI's real-provider
job, and the others are unit tests; identity, membership, host authority and
audit come from test doubles of their seams. SEC-08 maps every control to its
evidence.
The controls of TB-G02-04, identity and sessions, are recorded in
[the identity module](../Modules/Modularyn.Modules.Identity.Application/README.md#threat-model-controls),
and those of TB-G02-05, authorization, with SEC-05's parts of TM-G02-017,
TM-G02-028 and TM-G02-033, in
[authorization](Modularyn.Framework.Authorization/README.md#threat-model-controls).

- **TM-G02-001, partly.** Delivered: the deployment lock and package integrity
  as a trust validator, and MDC108 for a host without one. Tests:
  `Modularity.Tests` `DeploymentTrustTests` and `Hosting.Tests`
  `DeploymentTrustHostingTests`, all `TM_G02_001_*`. Remaining: composition
  switches in the lock (FND-001), which the composer has no input for and no
  G-02 work order covers; lock generation in release tooling and verification of
  artifacts before the process loads them (OPS-009, G-08); real-provider evidence
  on the sample host (CODE-10 on CI-01).
- **TM-G02-002, partly.** Delivered: required deployment configuration, including
  ingress and the key ring, validates before any hosted service starts, so no
  module is constructed while it is invalid. Tests: `Configuration.Tests`
  `DeploymentOptionsTests.TM_G02_048_Invalid_deployment_options_refuse_startup_before_any_module_is_constructed`
  and `KeyRingTests.AC_031_R04_A_deployment_without_a_declared_key_ring_stays_unready_and_generates_no_keys`;
  `Ingress.Tests`
  `IngressOptionsTests.TM_G02_006_Widened_ingress_options_or_a_missing_key_ring_refuse_startup_before_module_activation`.
  Declared permissions and host operations must match the composed manifests
  before activation (SEC-05, `MDC310`; `Authorization.Tests`
  `DecisionTests.TM_G02_002_*`).
  Remaining: activation only after migrations complete (CODE-03); declared
  endpoints (CODE-06); disabling a module with a
  report of its dependents, active work and retained data (FND-003), which no
  G-02 work order covers: CODE-02's scope excludes it and the tool command is
  DEV-005, Tooling, at G-06. Under the restart model, a selection that drops a
  required module is already refused with MDC001 naming both modules
  (`Hosting.Tests` `AcceptanceTests.AC_001_N01_*`), but active work and retained
  data are not reported.
- **TM-G02-003, not SEC-02's.** CODE-02 keeps the host unready after a lifecycle
  failure and CODE-08 adds readiness and draining; SEC-02 adds
  `DeploymentTrustHostingTests.TM_G02_001_Readiness_follows_a_verified_activation_and_a_refused_composition_stays_unready_with_status_only_health`.
  Remaining: migration-readiness failure keeping the host unready (CODE-03) and
  recovery evidence (CODE-10 on CI-01).
- **TM-G02-004, not SEC-02's.** The architecture checks reject cross-module
  persistence references (DEP009). Remaining: migrations confined to the owning
  module's schema (CODE-03).
- **TM-G02-005, deferred.** No extension-property mechanism exists, and no G-02
  work order covers FND-005.
- **TM-G02-006, partly.** Delivered: declared-proxy forwarded headers, host
  filtering, `PublicOrigins` links and redirect targets, and refusal of widened
  options; a warning for forwarded headers from a peer that is not a declared
  proxy (SEC-03). Tests: `Ingress.Tests` `ProxyAndHostTests` (`AC_004_N01_*`),
  `UndeclaredForwarderTests` and `IngressOptionsTests.TM_G02_006_*`. Tenant domains, limited to the configured
  public origins, are resolved through `PublicOrigins` (SEC-04, under
  TM-G02-025), and the identity module's sign-in returns only a local path or a
  configured origin as its redirect (SEC-03; `Identity.HttpApi.Tests`
  `SignInEndpointTests.TM_G02_006_*`), and identity answers tenancy's membership
  seam (SEC-03). Remaining: adversarial isolation evidence (SEC-08).
- **TM-G02-007, partly.** Delivered: antiforgery on every state-changing request
  that carries cookies, and the antiforgery cookie. Tests: `AntiforgeryTests`
  (`AC_003_N02_*`, `TM_G02_007_*`). The session cookie is the identity module's
  `__Host-` cookie, and antiforgery tokens are validated against the
  authenticated user (SEC-03; `AuthenticationOrderTests.TM_G02_007_*` and
  `Identity.HttpApi.Tests` `SessionCookieTests.TM_G02_007_*`). Remaining: no state
  change on safe methods, which the ingress cannot observe, through endpoint
  conventions (CODE-06) and contract tests (SEC-08).
- **TM-G02-008, deferred.** Operation-specific input contracts and server-derived
  fields (CODE-06; SEC-08 for AC-003-N01).
- **TM-G02-009, partly.** Delivered: body size, header size and count, request
  line, header timeout, form limits and JSON depth. Tests: `RequestLimitTests`
  (`TM_G02_009_*`) and `HttpsTests`. Remaining: whole-request time limits and
  field lengths in JSON bodies (CODE-06); bounded paging, sorting and filtering on
  declared fields (CODE-06, with CODE-03 for query translation); per-endpoint
  upload limits (SEC-06); per-tenant resource governance (OPS-018, G-08).
- **TM-G02-010, deferred.** Stable public error codes with correlation (CODE-06,
  with CODE-08's correlation). Ingress refusals are bare status codes with no
  detail.
- **TM-G02-059, partly.** Delivered: credentialed access only for exactly listed
  origins. Tests: `CrossOriginTests` (`AC_003_N02_*`, `TM_G02_059_*`).
  Remaining: browser evidence on the sample's Razor surface (SEC-08 on CODE-09)
  and client evidence (CODE-06).
- **TM-G02-060, partly.** Delivered: plain HTTP redirected before processing and
  HSTS on HTTPS responses for configured hosts, judged only from a declared
  proxy; authentication only after those checks; refusal of authentication or
  authorization placed before the ingress, and of authorization services without
  `UseAuthorization` after it (SEC-03). Tests: `HttpsTests` (`AC_031_P01_*`) and
  `AuthenticationOrderTests` (`TM_G02_060_*`). Remaining: recording the subdomain
  and preload choices with the release, which no G-02 work order covers; host
  and container evidence (CODE-10 on CI-01). Kestrel's own refusals carry no
  HSTS.
- **TM-G02-047, partly.** Delivered: secret references, the mounted file store
  and redacted secret values. Tests: `SecretStoreTests` and
  `DeploymentOptionsTests` (`TM_G02_047_*`). Remaining: masked secret settings
  that accept only replacement (CODE-04); redaction in logs and traces (SEC-07);
  secret scans of release artifacts (OPS-004, G-08). The repository scan runs in
  CI.
- **TM-G02-048, partly.** Delivered: deployment options validated before any
  hosted service starts, bound strictly and never reloaded. Tests:
  `DeploymentOptionsTests` and `IngressOptionsTests` (`TM_G02_048_*`).
  Remaining: provider compatibility at startup (CODE-03); validation before an
  application or tenant settings reload takes effect (CODE-04); real-provider
  evidence (CODE-10).
- **TM-G02-049, deferred.** Separate application and tenant settings contracts
  with precedence, and tenant-configurable declarations (CODE-04, with SEC-04).
  Deployment options are already a separate, restart-only contract.
- **TM-G02-050, partly.** Delivered: the declared, owner-only key ring scoped by
  application name, with a declared lifetime, refusal of a missing or replaced
  declaration, rotation and revocation. Tests: `KeyRingTests` (`AC_031_R04_*`,
  `TM_G02_050_*`). Remaining: the PostgreSQL key store (CODE-03), through the
  `keyStore` argument; key encryption at rest, which no G-02 work order covers;
  the backup procedure, which is operational; recovery evidence on host and
  container (CODE-10 on CI-01).
- **TM-G02-025, partly.** Delivered: host names, routes, headers and claims are
  candidates only; canonical identifiers; refusal of malformed, ambiguous,
  conflicting, unknown, suspended and foreign candidates before any handler, with
  one contract for existing and unknown tenants; the effective tenant only after
  the directory, domain and membership checks; tenant domains limited to the
  configured public origins, read through `PublicOrigins`; declared endpoints that
  refuse to run without a context, whatever the pipeline order. Tests:
  `Tenancy.Tests` `TenantResolutionTests` and `TenancyHttpTests` (`TM_G02_025_*`),
  and `G02AC004Tests.AC_004_P01_*`; the tests replace the seams with doubles.
  Identity supplies the actor and membership seams and the delegation's tenant
  claim (SEC-03; `Identity.HttpApi.Tests`
  `TenancyIntegrationTests.TM_G02_025_*`). The permission for the requested
  operation is the evaluator's, within the established context, and membership
  administration needs a host scope for identity's memberships operation that
  reaches the membership table (SEC-05; `Authorization.Tests`
  `G02AC003Tests.AC_003_P01_*` and `Identity.Application.Tests`
  `AdministrationTests.TM_G02_019_*`). Remaining: adversarial evidence for AC-004-N01 (SEC-08); the
  website profile's equivalent host forms (AC-004-B03). An unavailable seam on a
  member endpoint answers 404, by design, and is logged as a warning.
- **TM-G02-026, partly.** Delivered: a scoped `TenantExecution` for each request
  and each propagated unit, established once, unchangeable and ended with its
  scope on every path, including exceptions and cancellation; no ambient state;
  persistence refuses a unit whose context has ended. Tests:
  `G02AC004Tests.AC_004_B01_*`, `ExecutionContextTests` and
  `TenancyHttpTests.TM_G02_026_*` (`Tenancy.Tests`). Remaining: CODE-05's workers
  running each message and job through `TenantWorkRunner`; host and container
  evidence on the sample application (CODE-10 on CI-01).
- **TM-G02-027, partly.** Delivered: the `PropagatedTenantContext` format, which
  holds references only and is parsed strictly; revalidation of tenant and actor,
  or host authority with a new audit record, before any work; terminal and
  transient dispositions; the named-tenant update for recording a denial. Tests:
  `PropagationTests` and `PropagationDatabaseTests` (`TM_G02_027_*`, `TM_G02_041_*`)
  and `G02AC004Tests.AC_004_R01_*`, over a test work table and dispatcher.
  Remaining: the durable outbox, inbox and job store adopting the contract, with
  AC-006 and AC-007 evidence (CODE-05), running each item through
  authorization's `AuthorizedWorkRunner`, which decides the producer's current
  permission for the specific action before any work (SEC-05;
  `Authorization.Tests` `G02AC003Tests.AC_003_R01_*`); file processing steps
  (CODE-07); adversarial evidence for AC-004-N03 (SEC-08).
- **TM-G02-028, partly.** Delivered: host scope only under host authority for a
  named operation and after its audit record is written; never derived from, or
  combined with, a tenant the request names; tenant data reached only through the
  explicit cross-tenant read, checked again at each execution, and never changed
  except a named tenant's disposition row; directory administration as a host
  operation; host-scoped data changed only in a host scope, a named deployment
  scope or the owning module's owner-bound unit, never by a unit without one; the
  owner-bound unit confined to its own module's host-scoped data, with no tenant
  access and no cross-tenant read or update, and opened only from that module's
  application and persistence projects (SEC-03). Tests: `HostScopeTests`,
  `HostScopeDatabaseTests` and `OwnerBoundUnitTests` (`Tenancy.Tests`),
  `HostWriteTests` and `OwnerScopeTests` (`Persistence.EfCore.Tests`) and
  `PersistenceBoundaryTests` (`Repository.Tests`), all `TM_G02_028_*`. Each
  host grant reaches only the entities its operation declares, on every host
  path (SEC-05; `HostScopeTests.TM_G02_028_*` and
  `HostScopeDatabaseTests.TM_G02_028_*`); the elevated policy and host-authority
  grants are authorization's. Remaining: the audit store, records in the
  transaction of the change and the affected tenants (SEC-07, TM-G02-051);
  adversarial evidence for AC-004-N03 (SEC-08).
- **TM-G02-029, tenant part (SEC-04), partly.** Delivered: every root entity
  declares its scope; the tenant and scope filters on CODE-03's seam for every
  tenant-scoped entity, on the EF and direct-SQL paths; the query guard, which
  refuses EF Core's general `IgnoreQueryFilters`, unissued named forms, raw SQL
  other than `ModuleSql`'s own text, `SqlQuery`, `ExecuteSql` and unsafe bulk
  writes, including physical deletes of soft-deleted rows and, under TM-G02-030,
  bulk writes that skip a version token; a scope predicate that holds a tenant
  unit to its tenant on its own; writes held to the unit's tenant; the tenant in
  every key, index and reference, in the model and in migrations; no tenant rows
  in migrations; the repository check that keeps raw SQL inside the adapter. Tests: `Persistence.EfCore.Tests` `QueryGuardTests` and
  `TenantModelTests`, `Tenancy.Tests` `TenantFilterTests` (`TM_G02_029_*`) and
  `Repository.Tests` `PersistenceBoundaryTests`. Remaining: audit records for the
  soft-delete and cross-tenant bypasses (SEC-07); bounded paging and sorting on
  declared fields (CODE-06); adversarial evidence for AC-004-N02 (SEC-08).
  In-process code that opens the connection itself can bypass every filter; only
  the repository check, over this repository's sources, covers that.
- **TM-G02-033, partly.** Delivered: `TenantCache` keys built only from an
  active context and a declared entry, varying by tenant or host, module, schema
  version, principal for principal-scoped entries, name and parts; declared
  staleness of at most a day; tenant and principal invalidation. Tests:
  `TenantCacheTests` (`TM_G02_033_*`) and `G02AC004Tests.AC_004_P01_*` and
  `AC_004_B01_*`. Host-scope entries keyed by their grant's operation and
  invalidated with it, and authority entries invalidated on every grant change
  and expiring at the declared freshness bound (SEC-05); settings and features
  invalidated on change (CODE-04). Remaining: bootstrap configuration
  (TM-G02-014, CODE-04); adversarial evidence for AC-004-B02 (SEC-08).
- **TM-G02-034, partly.** Delivered: one shared build for concurrent misses,
  independent of a waiter's cancellation, no cached failures, and the declared
  outage behavior of an in-process store that coordinates nothing across
  processes. Tests: `TenantCacheTests.TM_G02_034_*`. The settings and feature
  caches use it (CODE-04), and a settings fill that overruns its staleness bound
  is reported as `ValueUnavailableException` (SEC-05; `Settings.Tests`
  `ManagementTests.TM_G02_034_*`). Remaining: the operational-recovery evidence
  of AC-030, a website-profile family outside the G-02 reference plan.

## Coverage and verification

The matching test projects cover manifest semantics, resolution, trust, host
lifecycle, configuration, ingress and tenancy behavior. Test display names beginning
AC-001-P01, AC-001-N01, AC-001-B01 and AC-001-R01 correspond to the reference
corpus's valid graph, independent invalid graph mutations, inclusive version
boundaries and interrupted initialization recovery. Additional cases cover
exclusive capabilities, routes, extension ordering, cancellation, cleanup errors,
clock and trace propagation, concurrent starts and standard .NET host
integration.
Hosting tests also exercise the shared-framework diagnostic listeners and console
logger, status-only health responses over loopback HTTP, readiness transitions,
request admission, work draining and callbacks that outlive their shutdown deadline.

The corpus has no reference-profile trust, secret or key-ring case, so those
tests are named for the threat-model control they exercise, such as
`TM_G02_001_`. Ingress tests named for AC-003-N02, AC-004-N01 and AC-031-P01, and
key-ring tests named for AC-031-R04, exercise the rows their display names cite,
at the framework layer, against a real Kestrel server on loopback and the real
filesystem. They do not cover the tenant, identity, session or container parts
of those cases.

The tenancy tests named for AC-004-B01, AC-004-P01 and AC-004-R01, the cases the
G-02 evidence plan assigns to SEC-04, exercise those cases at the framework layer.
Their real-provider methods use the tenant directory, units of work and an items
fixture on PostgreSQL, a real Kestrel server behind the ingress, and a test work
table and dispatcher in place of CODE-05's outbox and job store; the unit method
uses an in-memory directory. AC-003, their security dependency, stays unmet:
identity, membership, host authority and audit are test doubles.

Apart from the tests with the trait `Requires=PostgreSQL`, these are unit and
local host tests. Reference-profile qualification requires the separately
executed real-provider, host and container evidence specified by the acceptance
corpus. Test source does not establish execution results.
