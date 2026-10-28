# Static clients

`ModularynClient` is generated from the framework endpoint inventory. The
TypeScript client lives in `typescript/` in this package and uses the same
inventory and generator. Each offers typed methods over a separate transport;
module inventories can generate additional typed clients using that transport.
The AC-008 fixture generates item and operation methods this way.

The .NET transport uses SEC-03's `IClientCredentialProvider` without changing its
storage or acquisition contracts. The TypeScript provider has the same explicit,
origin-bound request semantics. Each request asks its provider anew, including
reconciliation requests. The provider owns acquisition, expiry and rotation.
Clients keep no token cache, send no ambient cookies or process credentials, and
refuse automatic redirects. They do not retry authentication challenges or
mutations automatically. A caller may resolve a redirect explicitly and create
a new transport for that origin, which must obtain its own credential.

These are explicit-credential clients. SEC-03's shipping HTTP adapter accepts
session cookies; it does not implement bearer authentication. The real-provider
contract fixture uses a test transport adapter that validates the explicit
credential through SEC-03's actual session service. This qualifies neither a
production bearer adapter nor a browser cookie mode. Production authentication
integration and its security acceptance remain Builder work.

Validation, concurrency, authentication, authorization, compatibility, missing
resources, unavailable services, conflicting operation identities and unknown
outcomes have distinct failure types. Failures retain the server's code and
correlation. Cancelled reads remain cancellation. A mutation cancelled before
sending is cancelled locally; once sending starts, an interrupted response yields
`OutcomeUnknownFailure` with the original operation identity. Query that
operation using a fresh cancellation token. An absent receipt is not proof that
an in-flight request cannot commit. Retry only with the original identity and
input, after reconciliation.

A fully received response wins a later cancellation. An error response keeps its
typed failure even if a version header is missing or incompatible. A successful
response carrying an unexpected version remains available as the compatibility
failure's authoritative response, with the operation identity for reconciliation.
The client never turns that observation into an assertion of rollback.

For a cross-origin browser caller, the host must explicitly allow that origin,
its HTTP methods and the `Authorization`, `Content-Type` and `Idempotency-Key`
request headers under the existing ingress policy. Expose `Modularyn-Api-Version`,
`Modularyn-Correlation` and `Idempotency-Key` so the browser can read the contract
metadata. Client registration does not widen the ingress configuration.

## Builder verification

Restore and build the solution, generating and reviewing the .NET dependency
locks. Adopt only the exact TypeScript development dependencies declared in
`typescript/package.json`; generate and review its lock during that adoption.
The manifest is private. No package publication is part of this work.

Run the repository checks and the `Requires=PostgreSQL` tests using the existing
database script and `MODULARYN_TEST_POSTGRESQL`. Every AC-008 method runs once for
each generated client. The ordinary PostgreSQL suite runs the TypeScript
scenarios through the pinned Node.js runtime with its native type stripping.
To execute the same scenarios under Vitest after adoption, set
`MODULARYN_CLIENT_TEST_VITEST=1` for the .NET client test project. That project
starts each isolated real-provider fixture and supplies it to Vitest; standalone
TypeScript tests deliberately fail without a live fixture. Run the TypeScript
`check` script as well. No in-memory data provider or mocked gateway substitutes
for these tests.

The six evidence-plan methods cover typed round trips, negative cases, version
and cancellation edges, interrupted responses, concurrent duplicate attempts,
and a new host reconciling a committed receipt. Provider tests isolate their
schemas and take the shared PostgreSQL test lock. Audit remains SEC-07's test
seam. The Builder must retain the execution evidence and obtain all six CI checks
at the final commit. Source checks performed before that do not establish those
results.
