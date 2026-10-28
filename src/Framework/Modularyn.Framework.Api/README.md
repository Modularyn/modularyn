# Endpoint conventions

Register `AddModularynApi` and place `UseModularynApi` before the ingress. The
module host also registers the inventory validator. Apply SEC-05's existing
`RequirePermission`, `RequireTenantMember` or `RequireHostAuthority` wrapper, or
explicit anonymous access, and finish the endpoint with `WithApiContract`.
The final convention preserves those delegates. Its route and method must match
its declaration, and both startup inventory validation and invocation refuse a
missing or contradictory authorization classification. An interception-only
policy is insufficient. The authorization startup report includes member-only
endpoints as event 8306.

`ApiEndpoints.Describe` reads every mapped endpoint, including non-client routes,
and combines its declared schemas. Undeclared routes fail validation. The built-in
inventory contains the identity, health and bootstrap routes; a composed host's
inventory contains only its mapped routes. Application services never become
remotely callable merely through service registration.

Versioned routes contain `v{version}`. Supported responses carry
`Modularyn-Api-Version`; an unsupported version returns 426 and `MAP003`.
`UseModularynApi` supplies a server-issued correlation even to an early refusal.
API problems use `application/problem+json`, a stable code, status and correlation;
SEC-05's refusal codes retain their meanings. Existing identity code responses
and health text are separately represented in OpenAPI. Requests have a 30-second
endpoint deadline; the request cancellation token reaches the handler and its
unit. Validation precedes writes. The shared ingress retains its body, header,
JSON depth and antiforgery limits. Explicit-credential mutation endpoints declare
the ingress's existing cookie-free antiforgery exemption deliberately. Even on
such an endpoint, a request carrying any cookie must pass antiforgery validation.
The real-provider fixture includes that refusal case.

| Code | Meaning |
|---|---|
| `MAP001` | Invalid input |
| `MAP002` | Stale version |
| `MAP003` | Incompatible API version |
| `MAP004` | Missing or unavailable to this caller |
| `MAP005` | An operation identity was reused for different input |
| `MAP006` | The mutation's outcome requires reconciliation |
| `MAP007` | A dependency is unavailable |
| `MAP008` | The peer's response violates the contract |

## Transaction-bound operations

An idempotent mutation requires a nonempty UUID `Idempotency-Key`. Its module maps
`ApiOperation.Configure` in its context and includes the matching tenant-scoped
receipt table in its migration. Every key includes `TenantId`. The caller obtains
its context and principal from the validated server scope and authorizes the use
case before calling `ApiOperations.ExecuteAsync`.

The receipt reservation, effect and serialized result share one tracked module
transaction. The unique tenant/operation key serializes racing retries across
processes. The callback may write only through that supplied unit and may not
commit it, start independent effects, or dispatch external work. Flush tracked
business writes before constructing the result so the receipt retains the
adapter's final version token. The fingerprint binds the operation name, API
version and complete request, including resource identities. A retry with changed
input fails; a retry with identical input returns the original result. Receipts
are retained without automatic expiry: deleting one would permit its identity to
create a duplicate.

The stored subject restricts receipt lookup; it supplies no permission. Lookup
requires fresh endpoint authorization and a validated tenant context. A receipt
proves a committed result. Its absence means only not observed: an original
request may still be running. After a response or commit acknowledgement is
interrupted, read the receipt, then retry only under the original identity and
input. An interrupted response never proves rollback. No background work or
stored propagated context is introduced by this package.

## Bootstrap

`MapModularynBootstrap` is tenant-member-only and returns `no-store` responses.
Setting and feature definitions default to hidden. A definition must explicitly
set `clientVisible: true`; a secret-reference definition cannot do so. The reader
returns only primitive presentation values and evaluated feature results. It
returns no secret, reference, provenance, permission grant or other authority.
Feature evaluation still uses its existing gate, and a missing gate yields false.

Compilation and real-provider qualification remain Builder checks. The contract
fixture uses PostgreSQL, actual sessions, tenancy, authorization, tracked writes
and TLS. Its authority-audit seam remains test scaffolding for SEC-07; these tests
cannot establish SEC-07 or broader security acceptance.
