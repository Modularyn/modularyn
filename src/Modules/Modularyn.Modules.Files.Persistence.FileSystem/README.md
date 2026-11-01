# Files

The Files module keeps logical file identities and immutable content versions in
PostgreSQL, with bytes in a configured filesystem root. Its domain owns the
storage interfaces. Application contracts expose value records, streams, the
module's permissions and its policy seams; the application service decides
every action and coordinates the two adapters. Each persistence adapter
references only the domain and its permitted framework support. The projects
are not packable.

Create a `FilesMetadataModule` for the deployment's PostgreSQL options and schema,
and include its manifest and module in the existing migration coordinator and
activation validator. Pass the permission declarations to the manifest and the
definitions to authorization's registry, so both agree before activation:

```csharp
var manifest = FilesMetadataModule.Manifest(version, FilePermissions.Definitions(version).Select(definition => definition.Declaration));
var registry = new AuthorizationRegistry(manifests, [.. AuthorizationModule.Permissions(version), .. FilePermissions.Definitions(version)], hostOperations);
var files = new FileService(new FileMetadataStore(module), new FileSystemBlobStore(root, maximumLength), evaluator, timeProvider,
    policy, releasePolicy, recoverySchedule);
```

`FileService` is the module's one `IFiles`. It requires SEC-05's
`IPermissionEvaluator` and refuses construction without it, so no ungated
service exists to register by omission. `FilesPolicy` holds the deployment's
limits; the release policy and the recovery schedule are optional seams.

All operations require an active `TenantContext` established by the tenancy
runtime and the unit's `ExecutionPrincipal`, passed explicitly. Neither a tenant
identifier alone nor host scope can open the store. Storage names contain only
the validated tenant identifier and server-generated version identifiers.
Display names, logical containers and owner references are metadata and never
enter path construction.

The root, its ancestors, tenant directories and object entries are checked for
symbolic links and reparse points. A linked or missing configured root is refused.
The deployment must keep the root and its ancestors under its control; these
managed path checks do not arbitrate concurrent directory replacement by a local
filesystem administrator. Configure the resolved directory itself when a
platform's temporary-directory alias contains a link.

## Authorization

The module declares six permissions. Upload decides at the bounded profile;
every other permission reads current authority at each decision, as delivery,
removal, release and the issue of delivery credentials require.

| Permission | Actions |
|---|---|
| `files.upload` | Create a file, or add a version to one |
| `files.read` | List files and versions, read released content, redeem a download grant |
| `files.delete` | Soft-delete a version, which revokes its open grants |
| `files.restore` | List retained versions and restore one |
| `files.release` | Run the deployment's release policy over a pending version |
| `files.grant` | Issue, list and revoke download grants |

Every action asks the evaluator before it reads content or changes anything. A
file is a resource of type `files.file`, identified by its identity in
hyphenated form, whose owner is the actor that created it: the uploading user's
effective subject, or for a service identity the user that its revalidated
context names. The server derives it; the client's owner reference is metadata
and carries no authority. Other members reach a file through authorization's
resource grants, which its owner makes with `modularyn.authorization.share`.

The service resolves the file within the effective tenant first, then asks
with the resource. A file the subject may not use (`MAZ007`) is answered exactly
as a missing one, `FileStorageError.NotFound`, and a caller without the
permission is refused with the evaluator's code whether or not the file exists,
so refusals reveal nothing about which files exist. Other refusals throw
`FileAccessDeniedException` with the code. A file list asks for the evaluator's
visibility and composes it into the query, so files the subject may not read
affect neither the rows nor their order; a page holds at most 100 files.

## Validation

Nothing a client sends is trusted before these checks, and none of them reads,
reserves or writes anything:

- **Size.** A declared length above the store's maximum is refused with
  `InvalidLength` before any intent exists. While the body streams, a chunk that
  would exceed the declared length is refused before it is written.
- **Names.** A display name is normalized to Unicode NFC and must then have 1 to
  255 UTF-16 code units, no control, format, separator, unassigned or unpaired
  surrogate character, none of `/ \ < > : " | ? *`, no surrounding whitespace, no
  trailing dot and no device name such as `CON` or `LPT1`. Bidirectional
  overrides and zero-width characters are format characters and refused.
  Under invariant globalization .NET has no normalization data, and a name is
  checked as given.
- **Containers.** Up to 200 characters in `/`-separated segments of at most 64
  lowercase ASCII letters, digits, `_` and `-`, with single inner dots.
- **Owner references.** 1 to 200 printable ASCII characters without spaces.
- **Type.** `FileContentTypes.Detect` decides the type from the first 1,445
  bytes, following the WHATWG MIME Sniffing rules for an unknown type with
  scriptable content recognized; the name and any client claim are never
  consulted. A type outside `FilesPolicy.AllowedContentTypes` is refused with
  `UnsupportedContent`. The default allows PNG, JPEG, GIF, WebP, PDF and plain
  text; HTML, XML, SVG, executables and unrecognized binary content are refused
  unless a deployment allows them explicitly.

Invalid metadata is refused with `InvalidName`. The columns are bounded too.

## Quarantine and release

Every new version is `Pending`, and the read path refuses every version that is
not `Released` with `Quarantined`: direct reads, grant issue and grant delivery
alike. `ReleaseAsync` is an authorized action that runs the deployment's
`IFileReleasePolicy` over the version's verified bytes, with the detected type,
and records its verdict. `Released` and `Rejected` are final. Without a policy,
on `Defer` and when the policy throws, the version stays pending; the policy's
exception reaches the caller. Files contains no scanner: a scanner or moderation
step is an adapter the deployment supplies.

## Download grants and delivery

A download grant is bound to one tenant, one released version, one purpose and
an expiry within `FilesPolicy.MaximumGrantLifetime` (one hour by default). Its
token is `mfg1_`, the grant's identity in 32 lowercase hexadecimal digits and 32
random bytes in unpadded base64url. The identity only selects the grant; the
store keeps a SHA-256 verifier of the secret bound to the tenant and the grant,
and delivery compares it in constant time. The token is returned once, to its
issuer; it formats, debugs and serializes as `[file-grant]`, cannot be
deserialized, and appears in no exception or diagnostic.

| Purpose | Use | Disposition |
|---|---|---|
| `Download` | Until it expires | Attachment |
| `Preview` | Until it expires | Inline for a type in `FilesPolicy.InlineContentTypes`, attachment otherwise |
| `SingleDownload` | Consumed by its first delivery | Attachment |

A grant is revoked individually, and deleting its version revokes its open
grants in the same transaction, so a restore revives none of them. Revocation and
consumption are recorded and never undone; a new grant always has a new token.
Recovery removes grants after their expiry. An unknown, mistyped, expired,
revoked, consumed or foreign token, and a grant used for another purpose, are
refused alike with `GrantRefused`.

Delivery checks current authorization: the redeeming principal must hold
`files.read` on the file now, as its owner or through a current resource grant.
A grant therefore never outlasts its holder's access, and a token alone confers
nothing.

**Anonymous delivery.** No grant is served without a session. Every delivery
needs an authenticated principal that is a current member of the grant's
tenant, so every grant follows ARC-016's current-authorization profile. The
bounded-bearer profile, delivery of files approved as public without a session,
is not offered.

**Encryption at rest.** Content is stored unencrypted. The slice relies on host
and database access controls for data at rest, as the threat model's data-at-rest
residual risk states; FND-031's optional content encryption is not enabled.

Every read returns a `FileDelivery` with the detected type, the disposition and
the display name. An endpoint sends its `Headers()` as they are:
`X-Content-Type-Options: nosniff`, an RFC 6266 `Content-Disposition` with an
RFC 8187 extended name, `Content-Security-Policy: default-src 'none'; sandbox`,
`Cross-Origin-Resource-Policy: same-origin` and `Cache-Control: private, no-store`.

## Upload and recovery

An upload takes the tenant's exclusive lease only for two short steps, and never
while it streams the client's body. Under the lease it reserves quota, commits a
tenant-scoped intent containing a server-issued version identifier, the detected
type and the captured `PropagatedTenantContext`, and creates and holds a
conditionally created staging file beside its destination; it then releases the
lease and streams, checking the declared length and SHA-256, and flushes the
content to disk. Under the lease again it finds its intent by identity, releases
the staging file, renames it without overwrite and, in one PostgreSQL
transaction, inserts the completed revision and removes the intent. A new
logical file may therefore exist without any completed versions until recovery.
An upload never replaces an earlier revision's bytes. An upload waits up to
`FilesPolicy.LeaseWait`, five seconds by default, for the lease before
reporting `Busy`.

The caller owns the input stream. The provider bounds bytes independently of the
source's seekability and refuses extra, missing or mismatched bytes. A cancellation
or failure leaves no partial completed revision; its intent and any staged or
renamed content remain for reconciliation. A failed response after metadata
commit can still represent a completed version. Inspect the version inventory
before deciding to upload again. An upload that streams longer than
`FilesPolicy.UploadTimeout`, fifteen minutes by default, is cancelled and
abandoned like any other failed one.

`RecoverAsync` takes the same exclusive per-tenant lease, reads the
authoritative intents, and deletes unreferenced staged and renamed objects
before removing their intents. A staging file that a live upload still holds is
in flight: recovery leaves it and its intent alone, so recovery may run at any
time. Recovery also removes a file that its failed first upload left without a
version, empties abandoned read slots and removes expired grants. Repeating
recovery after interruption is safe. Reconciliation includes soft-deleted
revisions so their content is retained. It never infers rollback from a failed
commit acknowledgement. Concurrent leased work for the same tenant reports
`Busy`; work for another tenant has its own lease. The lease file has a stable
identity and is never deleted by the provider.

Each tenant may hold at most `FilesPolicy.MaximumPendingUploads` intents, 16 by
default, declaring at most `MaximumPendingBytes`, four times the store's maximum
length by default. The reservation counts them under the lease before the intent
is committed, so concurrent uploads cannot exceed it. At the quota the upload
first reclaims the tenant's abandoned uploads under the same lease, and refuses
with `QuotaExceeded` if the quota still does not admit it.

Recovery is scheduled through `IFileRecoverySchedule` until CODE-05's durable
jobs take it over: after an upload fails with its intent committed, the service
hands the schedule the failed upload's context, captured in that unit with the
injected clock, and the schedule later runs `RecoverAsync` with it through
`TenantWorkRunner`, which revalidates the tenant and the actor first. Without a
schedule, recovery runs when the tenant next reaches its quota or when called.
A denied context, such as a removed uploader's, does not run recovery; the next
quota check still reclaims the abandoned upload. This module introduces no
host-wide dispatcher or implicit host scope.

## Reads, deletion and restore

Reads take no lease and run concurrently. Every read verifies the entire stored
length and digest before returning any content, including for a one-byte range,
into one of the tenant's read slots: a fixed set of files, `readSlots`, eight by
default, each held exclusively by one reader. Changes to the original after
verification do not alter the returned bytes. The returned stream is read-only
and nonseekable, and disposing the result empties and frees its slot. A read
beyond the tenant's slots reports `Busy`, so a tenant's snapshots occupy at most
the slot count times the maximum length. The operating system frees the slot of
a reader that ends without disposing, and its next reader or recovery truncates
the bytes left in it. Keep the tenant context active until streaming finishes.

Ranges are half-open intervals of offset and length. Negative values, overflow
and intervals beyond the content are refused; an empty interval at EOF is valid.
An empty upload is valid with the SHA-256 of empty content.

Deletion is a tracked, concurrency-checked soft delete with no lease. Ordinary
reads and lists then hide the revision while its bytes remain available for
restore. Delete returns the new concurrency token. Restore requires that token
and hashes the retained bytes where they are, with no copy, no read slot and no
lease, before making the same version visible again. Missing or corrupt content
leaves the deletion unchanged. Content identity, file reference, length, digest,
type and creation time cannot be changed on a persisted revision. All roots,
keys, indexes and references include the tenant, and the existing persistence
query and write guards remain active. Creation, expiry and grant times pass
through `FileTime`, the one UTC microsecond normalization function, before
persistence, and are read from the injected `TimeProvider`.
`ListDeletedAsync` provides the retained inventory and current tokens for restore,
including after a delete response was lost. The four named soft-delete bypasses
lift only that predicate and keep the tenant and scope filters active.

## Capabilities and verification

The filesystem adapter declares conditional creation, range reads, SHA-256
verification and flushed content with a same-directory atomic rename. It does
not provide native copy, signed access or a power-loss durability guarantee:
portable managed filesystem APIs do not expose directory synchronization after
rename. Use a local filesystem supporting exclusive sharing and atomic rename.

The lease, the read slots and the staging exclusion rest on exclusive opens.
Windows enforces them as share modes. On Linux and macOS they are non-blocking
advisory locks, which the runtime skips silently when
`System.IO.DisableFileLocking` (`DOTNET_SYSTEM_IO_DISABLEFILELOCKING`) is set or
the filesystem refuses locks. The store's constructor therefore opens a probe
file twice and refuses to start, with `LockingUnavailable`, when the second
exclusive open succeeds. Linux x64 is the qualified host; Windows and macOS are
build and unit-test platforms.

The interruption cases exercise failed streams and injected faults around real
filesystem and PostgreSQL operations, not power loss or network filesystems.

## Threat-model controls

For each control of boundary TB-G02-10, this records what the module delivers,
the tests that exercise it and what remains, with the work order it waits on.
`Application.Tests` is `Modularyn.Modules.Files.Application.Tests`, whose tests
with the trait `Requires=PostgreSQL` run over PostgreSQL through the database
script with the real tenancy runtime, identity, SEC-05's authorization and the
filesystem provider; only the request's authentication and SEC-07's audit seams
are doubles. `FileSystem.Tests` is
`Modularyn.Modules.Files.Persistence.FileSystem.Tests`, whose PostgreSQL tests
use a permissive evaluator double, because they exercise storage, not
authorization. Tests without the trait run on every CI platform. SEC-08 maps
every control to its evidence.

- **TM-G02-042, partly.** Delivered: new versions held pending and unreadable on
  the read path until the deployment's release policy releases them; rejected
  versions unreadable; release an authorized action over the verified bytes; a
  missing, deferring or failing policy keeping the version quarantined; types
  detected from the bytes against an allowed list, never from the name or a
  claim. Tests: `Application.Tests` `FileQuarantineTests` and `FileRulesTests`
  (`TM_G02_042_*`). Remaining: a scanner or moderation adapter, which the
  deployment supplies and no G-02 work order builds; the sample's policy
  (CODE-09); adversarial evidence (SEC-08).
- **TM-G02-043, partly.** Delivered by CODE-07: storage locations from
  server-issued identities only, links and reparse points refused on every path
  component. SEC-06 adds names normalized, bounded and refused when unsafe, and
  canonical containers and owner references, refused before any intent. Tests:
  `Application.Tests` `FileRulesTests` (`TM_G02_043_*`); `FileSystem.Tests`
  `StorageBoundaryTests` and, on every platform, `FileSystemStoreTests`.
  Remaining: adversarial evidence (SEC-08).
- **TM-G02-044, partly.** Delivered: every action decided by SEC-05's evaluator
  after the file resolves in the effective tenant, a refused file answered as
  missing; visibility composed into file lists; download grants bound to tenant,
  version, purpose and expiry, stored as verifiers, compared in constant time,
  revocable, single-use where the purpose says so, revoked with their version
  and delivered only with the holder's current authorization; no delivery
  without a session; the delivery headers, with sniffing disabled and attachment
  disposition unless a preview's type is approved for inline display. Tests:
  `Application.Tests` `FileAuthorizationTests`, `DownloadGrantTests` and
  `FileRulesTests` (`TM_G02_044_*`). Remaining: the endpoints that send the
  delivery headers and the declared endpoint inventory (CODE-06, CODE-09); the
  bounded-bearer profile for public files, which no G-02 work order covers;
  audit of grant issue and revocation (SEC-07); adversarial evidence for
  AC-003-N01 and AC-004 (SEC-08).
- **TM-G02-045, partly.** Delivered: the declared size refused before the
  intent and bytes bounded while they stream; pending uploads reserved under the
  tenant's lease and bounded by count and bytes; uploads past their deadline
  abandoned; abandoned uploads, versionless files and read slots reclaimed, at
  the quota, by recovery and through the recovery schedule; read snapshots
  bounded per tenant; reads, deletes and restores free of the lease, which an
  upload never holds while streaming; the startup check for exclusive opens.
  Tests: `Application.Tests` `FileCapacityTests` (`TM_G02_045_*`) and
  `G02AC009Tests.AC_009_B01_*`; `FileSystem.Tests` `FileSystemStoreTests` and
  `StorageBoundaryTests`. Remaining: recovery scheduled by CODE-05's durable
  jobs; per-tenant resource governance across the host (OPS-018, G-08), since
  these quotas bound one tenant's temporary storage, not its share of the host.
- **TM-G02-046, done for G-02 by CODE-07.** Hashes computed while writing,
  metadata finalized only on a matching length and hash, and verification before
  delivery and restore. SEC-06 makes restore verify in place, without a copy.
  Tests: `FileSystem.Tests` `G02AC009Tests.AC_009_N01_*` and `AC_009_R01_*` and
  `StorageBoundaryTests`; `Application.Tests`
  `FileCapacityTests.TM_G02_046_*`. Remaining: host and container recovery
  evidence (CODE-10 on CI-01).

Encryption at rest under FND-031 is not enabled, and no G-02 work order builds
it; until a release enables it and records its key handling, the threat model's
data-at-rest residual risk applies.

## Tests and evidence

`Modularyn.Modules.Files.Persistence.FileSystem.Tests` contains the six CODE-07
methods of the evidence plan, for AC-009-N01, AC-009-P01 and AC-009-R01, and
`Modularyn.Modules.Files.Application.Tests` the two SEC-06 methods for
AC-009-B01: a 1,024-byte maximum with 1,023, 1,024 and 1,025-byte inputs, and a
fourth upload paused before finalization and cancelled. Additional cases cover
unsafe names, linked paths, tenant confinement, corrupt and truncated content,
range bounds, cancellation, size limits, concurrency, propagated recovery and
existing guard refusals. Recovery tests capture quiesced metadata and verified
bytes, restore them into a fresh migrated schema and filesystem root, and
compare historical identities, lengths, timestamps and digests.

Every integration test has `Requires=PostgreSQL`, reads
`MODULARYN_TEST_POSTGRESQL` through the repository probe, takes the shared
`PostgreSqlTestLock` and creates only its own schemas in that database.
`FileSystemStoreTests` and `FileRulesTests` need no database: the existing CI
step that runs `Requires!=PostgreSQL` runs them on Linux, Windows and macOS, the
first over contexts that tenancy establishes from its in-memory directory. One
of them runs the test assembly again with file locking disabled by the runtime
switch, to show that the store refuses to start; on Windows, where the switch
has no effect, the store starts.

These tests do not establish endpoint, container, host or browser evidence, and
test source does not establish execution results.
