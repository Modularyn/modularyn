using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Files.Application.Contracts;

public enum FileQuarantineState { Pending = 1, Released = 2, Rejected = 3 }

public sealed record FileUploadRequest(Guid? FileId, string Name, string Container, string Owner, long Length, string Sha256);
public sealed record FileVersionInfo(Guid FileId, Guid VersionId, long Length, string Sha256, DateTimeOffset CreatedAt,
    FileQuarantineState Quarantine, bool IsDeleted, Guid ConcurrencyVersion);
public sealed record FileReadRange(long Offset, long Length);
public sealed record FileReadResult(FileVersionInfo Version, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Storage use cases for a validated tenant. Policy adapters attach authorization and content eligibility before
/// invoking these services; no HTTP endpoint, anonymous delivery or release policy is installed here.
/// </summary>
public interface IFiles
{
    Task<FileVersionInfo> UploadAsync(TenantContext tenant, FileUploadRequest request, Stream content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileVersionInfo>> ListAsync(TenantContext tenant, Guid fileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileVersionInfo>> ListDeletedAsync(TenantContext tenant, Guid fileId, CancellationToken cancellationToken = default);
    Task<FileReadResult> ReadAsync(TenantContext tenant, Guid fileId, Guid versionId, FileReadRange? range = null, CancellationToken cancellationToken = default);
    Task<FileVersionInfo> DeleteAsync(TenantContext tenant, Guid fileId, Guid versionId, Guid expectedVersion, CancellationToken cancellationToken = default);
    Task<FileVersionInfo> RestoreAsync(TenantContext tenant, Guid fileId, Guid versionId, Guid expectedVersion, CancellationToken cancellationToken = default);
}
