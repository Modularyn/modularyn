# Files storage

The Files module keeps logical file identities and immutable content versions in
PostgreSQL, with bytes in a configured filesystem root. Its domain owns the
storage interfaces. Application contracts expose value records and streams;
the application service coordinates the two adapters. Each persistence adapter
references only the domain and its permitted framework support. The projects
are not packable.

Create a `FilesMetadataModule` for the deployment's PostgreSQL options and schema,
and include its manifest and module in the existing migration coordinator and
activation validator. Construct `FileMetadataStore` with that module,
`FileSystemBlobStore` with an existing absolute directory and a maximum upload
length, and `FileService` with those adapters and a `TimeProvider`. Register the
service as `IFiles` in the composition that consumes these components.

All operations require an active `TenantContext` established by the tenancy
runtime. Neither a tenant identifier alone nor host scope can open the store.
Storage names contain only the validated tenant identifier and server-generated
version identifiers. Display names, logical containers and owner references are
metadata and never enter path construction. Owner references carry no authority.

The root, its ancestors, tenant directories and object entries are checked for
symbolic links and reparse points. A linked or missing configured root is refused.
The deployment must keep the root and its ancestors under its control; these
managed path checks do not arbitrate concurrent directory replacement by a local
filesystem administrator. Configure the resolved directory itself when a
platform's temporary-directory alias contains a link.

## Upload and recovery

An upload first commits a tenant-scoped intent containing a server-issued version
identifier and the captured `PropagatedTenantContext`. A new logical file may
therefore exist without any completed versions. The service streams into a
conditionally created temporary file beside its destination, checks the declared
length and SHA-256, flushes the content to disk and renames it without overwrite.
Only then does one PostgreSQL transaction insert the completed revision and
remove the intent. An upload never replaces an earlier revision's bytes.

The caller owns the input stream. The provider bounds bytes independently of the
source's seekability and refuses extra, missing or mismatched bytes. A cancellation
or failure leaves no partial completed revision; its intent and any staged or
renamed content remain for reconciliation. A failed response after metadata
commit can still represent a completed version. Inspect the version inventory
before deciding to upload again.

`RecoverAsync` takes the same exclusive per-tenant filesystem lease as uploads,
reads the authoritative intents, and deletes unreferenced staged and renamed
objects before removing their intents. Repeating recovery after interruption is
safe. Reconciliation includes soft-deleted revisions so their content is retained.
It never infers rollback from a failed commit acknowledgement. Concurrent work
for the same tenant reports `Busy`; work for another tenant has its own lease.
The lease file has a stable identity and is never deleted by the provider.
Sessions and metadata units are sequential objects, not shared workers.

Background callers use the overload accepting a `PropagatedTenantContext` and
`TenantWorkRunner`. The runner revalidates the tenant and actor before recovery
and ends the context afterwards. A denied or deferred context does not execute
storage work. This module introduces no host-wide dispatcher or implicit host
scope.

## Reads, deletion and restore

Every read verifies the entire stored length and digest before returning any
content, including for a one-byte range. Verification writes a private temporary
snapshot, so changes to the original after verification do not alter the returned
bytes. The returned stream is read-only and nonseekable, and disposing the result
disposes its snapshot. Keep its tenant context active until streaming finishes.
Memory usage is bounded by the copy buffer; temporary disk usage includes the
whole version for each active read.
Recovery also reclaims abandoned read snapshots while retaining those still held
by a live reader.

Ranges are half-open intervals of offset and length. Negative values, overflow
and intervals beyond the content are refused; an empty interval at EOF is valid.
An empty upload is valid with the SHA-256 of empty content.

Deletion is a tracked, concurrency-checked soft delete. Ordinary reads and lists
then hide the revision while its bytes remain available for restore. Delete
returns the new concurrency token. Restore requires that token and verifies the
retained bytes before making the same version visible again. Missing or corrupt
content leaves the deletion unchanged. Content identity, file reference, length,
digest and creation time cannot be changed on a persisted revision. All roots,
keys, indexes and references include the tenant, and the existing persistence
query and write guards remain active. Creation times pass through `FileTime`,
the one UTC microsecond normalization function, before persistence.
`ListDeletedAsync` provides the retained inventory and current tokens for restore,
including after a delete response was lost. Its named soft-delete bypass keeps
the tenant and scope filters active.

Quarantine has the data states `Pending`, `Released` and `Rejected`. New revisions
are pending. Storage reads do not decide eligibility; policy adapters attach
authorization, content processing, quarantine release, download grants and audit
before exposing operations. This module installs no HTTP endpoint or such policy.

## Capabilities and verification

The filesystem adapter declares conditional creation, range reads, SHA-256
verification and flushed content with a same-directory atomic rename. It does
not provide native copy, signed access or a power-loss durability guarantee:
portable managed filesystem APIs do not expose directory synchronization after
rename. Use a local filesystem supporting exclusive sharing and atomic rename.
The interruption cases exercise failed streams and injected faults around real
filesystem and PostgreSQL operations, not power loss or network filesystems.

`Modularyn.Modules.Files.Persistence.FileSystem.Tests` contains the six named
CODE-07 methods from the evidence plan. Additional cases cover unsafe names,
linked paths, tenant confinement, corrupt and truncated content, range bounds,
cancellation, size limits, concurrency, propagated recovery and existing guard
refusals. Recovery tests capture quiesced metadata and verified bytes, restore
them into a fresh migrated schema and filesystem root, and compare historical
identities, lengths, timestamps and digests.

Every integration test has `Requires=PostgreSQL`, reads
`MODULARYN_TEST_POSTGRESQL` through the repository probe, takes the shared
`PostgreSqlTestLock` and creates only its own schemas in that database. The tests
use real storage and database-backed tenancy and membership fixture data. Fixture
identity provisioning is not evidence for authorization or audit policy.

Dependency locks reuse the repository's existing resolved versions and archive
hashes, with project edges derived offline. The Builder must validate them with
restore, then run the Release build, repository tests and real-provider suite.
All six CI checks must pass at the same final commit. Source review and these
test definitions do not establish a passing run or provider qualification.
