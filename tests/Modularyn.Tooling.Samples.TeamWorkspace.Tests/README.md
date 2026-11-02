# Team workspace tests

An xUnit test project like the repository's other test projects, with the same
centrally versioned test references. `UnitTests` checks the inventory and media
declarations, whole-microsecond time and IANA daylight-saving edges, and runs with
the unit tests. `SmokeTests` carries the trait `Requires=PostgreSQL` and runs the
real-provider scenario, so the real-provider script runs it with the other
PostgreSQL tests. It runs from a build output under this repository; it finds
`Modularyn.slnx` by walking upward. All runtime secret files, the key ring and
BLOBs live in an owner-only `modularyn-team-workspace-*` directory in the
system's temporary directory, never in the repository, and are deleted on
disposal.

The scenario reads `MODULARYN_TEST_POSTGRESQL` exactly as the repository's database
script provides it, calls the shared probe, holds `PostgreSqlTestLock`, and creates
only its own randomly prefixed schemas through the migration coordinator. The
role needs neither superuser nor `CREATEDB`/`CREATE ROLE`. Cleanup drops only those
fixture schemas. It generates the certificate, credentials and fixture deployment
identity at run time; production startup has no fixture authority path.

The fixture runs the real host over Kestrel/TLS, real Identity session cookies,
the declared key ring, real audit and authorization, PostgreSQL and Files. Its
client pins the generated certificate. No authentication or audit double is used.
An adjustable clock controls schedule and expiry comparisons; timeout timers use
real elapsed time. A deliberately blocked response stream is the test seam for
the fifteen-second download deadline.

The scenario covers:

- Health, external sign-in script, Identity sign-in/session and default denial.
- Audited role definition/assignment and cross-tenant refusal.
- Settings authorization/version conflicts, cache invalidation, feature denial and culture fallback.
- Idempotent note creation, optimistic revision conflict, history and form antiforgery.
- Quarantined upload, a member's allowed release and refused PDF release, an owner's release of its own file, and hidden unreleased content.
- Header-only download grants, integrity/security headers, expiry and log/trace canaries for the grant, its header and the passwords.
- No ASP.NET Core log below Warning, and its antiforgery warning kept.
- A stalled download followed by another download through the single read slot.
- Durable inbox activity and one digest per occurrence in the member's IANA zone.
- Active OpenAPI inventory and separately authorized audit access.
- Host tenant creation and membership addition refused without host authority, even to a tenant owner, and working once the owner holds stored host authority for each.
- Invalid input to either host endpoint answered as a validation failure.

The scenario does not establish browser automation, container execution or
exhaustive G-02 acceptance.

`ProvisioningTests`, also `Requires=PostgreSQL`, run the host's one-time
`provision-host-operator` command through `HostOperatorProvisioning.RunAsync`,
building the provisioning host afresh for each run over one set of schemas, as
successive runs with different deployment configuration. They show the first
operator receiving exactly the bootstrap entry's operations but provisioning,
each change audited; refusals for a configured user operator, a missing or
incomplete bootstrap entry, an unknown or disabled account and every second run;
a stopped run resuming for its own account only; and a serving host unable to
act as the provisioning service. A fixture service, declared and configured only
there, creates the accounts and reads host audit. `UnitTests` checks that the
command refuses every argument form but one account identifier.

`CompositionAcceptanceTests` contains the four CODE-10 AC-001 integration
methods. Its in-process rows add the corpus's A/B/C fixtures to the real sample
module host, preserving deployment trust, migration readiness and every other
activation validator. Effects pass through authenticated Files operations into
PostgreSQL and the filesystem store. Its container rows require
`MODULARYN_SAMPLE_BASE_URL`; an absent address skips them. With an address they
probe the deployed sample over HTTP and still skip because the published host
has no graph-mutation, fault-injection or lifecycle-inspection surface. They
never claim that a healthy container passed AC-001.

The [evidence instructions](../../docs/evidence/README.md#code-10-record-output)
describe opt-in output, exact source and tuple checks, retained hashes and
blocked qualification. `EvidenceWriterTests` checks serialized records against
the schema without treating its transient metadata inputs as provider evidence.
The four methods, their writer tests and the new regressions await Builder
execution; no new runtime result is claimed by this documentation.

`ReviewRegressionTests` carries the CODE-09 review checks for a query-string
grant refused with 400 and absent from captured logs/traces, a previously
released PDF refused by the current delivery policy with 404, and startup with
the development seed disabled. The PDF setup uses real Files operations under
the existing fixture authority with a prior release policy; the serving host's
policy stays unchanged. `ProvisioningTests` also races two independently built
hosts over one deployment and checks that only one account gains the grants,
with one claim and completion. A refusal after acquiring both host scopes
retains their two tenancy audit entries. These regressions do not take ownership
of a security work order or qualify a security prerequisite.
