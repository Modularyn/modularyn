using System.Buffers;
using System.Security.Cryptography;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.FileSystem;

/// <summary>A single configured filesystem root, with immutable, conditionally created content per validated tenant.</summary>
public sealed class FileSystemBlobStore : IFileBlobStore
{
    public const int DefaultReadSlots = 8;
    public const int MaxReadSlots = 64;
    private readonly ConfinedPaths paths;
    private readonly int readSlots;
    private readonly IFileStorageObserver? observer;

    /// <summary>
    /// Opens the store over an existing absolute root. Each tenant may hold at most <paramref name="readSlots"/> verified
    /// read snapshots at once, each at most <paramref name="maximumLength"/> bytes. Construction is refused where the
    /// filesystem does not enforce exclusive opens.
    /// </summary>
    public FileSystemBlobStore(string root, long maximumLength, IFileStorageObserver? observer = null, int readSlots = DefaultReadSlots)
    {
        if (maximumLength < 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (readSlots is < 1 or > MaxReadSlots) throw new ArgumentOutOfRangeException(nameof(readSlots));
        paths = new(root);
        MaximumLength = maximumLength;
        this.readSlots = readSlots;
        this.observer = observer;
        RequireExclusiveLocking(paths);
    }

    // Flush(true) and same-directory rename cover process interruption. Portable .NET does not expose directory fsync.
    public BlobCapabilities Capabilities { get; } = new(true, true, false, true, true, false, false);

    public long MaximumLength { get; }

    public ValueTask<IFileBlobSession> OpenAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tenantId = FileTenant.Require(tenant);
        FileStream? lease = null;
        try
        {
            paths.Tenant(tenantId);
            var location = paths.Lease(tenantId);
            try { lease = new FileStream(location, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new FileStorageException(FileStorageError.Busy); }
            // The stable lease file is never unlinked: replacing it would split the exclusion boundary.
            paths.Lease(tenantId);
            return ValueTask.FromResult<IFileBlobSession>(new Session(this, tenant, lease));
        }
        catch (Exception exception)
        {
            lease?.Dispose();
            if (ConfinedPaths.IsIo(exception)) throw new FileStorageException(FileStorageError.StorageUnavailable);
            throw;
        }
    }

    public async Task<Stream> ReadAsync(TenantContext tenant, Guid revisionId, BlobIntegrity expected, BlobRange? range, CancellationToken cancellationToken)
    {
        var tenantId = FileTenant.Require(tenant);
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        range ??= new(0, expected.Length);
        range.Validate(expected.Length);
        FileStream? slot = null;
        try
        {
            paths.Tenant(tenantId);
            await using var source = OpenContent(tenantId, revisionId, expected);
            slot = AcquireSlot(tenantId);
            // A private snapshot prevents later source mutation from changing already verified response bytes.
            await CopyVerifiedAsync(source, slot, expected, cancellationToken).ConfigureAwait(false);
            await slot.FlushAsync(cancellationToken).ConfigureAwait(false);
            FileTenant.Require(tenant);
            slot.Position = range.Offset;
            return new VerifiedRangeStream(slot, range.Length, tenant);
        }
        catch (Exception exception)
        {
            if (slot is not null) VerifiedRangeStream.Release(slot);
            if (ConfinedPaths.IsIo(exception)) throw new FileStorageException(FileStorageError.StorageUnavailable);
            throw;
        }
    }

    public async Task VerifyAsync(TenantContext tenant, Guid revisionId, BlobIntegrity expected, CancellationToken cancellationToken)
    {
        var tenantId = FileTenant.Require(tenant);
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            paths.Tenant(tenantId);
            await using var source = OpenContent(tenantId, revisionId, expected);
            await CopyVerifiedAsync(source, Stream.Null, expected, cancellationToken).ConfigureAwait(false);
            FileTenant.Require(tenant);
        }
        catch (Exception exception) when (ConfinedPaths.IsIo(exception))
        {
            throw new FileStorageException(FileStorageError.StorageUnavailable);
        }
    }

    private FileStream OpenContent(Guid tenantId, Guid revisionId, BlobIntegrity expected)
    {
        var location = paths.File(tenantId, revisionId, ".blob");
        if (!File.Exists(location)) throw new FileStorageException(FileStorageError.NotFound);
        var source = new FileStream(location, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        try
        {
            paths.File(tenantId, revisionId, ".blob");
            if (source.Length != expected.Length) throw new FileStorageException(FileStorageError.IntegrityMismatch);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes one of the tenant's fixed read slots. An exclusive open holds a slot, so the operating system frees it when
    /// its reader closes it or exits; bytes an abandoned reader left behind are truncated before the slot is used again.
    /// </summary>
    private FileStream AcquireSlot(Guid tenantId)
    {
        var start = RandomNumberGenerator.GetInt32(readSlots);
        for (var attempt = 0; attempt < readSlots; attempt++)
        {
            var index = (start + attempt) % readSlots;
            FileStream slot;
            try
            {
                slot = new FileStream(paths.ReadSlot(tenantId, index), new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            }
            catch (IOException)
            {
                // Another reader holds this slot.
                continue;
            }
            try
            {
                paths.ReadSlot(tenantId, index);
                slot.SetLength(0);
                return slot;
            }
            catch
            {
                slot.Dispose();
                throw;
            }
        }
        throw new FileStorageException(FileStorageError.Busy);
    }

    // FileShare.None is a mandatory share mode on Windows but a non-blocking advisory lock on Linux and macOS, which the
    // runtime skips silently when System.IO.DisableFileLocking is set or the filesystem refuses locks. The lease, the read
    // slots and the staging exclusion all rest on it, so a root where a second exclusive open succeeds is refused.
    private static void RequireExclusiveLocking(ConfinedPaths paths)
    {
        try
        {
            var probe = paths.Probe(Guid.NewGuid());
            using var held = new FileStream(probe, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.DeleteOnClose
            });
            try
            {
                using var second = new FileStream(probe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return;
            }
        }
        catch (Exception exception) when (ConfinedPaths.IsIo(exception))
        {
            throw new FileStorageException(FileStorageError.StorageUnavailable);
        }
        throw new FileStorageException(FileStorageError.LockingUnavailable);
    }

    private ValueTask ObserveAsync(FileStorageStage stage, Guid id, CancellationToken cancellationToken) =>
        observer?.ReachedAsync(stage, id, cancellationToken) ?? ValueTask.CompletedTask;

    private sealed class Session(FileSystemBlobStore store, TenantContext tenant, FileStream lease) : IFileBlobSession
    {
        private bool disposed;

        public Task<IFileBlobStaging> StageAsync(Guid revisionId, CancellationToken cancellationToken)
        {
            var tenantId = Require();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(store.paths.File(tenantId, revisionId, ".blob"))) throw new FileStorageException(FileStorageError.Conflict);
                var stream = new FileStream(store.paths.File(tenantId, revisionId, ".pending"), new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                return Task.FromResult<IFileBlobStaging>(new Staging(store, tenant, tenantId, revisionId, stream));
            }
            catch (Exception exception) when (ConfinedPaths.IsIo(exception))
            {
                throw new FileStorageException(FileStorageError.StorageUnavailable);
            }
        }

        public async Task PublishAsync(IFileBlobStaging staging, CancellationToken cancellationToken)
        {
            var tenantId = Require();
            if (staging is not Staging owned || !ReferenceEquals(owned.Store, store) || owned.TenantId != tenantId)
                throw new ArgumentException("Publish a staging file this store created for the session's tenant.", nameof(staging));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // This session's lease is held from before the staging file is released until it is renamed, so recovery
                // never finds it released and unpublished.
                await owned.ReleaseWrittenAsync().ConfigureAwait(false);
                Require();
                // Revalidate both names after user stream and observer callbacks, before the only publication of bytes.
                var source = store.paths.File(tenantId, owned.RevisionId, ".pending");
                var destination = store.paths.File(tenantId, owned.RevisionId, ".blob");
                File.Move(source, destination, overwrite: false);
                await store.ObserveAsync(FileStorageStage.ContentRenamed, owned.RevisionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ConfinedPaths.IsIo(exception))
            {
                throw new FileStorageException(FileStorageError.StorageUnavailable);
            }
        }

        public async Task WriteAsync(Guid revisionId, Stream source, BlobIntegrity expected, CancellationToken cancellationToken)
        {
            Require();
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(expected);
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.CanRead || expected.Length > store.MaximumLength) throw new FileStorageException(FileStorageError.InvalidLength);
            await using var staging = await StageAsync(revisionId, cancellationToken).ConfigureAwait(false);
            await staging.WriteAsync(source, expected, cancellationToken).ConfigureAwait(false);
            await PublishAsync(staging, cancellationToken).ConfigureAwait(false);
        }

        public Task<Stream> ReadAsync(Guid revisionId, BlobIntegrity expected, BlobRange? range, CancellationToken cancellationToken)
        {
            Require();
            return store.ReadAsync(tenant, revisionId, expected, range, cancellationToken);
        }

        public Task<bool> DiscardAsync(Guid revisionId, CancellationToken cancellationToken)
        {
            var tenantId = Require();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var staging = store.paths.File(tenantId, revisionId, ".pending");
                if (File.Exists(staging) && !TryClaim(staging)) return Task.FromResult(false);
                File.Delete(store.paths.File(tenantId, revisionId, ".blob"));
                return Task.FromResult(true);
            }
            catch (Exception exception) when (ConfinedPaths.IsIo(exception))
            {
                throw new FileStorageException(FileStorageError.StorageUnavailable);
            }
        }

        public Task RecoverReadSnapshotsAsync(CancellationToken cancellationToken)
        {
            var tenantId = Require();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Every index a deployment may configure, so slots above a reduced count are emptied too.
                for (var index = 0; index < MaxReadSlots; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var location = store.paths.ReadSlot(tenantId, index);
                    if (!File.Exists(location)) continue;
                    try
                    {
                        using var abandoned = new FileStream(location, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        abandoned.SetLength(0);
                    }
                    catch (IOException)
                    {
                        // A reader still holds this slot, or it was removed during the scan.
                    }
                }
                return Task.CompletedTask;
            }
            catch (Exception exception) when (ConfinedPaths.IsIo(exception))
            {
                throw new FileStorageException(FileStorageError.StorageUnavailable);
            }
        }

        // An exclusive open succeeds only when no live upload holds the staging file; closing it then removes the file.
        private static bool TryClaim(string location)
        {
            try
            {
                using var abandoned = new FileStream(location, new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.DeleteOnClose
                });
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private Guid Require()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return FileTenant.Require(tenant);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Staging(FileSystemBlobStore store, TenantContext tenant, Guid tenantId, Guid revisionId, FileStream stream) : IFileBlobStaging
    {
        private bool attempted;
        private bool written;
        private bool released;

        internal FileSystemBlobStore Store => store;
        internal Guid TenantId => tenantId;
        public Guid RevisionId => revisionId;

        public async Task WriteAsync(Stream source, BlobIntegrity expected, CancellationToken cancellationToken)
        {
            Require();
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(expected);
            cancellationToken.ThrowIfCancellationRequested();
            if (attempted) throw new InvalidOperationException("A staging file is written once.");
            attempted = true;
            if (!source.CanRead || expected.Length > store.MaximumLength) throw new FileStorageException(FileStorageError.InvalidLength);
            try
            {
                await CopyVerifiedAsync(source, stream, expected, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (ConfinedPaths.IsIo(exception))
            {
                throw new FileStorageException(FileStorageError.StorageUnavailable);
            }
            written = true;
            await store.ObserveAsync(FileStorageStage.ContentFlushed, revisionId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Require();
        }

        internal async ValueTask ReleaseWrittenAsync()
        {
            Require();
            if (!written) throw new InvalidOperationException("Only a completely written staging file is published.");
            released = true;
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        private void Require()
        {
            ObjectDisposedException.ThrowIf(released, this);
            FileTenant.Require(tenant);
        }

        public async ValueTask DisposeAsync()
        {
            if (released) return;
            released = true;
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CopyVerifiedAsync(Stream source, Stream destination, BlobIntegrity expected, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read > expected.Length - length) throw new FileStorageException(FileStorageError.IntegrityMismatch);
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                length += read;
            }
            if (length != expected.Length || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected.Sha256, StringComparison.Ordinal))
                throw new FileStorageException(FileStorageError.IntegrityMismatch);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
