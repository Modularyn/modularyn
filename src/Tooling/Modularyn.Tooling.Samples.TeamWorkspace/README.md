# Team workspace

This sample composes the current foundation into a small tenant application.
Members create projects, write notes with immutable revision history, upload
attachments, read a durable activity feed and subscribe to a daily digest in an
IANA time zone. Notes are shared within their tenant: `workspace.read` and
`workspace.write` are tenant-wide permissions. Attachments retain Files' own
creator/resource authorization; a tenant permission alone does not expose another
member's file.

The sibling `Modularyn.Tooling.Samples.TeamWorkspace.Host` is the composition
root and the only product project that references module implementations. This
project uses framework projects and Files' contracts. All projects use the plain
`Microsoft.NET.Sdk`; the two browser pages are HTML endpoints, without Razor.

## Foundation coverage

| Area | Sample surface |
|---|---|
| Composition and lifecycle | One manifest selection, deployment trust, migration readiness, module work admission and draining |
| Identity | Identity's JSON sign-in, antiforgery issuance, default session authentication and current-user endpoint |
| Tenancy and authorization | Validated route tenant, current membership, deny-by-default permission wrappers, fixed member role, audited directory/membership operations |
| Persistence | PostgreSQL module schema; tenant-qualified keys, indexes and foreign keys; tracked units; note version tokens; transactional API receipts |
| Settings, features and cache | Audited tenant title/feature overrides; bootstrap projection; foundation scoped caches; digest feature checked when subscribing and running |
| Localization | Versioned English/Spanish resource with foundation culture fallback |
| Durable events | Note, revision and outbox intent in one transaction; activity from inbox consumption, ordered by note revision |
| Background jobs | Foundation `JobStore`, `ScheduleWorker`, `DailySchedule` and `JobWorker`; member digests and attachment release |
| API and clients | Versions 1 and 2, complete routed inventory, transaction-bound idempotent JSON mutations, active OpenAPI document |
| Files | Quarantine, explicit allowlist release, short-lived grants in headers, bounded delivery and one read slot |
| Configuration and secrets | Environment configuration, SEC-02 secret references, declared persistent data-protection key ring |
| Audit and operations | Transactional `IAuditTrail` records, separately authorized audit/job/event diagnostics, foundation health endpoints and redacted logging |

The job tables live in the workspace module so digest effects and completion
commit in the same transaction, using the foundation's documented application-owned
job model. The separate Background Jobs module is not composed. No replacement
scheduler, transport, identity implementation or authorization evaluator is added.
The digest is a durable, member-specific activity count in the application's
database; it sends no email because the foundation has no mail transport here.

## Security and consistency

Every routed endpoint has a `WorkspaceApi` or foundation inventory declaration.
The host validates the active inventory before listening. JSON mutations that
advertise client methods use `Idempotency-Key`; the operation receipt, note,
revision, audit record and event intent commit together. A reused identity with
different input fails. Revisions require the original opaque version. List reads
are bounded to 100 items; digest history is bounded to 30.

Every job and message enters `AuthorizedWorkRunner` through the foundation
workers, including the file-release job. Immediate file steps also enter it with
a reference captured from the request's validated server context. No payload,
route, tenant row or client permission list grants authority. Durable payloads
contain only note/version identifiers, file/version identifiers, or `{}` for a
digest. Workers obtain the producer from the sealed, revalidated context.
The declared key ring is mandatory. Missing keys refuse startup or defer work;
the sample does not supply an unsealed path.

The file-release job calls Files' idempotent release service in its own module
transaction. A lost job acknowledgement can repeat the same final decision; it
cannot release another version or replace bytes. That transaction is separate
from the job acknowledgement. The requested release is audited with its queue
entry. No cross-module atomicity is claimed.

The fixed member role holds `files.release` as well as `files.grant`. A release
only asks the deployment's allowlist policy to judge a file, and the policy
decides; Files authorizes it only for the file's creator or a holder of a
current grant on it, so a member asks for its own uploads and an owner for its
own.

`AllowlistFileReleasePolicy` is explicitly registered for exactly PNG, JPEG and
plain text, with a maximum of **10 MiB**. It checks detected type and length and
**does no content scanning**. Files' upload detector may admit other supported
types into quarantine, such as PDF, but this deployment refuses their release.
Versions the allowlist refuses stay **pending** in quarantine, unreadable, and
are never marked rejected, which would be final. There is no scanner,
moderation adapter or public anonymous file access.

Grant issuance returns the token once in an uncached JSON body. Download uses
`X-Modularyn-File-Grant` on `GET /api/v1/t/{tenant}/download`; it accepts no query
parameters. Never put the token in a URL, browser storage or a diagnostic. The
host installs no HTTP body/header logging, keeps ASP.NET Core's logs at Warning
and above, so its Information-level request logs stay out while antiforgery and
data-protection warnings remain, and retains the foundation's redacting logger. A reverse proxy must likewise
omit credentials, request/response bodies and the grant header from its logs and
traces. The token lifetime is two minutes. A download has a fifteen-second
deadline that aborts the request and disposes the read stream, releasing the
single Files read slot even when the receiver stops accepting bytes. Files'
detected media type and security/disposition headers are sent unchanged.

The sign-in page loads only `/assets/sign-in.js`. That file fetches
`/identity/antiforgery`, sends its token in `RequestVerificationToken` and posts
JSON to Identity's existing sign-in endpoint. The page's inputs carry no names
and its form no action or method, so a browser that blocks the script submits
no credentials. The note form is a declared HTML
POST with a hidden antiforgery token; the ingress performs validation. Pages
encode user text and restrict scripts, connections and forms to the same origin.

All sample database timestamps come from `TimeProvider` through the foundation's
`UtcMicroseconds` boundary. Digest schedules use `DailySchedule` and IANA zones,
including its explicit gap, fold and catch-up behavior.

## Endpoints

The source inventory is `WorkspaceApi.cs`. The host's protected
`GET /api/v1/t/{tenant}/contract` generates OpenAPI from the exact active routed
inventory, including Identity, health and bootstrap. The same inventory can be
given to the foundation's .NET and TypeScript generators by deployment tooling;
no generated sample clients are checked in. Session cookies are the production
credential surface; no bearer-token substitute is installed.

The API prefix is `/api/v{version}/t/{tenant}`, with canonical tenant UUIDs.
Foundation bootstrap is at `/api/v{version}/t/{tenant}/configuration`.

| Suffix | Methods | Permission or purpose |
|---|---|---|
| `/projects` | GET, POST | Read or create tenant projects |
| `/notes` | POST, PUT | Create or revise with a version token |
| `/notes/{note}/revisions` | GET | Read revision history |
| `/activity` | GET | Read the durable projection |
| `/digests` | GET, POST | Read own digests or subscribe with `timeZone` and `localTime` (`HH:mm`) |
| `/attachments/{note}` | POST | Raw upload with `Content-Length`, `X-File-Name`, `X-Content-SHA256` and declared media type |
| `/attachments`, `/files/{file}/versions` | GET | Files-authorized lists |
| `/releases` | POST | Enqueue policy evaluation of a file/version pair |
| `/grants`, `/download` | POST, GET respectively | Issue a grant and redeem it from its header |
| `/title`, `/digest-feature` | PUT | Audited tenant overrides with expected version |
| `/member-role`, `/members/{member}/role` | POST | Define the fixed member role or assign it within the actor's ceiling |
| `/audit`, `/jobs`, `/events` | GET | Separate audit or diagnostic permissions |
| `/localization`, `/contract` | GET | Versioned resource (`culture` query, English by default) or active API description |
| `/development-seed` | POST | Only when explicitly enabled in Development/Testing; creates a project and note for an already authorized member |

Host-scoped tenant creation and membership addition are at
`/api/v{version}/host/tenants` and `/api/v{version}/host/memberships`. Both require
the foundation's existing audited host operations. No endpoint or startup path
creates accounts, grants host authority, names a first operator or assigns a
tenant owner. The host's one-time `provision-host-operator` command, a command
and never an endpoint, makes an existing account the first host operator under
the deployment's bootstrap authority, as the host README describes.

## Validation status and limits

This is implementation source for Builder validation, not Linux qualification.
Restore, compilation, execution against PostgreSQL, Linux host/container startup,
browser automation and the six exact-head CI checks have not run in this task.
No foundation file, package reference, central package version or CI file is
changed. The Builder creates the new projects' package locks through its restore
workflow; a deployment trust lock is a separate artifact and is not a package lock.

The sibling test project is an xUnit test project like the repository's others.
Its smoke scenario carries `[Trait("Requires", "PostgreSQL")]`, so the
real-provider CI job runs it with the other PostgreSQL tests.
CI's `container-build` job builds the sample's image from the host's published
output and runs it, as the host README describes.

The development endpoint seeds only project/note data after authorization.
Tenant/member/role seed data lives in the isolated smoke fixture, where
credentials and deployment identities are generated at run time. Production
refuses the development-seed switch. Account and tenant-owner provisioning,
email delivery, browser evidence, exhaustive G-02 acceptance and generated
sample client execution remain outside this delivery.

See the sibling host README for deployment and the test README for the smoke
scenario and its execution limitations.
