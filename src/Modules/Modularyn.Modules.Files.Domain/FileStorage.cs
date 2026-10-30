using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Files.Domain;

public sealed record BlobIntegrity
{
    public BlobIntegrity(long length, string sha256)
    {
        if (length < 0) throw new FileStorageException(FileStorageError.InvalidLength);
        if (sha256 is null || sha256.Length != 64 || sha256.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new FileStorageException(FileStorageError.IntegrityMismatch);
        Length = length;
        Sha256 = sha256;
    }

    public long Length { get; }
    public string Sha256 { get; }
}

/// <summary>Half-open byte interval. An empty interval at EOF is valid, including for an empty file.</summary>
public sealed record BlobRange(long Offset, long Length)
{
    public void Validate(long total)
    {
        if (Offset < 0 || Length < 0 || Offset > total || Length > total - Offset)
            throw new FileStorageException(FileStorageError.InvalidRange);
    }
}

public sealed record BlobCapabilities(bool ConditionalCreate, bool Ranges, bool Copy, bool Sha256,
    bool FlushedAtomicRename, bool PowerLossDurability, bool SignedAccess);

public interface IFileBlobStore
{
    BlobCapabilities Capabilities { get; }

    /// <summary>The largest content length the store accepts, so a larger declaration is refused before any intent exists.</summary>
    long MaximumLength { get; }

    /// <summary>
    /// The exclusive per-tenant lease, across provider instances and processes, for an upload's short intent and
    /// publication steps and for recovery. Concurrent leased work for the same tenant reports <see cref="FileStorageError.Busy"/>.
    /// </summary>
    ValueTask<IFileBlobSession> OpenAsync(TenantContext tenant, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies all bytes into one of the tenant's bounded read slots before returning a bounded, read-only stream over
    /// it. Reads take no lease and run concurrently; a read beyond the tenant's slots reports <see cref="FileStorageError.Busy"/>.
    /// The caller owns the stream, and disposing it frees the slot.
    /// </summary>
    Task<Stream> ReadAsync(TenantContext tenant, Guid revisionId, BlobIntegrity expected, BlobRange? range, CancellationToken cancellationToken);

    /// <summary>Hashes the stored bytes where they are, with no copy and no lease, and refuses any difference.</summary>
    Task VerifyAsync(TenantContext tenant, Guid revisionId, BlobIntegrity expected, CancellationToken cancellationToken);
}

public interface IFileBlobSession : IAsyncDisposable
{
    /// <summary>
    /// Creates the revision's staging file and holds it. Streaming into it needs no lease, and recovery leaves a held
    /// staging file alone; disposing the staging without publishing it leaves it for recovery.
    /// </summary>
    Task<IFileBlobStaging> StageAsync(Guid revisionId, CancellationToken cancellationToken);

    /// <summary>Releases a completely written staging file and renames it, without overwrite, to its immutable content.</summary>
    Task PublishAsync(IFileBlobStaging staging, CancellationToken cancellationToken);

    /// <summary>Stages, writes and publishes within this session's lease, for coordinated administrative restore.</summary>
    Task WriteAsync(Guid revisionId, Stream source, BlobIntegrity expected, CancellationToken cancellationToken);

    /// <summary>The store's verified read, within this session's lease, for coordinated backup.</summary>
    Task<Stream> ReadAsync(Guid revisionId, BlobIntegrity expected, BlobRange? range, CancellationToken cancellationToken);

    /// <summary>
    /// Called only after metadata reconciliation establishes that no revision references this identity. Returns false, and
    /// removes nothing, while a live upload still holds the staging file.
    /// </summary>
    Task<bool> DiscardAsync(Guid revisionId, CancellationToken cancellationToken);

    /// <summary>Reclaims read slots whose readers ended without disposing their streams; active readers are retained.</summary>
    Task RecoverReadSnapshotsAsync(CancellationToken cancellationToken);
}

/// <summary>One upload's held staging file. It belongs to the upload that created it and is never shared.</summary>
public interface IFileBlobStaging : IAsyncDisposable
{
    Guid RevisionId { get; }

    /// <summary>Streams the source into the staging file, refusing extra, missing or mismatched bytes as they arrive, and flushes it to disk.</summary>
    Task WriteAsync(Stream source, BlobIntegrity expected, CancellationToken cancellationToken);
}

public interface IFileMetadataStore
{
    Task<IFileMetadataUnit> BeginAsync(TenantContext tenant, CancellationToken cancellationToken);
}

/// <summary>The tenant's outstanding upload intents and the bytes they declared, as quotas count them.</summary>
public sealed record PendingUploads(int Count, long Bytes);

public interface IFileMetadataUnit : IAsyncDisposable
{
    Task<FileAsset?> FindFileAsync(Guid fileId, CancellationToken cancellationToken);
    Task<FileRevision?> FindRevisionAsync(Guid fileId, Guid revisionId, bool includeDeleted, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileRevision>> ListRevisionsAsync(Guid fileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileRevision>> ListDeletedRevisionsAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>The files of one container that the visibility admits, filtered in the query, in creation order.</summary>
    Task<IReadOnlyList<FileAsset>> ListFilesAsync(string container, ResourceVisibility visibility, int offset, int limit, CancellationToken cancellationToken);

    /// <summary>Whether any revision of the file exists, soft-deleted ones included.</summary>
    Task<bool> HasRevisionsAsync(Guid fileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileUpload>> ListUploadsAsync(CancellationToken cancellationToken);
    Task<FileUpload?> FindUploadAsync(Guid uploadId, CancellationToken cancellationToken);
    Task<PendingUploads> PendingUploadsAsync(CancellationToken cancellationToken);
    Task<bool> IsContentReferencedAsync(Guid revisionId, CancellationToken cancellationToken);
    Task<FileGrant?> FindGrantAsync(Guid grantId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileGrant>> ListGrantsAsync(Guid fileId, CancellationToken cancellationToken);

    /// <summary>The version's grants that are neither revoked nor consumed, tracked for revocation.</summary>
    Task<IReadOnlyList<FileGrant>> ListOpenGrantsAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>Removes grants that expired at or before the instant; none of them can be redeemed again.</summary>
    Task<int> RemoveExpiredGrantsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    void Add(FileAsset file);
    void Add(FileRevision revision);
    void Add(FileUpload upload);
    void Add(FileGrant grant);
    void Remove(FileAsset file);
    void Remove(FileUpload upload);
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Operational fault checkpoints over real storage; an observer never decides publication eligibility.</summary>
public enum FileStorageStage { IntentCommitted, ContentFlushed, ContentRenamed, MetadataCommitted }

public interface IFileStorageObserver
{
    ValueTask ReachedAsync(FileStorageStage stage, Guid revisionId, CancellationToken cancellationToken);
}
