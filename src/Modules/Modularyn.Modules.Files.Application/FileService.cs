using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Application;

public sealed class FileService(IFileMetadataStore metadata, IFileBlobStore blobs, TimeProvider time,
    IFileStorageObserver? observer = null) : IFiles
{
    public async Task<FileVersionInfo> UploadAsync(TenantContext tenant, FileUploadRequest request, Stream content,
        CancellationToken cancellationToken = default)
    {
        var tenantId = FileTenant.Require(tenant);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(request.Name);
        ArgumentNullException.ThrowIfNull(request.Container);
        ArgumentNullException.ThrowIfNull(request.Owner);
        var expected = new BlobIntegrity(request.Length, request.Sha256);
        await using var storage = await blobs.OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
        var fileId = request.FileId ?? Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var createdAt = FileTime.UtcNow(time);
        // This durable intent makes every interruption accountable, even after a rename or a lost commit response.
        await using (var intent = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            if (request.FileId is null)
                intent.Add(new FileAsset { TenantId = tenantId, Id = fileId, Name = request.Name, Container = request.Container,
                    Owner = request.Owner, CreatedAt = createdAt });
            else if (await intent.FindFileAsync(fileId, cancellationToken).ConfigureAwait(false) is null)
                throw new FileStorageException(FileStorageError.NotFound);
            intent.Add(new FileUpload { TenantId = tenantId, Id = revisionId, FileId = fileId, Length = expected.Length,
                Sha256 = expected.Sha256, CreatedAt = createdAt, PropagatedContext = PropagatedTenantContext.Capture(tenant).ToJson() });
            await intent.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        await ObserveAsync(FileStorageStage.IntentCommitted, revisionId, cancellationToken).ConfigureAwait(false);
        // Failures leave the intent for recovery. Never guess that a failed commit response means rollback.
        await storage.WriteAsync(revisionId, content, expected, cancellationToken).ConfigureAwait(false);
        await using var publish = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var upload = (await publish.ListUploadsAsync(cancellationToken).ConfigureAwait(false)).Single(row => row.Id == revisionId);
        var revision = new FileRevision { TenantId = tenantId, Id = revisionId, FileId = fileId,
            Length = expected.Length, Sha256 = expected.Sha256, CreatedAt = createdAt };
        publish.Add(revision);
        publish.Remove(upload);
        await publish.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ObserveAsync(FileStorageStage.MetadataCommitted, revisionId, cancellationToken).ConfigureAwait(false);
        return Describe(revision);
    }

    public async Task<IReadOnlyList<FileVersionInfo>> ListAsync(TenantContext tenant, Guid fileId, CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        return (await unit.ListRevisionsAsync(fileId, cancellationToken).ConfigureAwait(false)).Select(Describe).ToArray();
    }

    public async Task<FileReadResult> ReadAsync(TenantContext tenant, Guid fileId, Guid versionId, FileReadRange? range = null,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var storage = await blobs.OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var revision = await RequiredAsync(unit, fileId, versionId, false, cancellationToken).ConfigureAwait(false);
        var content = await storage.ReadAsync(revision.Id, new(revision.Length, revision.Sha256),
            range is null ? null : new(range.Offset, range.Length), cancellationToken).ConfigureAwait(false);
        return new(Describe(revision), content);
    }

    public async Task<IReadOnlyList<FileVersionInfo>> ListDeletedAsync(TenantContext tenant, Guid fileId, CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        return (await unit.ListDeletedRevisionsAsync(fileId, cancellationToken).ConfigureAwait(false)).Select(Describe).ToArray();
    }

    public async Task<FileVersionInfo> DeleteAsync(TenantContext tenant, Guid fileId, Guid versionId, Guid expectedVersion,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var storage = await blobs.OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var revision = await RequiredAsync(unit, fileId, versionId, true, cancellationToken).ConfigureAwait(false);
        RequireVersion(revision, expectedVersion);
        revision.IsDeleted = true;
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Describe(revision);
    }

    public async Task<FileVersionInfo> RestoreAsync(TenantContext tenant, Guid fileId, Guid versionId, Guid expectedVersion,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var storage = await blobs.OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var revision = await RequiredAsync(unit, fileId, versionId, true, cancellationToken).ConfigureAwait(false);
        RequireVersion(revision, expectedVersion);
        await using (await storage.ReadAsync(revision.Id, new(revision.Length, revision.Sha256), null, cancellationToken).ConfigureAwait(false)) { }
        revision.IsDeleted = false;
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Describe(revision);
    }

    /// <summary>Reconciles one tenant under the same exclusive lease as uploads. Safe to repeat after any interruption.</summary>
    public async Task<int> RecoverAsync(TenantContext tenant, CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var storage = await blobs.OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        await storage.RecoverReadSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var pending = await unit.ListUploadsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var upload in pending)
        {
            // Include deleted revisions: their bytes remain necessary for restore.
            var referenced = await unit.IsContentReferencedAsync(upload.Id, cancellationToken).ConfigureAwait(false);
            if (!referenced) await storage.DiscardAsync(upload.Id, cancellationToken).ConfigureAwait(false);
            unit.Remove(upload);
        }
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pending.Count;
    }

    /// <summary>Background recovery revalidates a captured context through the tenancy runtime.</summary>
    public Task<TenantWorkOutcome> RecoverAsync(PropagatedTenantContext propagated, TenantWorkRunner runner,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(propagated, async (unit, token) => { await RecoverAsync(unit.Context, token).ConfigureAwait(false); }, cancellationToken);

    private ValueTask ObserveAsync(FileStorageStage stage, Guid id, CancellationToken cancellationToken) =>
        observer?.ReachedAsync(stage, id, cancellationToken) ?? ValueTask.CompletedTask;

    private static async Task<FileRevision> RequiredAsync(IFileMetadataUnit unit, Guid fileId, Guid versionId, bool includeDeleted,
        CancellationToken cancellationToken) =>
        await unit.FindRevisionAsync(fileId, versionId, includeDeleted, cancellationToken).ConfigureAwait(false)
            ?? throw new FileStorageException(FileStorageError.NotFound);

    private static void RequireVersion(FileRevision revision, Guid expected)
    {
        if (expected == Guid.Empty || revision.Version != expected) throw new FileStorageException(FileStorageError.Conflict);
    }

    private static FileVersionInfo Describe(FileRevision revision) => new(revision.FileId, revision.Id, revision.Length,
        revision.Sha256, revision.CreatedAt, (FileQuarantineState)revision.Quarantine, revision.IsDeleted, revision.Version);
}
