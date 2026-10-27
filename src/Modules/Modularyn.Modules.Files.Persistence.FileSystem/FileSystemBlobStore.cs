using System.Buffers;
using System.Security.Cryptography;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.FileSystem;

/// <summary>A single configured filesystem root, with immutable, conditionally created content per validated tenant.</summary>
public sealed class FileSystemBlobStore : IFileBlobStore
{
    private readonly ConfinedPaths paths;
    private readonly long maximumLength;
    private readonly IFileStorageObserver? observer;

    public FileSystemBlobStore(string root, long maximumLength, IFileStorageObserver? observer = null)
    {
        if (maximumLength < 0) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        paths = new(root);
        this.maximumLength = maximumLength;
        this.observer = observer;
    }

    // Flush(true) and same-directory rename cover process interruption. Portable .NET does not expose directory fsync.
    public BlobCapabilities Capabilities { get; } = new(true, true, false, true, true, false, false);

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
            return ValueTask.FromResult<IFileBlobSession>(new Session(paths, tenant, maximumLength, observer, lease));
        }
        catch (Exception exception)
        {
            lease?.Dispose();
            if (ConfinedPaths.IsIo(exception)) throw new FileStorageException(FileStorageError.StorageUnavailable);
            throw;
        }
    }

    private sealed class Session(ConfinedPaths paths, TenantContext tenant, long maximumLength,
        IFileStorageObserver? observer, FileStream lease) : IFileBlobSession
    {
        private bool disposed;

        public async Task WriteAsync(Guid revisionId, Stream source, BlobIntegrity expected, CancellationToken cancellationToken)
        {
            var tenantId = Require();
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(expected);
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.CanRead || expected.Length > maximumLength) throw new FileStorageException(FileStorageError.InvalidLength);
            try
            {
                var destination = paths.File(tenantId, revisionId, ".blob");
                if (System.IO.File.Exists(destination)) throw new FileStorageException(FileStorageError.Conflict);
                var staging = paths.File(tenantId, revisionId, ".pending");
                await using (var stream = new FileStream(staging, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                }))
                {
                    await CopyVerifiedAsync(source, stream, expected, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                await ObserveAsync(FileStorageStage.ContentFlushed, revisionId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Require();
                // Revalidate both names after user stream and observer callbacks, before the only publication of bytes.
                staging = paths.File(tenantId, revisionId, ".pending");
                destination = paths.File(tenantId, revisionId, ".blob");
                System.IO.File.Move(staging, destination, overwrite: false);
                await ObserveAsync(FileStorageStage.ContentRenamed, revisionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ConfinedPaths.IsIo(exception))
            {
                throw new FileStorageException(FileStorageError.StorageUnavailable);
            }
        }

        public async Task<Stream> ReadAsync(Guid revisionId, BlobIntegrity expected, BlobRange? range, CancellationToken cancellationToken)
        {
            var tenantId = Require();
            ArgumentNullException.ThrowIfNull(expected);
            cancellationToken.ThrowIfCancellationRequested();
            range ??= new(0, expected.Length);
            range.Validate(expected.Length);
            FileStream? snapshot = null;
            try
            {
                var location = paths.File(tenantId, revisionId, ".blob");
                if (!System.IO.File.Exists(location)) throw new FileStorageException(FileStorageError.NotFound);
                await using var source = new FileStream(location, new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                paths.File(tenantId, revisionId, ".blob");
                if (source.Length != expected.Length) throw new FileStorageException(FileStorageError.IntegrityMismatch);
                snapshot = new FileStream(paths.File(tenantId, Guid.NewGuid(), ".read"), new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
                });
                // A private snapshot prevents later source mutation from changing already verified response bytes.
                await CopyVerifiedAsync(source, snapshot, expected, cancellationToken).ConfigureAwait(false);
                await snapshot.FlushAsync(cancellationToken).ConfigureAwait(false);
                Require();
                snapshot.Position = range.Offset;
                return new VerifiedRangeStream(snapshot, range.Length, tenant);
            }
            catch (Exception exception)
            {
                if (snapshot is not null) await snapshot.DisposeAsync().ConfigureAwait(false);
                if (ConfinedPaths.IsIo(exception)) throw new FileStorageException(FileStorageError.StorageUnavailable);
                throw;
            }
        }

        public Task DiscardAsync(Guid revisionId, CancellationToken cancellationToken)
        {
            var tenantId = Require();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                System.IO.File.Delete(paths.File(tenantId, revisionId, ".pending"));
                System.IO.File.Delete(paths.File(tenantId, revisionId, ".blob"));
                return Task.CompletedTask;
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
                foreach (var entry in Directory.EnumerateFiles(paths.Tenant(tenantId), "*.read", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(entry), "N", out var id) || id == Guid.Empty) continue;
                    // Rebuild the path from the parsed identity; never act on an enumerated path directly.
                    var location = paths.File(tenantId, id, ".read");
                    try
                    {
                        using var abandoned = new FileStream(location, new FileStreamOptions
                        {
                            Mode = FileMode.Open, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.DeleteOnClose
                        });
                    }
                    catch (IOException)
                    {
                        // A reader still holds this private snapshot, or it finished and removed it during the scan.
                    }
                }
                return Task.CompletedTask;
            }
            catch (Exception exception) when (ConfinedPaths.IsIo(exception))
            {
                throw new FileStorageException(FileStorageError.StorageUnavailable);
            }
        }

        private Guid Require()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return FileTenant.Require(tenant);
        }

        private ValueTask ObserveAsync(FileStorageStage stage, Guid id, CancellationToken cancellationToken) =>
            observer?.ReachedAsync(stage, id, cancellationToken) ?? ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await lease.DisposeAsync().ConfigureAwait(false);
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
