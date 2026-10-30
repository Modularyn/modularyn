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
