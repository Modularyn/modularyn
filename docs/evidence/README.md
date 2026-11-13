# Evidence records and the G-02 plan

[g02-plan.json](g02-plan.json) assigns each of the 41 reference cases in AC-001
and AC-003 through AC-009 once. Each test names its writing work order,
project, method and status, alongside the corpus layers and required execution
surfaces. These are obligations to implement and verify. No case is recorded as
executed or passed, and no requirement or capability becomes qualified here.

## Case ownership and execution

The [corpus](../acceptance/corpus.json) remains authoritative for fixture inputs,
steps, assertions and retained artifacts. The plan copies requirement and layer
identities so repository checks detect drift. Each planned method names its case
and layer; the methods together must cover all of that case's observations,
including each row of an input or failure matrix. A method name alone cannot
satisfy a case. Planned projects follow the [layout contract](../names-and-layout.md):
one test project per tested package, with matching assembly and namespace.
Every method begins with its case id rendered with underscores, such as
`AC_001_B01_`. The case's owners and projects are the distinct `workOrder` and
`project` values in `plannedTests`; there is no single case-level owner or project.
Multiple methods may cover one layer, and their combined layers must equal the
corpus layers. A test's `status` is `planned` or `existing`: existing means its
exact method is present in repository source, with no execution claim. Case
status remains `planned`. Naming future locations creates no packages.

AC-001's existing unit methods are in
[Hosting's AcceptanceTests](../../tests/Modularyn.Framework.Hosting.Tests/AcceptanceTests.cs)
and [Modularity's CompositionTests](../../tests/Modularyn.Framework.Modularity.Tests/CompositionTests.cs),
as merged on main at `86c364a`. CODE-02 supplies those seven methods. CODE-09's
sample application, the team workspace, is planned with its host
`Modularyn.Tooling.Samples.TeamWorkspace.Host`, the sample's composition root
under the [dependency rules](../architecture/dependencies.md#composition-roots),
composed from foundation modules. Under the layout contract, the host's project
is
`src/Tooling/Modularyn.Tooling.Samples.TeamWorkspace.Host/Modularyn.Tooling.Samples.TeamWorkspace.Host.csproj`
and the sample's tests are in
`tests/Modularyn.Tooling.Samples.TeamWorkspace.Tests/Modularyn.Tooling.Samples.TeamWorkspace.Tests.csproj`,
the one test project that may reference that host.
CODE-10 supplies its four named AC-001 integration methods, with the execution
limits described below. Each must run on
both Linux x64 host and container deployments with real PostgreSQL and the
filesystem store. Container execution is a deployment of the corpus's
`real-provider-integration` layer, not an additional corpus layer.

The work-order scopes distinguish the following responsibilities:

| Cases | Test-writing order | Boundary of the assignment |
|---|---|---|
| AC-001 unit methods | CODE-02 | Existing graph selection and lifecycle methods in Hosting and Modularity; the corpus's trust prerequisite remains separate. |
| AC-001 real-provider methods, on host and container | CODE-10 | Planned acceptance methods on CODE-09's sample application, including durable effects and cleanup. |
| AC-003-P02 | CODE-04 | Settings and feature precedence and invalidation, using established security fixtures. |
| AC-003-B02 | SEC-03 | The corpus's delegated identity, scope and expiry observations. |
| AC-003-P01, B01, R01, R02 | SEC-05 | Grant application, freshness and revocation observations. |
| AC-003-N01 through N03 | SEC-08 | Negative authorization, including the browser surface in N02. |
| AC-004-B01, P01, R01 | SEC-04 | Context isolation, propagation and reconstruction. |
| AC-004-B02, N01 through N03, R02 | SEC-08 | Adversarial cross-tenant attempts, including cache and restore surfaces. |
| AC-005 | CODE-03 | Actual PostgreSQL transaction, concurrency, filter and migration behavior. |
| AC-006 and AC-007 | CODE-05 | Durable events, jobs, schedules and recovery. |
| AC-008 | CODE-06 | Each advertised .NET and TypeScript client mode and API contract. |
| AC-009-B01 | SEC-06 | Declared upload-size and cancellation observations. |
| AC-009-N01, P01, R01 | CODE-07 | Filesystem integrity, immutable versions and recovery. |

Every SEC order is the Builder's. PLAN-10 selects dependencies and CI-01 supplies
the real-provider execution environment; both are also the Builder's. CODE-09
provides the team workspace, including the minimal Razor sign-in and form surface.
CODE-10 assembles functional acceptance evidence without taking ownership of
security tests. An evidence record's `workOrder` identifies the order responsible
for assembling that case's evidence: CODE-10 for AC-001, and the table's assigned
order for the other cases. Its `tests` retain the exact project, name and layer
from every planned entry; test authorship is derived from those entries rather
than reassigned to the record's assembling order. SEC-08 assembles adversarial
and control evidence. This plan
adds no security mechanism or case beyond the corpus.

The `needs` flags describe the complete case qualification run. All cases use
the reference application on actual PostgreSQL and run on both the Linux x64
host and container deployments. Unit methods can run independently, but cannot
replace that deployed evidence. `filesystemStore` identifies cases that also
exercise real file storage; `browser` identifies AC-003-N02. API and authorization
cases must enumerate every advertised client mode. The layer names remain the
corpus's REL-002 vocabulary. AC-007 retains its existing scheduler obligation
without amending the master gate definition.

Each `securityDependencies` entry is explicitly unmet with no evidence records.
The Builder must supply evidence for the exercised scope on the same commit and
tuple. AC-002, AC-012 and AC-031 have no reference cases in this slice. Sign-in,
audit and trust fixtures still require G-02 evidence; the complete families
remain deferred to their recorded later gates. In particular, AC-001's four
cases do not test untrusted selections. Mapping those four cases does not close
the separate SEC-02 trust obligation. The capability skeleton preserves these
gaps rather than treating dependent tests as their own prerequisites.

## Evidence format 1

[The schema](../../schemas/evidence-record/evidence-record.schema.json) uses
JSON Schema draft 2020-12 with identity `urn:modularyn:evidence-record:1` and
`formatVersion` 1. Unknown fields and duplicate JSON properties are rejected.
The repository uses its existing local schema evaluator; no schema reference
requires network access. Semantic checks supplement the schema for calendar
dates, identities, exact version pins and corpus agreement.

The corpus digest covers its UTF-8 text with LF line endings, matching the
versioned source independently of checkout line endings. Artifact and result
digests cover their actual retained bytes.

| REL-001 element | Record fields |
|---|---|
| Requirement, module contract or benchmark row and its owner | `subjects`: stable `id`, `kind` and `owner` for each covered subject |
| Supported profile and exact selection | `profile`, `tuple`: module versions, SDK/runtime/language, database, UI, tenant placement, host, topology and providers |
| Implementation artifact | `implementationArtifacts`: repository-relative locations, or for retained bytes locations in the evidence bundle, and measured SHA-256 hashes |
| Acceptance scenario | `caseId`, `family`, the corpus location and SHA-256, `fixtureInputs`, `expectedOutcomes` |
| Test and result locations | `tests`: project, fully qualified method and layer; `verification.results`: retained locations and SHA-256 hashes |
| Documentation | `documentation`: repository-relative references |
| Known limitations | `knownLimitations` and explicit `securityDependencies` |
| Exact implementation and verification date | `sourceCommit`, version pins in `tuple`, and `verification.date` |
| Measured behavior and failure paths | `observedOutcomes`, result artifacts and `verification.status` |

`subjects` covers the exercised part of a requirement, not automatic completion
of its whole scope. Requirement owners agree with the coverage register.
Module-contract subjects identify a module in the tuple; benchmark-row subjects
identify a foundation or tooling row in the register. Each case must still
include all its corpus requirement identities.

`kind: plan` has a null source commit and verification date, `status: not-run`,
and empty observed outcomes and result arrays. Null version or artifact fields
mean the selection or measurement is unavailable; `knownLimitations` explains
the gap. The two [plan examples](examples/) show host persistence and container
file recovery without inventing an implementation, verification date or result.
Their runtime and database numbers describe the intended G-02 selection only.

`kind: verification` requires an exact source commit, a real calendar date,
observed outcomes, hashed results and hashed implementation artifacts. Every
module, provider, database, UI and operating-system version must be pinned;
container runs also require the measured image digest. No version range or
moving label is an exact version. A passed record cannot retain an unmet security
dependency; a failed or blocked record must explain its limitations. Records
for in-memory providers or mocked gateways cannot supply REL-002 evidence.
Controlling a fault or clock is compatible with testing an actual provider;
substituting its storage or gateway behavior is not.

A record binds to exactly one commit and one tuple. Host and container runs
produce separate records. A gate bundle must compare their source commits,
corpus identities and exact selections and retain the results for every required
case and surface. Combining results from different commits, treating build
platforms as qualified hosts, or borrowing evidence from another provider cannot
close a gap. Schema validity alone does not establish that a result occurred,
that its hash matches retained bytes, or that the gate is accepted.

## Review and verification

### Running the CODE-10 methods

`CompositionAcceptanceTests` in the sample's test project has the four exact
integration method names in the plan. Each is a theory with a `single-host` row
and a `single-host-container` row. `MODULARYN_DEPLOYMENT_MODE` names the
deployment a process runs in: the composition fixture image sets it to
`single-host-container`, and a runner or workstation leaves it unset. A host
row runs only where it is unset and a container row only where it is set; the
other row skips with a stated reason and writes no record, and any other value
fails. Host rows carry the traits `Requires=PostgreSQL` and
`Deployment=single-host`, so the real-provider job runs them. Container rows
carry only `Deployment=single-host-container`: the unit jobs select them and
they skip there, and the container-build job runs them inside the image.

Both rows run the same composition. They read `MODULARYN_TEST_POSTGRESQL`, use
the existing database probe and shared lock, and build the sample with isolated
schemas, its filesystem store and generated fixture identities. The sample's
unchanged selection is migrated first. A second build adds the corpus's A, B
and C test modules with a matching fixture deployment lock over the built
files. The registered module host retains the sample's composer, every
activation validator, work gate, telemetry and lifetime. No
unverified-composition option is enabled. Tenant and owner authority comes only
from the sample's existing authenticated fixtures.

| Case | Observations, in each mode |
|---|---|
| AC-001-B01 | All four B versions; readiness, the resolved selection and real Files effects at both inclusive edges; the same range diagnostic, differing only in the candidate's version, and no activation for both outside versions. |
| AC-001-N01 | A valid control, independently missing C, C-to-A cycle and incompatible B; input graphs, exact diagnostic identities and before/after activation inventories with no rejected effects. |
| AC-001-P01 | Reverse discovery, exact resolved versions, every selected module ready, C/B/A initialization once, HTTP readiness, reverse shutdown and no retained resources. |
| AC-001-R01 | B's initialization fault after C succeeds, A unavailable, B/C cleanup, the identical selection recovering to HTTP readiness and fresh durable-effect counts without duplicates. |

The test modules write through the real Files service using PostgreSQL metadata,
the filesystem store, permission evaluation and audit. Each immutable effect is
queried again after failure and recovery, including its count, length, digest
and bytes. Exclusive filesystem markers expose resources left by a prior
activation. Result snapshots retain graph/version/provider declarations, ordered
lifecycle events, states, cleanup inventories and measured effect counts.

The container rows run in the composition fixture image,
[tests/Fixtures/composition/Dockerfile](../../tests/Fixtures/composition/Dockerfile),
not in the published sample image. The four cases compose test modules in
version variants, invalid graphs and an injected fault. Shipping those modules,
or a switch that selects them, in the product image would be a security
regression, so the published image never carries either. The fixture image is
test-only and never published. It holds the framework-dependent publish of the
sample's test project, an xunit v3 executable that carries the sample host, its
modules and the A, B and C fixtures, on the same pinned ASP.NET Core base, by
the same tag and index digest, that every Dockerfile must use. It runs as a
numeric non-root user, and root owns its files. CI's container-build job runs
it on an internal network, in the database container's network namespace, so
the tests reach PostgreSQL on loopback as their probe requires. The published
sample image runs on the same network against the same database. The
published-sample readiness test, `The_published_sample_image_is_ready_over_HTTP`,
reaches it at `MODULARYN_SAMPLE_BASE_URL` from inside the fixture image and
requires `/health/ready` to answer `Healthy`, and
`The_published_sample_image_serves_its_sign_in_page_over_HTTP` makes an
application request there, for the sample's sign-in page under its content
security policy. Those are container observations of the shipped image, never
AC-001 coverage. [CI's container acceptance](../ci.md#container-acceptance)
describes the job.

### CODE-10 record output

The single bundle assembler, [EvidenceBundle](../../tests/Modularyn.Repository.Tests/EvidenceBundle.cs)
in the repository tests, runs on the pinned SDK and reads the plan, corpus,
measured selection and xunit v3 results. CI runs its `collect`, `select` and
`assemble` commands through [evidence-bundle.sh](../../.github/scripts/evidence-bundle.sh),
each as one test method of the repository tests' executable. The host job
collects VSTest TRX reports, then runs the planned methods absent from that
provider selection through their own executables and collects CTRF reports.
The container job collects CTRF reports from those executables inside the
existing composition fixture image. A result belongs to a planned method by the
test identity its report records, the fully qualified type and method, never by
its display name, which a `DisplayName` or a data row replaces. Every row of a
planned theory is required: AC-001's integration methods need their
deployment's row, and AC-008 needs both advertised clients, each named by its
exact display name. Every other planned method has one row, made up of all its
results, so each data row of a theory such as AC-001's unit theories must pass,
and an unexecuted one blocks the case. A row from another deployment cannot
supply a result.

`EvidenceMeasurementTests` runs in the deployment before result collection. It
probes PostgreSQL and measures the runtime, operating system, provider versions
and built assemblies. Each Modularyn assembly in the inventory must embed the
exact source revision; conflicting copies refuse the run. The host also checks
the clean checkout. The container job checks that checkout before publishing,
and supplies the measured fixture image digest. Measurement never substitutes
planned versions for unavailable measurements. If the database or built inventory
cannot be measured, bundle generation fails and CI cannot claim valid evidence.

| Variable | Meaning |
|---|---|
| `MODULARYN_EVIDENCE_DIR` | Absolute bundle directory. CI uses `host-evidence` or `composition-output/evidence` beneath `RUNNER_TEMP`. Linked paths and overwrites are refused. |
| `MODULARYN_SOURCE_COMMIT` | The clean checkout's exact forty-character lowercase HEAD, embedded in the measured assemblies. |
| `MODULARYN_EVIDENCE_RUN_ID` | The workflow run id and attempt, shared by the two jobs. Each half has a distinct deployment mode. |
| `MODULARYN_CONTAINER_IMAGE_DIGEST` | The measured composition fixture image digest, required only in container mode. |
| `MODULARYN_EVIDENCE_MEASURE` | `1` only for the explicit measurement invocation. Ordinary unit runs skip that invocation. |
| `MODULARYN_EVIDENCE_BINARIES` | The built test tree on the host or the fixture image's application directory. This path is never retained in a record. |

A bundle half contains `measurement.json`, retained implementation bytes,
sanitized reports, and one `<run>/<case>/<mode>/result.json` and `record.json`
for each of the 41 plan cases. `manifest.json` is written last and lists each
record's case, mode, location, tuple hash and SHA-256. It also binds the run,
commit, measured selection, plan and corpus, together with the measurement and
sanitized report hashes. Records retain every planned test
mapping and every corpus precondition and expected observation. The shared
measured selection includes every tested package; each record selects the
providers its case requires, as the repository contract specifies.

AC-001 uses this same assembler. Its methods now write observation inputs,
retaining the original graph, versions, lifecycle events, diagnostics, states,
cleanup inventories, effect counts, result outcome and implementation bytes.
Those inputs are hashed results in the assembled record. They are bound to the
same run and measured selection as the runner results. A method failure remains
the test runner's failure if observation capture also refuses; absent observations
block the case. The older writer's metadata fixtures remain regression checks
of its schema and refusal rules and are never CI qualification records.

| Condition | Case outcome |
|---|---|
| Any planned method or required theory row is missing, skipped or unexecuted | `blocked`, even if another row failed |
| Every required row executed, and at least one failed | `failed` |
| Every required row passed, but a security dependency is unmet | `blocked` |
| Every required row passed and all security prerequisites are satisfied | Eligible for `passed`; cannot occur yet, as below |

The last row cannot occur while the plan's security dependencies are unmet. The
assembler refuses a plan with a satisfied dependency ("security evidence needs
Builder review"), and the bundle contract requires each record's
`securityDependencies` to equal the plan's, so no record this assembler writes,
and none the contract accepts, can be `passed`. The rule stands: a `passed`
record needs every security prerequisite satisfied. The row becomes reachable
only when the security-evidence path changes both the assembler and the contract.

The current plan declares every security dependency unmet. This assembler
preserves that state and refuses to promote functional results into security
clearance. All 41 cases therefore remain blocked when their methods pass,
until the Builder supplies reviewed prerequisite evidence on the same commit
and tuple. AC-001 requires AC-031; AC-003 and AC-004 have the identity,
authorization, tenancy and audit dependencies each row names; AC-005 through
AC-009 retain their respective authorization, tenancy, audit and trust dependencies.
No inferred family pass resolves circular prerequisites.

In container mode AC-008's four cases also remain blocked because their planned
methods require both .NET and TypeScript. The TypeScript method starts node in the
TypeScript client's directory, and the fixture image has no node on PATH and none
of the TypeScript client's sources or packages. The image does carry the pinned
Node.js release that Playwright's driver bundles, off `PATH`.
AC-003-N02's browser method cannot run there because the image lacks the pinned
browser and its runtime dependencies; its API and integration methods still run.
The remaining planned methods run inside the fixture image, including unit
methods. Unsupported methods are not edited, renamed or made to skip; their
absence and specific reason remain in their case records. AC-003-N01 also states
that its second advertised client is covered by argument about the server
boundary rather than by executing that client. AC-003-B01 and AC-003-P01 state
the same limitation for the advertised clients; their methods execute direct API
and server entry points.

Collection accepts only exact planned identities and bounded row outcomes.
CI runs the remaining planned methods with `-failSkips`, so that a skip fails
the job; xunit v3 then reports the skipped test as failed with the exception
`FAIL_SKIP`, and collection records it as skipped, which blocks its case rather
than failing it. Runner output, failure messages, traces, attachments, arbitrary display names,
credentials, connection strings and machine paths are not copied. Raw reports
are deleted after collection, and the retained reports have no free-form
diagnostic field. A failure's message and stack trace never reach the bundle;
the CI log shows them for the runs that [CI's real-provider tests](../ci.md#in-ci)
and [container acceptance](../ci.md#container-acceptance) name. Unit tests use
synthetic reports, generated beneath the checkout and deleted afterward; they
supply no provider evidence.

Both jobs run [evidence-records.sh](../../.github/scripts/evidence-records.sh)
even after a test failure. It assembles the half, requires its manifest, and runs
`CiEvidenceTests` with `-failSkips`. `EvidenceBundleContract` checks the complete
41-case inventory, the manifest, identities, tuple projections, retained hashes,
and outcome consistency; `EvidenceRecordContract` applies the unchanged schema
and semantic contracts to every record. Duplicate records, conflicting reports,
other commits or selections, and interrupted writes refuse the bundle. The job
then prints every record hash and the checked manifest. Both halves must come
from the same workflow attempt and exact head; neither half alone establishes
the gate. CI uploads no artifact, and nothing is published as a package.

### Remaining checks

The repository checks the 41-case bijection, scope assignments, project and
method names, layers, requirements, resource flags, evidence examples and the
[capability skeleton](../capability-reports/g02-skeleton.json). Rejection fixtures
exercise missing cases, wrong ownership, invalid pins and erased exclusions.
These are planning-contract checks, not product acceptance runs.

The Builder must run locked restore, Release build and repository tests after
this preparation. Three-platform CI must pass on the exact reviewed head, and
the permitted-path and secret-scan checks must cover that same revision. Until
those results exist, the review remains pending. This plan authorizes no push,
release or package publication and changes no master-design decision.
