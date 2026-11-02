# Team workspace smoke sources

This project uses the .NET 10 shared frameworks and the two sample projects,
without package references. It is an executable smoke harness, not a registered
`dotnet test` project. The repository keeps xUnit and Test SDK references private
to each existing test project, and this assignment prohibits new package
references. The Builder must integrate `Program.UnitChecks` and
`SmokeScenario.RunAsync` with that runner and give the latter the actual
`Requires=PostgreSQL` trait. The real-provider CI script does not discover this
scenario yet.

Once the Builder has restored and built it, `--unit` checks inventory/media
declarations, whole-microsecond time and IANA daylight-saving edges. The explicit
`--postgresql` argument additionally runs the real-provider scenario. Run it from
a build output under this repository; it finds `Modularyn.slnx` by walking upward.
All runtime secret files, the key ring and BLOBs live in an isolated `.run-*`
directory inside this test project and are deleted on disposal.

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
- Quarantined upload, allowed release, refused PDF release and hidden unreleased content.
- Header-only download grants, integrity/security headers, expiry and log/trace canaries.
- A stalled download followed by another download through the single read slot.
- Durable inbox activity and one digest per occurrence in the member's IANA zone.
- Active OpenAPI inventory and separately authorized audit access.

These sources were not compiled or run in the authoring sandbox. They do not
establish browser automation, container execution or exhaustive G-02 acceptance.
The Builder owns restore/build/test execution, test-runner integration and all
six CI results at the final head.
