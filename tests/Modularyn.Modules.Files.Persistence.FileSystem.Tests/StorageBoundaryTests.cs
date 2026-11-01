using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Files.Persistence.EfCore;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Files.Persistence.FileSystem.Tests;

[Collection(FilesPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class StorageBoundaryTests
{
    private static CancellationToken Cancellation => FileDatabase.Cancellation;

    [Fact]
    public async Task Client_names_are_metadata_and_every_operation_stays_in_the_validated_tenant()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenantA = await database.EnterAsync(database.TenantA);
        await using var tenantB = await database.EnterAsync(database.TenantB);
        var outside = database.NewRoot();
        var marker = Path.Combine(outside, "untouched");
        var content = FileDatabase.Bytes(123);
        await File.WriteAllBytesAsync(marker, content, Cancellation);
        // Names that could act as paths, streams or devices are refused before anything is reserved or written.
        foreach (var name in new[] { "../../untouched", "..\\..\\untouched", marker, "name:stream", "CON", "a/b\\c" })
            Assert.Equal(FileStorageError.InvalidName, (await Assert.ThrowsAsync<FileStorageException>(() => database.Service().UploadAsync(tenantA.Context,
                tenantA.Principal, FileDatabase.Request(content, name: name), new MemoryStream(content), Cancellation))).Code);
        Assert.Equal(0, await database.CountAsync("Files", database.TenantA));
        Assert.Equal(0, await database.CountAsync("FileUploads", database.TenantA));
        // Accepted names, and an owner reference shaped like a path, stay metadata and never reach a storage location.
        var names = new[] { "..untouched", "untouched..txt", "re\u0301sume\u0301 (final).pdf", "#1 notes.txt" };
        foreach (var name in names)
        {
            var service = database.Service();
            var request = FileDatabase.Request(content, name: name) with { Owner = "../../untouched" };
            var version = await service.UploadAsync(tenantA.Context, tenantA.Principal, request, new MemoryStream(content), Cancellation);
            await using (var unit = await database.Metadata.BeginAsync(tenantA.Context, Cancellation))
            {
                var file = await unit.FindFileAsync(version.FileId, Cancellation);
                Assert.NotNull(file);
                Assert.Equal(name.Normalize(NormalizationForm.FormC), file.Name);
                Assert.Equal("documents", file.Container);
                Assert.Equal("../../untouched", file.Owner);
            }
            foreach (var refused in new Func<Task>[]
            {
                () => service.ListAsync(tenantB.Context, tenantB.Principal, version.FileId, Cancellation),
                () => service.ListDeletedAsync(tenantB.Context, tenantB.Principal, version.FileId, Cancellation),
                () => service.ReadAsync(tenantB.Context, tenantB.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation),
                () => service.DeleteAsync(tenantB.Context, tenantB.Principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation),
                () => service.RestoreAsync(tenantB.Context, tenantB.Principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation),
                () => service.ReleaseAsync(tenantB.Context, tenantB.Principal, version.FileId, version.VersionId, Cancellation),
                () => service.UploadAsync(tenantB.Context, tenantB.Principal, request with { FileId = version.FileId }, new MemoryStream(content), Cancellation)
            })
                Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(refused)).Code);
            Assert.True(File.Exists(database.Blob(database.TenantA, version.VersionId)));
        }
        Assert.Equal(content, await File.ReadAllBytesAsync(marker, Cancellation));
        Assert.Single(Directory.GetFiles(outside));
        Assert.Equal(names.Length, database.Objects(database.TenantA).Length);
        Assert.Empty(database.Objects(database.TenantB));
        Assert.Equal(0, await database.CountAsync("Files", database.TenantB));
    }

    [Fact]
    public async Task Linked_roots_tenant_directories_and_content_are_refused_including_dangling_links()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var outside = database.NewRoot();
        var linkedRoot = Path.Combine(database.Root, "linked");
        Directory.CreateSymbolicLink(linkedRoot, outside);
        Assert.Equal(FileStorageError.UnsafeLocation, Assert.Throws<FileStorageException>(() => new FileSystemBlobStore(linkedRoot, 1024)).Code);
        var tenantPath = Path.Combine(database.Root, database.TenantA.ToString("N"));
        Directory.CreateSymbolicLink(tenantPath, outside);
        Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(async () =>
            await new FileSystemBlobStore(database.Root, 1024).OpenAsync(tenant.Context, Cancellation))).Code);
        Directory.Delete(tenantPath);
        var content = FileDatabase.Bytes(16);
        var version = await FileDatabase.ReleasedAsync(database.Service(), tenant, await database.Service().UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(content), new MemoryStream(content), Cancellation));
        var location = database.Blob(database.TenantA, version.VersionId);
        var external = Path.Combine(outside, "original");
        File.Move(location, external);
        foreach (var target in new[] { external, Path.Combine(outside, "absent") })
        {
            File.CreateSymbolicLink(location, target);
            Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() =>
                database.Service().ReadAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation))).Code);
            File.Delete(location);
        }
        Assert.Equal(content, await File.ReadAllBytesAsync(external, Cancellation));
        Assert.False(File.Exists(Path.Combine(outside, "absent")));
        File.Move(external, location);
        Directory.Delete(linkedRoot);
    }

    [Fact]
    public async Task Linked_staging_and_lease_files_are_refused_without_touching_their_targets()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var marker = Path.Combine(database.NewRoot(), "marker");
        var content = FileDatabase.Bytes(32);
        await File.WriteAllBytesAsync(marker, content, Cancellation);
        var stop = new StopAt(FileStorageStage.IntentCommitted);
        await Assert.ThrowsAsync<StorageInterruption>(() => database.Service(stop).UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(content), new MemoryStream(content), Cancellation));
        var staging = Path.ChangeExtension(database.Blob(database.TenantA, stop.VersionId), ".pending");
        File.CreateSymbolicLink(staging, marker);
        Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() =>
            database.Service().RecoverAsync(tenant.Context, Cancellation))).Code);
        Assert.Equal(1, await database.CountAsync("FileUploads", database.TenantA));
        File.Delete(staging);
        Assert.Equal(1, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        var lease = Path.Combine(database.Root, database.TenantA.ToString("N"), ".lease");
        File.Delete(lease);
        File.CreateSymbolicLink(lease, marker);
        Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(async () =>
            await new FileSystemBlobStore(database.Root, 1024).OpenAsync(tenant.Context, Cancellation))).Code);
        Assert.Equal(content, await File.ReadAllBytesAsync(marker, Cancellation));
        File.Delete(lease);
    }

    [Fact]
    public async Task Short_long_oversized_and_cancelled_streams_never_publish_a_partial_version()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var bytes = FileDatabase.Bytes(64);
        foreach (var length in new[] { 63, 65 })
        {
            var request = FileDatabase.Request(bytes) with { Length = length };
            Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
                database.Service().UploadAsync(tenant.Context, tenant.Principal, request, new MemoryStream(bytes), Cancellation))).Code);
            Assert.Equal(1, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        }
        // A declaration above the store's limit is refused before any intent exists, so recovery has nothing to do.
        Assert.Equal(FileStorageError.InvalidLength, (await Assert.ThrowsAsync<FileStorageException>(() =>
            database.Service(maximumLength: 63).UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(bytes), new MemoryStream(bytes),
                Cancellation))).Code);
        Assert.Equal(0, await database.CountAsync("FileUploads", database.TenantA));
        Assert.Equal(0, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Cancellation))
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.Service().UploadAsync(tenant.Context, tenant.Principal,
                FileDatabase.Request(bytes), new MemoryStream(bytes), cancelled.Token));
            Assert.Equal(0, await database.CountAsync("FileUploads", database.TenantA));
        }
        // Cancelled after the bytes read for type detection, while the staged content is being written.
        var large = FileDatabase.Bytes(4096);
        using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Cancellation))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.Service().UploadAsync(tenant.Context, tenant.Principal,
                FileDatabase.Request(large), new CancellingStream(large, 2048, cancelled), cancelled.Token));
            Assert.Equal(1, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        }
        Assert.Equal(0, await database.CountAsync("FileRevisions", database.TenantA));
        Assert.Empty(database.Objects(database.TenantA));
        var complete = await FileDatabase.ReleasedAsync(database.Service(), tenant, await database.Service(maximumLength: 64).UploadAsync(tenant.Context,
            tenant.Principal, FileDatabase.Request(bytes), new MemoryStream(bytes), Cancellation));
        Assert.Equal(bytes, await FileDatabase.ReadAllAsync(await database.Service().ReadAsync(tenant.Context, tenant.Principal, complete.FileId,
            complete.VersionId, cancellationToken: Cancellation)));
    }

    [Fact]
    public async Task Corruption_refuses_restore_and_a_verified_snapshot_does_not_change_with_the_original()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var bytes = FileDatabase.Bytes(512);
        var service = database.Service();
        var version = await FileDatabase.ReleasedAsync(service, tenant,
            await service.UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(bytes), new MemoryStream(bytes), Cancellation));
        var snapshot = await service.ReadAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation);
        var damaged = FileDatabase.Bytes(512, 2);
        await File.WriteAllBytesAsync(database.Blob(database.TenantA, version.VersionId), damaged, Cancellation);
        Assert.Equal(bytes, await FileDatabase.ReadAllAsync(snapshot));
        var deleted = await service.DeleteAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation);
        Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
            service.RestoreAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, deleted.ConcurrencyVersion, Cancellation))).Code);
        await using (var unit = await database.Metadata.BeginAsync(tenant.Context, Cancellation))
        {
            var row = await unit.FindRevisionAsync(version.FileId, version.VersionId, true, Cancellation);
            Assert.True(row!.IsDeleted);
            Assert.Equal(deleted.ConcurrencyVersion, row.Version);
            Assert.Equal(version.Sha256, row.Sha256);
        }
        await File.WriteAllBytesAsync(database.Blob(database.TenantA, version.VersionId), bytes[..^1], Cancellation);
        Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
            service.RestoreAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, deleted.ConcurrencyVersion, Cancellation))).Code);
        await File.WriteAllBytesAsync(database.Blob(database.TenantA, version.VersionId), bytes, Cancellation);
        var restored = await service.RestoreAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, deleted.ConcurrencyVersion, Cancellation);
        Assert.Equal(FileStorageError.Conflict, (await Assert.ThrowsAsync<FileStorageException>(() =>
            service.DeleteAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation))).Code);
        Assert.False(restored.IsDeleted);
    }

    [Fact]
    public async Task Recovery_preserves_committed_and_deleted_content_after_a_lost_response()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var bytes = FileDatabase.Bytes(20);
        var version = await database.Service().UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(bytes), new MemoryStream(bytes), Cancellation);
        var stop = new StopAt(FileStorageStage.MetadataCommitted);
        await Assert.ThrowsAsync<StorageInterruption>(() => database.Service(stop).UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(bytes, version.FileId), new MemoryStream(bytes), Cancellation));
        Assert.Equal(0, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        var committed = Assert.Single(await database.Service().ListAsync(tenant.Context, tenant.Principal, version.FileId, Cancellation),
            row => row.VersionId == stop.VersionId);
        var deleted = await database.Service().DeleteAsync(tenant.Context, tenant.Principal, version.FileId, committed.VersionId, committed.ConcurrencyVersion,
            Cancellation);
        // A retained intent must not make reconciliation discard a referenced, soft-deleted version.
        await using (var unit = await database.Metadata.BeginAsync(tenant.Context, Cancellation))
        {
            var otherFileId = Guid.NewGuid();
            unit.Add(new FileAsset { TenantId = database.TenantA, Id = otherFileId, Name = "other.bin", Container = "documents",
                Owner = "file-owner", CreatedBy = "file-owner", CreatedAt = committed.CreatedAt });
            unit.Add(new FileUpload { TenantId = database.TenantA, Id = committed.VersionId, FileId = otherFileId,
                Length = committed.Length, Sha256 = committed.Sha256, ContentType = committed.ContentType, CreatedAt = committed.CreatedAt,
                PropagatedContext = PropagatedTenantContext.Capture(tenant.Context, database.Clock).ToJson() });
            await unit.CommitAsync(Cancellation);
        }
        Assert.Equal(1, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        var restored = await database.Service().RestoreAsync(tenant.Context, tenant.Principal, version.FileId, committed.VersionId, deleted.ConcurrencyVersion,
            Cancellation);
        restored = await FileDatabase.ReleasedAsync(database.Service(), tenant, restored);
        Assert.Equal(bytes, await FileDatabase.ReadAllAsync(await database.Service().ReadAsync(tenant.Context, tenant.Principal,
            restored.FileId, restored.VersionId, cancellationToken: Cancellation)));
        Assert.Equal(2, database.Objects(database.TenantA).Length);
    }

    [Fact]
    public async Task Tenant_lease_excludes_competing_writers_and_recovery_without_blocking_another_tenant()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenantA = await database.EnterAsync(database.TenantA);
        await using var tenantB = await database.EnterAsync(database.TenantB);
        var store = new FileSystemBlobStore(database.Root, 1024);
        await using (var held = await store.OpenAsync(tenantA.Context, Cancellation))
        {
            Assert.Equal(FileStorageError.Busy, (await Assert.ThrowsAsync<FileStorageException>(async () =>
                await new FileSystemBlobStore(database.Root, 1024).OpenAsync(tenantA.Context, Cancellation))).Code);
            Assert.Equal(FileStorageError.Busy, (await Assert.ThrowsAsync<FileStorageException>(() =>
                database.Service().RecoverAsync(tenantA.Context, Cancellation))).Code);
            await using var other = await store.OpenAsync(tenantB.Context, Cancellation);
        }
        Assert.Equal(0, await database.Service().RecoverAsync(tenantA.Context, Cancellation));
    }

    [Fact]
    public async Task Nonseekable_input_is_streamed_without_ownership_transfer_and_existing_objects_are_never_replaced()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var bytes = FileDatabase.Bytes(300000, 7);
        using var input = new NonseekableInput(bytes);
        var version = await FileDatabase.ReleasedAsync(database.Service(), tenant,
            await database.Service().UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(bytes), input, Cancellation));
        Assert.True(input.CanRead);
        Assert.True(input.Reads > 1);
        Assert.Equal(bytes, await FileDatabase.ReadAllAsync(await database.Service().ReadAsync(tenant.Context, tenant.Principal,
            version.FileId, version.VersionId, cancellationToken: Cancellation)));
        await using var session = await new FileSystemBlobStore(database.Root, 1024 * 1024).OpenAsync(tenant.Context, Cancellation);
        var replacement = FileDatabase.Bytes(12, 9);
        Assert.Equal(FileStorageError.Conflict, (await Assert.ThrowsAsync<FileStorageException>(() => session.WriteAsync(version.VersionId,
            new MemoryStream(replacement), new(replacement.LongLength, FileDatabase.Digest(replacement)), Cancellation))).Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(database.Blob(database.TenantA, version.VersionId), Cancellation));
        Assert.Single(database.Objects(database.TenantA));
    }

    [Fact]
    public async Task Recovery_cleans_abandoned_read_snapshots_and_retains_active_streams()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var bytes = FileDatabase.Bytes(30);
        var version = await FileDatabase.ReleasedAsync(database.Service(), tenant,
            await database.Service().UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(bytes), new MemoryStream(bytes), Cancellation));
        var read = await database.Service().ReadAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation);
        // Every other slot is left with bytes and no holder, as a reader that ended without disposing its stream leaves it.
        var held = Enumerable.Range(0, FileSystemBlobStore.DefaultReadSlots).Single(slot => FileDatabase.IsHeld(database.ReadSlot(database.TenantA, slot)));
        foreach (var slot in Enumerable.Range(0, FileSystemBlobStore.DefaultReadSlots).Where(slot => slot != held))
            await File.WriteAllBytesAsync(database.ReadSlot(database.TenantA, slot), bytes, Cancellation);
        Assert.Equal(0, await database.Service().RecoverAsync(tenant.Context, Cancellation));
        Assert.Equal(bytes.Length, new FileInfo(database.ReadSlot(database.TenantA, held)).Length);
        Assert.Equal(FileSystemBlobStore.DefaultReadSlots - 1, database.ReadSlotLengths(database.TenantA).Count(length => length == 0));
        Assert.Equal(bytes, await FileDatabase.ReadAllAsync(read));
        Assert.All(database.ReadSlotLengths(database.TenantA), length => Assert.Equal(0, length));
        Assert.Single(database.Objects(database.TenantA));
    }

    [Fact]
    public async Task Ending_a_context_refuses_further_stream_reads_and_storage_sessions()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var bytes = FileDatabase.Bytes(5);
        var version = await FileDatabase.ReleasedAsync(database.Service(), tenant,
            await database.Service().UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(bytes), new MemoryStream(bytes), Cancellation));
        await using var read = await database.Service().ReadAsync(tenant.Context, tenant.Principal, version.FileId, version.VersionId,
            cancellationToken: Cancellation);
        await tenant.DisposeAsync();
        Assert.Equal(FileStorageError.InvalidScope, (await Assert.ThrowsAsync<FileStorageException>(async () =>
            await read.Content.ReadExactlyAsync(new byte[1], Cancellation))).Code);
        Assert.Equal(FileStorageError.InvalidScope, (await Assert.ThrowsAsync<FileStorageException>(() =>
            database.Service().ListAsync(tenant.Context, tenant.Principal, version.FileId, Cancellation))).Code);
        Assert.Equal(FileStorageError.InvalidScope, (await Assert.ThrowsAsync<FileStorageException>(async () =>
            await new FileSystemBlobStore(database.Root, 1024).OpenAsync(tenant.Context, Cancellation))).Code);
    }

    [Fact]
    public async Task Recovery_revalidates_propagated_context_and_does_not_run_after_membership_is_removed()
    {
        await using var database = await FileDatabase.CreateAsync();
        var runner = database.Services.GetRequiredService<TenantWorkRunner>();
        var tenant = await database.EnterAsync(database.TenantA);
        var bytes = FileDatabase.Bytes(20);
        var stop = new StopAt(FileStorageStage.ContentFlushed);
        await Assert.ThrowsAsync<StorageInterruption>(() => database.Service(stop).UploadAsync(tenant.Context, tenant.Principal,
            FileDatabase.Request(bytes), new MemoryStream(bytes), Cancellation));
        PropagatedTenantContext propagated;
        await using (var unit = await database.Metadata.BeginAsync(tenant.Context, Cancellation))
        {
            var upload = Assert.Single(await unit.ListUploadsAsync(Cancellation));
            Assert.True(PropagatedTenantContext.TryParse(upload.PropagatedContext, out var parsed));
            propagated = parsed!;
        }
        await tenant.DisposeAsync();
        var recovered = await database.Service().RecoverAsync(propagated, runner, Cancellation);
        Assert.Equal(TenantWorkDisposition.Completed, recovered.Disposition);
        Assert.False(recovered.Context!.IsActive);
        await using (var next = await database.EnterAsync(database.TenantA))
            await Assert.ThrowsAsync<StorageInterruption>(() => database.Service(stop).UploadAsync(next.Context, next.Principal,
                FileDatabase.Request(bytes), new MemoryStream(bytes), Cancellation));
        await database.ExecuteAsync("DELETE FROM " + FileDatabase.Quote(database.IdentitySchema) + ".\"Members\" WHERE \"TenantId\" = @tenant",
            new NpgsqlParameter("tenant", database.TenantA));
        var refused = await database.Service().RecoverAsync(propagated, runner, Cancellation);
        Assert.Equal(TenantWorkDisposition.Denied, refused.Disposition);
        Assert.Equal(1, await database.CountAsync("FileUploads", database.TenantA));
        Assert.Equal(FileStorageError.InvalidScope, (await Assert.ThrowsAsync<FileStorageException>(() =>
            database.Service().RecoverAsync(tenant.Context, Cancellation))).Code);
    }

    [Fact]
    public async Task Files_metadata_keeps_the_existing_filter_concurrency_and_immutability_guards()
    {
        await using var database = await FileDatabase.CreateAsync();
        await using var tenant = await database.EnterAsync(database.TenantA);
        var bytes = FileDatabase.Bytes(8);
        var version = await database.Service().UploadAsync(tenant.Context, tenant.Principal, FileDatabase.Request(bytes), new MemoryStream(bytes), Cancellation);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.FilesModule, tenant.Context, cancellationToken: Cancellation))
        {
            var context = (FilesContext)work.Context;
            Assert.Equal("MPS033", Assert.Throws<PersistenceContractException>(() => context.Revisions.IgnoreQueryFilters().ToQueryString()).Code);
            Assert.Equal("MPS041", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Revisions.ExecuteDeleteAsync(Cancellation))).Code);
            var row = await context.Revisions.SingleAsync(Cancellation);
            row.Sha256 = new string('0', 64);
            await Assert.ThrowsAsync<InvalidOperationException>(() => work.CompleteAsync(Cancellation));
        }
        await using (var first = await database.Metadata.BeginAsync(tenant.Context, Cancellation))
        await using (var second = await database.Metadata.BeginAsync(tenant.Context, Cancellation))
        {
            var one = (await first.FindRevisionAsync(version.FileId, version.VersionId, false, Cancellation))!;
            var two = (await second.FindRevisionAsync(version.FileId, version.VersionId, false, Cancellation))!;
            one.IsDeleted = true;
            await first.CommitAsync(Cancellation);
            two.Quarantine = QuarantineState.Rejected;
            Assert.Equal(FileStorageError.Conflict, (await Assert.ThrowsAsync<FileStorageException>(() => second.CommitAsync(Cancellation))).Code);
        }
        await using var read = await database.Metadata.BeginAsync(tenant.Context, Cancellation);
        var deleted = (await read.FindRevisionAsync(version.FileId, version.VersionId, true, Cancellation))!;
        Assert.Equal(version.Sha256, deleted.Sha256);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(QuarantineState.Pending, deleted.Quarantine);
    }
}

/// <summary>Delivers bytes eight at a time and cancels its token once <paramref name="after"/> bytes have been read.</summary>
internal sealed class CancellingStream(byte[] bytes, int after, CancellationTokenSource cancellation) : MemoryStream(bytes)
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await base.ReadAsync(buffer[..Math.Min(8, buffer.Length)], cancellationToken);
        if (Position >= after) cancellation.Cancel();
        return read;
    }
}

internal sealed class NonseekableInput(byte[] bytes) : MemoryStream(bytes)
{
    internal int Reads { get; private set; }
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Reads++;
        return base.ReadAsync(buffer, cancellationToken);
    }
}
