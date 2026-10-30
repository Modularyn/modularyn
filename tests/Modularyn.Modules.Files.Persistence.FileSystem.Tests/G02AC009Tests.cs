using Modularyn.Modules.Files.Application;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Files.Persistence.EfCore;
using Xunit;

namespace Modularyn.Modules.Files.Persistence.FileSystem.Tests;

[Collection(FilesPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class G02AC009Tests
{
    private static CancellationToken Cancellation => FileDatabase.Cancellation;

    [Fact]
    public async Task AC_009_N01_DigestMismatchPreservesThePriorVersion_Integration()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var service = database.Service();
        var original = FileDatabase.Bytes(512);
        var previous = await FileDatabase.ReleasedAsync(service, tenant,
            await service.UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(original), new MemoryStream(original), Cancellation));
        var different = FileDatabase.Bytes(768, 3);
        var failure = await Assert.ThrowsAsync<FileStorageException>(() => service.UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(different, previous.FileId) with { Sha256 = previous.Sha256 }, new MemoryStream(different), Cancellation));
        Assert.Equal(FileStorageError.IntegrityMismatch, failure.Code);
        Assert.Equal(1, await database.CountAsync("FileRevisions", database.TenantA));
        Assert.Equal(1, await database.CountAsync("FileUploads", database.TenantA));
        Assert.Equal(previous, Assert.Single(await service.ListAsync(tenant.Context, tenant.Principal, previous.FileId, Cancellation)));
        Assert.Equal(original, await FileDatabase.ReadAllAsync(await service.ReadAsync(tenant.Context, tenant.Principal, previous.FileId, previous.VersionId,
            cancellationToken: Cancellation)));
        Assert.Equal(1, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        Assert.Equal(0, await database.CountAsync("FileUploads", database.TenantA));
        Assert.Single(database.Objects(database.TenantA));
        Assert.Equal(original, await File.ReadAllBytesAsync(database.Blob(database.TenantA, previous.VersionId), Cancellation));
    }

    [Fact]
    public async Task AC_009_N01_DigestMismatchPreservesThePriorVersion_Contract()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        IFiles files = database.Service();
        var content = FileDatabase.Bytes(512);
        var version = await FileDatabase.ReleasedAsync(files, tenant,
            await files.UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(content), new MemoryStream(content), Cancellation));
        var request = FileDatabase.Request(content, version.FileId) with { Sha256 = new string('0', 64) };
        Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
            files.UploadAsync(tenant.Context, tenant.Principal, request, new MemoryStream(content), Cancellation))).Code);
        Assert.Equal(version, Assert.Single(await files.ListAsync(tenant.Context, tenant.Principal, version.FileId, Cancellation)));
        // Corruption outside the requested interval must still be found before returning any stream.
        content[^1] ^= 1;
        await File.WriteAllBytesAsync(database.Blob(database.TenantA, version.VersionId), content, Cancellation);
        Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
            files.ReadAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, new(0, 1), Cancellation))).Code);
        Assert.Equal(version.Sha256, Assert.Single(await files.ListAsync(tenant.Context, tenant.Principal, version.FileId, Cancellation)).Sha256);
        // The refused read keeps no copy: every read slot it used is empty again.
        Assert.All(database.ReadSlotLengths(database.TenantA), length => Assert.Equal(0, length));
    }

    [Fact]
    public async Task AC_009_P01_ImmutableVersionsRetainIdentityLengthAndDigest_Integration()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var service = database.Service();
        var firstBytes = FileDatabase.Bytes(512);
        var secondBytes = FileDatabase.Bytes(768, 2);
        var first = await service.UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(firstBytes), new MemoryStream(firstBytes), Cancellation);
        var second = await service.UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(secondBytes, first.FileId), new MemoryStream(secondBytes),
            Cancellation);
        Assert.Equal(first.FileId, second.FileId);
        Assert.NotEqual(first.VersionId, second.VersionId);
        Assert.Equal(512, first.Length);
        Assert.Equal(768, second.Length);
        Assert.Equal(FileDatabase.Digest(firstBytes), first.Sha256);
        Assert.Equal(FileDatabase.Digest(secondBytes), second.Sha256);
        Assert.Equal(FileQuarantineState.Pending, first.Quarantine);
        Assert.Equal(FileQuarantineState.Pending, second.Quarantine);
        Assert.Equal(0, first.CreatedAt.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(FileTime.Normalize(database.Clock.GetUtcNow()), first.CreatedAt);
        first = await FileDatabase.ReleasedAsync(service, tenant, first);
        second = await FileDatabase.ReleasedAsync(service, tenant, second);
        // Reconstruct both adapters so the observations come from persisted state.
        service = database.Service();
        var listed = await service.ListAsync(tenant.Context, tenant.Principal, first.FileId, Cancellation);
        Assert.Contains(first, listed);
        Assert.Contains(second, listed);
        Assert.Equal(firstBytes, await FileDatabase.ReadAllAsync(await service.ReadAsync(tenant.Context, tenant.Principal, first.FileId, first.VersionId,
            cancellationToken: Cancellation)));
        Assert.Equal(secondBytes, await FileDatabase.ReadAllAsync(await service.ReadAsync(tenant.Context, tenant.Principal, second.FileId, second.VersionId,
            cancellationToken: Cancellation)));
        Assert.Equal(2, await database.CountAsync("FileRevisions", database.TenantA));
        Assert.Equal(0, await database.CountAsync("FileUploads", database.TenantA));
        Assert.Equal(2, database.Objects(database.TenantA).Length);
    }

    [Fact]
    public async Task AC_009_P01_ImmutableVersionsRetainIdentityLengthAndDigest_Contract()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        IFiles files = database.Service();
        var content = FileDatabase.Bytes(768);
        var version = await FileDatabase.ReleasedAsync(files, tenant,
            await files.UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(content), new MemoryStream(content), Cancellation));
        foreach (var range in new[] { new FileReadRange(0, 768), new(0, 1), new(767, 1), new(20, 100), new(768, 0) })
        {
            var read = await files.ReadAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, range, Cancellation);
            Assert.Equal(version, read.Version);
            Assert.Equal(range.Length, read.Content.Length);
            Assert.False(read.Content.CanSeek);
            Assert.False(read.Content.CanWrite);
            Assert.Equal(content.Skip((int)range.Offset).Take((int)range.Length).ToArray(), await FileDatabase.ReadAllAsync(read));
        }
        foreach (var range in new[] { new FileReadRange(-1, 1), new(0, -1), new(769, 0), new(767, 2), new(long.MaxValue, long.MaxValue) })
            Assert.Equal(FileStorageError.InvalidRange, (await Assert.ThrowsAsync<FileStorageException>(() =>
                files.ReadAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, range, Cancellation))).Code);
        var empty = await FileDatabase.ReleasedAsync(files, tenant,
            await files.UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request([], version.FileId), new MemoryStream(), Cancellation));
        Assert.Empty(await FileDatabase.ReadAllAsync(await files.ReadAsync(tenant.Context, tenant.Principal, empty.FileId, empty.VersionId,
            cancellationToken: Cancellation)));
        Assert.Equal(FileDatabase.Digest([]), empty.Sha256);
    }

    [Fact]
    public async Task AC_009_R01_InterruptedWritesAndRestorePreserveVersionIntegrity_Integration()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var content = FileDatabase.Bytes(512);
        var first = await FileDatabase.ReleasedAsync(database.Service(), tenant, await database.Service().UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(content), new MemoryStream(content), Cancellation));
        foreach (var stage in new[] { FileStorageStage.IntentCommitted, FileStorageStage.ContentFlushed, FileStorageStage.ContentRenamed })
        {
            var fault = new StopAt(stage);
            await Assert.ThrowsAsync<StorageInterruption>(() => database.Service(fault).UploadAsync(tenant.Context, tenant.Principal,
                FileDatabase.Request(content, first.FileId), new MemoryStream(content), Cancellation));
            Assert.NotEqual(Guid.Empty, fault.VersionId);
            Assert.Equal(1, await database.CountAsync("FileRevisions", database.TenantA));
            Assert.Equal(1, await database.CountAsync("FileUploads", database.TenantA));
            Assert.Equal(stage == FileStorageStage.ContentRenamed, File.Exists(database.Blob(database.TenantA, fault.VersionId)));
            Assert.Equal(1, await database.Service().RecoverAsync(tenant.Context, Cancellation));
            Assert.Equal(0, await database.Service().RecoverAsync(tenant.Context, Cancellation));
            Assert.Single(database.Objects(database.TenantA));
            Assert.Equal(content, await FileDatabase.ReadAllAsync(await database.Service().ReadAsync(tenant.Context, tenant.Principal, first.FileId,
                first.VersionId, cancellationToken: Cancellation)));
        }
        // The stream fails after the bytes read for type detection, while the staged content is being written.
        var large = FileDatabase.Bytes(4096);
        await Assert.ThrowsAsync<StorageInterruption>(() => database.Service().UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(large, first.FileId), new InterruptedStream(large, 2048), Cancellation));
        Assert.Equal(1, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        Assert.Single(database.Objects(database.TenantA));
        var deleted = await database.Service().DeleteAsync(tenant.Context, tenant.Principal, first.FileId, first.VersionId, first.ConcurrencyVersion, Cancellation);
        Assert.True(deleted.IsDeleted);
        Assert.Empty(await database.Service().ListAsync(tenant.Context, tenant.Principal, first.FileId, Cancellation));
        var retained = Assert.Single(await database.Service().ListDeletedAsync(tenant.Context, tenant.Principal, first.FileId, Cancellation));
        Assert.Equal(deleted, retained);
        Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() =>
            database.Service().ReadAsync(tenant.Context, tenant.Principal, first.FileId, first.VersionId, cancellationToken: Cancellation))).Code);
        Assert.Equal(content, await File.ReadAllBytesAsync(database.Blob(database.TenantA, first.VersionId), Cancellation));
        var restored = await database.Service().RestoreAsync(tenant.Context, tenant.Principal, first.FileId, first.VersionId, retained.ConcurrencyVersion,
            Cancellation);
        Assert.False(restored.IsDeleted);
        Assert.Equal(first.Sha256, restored.Sha256);
        Assert.Equal(first.CreatedAt, restored.CreatedAt);
        Assert.Empty(await database.Service().ListDeletedAsync(tenant.Context, tenant.Principal, first.FileId, Cancellation));
        Assert.Equal(content, await FileDatabase.ReadAllAsync(await database.Service().ReadAsync(tenant.Context, tenant.Principal, first.FileId,
            first.VersionId, cancellationToken: Cancellation)));
    }

    [Fact]
    public async Task AC_009_R01_InterruptedWritesAndRestorePreserveVersionIntegrity_Recovery()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var content = FileDatabase.Bytes(512);
        var first = await FileDatabase.ReleasedAsync(database.Service(), tenant, await database.Service().UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(content), new MemoryStream(content), Cancellation));
        var next = FileDatabase.Bytes(768, 3);
        var second = await FileDatabase.ReleasedAsync(database.Service(), tenant, await database.Service().UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(next, first.FileId), new MemoryStream(next), Cancellation));
        // Quiesced, coordinated backup: capture metadata and verified bytes under the provider's tenant lease.
        FileAsset asset;
        IReadOnlyList<FileRevision> revisions;
        var backup = new Dictionary<Guid, byte[]>();
        await using (var storage = await new FileSystemBlobStore(database.Root, 1024 * 1024).OpenAsync(tenant.Context, Cancellation))
        await using (var metadata = await database.Metadata.BeginAsync(tenant.Context, Cancellation))
        {
            asset = (await metadata.FindFileAsync(first.FileId, Cancellation))!;
            revisions = await metadata.ListRevisionsAsync(first.FileId, Cancellation);
            foreach (var revision in revisions)
            {
                await using var read = await storage.ReadAsync(revision.Id, new(revision.Length, revision.Sha256), null, Cancellation);
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy, Cancellation);
                backup.Add(revision.Id, copy.ToArray());
            }
        }
        var fault = new StopAt(FileStorageStage.ContentRenamed);
        await Assert.ThrowsAsync<StorageInterruption>(() => database.Service(fault).UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(content, first.FileId), new MemoryStream(content), Cancellation));
        Assert.Equal(1, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        // Restore into another migrated PostgreSQL schema and another root, preserving durable identities.
        var recoveryModule = await database.NewRecoveryModuleAsync();
        var recoveryMetadata = new FileMetadataStore(recoveryModule);
        var recoveryBlobs = new FileSystemBlobStore(database.NewRoot(), 1024 * 1024);
        await using (var storage = await recoveryBlobs.OpenAsync(tenant.Context, Cancellation))
        await using (var metadata = await recoveryMetadata.BeginAsync(tenant.Context, Cancellation))
        {
            metadata.Add(asset);
            foreach (var revision in revisions)
            {
                await storage.WriteAsync(revision.Id, new MemoryStream(backup[revision.Id]), new(revision.Length, revision.Sha256), Cancellation);
                metadata.Add(revision);
            }
            await metadata.CommitAsync(Cancellation);
        }
        var recovered = database.Service(recoveryMetadata, recoveryBlobs);
        var listed = await recovered.ListAsync(tenant.Context, tenant.Principal, first.FileId, Cancellation);
        Assert.Equal(2, listed.Count);
        foreach (var version in new[] { first, second })
        {
            var restored = Assert.Single(listed, row => row.VersionId == version.VersionId);
            Assert.Equal(version.FileId, restored.FileId);
            Assert.Equal(version.Length, restored.Length);
            Assert.Equal(version.Sha256, restored.Sha256);
            Assert.Equal(version.CreatedAt, restored.CreatedAt);
            Assert.Equal(backup[version.VersionId], await FileDatabase.ReadAllAsync(await recovered.ReadAsync(
                tenant.Context, tenant.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation)));
        }
        Assert.Equal(0, await recovered.RecoverAsync(tenant.Context, Cancellation));
    }
}

internal sealed class StorageInterruption : Exception { }

internal sealed class StopAt(FileStorageStage stop) : IFileStorageObserver
{
    internal Guid VersionId { get; private set; }
    public ValueTask ReachedAsync(FileStorageStage stage, Guid revisionId, CancellationToken cancellationToken)
    {
        VersionId = revisionId;
        if (stage == stop) throw new StorageInterruption();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Delivers its first <paramref name="before"/> bytes, at most 32 per read, and then fails.</summary>
internal sealed class InterruptedStream(byte[] content, int before) : MemoryStream(content)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position >= before) throw new StorageInterruption();
        return base.ReadAsync(buffer[..(int)Math.Min(32, Math.Min(buffer.Length, before - Position))], cancellationToken);
    }
}
