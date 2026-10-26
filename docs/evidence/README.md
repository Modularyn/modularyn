# Evidence records and the G-02 plan

[g02-plan.json](g02-plan.json) assigns each of the 41 reference cases in AC-001
and AC-003 through AC-009 once. It names the work order that writes the tests,
their planned project and methods, the corpus layers and the required execution
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
Naming these future test locations does not create or qualify the packages.

The work-order scopes distinguish the following responsibilities:

| Cases | Test-writing order | Boundary of the assignment |
|---|---|---|
| AC-001 | CODE-02 | Graph selection and lifecycle; the corpus's trust prerequisite remains separate. |
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
security tests. SEC-08 assembles adversarial and control evidence. This plan
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
| Implementation artifact | `implementationArtifacts`: repository-relative locations and measured SHA-256 hashes |
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
