# Module composition and hosting

`Modularyn.Framework.Modularity.Abstractions` contains immutable manifests,
SemVer values, explicit version intervals, composition validators and lifecycle
contracts. It uses only the .NET shared framework and can be referenced by
domain and application contract projects under the existing dependency rules.

`Modularyn.Framework.Modularity` validates an exact selection and produces a
dependency-ordered activation plan. It does no installation or module discovery.
`Modularyn.Framework.Hosting` connects that plan to the standard .NET host used
by ASP.NET Core, using a `Microsoft.AspNetCore.App` framework reference.
`Modularyn.Framework.Observability` supplies structured logging, W3C correlation,
traces and metrics through the shared frameworks. Hosting references this package;
the modularity contracts remain independent of logging and HTTP adapters.

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

Built-in checks run first. After they succeed, `ICompositionValidator`
implementations run in ascending `Order`, with registration order breaking ties.
Every returned diagnostic refuses the entire selection. Validator failures and
cancellation also prevent module construction. SEC-02 can supply its validator
through this extension point. This implementation makes no trust decision.

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
missing or undeclared health-check bindings.

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

## Coverage and verification

The three matching test projects cover manifest semantics, resolution and host
lifecycle behavior. Test display names beginning AC-001-P01, AC-001-N01,
AC-001-B01 and AC-001-R01 correspond to the reference corpus's valid graph,
independent invalid graph mutations, inclusive version boundaries and interrupted
initialization recovery. Additional cases cover exclusive capabilities, routes,
extension ordering, cancellation, cleanup errors, clock and trace propagation,
concurrent starts and standard .NET host integration.
Hosting tests also exercise the shared-framework diagnostic listeners and console
logger, status-only health responses over loopback HTTP, readiness transitions,
request admission, work draining and callbacks that outlive their shutdown deadline.

These are unit and local host tests. Reference-profile qualification requires
the separately executed real-provider, host and container evidence specified by
the acceptance corpus. Test source does not establish execution results.
