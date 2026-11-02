# Endpoint conventions

Register `AddModularynApi` and place `UseModularynApi` before the ingress. Apply
SEC-05's existing `RequirePermission`, `RequireTenantMember` or
`RequireHostAuthority` wrapper, `RequireAuthenticatedPrincipal` for an endpoint
that serves only the caller's own principal, or explicit anonymous access, and
finish the endpoint with `WithApiContract`. The final convention preserves those
delegates. Its route and method must match its declaration, and both startup
inventory validation and invocation refuse a missing or contradictory
authorization classification. An interception-only policy is insufficient. The
authorization startup report includes member-only endpoints as event 8306 and
authenticated-only endpoints as event 8307.

The module host also registers the inventory check. It validates every routed
endpoint while the host builds its request pipeline, after the application's
endpoints are added to routing and before the server starts, so a host with an
undeclared or misclassified endpoint never listens and no endpoint answers. A
host that captures startup errors, as one hosted in IIS does, serves only its
startup error page instead. A GET declaration may also answer HEAD, as the
health probes do.

Pages, file downloads and uploads are declared like every other endpoint, with
their authorization classification, as the [inventory](../Modularyn.Framework.Api.Contracts/README.md)
describes; map a page and its form as one endpoint for each method. A form post,
a page's form or a form upload, declares antiforgery, and keeps the ingress's
antiforgery validation (TM-G02-007): an endpoint that opts out with
`DisableAntiforgery`, before or after `WithApiContract`, is not the declared
form post, so the inventory check refuses the host before it listens and the
endpoint answers each request with 500 and `MAP008` without running its handler.
Both read the endpoint's last antiforgery declaration, as the ingress does.

`ApiEndpoints.Describe` reads every mapped endpoint, including non-client routes,
and combines its declared schemas. Undeclared routes fail validation. The built-in
inventory contains the identity, health and bootstrap routes; a composed host's
inventory contains only its mapped routes. Application services never become
remotely callable merely through service registration.

Versioned routes contain `v{version}`. A client-visible endpoint, which generates
client methods that pin a version, must be versioned, and a client-visible
mutation idempotent; inventory validation refuses either otherwise. Supported responses carry
`Modularyn-Api-Version`; an unsupported version returns 426 and `MAP003`.
`UseModularynApi` supplies a server-issued correlation even to an early refusal:
`AddModularynApi` stops the server from adopting an inbound `traceparent` or
baggage, and a request continues a peer's trace only when its direct peer is a
declared proxy (`RequestCorrelation`, TM-G02-053). `UseModularynApi` decides that
once per request, from the connection's own address before the ingress rewrites
it, and later steps keep the decision, so the response header, the endpoint's
trace and a problem body's `correlationId` always agree. API problems use
`application/problem+json`, a stable code, status and correlation; SEC-05's
refusal codes retain their meanings and appear only where SEC-05 decided. A
refusal that left no body, such as the ingress's, a missing route or a method the
route does not answer, is answered by `UseModularynApi` with a code that says only
what its status says: `MAP004` for a missing route, `MAP003` for a version
mismatch, `MAP007` for unavailability and `MAP009` for every other status, never
a permission decision or a peer's contract violation. Existing identity code responses
and health text are separately represented in OpenAPI. Requests have a 30-second
endpoint deadline; the request cancellation token reaches the handler and its
unit. Validation precedes writes. The shared ingress retains its body, header,
JSON depth and antiforgery limits. Production hosts authenticate API requests
today only through the browser's same-origin `__Host-Modularyn-Session` cookie.
An endpoint whose inventory entry names the explicit credential accepts one only
once G-03's authorization server issues tokens; the contract fixture's bearer
adapter is test-only. Explicit-credential mutation endpoints declare
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
| `MAP009` | The request was refused before or without reaching an endpoint, as its status says |

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

## Threat-model controls

- **TM-G02-011, SEC-09's part.** Delivered: pages, file downloads and uploads
  declared in the inventory with their authorization classification and media
  types, never inferred; HTML only for a declared page; binary responses only for
  a declared download with its explicit content types and disposition; upload
  bodies only with their explicit media types, either a form upload or binary
  types; antiforgery declared on every form post; JSON for every client-visible
  endpoint; no page, download or upload generating a client method, and every
  other operation's OpenAPI description unchanged. Tests: `Sdk.Client.Tests`
  `ContractDefinitionTests.TM_G02_011_*` and `Repository.Tests`
  `ApiGenerationTests.Pages_downloads_and_uploads_are_described_with_their_declared_media_types_and_generate_no_client_methods`.
  Removal checks: dropping any of those rules, or the document's description of
  a page, its form, a download or an upload, fails its test. A form post's
  antiforgery is TM-G02-007's, recorded in [the framework](../README.md#threat-model-controls).
  Response media types are declared, not yet checked at runtime: neither the
  inventory check nor the endpoint compares a response's `Content-Type` with its
  declaration, so a handler that sends another type than it declares is not
  refused. The ingress disables content sniffing on every response, so such a
  response is never rendered as HTML it did not declare (`Ingress.Tests`
  `ContentTypeOptionsTests.TM_G02_011_*`). Remaining: the runtime check of each
  response's type against its declaration (SEC-08); the sample's pages,
  downloads and uploads (CODE-09); adversarial evidence (SEC-08).

Compilation and real-provider qualification remain Builder checks. The contract
fixture uses PostgreSQL, actual sessions, tenancy, authorization, tracked writes
and TLS. Its authority-audit seam is test scaffolding; the audit package's own
tests exercise the real store, and these tests establish no broader security
acceptance.
