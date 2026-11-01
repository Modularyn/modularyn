using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.EfCore;

public sealed class FileMetadataStore : IFileMetadataStore
{
    // Expired grants are removed in bounded batches, so one recovery run holds the tenant's lease for a bounded time.
    private const int ExpiredGrantBatch = 1000;
    private readonly PostgreSqlModule module;

    public FileMetadataStore(PostgreSqlModule module)
    {
        if (module.ContextType != typeof(FilesContext)) throw new ArgumentException("Use the Files metadata module.", nameof(module));
        this.module = module;
    }

    public async Task<IFileMetadataUnit> BeginAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        FileTenant.Require(tenant);
        return new Unit(await EfCoreUnitOfWork.BeginAsync(module, tenant, UnitOfWorkMode.New,
            cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    private sealed class Unit(EfCoreUnitOfWork work) : IFileMetadataUnit
    {
        private FilesContext Context => (FilesContext)work.Context;
        private readonly List<(FileRevision Source, StoredFileRevision Stored)> addedRevisions = [];

        public async Task<FileAsset?> FindFileAsync(Guid fileId, CancellationToken cancellationToken) =>
            await Context.Files.SingleOrDefaultAsync(row => row.Id == fileId, cancellationToken).ConfigureAwait(false);

        public async Task<FileRevision?> FindRevisionAsync(Guid fileId, Guid revisionId, bool includeDeleted, CancellationToken cancellationToken)
        {
            var query = includeDeleted
                ? Context.IncludingSoftDeleted<StoredFileRevision>(new("Reconcile or restore retained file content.", "files.restore-reconcile")).Query
                : Context.Revisions;
            return await query.SingleOrDefaultAsync(row => row.FileId == fileId && row.Id == revisionId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<FileRevision>> ListRevisionsAsync(Guid fileId, CancellationToken cancellationToken) =>
            await Context.Revisions.Where(row => row.FileId == fileId).OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<FileRevision>> ListDeletedRevisionsAsync(Guid fileId, CancellationToken cancellationToken) =>
            await Context.IncludingSoftDeleted<StoredFileRevision>(new("Inventory retained revisions for restore.", "files.deleted-inventory"))
                .Query.Where(row => row.FileId == fileId && row.IsDeleted).OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The visibility becomes part of the query, with its values bound as parameters, so hidden files affect neither
        // the rows nor their order. A file's resource identifier is its identity in the canonical hyphenated form.
        public async Task<IReadOnlyList<FileAsset>> ListFilesAsync(string container, ResourceVisibility visibility, int offset, int limit,
            CancellationToken cancellationToken) =>
            await visibility.Apply(Context.Files.AsNoTracking().Where(row => row.Container == container),
                    row => row.CreatedByKind == ActorKind.User ? row.CreatedBy : "", row => row.Id.ToString())
                .OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).Skip(offset).Take(limit)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        public Task<bool> HasRevisionsAsync(Guid fileId, CancellationToken cancellationToken) =>
            Context.IncludingSoftDeleted<StoredFileRevision>(new("Keep a file while any of its revisions is retained.", "files.asset-reconcile"))
                .Query.AnyAsync(row => row.FileId == fileId, cancellationToken);

        public async Task<IReadOnlyList<FileUpload>> ListUploadsAsync(CancellationToken cancellationToken) =>
            await Context.Uploads.OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        public async Task<FileUpload?> FindUploadAsync(Guid uploadId, CancellationToken cancellationToken) =>
            await Context.Uploads.SingleOrDefaultAsync(row => row.Id == uploadId, cancellationToken).ConfigureAwait(false);

        public async Task<PendingUploads> PendingUploadsAsync(CancellationToken cancellationToken)
        {
            var count = await Context.Uploads.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await Context.Uploads.AsNoTracking().SumAsync(row => (long?)row.Length, cancellationToken).ConfigureAwait(false);
            return new(count, bytes ?? 0);
        }

        public Task<bool> IsContentReferencedAsync(Guid revisionId, CancellationToken cancellationToken) =>
            Context.IncludingSoftDeleted<StoredFileRevision>(new("Retain every referenced content identity during recovery.", "files.content-reconcile"))
                .Query.AnyAsync(row => row.Id == revisionId, cancellationToken);

        public async Task<FileGrant?> FindGrantAsync(Guid grantId, CancellationToken cancellationToken) =>
            await Context.Grants.SingleOrDefaultAsync(row => row.Id == grantId, cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<FileGrant>> ListGrantsAsync(Guid fileId, CancellationToken cancellationToken) =>
            await Context.Grants.AsNoTracking().Where(row => row.FileId == fileId).OrderBy(row => row.CreatedAt).ThenBy(row => row.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        public async Task<IReadOnlyList<FileGrant>> ListOpenGrantsAsync(Guid versionId, CancellationToken cancellationToken) =>
            await Context.Grants.Where(row => row.VersionId == versionId && row.RevokedAt == null && row.ConsumedAt == null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        public async Task<int> RemoveExpiredGrantsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            var expired = await Context.Grants.Where(row => row.ExpiresAt <= now).OrderBy(row => row.ExpiresAt).ThenBy(row => row.Id)
                .Take(ExpiredGrantBatch).ToListAsync(cancellationToken).ConfigureAwait(false);
            Context.Grants.RemoveRange(expired);
            return expired.Count;
        }

        public void Add(FileAsset file) => Context.Files.Add(new StoredFileAsset
        {
            TenantId = file.TenantId, Id = file.Id, Name = file.Name, Container = file.Container, Owner = file.Owner,
            CreatedByKind = file.CreatedByKind, CreatedBy = file.CreatedBy, CreatedAt = FileTime.Normalize(file.CreatedAt)
        });

        public void Add(FileRevision revision)
        {
            var stored = new StoredFileRevision { TenantId = revision.TenantId, Id = revision.Id, FileId = revision.FileId,
                Length = revision.Length, Sha256 = revision.Sha256, ContentType = revision.ContentType, CreatedAt = FileTime.Normalize(revision.CreatedAt),
                IsDeleted = revision.IsDeleted, Quarantine = revision.Quarantine };
            Context.Revisions.Add(stored);
            addedRevisions.Add((revision, stored));
        }

        public void Add(FileUpload upload) => Context.Uploads.Add(new StoredFileUpload
        {
            TenantId = upload.TenantId, Id = upload.Id, FileId = upload.FileId, Length = upload.Length, Sha256 = upload.Sha256,
            ContentType = upload.ContentType, CreatedAt = FileTime.Normalize(upload.CreatedAt), PropagatedContext = upload.PropagatedContext
        });

        public void Add(FileGrant grant) => Context.Grants.Add(new StoredFileGrant
        {
            TenantId = grant.TenantId, Id = grant.Id, FileId = grant.FileId, VersionId = grant.VersionId, Purpose = grant.Purpose,
            Verifier = grant.Verifier.ToArray(), CreatedByKind = grant.CreatedByKind, CreatedBy = grant.CreatedBy,
            CreatedAt = FileTime.Normalize(grant.CreatedAt), ExpiresAt = FileTime.Normalize(grant.ExpiresAt)
        });

        public void Remove(FileAsset file) => Context.Files.Remove((StoredFileAsset)file);

        public void Remove(FileUpload upload) => Context.Uploads.Remove((StoredFileUpload)upload);

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            var result = await work.CompleteAsync(cancellationToken).ConfigureAwait(false);
            if (result.State != UnitOfWorkState.Committed) throw new FileStorageException(FileStorageError.Conflict);
            foreach (var (source, stored) in addedRevisions) { source.Version = stored.Version; source.CreatedAt = stored.CreatedAt; }
        }

        public ValueTask DisposeAsync() => work.DisposeAsync();
    }
}
