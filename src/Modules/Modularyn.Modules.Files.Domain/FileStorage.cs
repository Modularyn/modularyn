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
    /// <summary>Excludes competing operations and recovery for this tenant, across provider instances and processes.</summary>
    ValueTask<IFileBlobSession> OpenAsync(TenantContext tenant, CancellationToken cancellationToken);
}

public interface IFileBlobSession : IAsyncDisposable
{
    Task WriteAsync(Guid revisionId, Stream source, BlobIntegrity expected, CancellationToken cancellationToken);
    /// <summary>Verifies all bytes before returning a bounded, read-only snapshot. The caller owns the stream.</summary>
    Task<Stream> ReadAsync(Guid revisionId, BlobIntegrity expected, BlobRange? range, CancellationToken cancellationToken);
    /// <summary>Called only after metadata reconciliation establishes that no revision references this identity.</summary>
    Task DiscardAsync(Guid revisionId, CancellationToken cancellationToken);
    /// <summary>Reclaims snapshots whose readers ended without disposing their streams; active readers are retained.</summary>
    Task RecoverReadSnapshotsAsync(CancellationToken cancellationToken);
}

public interface IFileMetadataStore
{
    Task<IFileMetadataUnit> BeginAsync(TenantContext tenant, CancellationToken cancellationToken);
}

public interface IFileMetadataUnit : IAsyncDisposable
{
    Task<FileAsset?> FindFileAsync(Guid fileId, CancellationToken cancellationToken);
    Task<FileRevision?> FindRevisionAsync(Guid fileId, Guid revisionId, bool includeDeleted, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileRevision>> ListRevisionsAsync(Guid fileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileRevision>> ListDeletedRevisionsAsync(Guid fileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileUpload>> ListUploadsAsync(CancellationToken cancellationToken);
    Task<bool> IsContentReferencedAsync(Guid revisionId, CancellationToken cancellationToken);
    void Add(FileAsset file);
    void Add(FileRevision revision);
    void Add(FileUpload upload);
    void Remove(FileUpload upload);
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Operational fault checkpoints over real storage; an observer never decides publication eligibility.</summary>
public enum FileStorageStage { IntentCommitted, ContentFlushed, ContentRenamed, MetadataCommitted }

public interface IFileStorageObserver
{
    ValueTask ReachedAsync(FileStorageStage stage, Guid revisionId, CancellationToken cancellationToken);
}
