using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

/// <summary>Concurrency, quotas and recovery of temporary storage, over real PostgreSQL, the filesystem provider and the evaluator.</summary>
[Collection(FilesPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class FileCapacityTests
{
    private static CancellationToken Cancellation => FilesHost.Cancellation;

    private static async Task<FileVersionInfo> ReleasedAsync(FileService files, Member member, byte[] content, Guid? fileId = null)
    {
        var version = await files.UploadAsync(member.Context, member.Principal, FilesHost.Request(content, fileId), new MemoryStream(content), Cancellation);
        return await files.ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId, Cancellation);
    }

    private static async Task<Member> AuthorAsync(FilesHost host, Tenant tenant, string name)
    {
        var user = await host.MemberAsync(tenant, name);
        await host.GrantAsync(tenant, user, FilePermissions.Release);
        return await host.EnterAsync(user, tenant.Id);
    }

    [Fact]
    public async Task TM_G02_045_Reads_deletes_restores_recovery_and_other_uploads_proceed_while_an_upload_streams()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        await using var member = await AuthorAsync(host, tenant, "author-1");
        var files = host.Files(release: new TestReleasePolicy());
        var existing = FilesHost.Text(100, 'e');
        var version = await ReleasedAsync(files, member, existing);
        var body = FilesHost.Text(4096, 's');
        var stream = new PausedStream(body, 2048);
        var upload = files.UploadAsync(member.Context, member.Principal, FilesHost.Request(body, name: "streaming.txt"), stream, Cancellation);
        await stream.Reached.WaitAsync(Cancellation);

        // The streaming upload holds no tenant lease: reads run side by side, and a delete, a restore and another upload complete.
        var first = await files.ReadAsync(member.Context, member.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation);
        var second = await files.ReadAsync(member.Context, member.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation);
        Assert.Equal(existing, await FilesHost.ReadAllAsync(second));
        Assert.Equal(existing, await FilesHost.ReadAllAsync(first));
        var deleted = await files.DeleteAsync(member.Context, member.Principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation);
        var restored = await files.RestoreAsync(member.Context, member.Principal, version.FileId, version.VersionId, deleted.ConcurrencyVersion, Cancellation);
        Assert.False(restored.IsDeleted);
        var other = FilesHost.Text(300, 'o');
        var quick = await ReleasedAsync(files, member, other);
        Assert.Equal(other, await FilesHost.ReadAllAsync(await files.ReadAsync(member.Context, member.Principal, quick.FileId, quick.VersionId,
            cancellationToken: Cancellation)));
        // Reads, deletes and restores take no lease at all: they complete while the tenant's lease is held, as recovery holds it.
        await using (await new Persistence.FileSystem.FileSystemBlobStore(host.Root, 1024).OpenAsync(member.Context, Cancellation))
        {
            Assert.Equal(existing, await FilesHost.ReadAllAsync(await files.ReadAsync(member.Context, member.Principal, version.FileId, version.VersionId,
                cancellationToken: Cancellation)));
            var removed = await files.DeleteAsync(member.Context, member.Principal, version.FileId, version.VersionId, restored.ConcurrencyVersion,
                Cancellation);
            Assert.False((await files.RestoreAsync(member.Context, member.Principal, version.FileId, version.VersionId, removed.ConcurrencyVersion,
                Cancellation)).IsDeleted);
        }
        // Recovery runs under the lease meanwhile and leaves the live upload's held staging file and intent alone.
        Assert.Equal(0, await files.RecoverAsync(member.Context, Cancellation));
        Assert.Equal(1, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Single(host.Objects(tenant.Id), path => path.EndsWith(".pending", StringComparison.Ordinal));

        stream.Resume();
        var streamed = await upload;
        streamed = await files.ReleaseAsync(member.Context, member.Principal, streamed.FileId, streamed.VersionId, Cancellation);
        Assert.Equal(body, await FilesHost.ReadAllAsync(await files.ReadAsync(member.Context, member.Principal, streamed.FileId, streamed.VersionId,
            cancellationToken: Cancellation)));
        Assert.Equal(0, await host.CountAsync("FileUploads", tenant.Id));
        Assert.DoesNotContain(host.Objects(tenant.Id), path => path.EndsWith(".pending", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TM_G02_045_Pending_uploads_are_reserved_atomically_per_tenant_and_abandoned_ones_are_reclaimed_at_the_quota()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenantA = await host.TenantAsync("Tenant A");
        var tenantB = await host.TenantAsync("Tenant B");
        await using var member = await AuthorAsync(host, tenantA, "author-1");
        await using var neighbour = await AuthorAsync(host, tenantB, "author-2");
        var files = host.Files(new FilesPolicy(maximumPendingUploads: 2), release: new TestReleasePolicy(), maximumLength: 8192);
        var body = FilesHost.Text(4096);

        // Five uploads race for two pending places. Each reserves before it accepts any byte past the detection head, so
        // exactly two stream and three are refused without leaving an intent or a staging file.
        var streams = Enumerable.Range(0, 5).Select(_ => new PausedStream(body, 2048)).ToArray();
        var cancellations = streams.Select(_ => CancellationTokenSource.CreateLinkedTokenSource(Cancellation)).ToArray();
        var uploads = streams.Select((stream, index) => files.UploadAsync(member.Context, member.Principal,
            FilesHost.Request(body, name: "race-" + index + ".txt"), stream, cancellations[index].Token)).ToArray();
        foreach (var (stream, index) in streams.Select((stream, index) => (stream, index)))
            await Task.WhenAny(stream.Reached, uploads[index]).WaitAsync(Cancellation);
        var streaming = Enumerable.Range(0, 5).Where(index => streams[index].Reached.IsCompleted).ToArray();
        Assert.Equal(2, streaming.Length);
        foreach (var index in Enumerable.Range(0, 5).Except(streaming))
            Assert.Equal(FileStorageError.QuotaExceeded, (await Assert.ThrowsAsync<FileStorageException>(() => uploads[index])).Code);
        Assert.Equal(2, await host.CountAsync("FileUploads", tenantA.Id));
        Assert.Equal(2, host.Objects(tenantA.Id).Length);
        // Another tenant's quota is its own.
        var elsewhere = await ReleasedAsync(files, neighbour, FilesHost.Text(10));
        Assert.Equal(FileQuarantineState.Released, elsewhere.Quarantine);
        // The pending bytes are bounded as well as the count: 8,192 bytes are reserved, so 10 more exceed a 8,200-byte quota.
        var bytes = host.Files(new FilesPolicy(maximumPendingUploads: 10, maximumPendingBytes: 8200), maximumLength: 8192);
        Assert.Equal(FileStorageError.QuotaExceeded, (await Assert.ThrowsAsync<FileStorageException>(() => bytes.UploadAsync(member.Context,
            member.Principal, FilesHost.Request(FilesHost.Text(10)), new MemoryStream(FilesHost.Text(10)), Cancellation))).Code);
        await bytes.UploadAsync(member.Context, member.Principal, FilesHost.Request(FilesHost.Text(8)), new MemoryStream(FilesHost.Text(8)), Cancellation);

        // One streaming upload is abandoned; at the quota, the next upload reclaims it under the lease and proceeds.
        cancellations[streaming[0]].Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploads[streaming[0]]);
        Assert.Equal(2, await host.CountAsync("FileUploads", tenantA.Id));
        var next = await ReleasedAsync(files, member, FilesHost.Text(20));
        Assert.Equal(FileQuarantineState.Released, next.Quarantine);
        Assert.Equal(1, await host.CountAsync("FileUploads", tenantA.Id));
        streams[streaming[1]].Resume();
        Assert.Equal(4096, (await uploads[streaming[1]]).Length);
        Assert.Equal(0, await host.CountAsync("FileUploads", tenantA.Id));
        foreach (var cancellation in cancellations) cancellation.Dispose();
    }

    [Fact]
    public async Task TM_G02_045_An_upload_that_streams_past_its_deadline_is_abandoned_and_reclaimed()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        await using var member = await AuthorAsync(host, tenant, "author-1");
        var files = host.Files(new FilesPolicy(uploadTimeout: TimeSpan.FromSeconds(1)));
        var body = FilesHost.Text(4096);
        var stalled = new PausedStream(body, 2048);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.UploadAsync(member.Context, member.Principal,
            FilesHost.Request(body), stalled, Cancellation).WaitAsync(TimeSpan.FromMinutes(1), Cancellation));
        Assert.True(stalled.Reached.IsCompleted);
        Assert.Equal(1, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Equal(1, await files.RecoverAsync(member.Context, Cancellation));
        Assert.Empty(host.Objects(tenant.Id));
        Assert.Equal(0, await host.CountAsync("Files", tenant.Id));
    }

    [Fact]
    public async Task TM_G02_045_Each_tenant_s_read_snapshots_are_bounded_by_its_read_slots()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenantA = await host.TenantAsync("Tenant A");
        var tenantB = await host.TenantAsync("Tenant B");
        await using var member = await AuthorAsync(host, tenantA, "author-1");
        await using var neighbour = await AuthorAsync(host, tenantB, "author-2");
        var files = host.Files(release: new TestReleasePolicy(), readSlots: 2);
        var content = FilesHost.Text(2000);
        var version = await ReleasedAsync(files, member, content);
        var theirs = await ReleasedAsync(files, neighbour, content);
        var first = await files.ReadAsync(member.Context, member.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation);
        var second = await files.ReadAsync(member.Context, member.Principal, version.FileId, version.VersionId, new FileReadRange(5, 5), Cancellation);
        Assert.Equal(FileStorageError.Busy, (await Assert.ThrowsAsync<FileStorageException>(() => files.ReadAsync(member.Context, member.Principal,
            version.FileId, version.VersionId, cancellationToken: Cancellation))).Code);
        Assert.Equal(content, await FilesHost.ReadAllAsync(await files.ReadAsync(neighbour.Context, neighbour.Principal, theirs.FileId, theirs.VersionId,
            cancellationToken: Cancellation)));
        Assert.Equal(content[5..10], await FilesHost.ReadAllAsync(second));
        Assert.Equal(content, await FilesHost.ReadAllAsync(await files.ReadAsync(member.Context, member.Principal, version.FileId, version.VersionId,
            cancellationToken: Cancellation)));
        Assert.Equal(content, await FilesHost.ReadAllAsync(first));
    }

    [Fact]
    public async Task TM_G02_045_A_failed_upload_is_handed_to_the_recovery_schedule_and_recovered_under_its_own_context()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        await using var member = await AuthorAsync(host, tenant, "author-1");
        var schedule = new RecordingSchedule();
        var files = host.Files(recovery: schedule);
        var content = FilesHost.Text(3000);
        // Refusals before any intent exists leave nothing to recover and schedule nothing.
        await Assert.ThrowsAsync<FileStorageException>(() => files.UploadAsync(member.Context, member.Principal,
            FilesHost.Request(content, name: "CON"), new MemoryStream(content), Cancellation));
        Assert.Empty(schedule.References);
        await Assert.ThrowsAsync<FileStorageException>(() => files.UploadAsync(member.Context, member.Principal,
            FilesHost.Request(content) with { Sha256 = new string('0', 64) }, new MemoryStream(content), Cancellation));
        var reference = Assert.Single(schedule.References);
        Assert.True(PropagatedTenantContext.TryParse(reference, out var propagated));
        Assert.Equal(tenant.Id, propagated!.TenantId);
        Assert.Equal(member.User.Actor, propagated.Actor);
        Assert.Equal(member.Context.UnitId, propagated.ProducerUnitId);
        Assert.Equal(host.Clock.Now, propagated.CapturedAt);
        Assert.Equal(1, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Single(host.Objects(tenant.Id));
        // The intent's own reference is captured the same way, with the injected clock.
        await using (var unit = await host.Metadata.BeginAsync(member.Context, Cancellation))
        {
            Assert.True(PropagatedTenantContext.TryParse(Assert.Single(await unit.ListUploadsAsync(Cancellation)).PropagatedContext, out var stored));
            Assert.Equal(host.Clock.Now, stored!.CapturedAt);
            Assert.Equal(member.User.Actor, stored.Actor);
        }
        // The scheduled reference runs recovery in a revalidated unit of its own, which removes the failed first upload's file too.
        var outcome = await files.RecoverAsync(reference, host.Get<TenantWorkRunner>(), Cancellation);
        Assert.Equal(TenantWorkDisposition.Completed, outcome.Disposition);
        Assert.Equal(0, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Equal(0, await host.CountAsync("Files", tenant.Id));
        Assert.Empty(host.Objects(tenant.Id));
    }

    [Fact]
    public async Task TM_G02_046_Restore_verifies_the_retained_bytes_in_place_while_every_read_slot_is_held()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        await using var member = await AuthorAsync(host, tenant, "author-1");
        var files = host.Files(release: new TestReleasePolicy(), readSlots: 1);
        var content = FilesHost.Text(700);
        var held = await ReleasedAsync(files, member, FilesHost.Text(50, 'h'));
        var version = await ReleasedAsync(files, member, content);
        await using var reading = await files.ReadAsync(member.Context, member.Principal, held.FileId, held.VersionId, cancellationToken: Cancellation);
        Assert.Equal(FileStorageError.Busy, (await Assert.ThrowsAsync<FileStorageException>(() => files.ReadAsync(member.Context, member.Principal,
            version.FileId, version.VersionId, cancellationToken: Cancellation))).Code);
        var deleted = await files.DeleteAsync(member.Context, member.Principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation);
        var damaged = content.ToArray();
        damaged[^1] ^= 1;
        await File.WriteAllBytesAsync(host.Content(tenant.Id, version.VersionId), damaged, Cancellation);
        Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() => files.RestoreAsync(member.Context,
            member.Principal, version.FileId, version.VersionId, deleted.ConcurrencyVersion, Cancellation))).Code);
        Assert.Equal(deleted, Assert.Single(await files.ListDeletedAsync(member.Context, member.Principal, version.FileId, Cancellation)));
        await File.WriteAllBytesAsync(host.Content(tenant.Id, version.VersionId), content, Cancellation);
        var restored = await files.RestoreAsync(member.Context, member.Principal, version.FileId, version.VersionId, deleted.ConcurrencyVersion, Cancellation);
        Assert.False(restored.IsDeleted);
        Assert.Equal(FileStorageError.Conflict, (await Assert.ThrowsAsync<FileStorageException>(() => files.RestoreAsync(member.Context,
            member.Principal, version.FileId, version.VersionId, deleted.ConcurrencyVersion, Cancellation))).Code);
    }

    private sealed class RecordingSchedule : IFileRecoverySchedule
    {
        internal List<string> References { get; } = [];

        public ValueTask ScheduleAsync(string propagatedTenantContext, CancellationToken cancellationToken)
        {
            References.Add(propagatedTenantContext);
            return ValueTask.CompletedTask;
        }
    }
}
