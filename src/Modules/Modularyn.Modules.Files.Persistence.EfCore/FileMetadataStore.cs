using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.EfCore;

public sealed class FileMetadataStore : IFileMetadataStore
{
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

        public async Task<IReadOnlyList<FileUpload>> ListUploadsAsync(CancellationToken cancellationToken) =>
            await Context.Uploads.OrderBy(row => row.CreatedAt).ThenBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

        public Task<bool> IsContentReferencedAsync(Guid revisionId, CancellationToken cancellationToken) =>
            Context.IncludingSoftDeleted<StoredFileRevision>(new("Retain every referenced content identity during recovery.", "files.content-reconcile"))
                .Query.AnyAsync(row => row.Id == revisionId, cancellationToken);

        public void Add(FileAsset file) => Context.Files.Add(new StoredFileAsset
        {
            TenantId = file.TenantId, Id = file.Id, Name = file.Name, Container = file.Container, Owner = file.Owner,
            CreatedAt = FileTime.Normalize(file.CreatedAt)
        });

        public void Add(FileRevision revision)
        {
            var stored = new StoredFileRevision { TenantId = revision.TenantId, Id = revision.Id, FileId = revision.FileId,
                Length = revision.Length, Sha256 = revision.Sha256, CreatedAt = FileTime.Normalize(revision.CreatedAt),
                IsDeleted = revision.IsDeleted, Quarantine = revision.Quarantine };
            Context.Revisions.Add(stored);
            addedRevisions.Add((revision, stored));
        }

        public void Add(FileUpload upload) => Context.Uploads.Add(new StoredFileUpload
        {
            TenantId = upload.TenantId, Id = upload.Id, FileId = upload.FileId, Length = upload.Length, Sha256 = upload.Sha256,
            CreatedAt = FileTime.Normalize(upload.CreatedAt), PropagatedContext = upload.PropagatedContext
        });

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
