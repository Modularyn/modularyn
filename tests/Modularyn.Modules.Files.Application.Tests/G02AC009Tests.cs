using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

/// <summary>
/// AC-009-B01: a supported 1,024-byte maximum with 1,023, 1,024 and 1,025-byte inputs, and a fourth upload paused before
/// finalization and cancelled, through the real evaluator, PostgreSQL and the filesystem provider.
/// </summary>
[Collection(FilesPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class G02AC009Tests
{
    private const long Maximum = 1024;
    private static CancellationToken Cancellation => FilesHost.Cancellation;

    [Fact]
    public async Task AC_009_B01_UploadSizeAndCancellationLeaveNoPartialVersion_Integration()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        await using var member = await host.EnterAsync(await host.MemberAsync(tenant, "member-1"), tenant.Id);
        var pause = new PauseAt(FileStorageStage.ContentFlushed);
        var files = host.Files(observer: pause, maximumLength: Maximum);
        var completed = new List<(FileVersionInfo Version, byte[] Content)>();
        foreach (var size in new[] { 1023, 1024 })
        {
            var content = FilesHost.Text(size);
            var version = await files.UploadAsync(member.Context, member.Principal, FilesHost.Request(content, name: "size-" + size + ".txt"),
                new MemoryStream(content), Cancellation);
            Assert.Equal(size, version.Length);
            Assert.Equal(FilesHost.Digest(content), version.Sha256);
            Assert.Equal("text/plain", version.ContentType);
            Assert.Equal(FileQuarantineState.Pending, version.Quarantine);
            Assert.Equal(0, version.CreatedAt.Ticks % TimeSpan.TicksPerMicrosecond);
            Assert.Equal(content, await File.ReadAllBytesAsync(host.Content(tenant.Id, version.VersionId), Cancellation));
            completed.Add((version, content));
        }
        // One byte over the configured maximum is refused as the declared size limit before any intent, staging or version exists.
        var over = FilesHost.Text(1025);
        Assert.Equal(FileStorageError.InvalidLength, (await Assert.ThrowsAsync<FileStorageException>(() => files.UploadAsync(member.Context, member.Principal,
            FilesHost.Request(over, name: "size-1025.txt"), new MemoryStream(over), Cancellation))).Code);
        Assert.Equal(2, await host.CountAsync("Files", tenant.Id));
        Assert.Equal(2, await host.CountAsync("FileRevisions", tenant.Id));
        Assert.Equal(0, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Equal(2, host.Objects(tenant.Id).Length);

        // The fourth upload stages its bytes and pauses before its version is finalized.
        var fourth = FilesHost.Text(1000, 'b');
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        pause.Arm();
        var upload = files.UploadAsync(member.Context, member.Principal, FilesHost.Request(fourth, name: "paused.txt"), new MemoryStream(fourth),
            cancellation.Token);
        await pause.Reached.WaitAsync(Cancellation);
        var staged = host.Content(tenant.Id, pause.VersionId, ".pending");
        Assert.True(File.Exists(staged));
        Assert.Equal(1, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Equal(2, await host.CountAsync("FileRevisions", tenant.Id));
        // Cancelled there, it reports its cancellation, and no authoritative version of it exists, then or after recovery.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.False(File.Exists(host.Content(tenant.Id, pause.VersionId)));
        Assert.Equal(2, await host.CountAsync("FileRevisions", tenant.Id));
        Assert.Equal(1, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Equal(3, await host.CountAsync("Files", tenant.Id));
        // Recovery reclaims the staged bytes, the intent and the file the cancelled first upload left without a version.
        Assert.Equal(1, await files.RecoverAsync(member.Context, Cancellation));
        Assert.False(File.Exists(staged));
        Assert.Equal(0, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Equal(2, await host.CountAsync("Files", tenant.Id));
        Assert.Equal(2, host.Objects(tenant.Id).Length);
        foreach (var (version, content) in completed)
        {
            Assert.Equal(version, Assert.Single(await files.ListAsync(member.Context, member.Principal, version.FileId, Cancellation)));
            Assert.Equal(content, await File.ReadAllBytesAsync(host.Content(tenant.Id, version.VersionId), Cancellation));
        }
    }

    [Fact]
    public async Task AC_009_B01_UploadSizeAndCancellationLeaveNoPartialVersion_Contract()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var user = await host.MemberAsync(tenant, "member-1");
        await host.GrantAsync(tenant, user, FilePermissions.Release);
        await using var member = await host.EnterAsync(user, tenant.Id);
        var pause = new PauseAt(FileStorageStage.ContentFlushed);
        IFiles files = host.Files(release: new TestReleasePolicy(), observer: pause, maximumLength: Maximum);
        var sizes = new Dictionary<int, FileVersionInfo>();
        foreach (var size in new[] { 1023, 1024 })
        {
            var content = FilesHost.Text(size);
            sizes[size] = await files.UploadAsync(member.Context, member.Principal, FilesHost.Request(content, name: "size-" + size + ".txt"),
                new MemoryStream(content), Cancellation);
        }
        var over = FilesHost.Text(1025);
        var refused = await Assert.ThrowsAsync<FileStorageException>(() => files.UploadAsync(member.Context, member.Principal,
            FilesHost.Request(over, name: "size-1025.txt"), new MemoryStream(over), Cancellation));
        Assert.Equal(FileStorageError.InvalidLength, refused.Code);
        Assert.DoesNotContain("1025", refused.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "size-1023.txt", "size-1024.txt" },
            (await files.ListFilesAsync(member.Context, member.Principal, "documents", cancellationToken: Cancellation)).Select(file => file.Name).Order().ToArray());

        // A new version of the 1,024-byte file pauses before finalization and is cancelled; its file keeps one version.
        var fourth = FilesHost.Text(900, 'c');
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        pause.Arm();
        var upload = files.UploadAsync(member.Context, member.Principal, FilesHost.Request(fourth, sizes[1024].FileId), new MemoryStream(fourth),
            cancellation.Token);
        await pause.Reached.WaitAsync(Cancellation);
        Assert.Equal(new[] { sizes[1024] }, await files.ListAsync(member.Context, member.Principal, sizes[1024].FileId, Cancellation));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        // The completed sizes keep exactly one version each, which the release policy can release and a reader then reads whole.
        foreach (var (size, version) in sizes)
        {
            Assert.Equal(version, Assert.Single(await files.ListAsync(member.Context, member.Principal, version.FileId, Cancellation)));
            var released = await files.ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId, Cancellation);
            Assert.Equal(FileQuarantineState.Released, released.Quarantine);
            var read = await files.ReadAsync(member.Context, member.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation);
            Assert.Equal("text/plain", read.Delivery.ContentType);
            Assert.Equal(FilesHost.Text(size), await FilesHost.ReadAllAsync(read));
        }
        Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() =>
            files.ReadAsync(member.Context, member.Principal, sizes[1024].FileId, pause.VersionId, cancellationToken: Cancellation))).Code);
        Assert.Equal(2, (await files.ListFilesAsync(member.Context, member.Principal, "documents", cancellationToken: Cancellation)).Count);
    }
}
