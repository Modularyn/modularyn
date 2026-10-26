# Framework composition, hosting, observability, configuration and ingress

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
   are client-supplied `X-Original-*` headers.
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
7. Every request with a method other than GET, HEAD, OPTIONS or TRACE that carries
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
contracts, session cookies and tenant resolution are not part of the ingress.

## Coverage and verification

The matching test projects cover manifest semantics, resolution, trust, host
lifecycle, configuration and ingress behavior. Test display names beginning
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

These are unit and local host tests. Reference-profile qualification requires
the separately executed real-provider, host and container evidence specified by
the acceptance corpus. Test source does not establish execution results.
