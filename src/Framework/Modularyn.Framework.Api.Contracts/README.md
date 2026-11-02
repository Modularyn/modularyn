# API contracts and generation

The endpoint inventory is the input to `ApiGenerator`. It declares every built-in
route, method, compatibility window, authorization classification, credential
surface, request and response shape, and client exposure. Identity's current-user
view is classified `Authenticated`: its declaration requires the request's
server-side principal before the handler runs, and refuses others with 401 and
`MAZ001`. Its cookie authentication is described separately from its
authorization classification. Health responses remain status text. These
existing surfaces are inventoried without generating client methods for them.

An entry's authentication names the credential its client is meant to present.
`explicitCredential`, the default for client endpoints, is the credential that a
credential provider supplies, and no production host accepts one until G-03's
authorization server issues tokens. Until then a production host authenticates
those endpoints only through the browser's same-origin `__Host-Modularyn-Session`
cookie, as the OpenAPI document's security schemes state. The operations still
list only the explicit credential: listing the cookie as an accepted alternative
is a generator change, not a description, and is left to a reviewed decision.

An entry's response media type is declared, never inferred from its handler, and
inventory validation refuses any other combination:

| Response | Declared as | Rules |
|---|---|---|
| `application/json` | The default | The only response a client-visible endpoint may have, because generated clients read JSON |
| `text/plain` | `responseMediaType` | Never client-visible; the health probes' status text |
| `text/html` | `responseMediaType` with `page: true` | A browser page: GET, or POST for its form; `authentication` is `session` or `none`; no request or response model and no response schema; never client-visible |
| `application/octet-stream` | `responseMediaType` with `download` | A binary download: no response model or schema; never client-visible |

A page that accepts a form, its POST, declares `antiforgery: true`, and no other
entry may; its request schema, if it declares one, describes the form, which the
OpenAPI document lists as `application/x-www-form-urlencoded`. HTML appears only
for a declared page. A download's `download` names its disposition, `attachment`
or `inline`, and the explicit `mediaTypes` its bytes are sent with: each a
concrete lowercase `type/subtype` with no parameters or wildcard, never HTML or
JSON, as RFC 6838's restricted names. The OpenAPI document lists those types,
without a schema, as OpenAPI 3.1 describes binary content, and marks the
operations `x-modularyn-page`, `x-modularyn-antiforgery` and
`x-modularyn-download`. The marks appear only where an entry declares a page or
a download, so every other operation's description is unchanged, and neither a
page nor a download generates a client method. The inventory states what an
endpoint sends; the endpoint itself sends its headers, such as the Files module's
delivery headers for a download.

`ApiGenerator.OpenApi`, `DotNet` and `TypeScript` produce deterministic text using
only the shared framework and System.Text.Json. The checked OpenAPI document and
both clients are compared byte for byte in the repository tests. The contract
fixture has its own inventory and generated clients, checked the same way.
Generation does not rewrite transport or application code. A host generates its
own complete document from its active endpoint inventory; it does not expose an
unprotected discovery endpoint.

The advertised modes are generated static .NET and TypeScript clients. No dynamic
discovery mode is advertised. Versions 1 and 2 preserve the same declared fields;
version 0 is unsupported. A client selects a version from the intersection of its
window and the server's declared window before issuing a command. It never retries
a command to negotiate a version. `ApiCompatibility.BreakingChanges` rejects
changed bindings and fields while the declared windows overlap. Ending support
for a version requires moving the declared oldest version explicitly.

All model fields are required on the wire; a nullable field carries an explicit
null. An empty string remains distinct from null. Version tokens are opaque UUIDs.
Bootstrap integers are decimal strings so TypeScript preserves the full stored
integer range. Additional response fields are ignored by older generated clients.

The repository tests are source for Builder execution. No generated artifact or
test source establishes that compilation, provider tests or CI passed.
