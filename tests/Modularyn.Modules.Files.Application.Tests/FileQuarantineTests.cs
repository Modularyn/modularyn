using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

/// <summary>Quarantine and content detection over real PostgreSQL, the filesystem provider and the real evaluator.</summary>
[Collection(FilesPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class FileQuarantineTests
{
    private static CancellationToken Cancellation => FilesHost.Cancellation;

    [Fact]
    public async Task TM_G02_042_New_versions_stay_unreadable_until_the_release_policy_releases_them_and_rejected_ones_stay_unreadable()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        await using var member = await host.EnterAsync(author, tenant.Id);
        var policy = new TestReleasePolicy(FileReleaseVerdict.Defer);
        var content = FilesHost.Text(300);
        var version = await host.Files(release: policy).UploadAsync(member.Context, member.Principal, FilesHost.Request(content, name: "report.txt"),
            new MemoryStream(content), Cancellation);
        Assert.Equal(FileQuarantineState.Pending, version.Quarantine);

        // The read path itself refuses pending content: a direct read, a read through a grant, and a grant's issue.
        foreach (var files in new[] { host.Files(), host.Files(release: policy) })
        {
            Assert.Equal(FileStorageError.Quarantined, (await Assert.ThrowsAsync<FileStorageException>(() => files.ReadAsync(member.Context,
                member.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation))).Code);
            Assert.Equal(FileStorageError.Quarantined, (await Assert.ThrowsAsync<FileStorageException>(() => files.IssueGrantAsync(member.Context,
                member.Principal, version.FileId, version.VersionId, FileGrantPurpose.Download, TimeSpan.FromMinutes(5), Cancellation))).Code);
        }
        // Without a policy, after a deferral and after a failed check, the version stays pending.
        Assert.Equal(version, await host.Files().ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId, Cancellation));
        Assert.Equal(version, await host.Files(release: policy).ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId,
            Cancellation));
        policy.Failing = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Files(release: policy).ReleaseAsync(member.Context, member.Principal,
            version.FileId, version.VersionId, Cancellation));
        Assert.Equal(version, Assert.Single(await host.Files().ListAsync(member.Context, member.Principal, version.FileId, Cancellation)));
        // The policy saw the verified bytes and the type detected from them, never a client's claim.
        var (candidate, seen) = policy.Seen[0];
        Assert.Equal(content, seen);
        Assert.Equal(new FileReleaseCandidate(tenant.Id, version.FileId, version.VersionId, "report.txt", 300, FilesHost.Digest(content), "text/plain"),
            candidate);

        // A released version becomes readable; a rejected one stays unreadable, and both decisions are final.
        policy.Failing = false;
        policy.Verdict = FileReleaseVerdict.Release;
        var released = await host.Files(release: policy).ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId, Cancellation);
        Assert.Equal(FileQuarantineState.Released, released.Quarantine);
        Assert.Equal(content, await FilesHost.ReadAllAsync(await host.Files().ReadAsync(member.Context, member.Principal, version.FileId,
            version.VersionId, cancellationToken: Cancellation)));
        var other = await host.Files().UploadAsync(member.Context, member.Principal, FilesHost.Request(content, version.FileId), new MemoryStream(content),
            Cancellation);
        policy.Verdict = FileReleaseVerdict.Reject;
        var rejected = await host.Files(release: policy).ReleaseAsync(member.Context, member.Principal, other.FileId, other.VersionId, Cancellation);
        Assert.Equal(FileQuarantineState.Rejected, rejected.Quarantine);
        policy.Verdict = FileReleaseVerdict.Release;
        Assert.Equal(rejected, await host.Files(release: policy).ReleaseAsync(member.Context, member.Principal, other.FileId, other.VersionId, Cancellation));
        Assert.Equal(released, await host.Files(release: policy).ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId,
            Cancellation));
        Assert.Equal(FileStorageError.Quarantined, (await Assert.ThrowsAsync<FileStorageException>(() => host.Files().ReadAsync(member.Context,
            member.Principal, other.FileId, other.VersionId, cancellationToken: Cancellation))).Code);
        Assert.Equal(4, policy.Seen.Count);
    }

    [Fact]
    public async Task TM_G02_042_Releasing_is_an_authorized_action_on_the_file()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        var releaser = await host.MemberAsync(tenant, "releaser-1");
        await host.GrantAsync(tenant, releaser, FilePermissions.Release);
        var policy = new TestReleasePolicy();
        var files = host.Files(release: policy);
        await using var writer = await host.EnterAsync(author, tenant.Id);
        var content = FilesHost.Text(40);
        var version = await files.UploadAsync(writer.Context, writer.Principal, FilesHost.Request(content), new MemoryStream(content), Cancellation);
        // The author may upload but not release; another member with the permission may not use the author's file.
        Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<FileAccessDeniedException>(() =>
            files.ReleaseAsync(writer.Context, writer.Principal, version.FileId, version.VersionId, Cancellation))).Code);
        await using var reviewer = await host.EnterAsync(releaser, tenant.Id);
        Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() =>
            files.ReleaseAsync(reviewer.Context, reviewer.Principal, version.FileId, version.VersionId, Cancellation))).Code);
        Assert.Empty(policy.Seen);
        // Shared with the reviewer for releasing, through SEC-05's resource grant, the release runs and is recorded.
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        await host.ShareAsync(writer, releaser, version.FileId, FilePermissions.Release);
        var released = await files.ReleaseAsync(reviewer.Context, reviewer.Principal, version.FileId, version.VersionId, Cancellation);
        Assert.Equal(FileQuarantineState.Released, released.Quarantine);
        Assert.NotEqual(version.ConcurrencyVersion, released.ConcurrencyVersion);
    }

    [Fact]
    public async Task TM_G02_042_The_type_is_detected_from_the_bytes_against_the_allowed_list_never_from_the_name()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        await using var member = await host.EnterAsync(author, tenant.Id);
        var files = host.Files(release: new TestReleasePolicy());
        // Scriptable and executable content is refused whatever it is called, before any intent or byte is stored.
        foreach (var (name, content) in new[]
        {
            ("photo.png", "\n  <html><body><script>alert(1)</script></body></html>"u8.ToArray()),
            ("image.png", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray()),
            ("data.txt", "<?xml version=\"1.0\"?><a/>"u8.ToArray()),
            ("notes.txt", "MZ"u8.ToArray().Concat(new byte[200]).ToArray()),
            ("notes.txt", FilesHost.Bytes(200))
        })
            Assert.Equal(FileStorageError.UnsupportedContent, (await Assert.ThrowsAsync<FileStorageException>(() => files.UploadAsync(member.Context,
                member.Principal, FilesHost.Request(content, name: name), new MemoryStream(content), Cancellation))).Code);
        Assert.Equal(0, await host.CountAsync("Files", tenant.Id));
        Assert.Equal(0, await host.CountAsync("FileUploads", tenant.Id));
        Assert.Empty(host.Objects(tenant.Id));

        // A PNG named as text is an image, and an empty file is text.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. FilesHost.Bytes(64)];
        var image = await files.UploadAsync(member.Context, member.Principal, FilesHost.Request(png, name: "notes.txt"), new MemoryStream(png), Cancellation);
        Assert.Equal("image/png", image.ContentType);
        var empty = await files.UploadAsync(member.Context, member.Principal, FilesHost.Request([], name: "empty.bin"), new MemoryStream(), Cancellation);
        Assert.Equal("text/plain", empty.ContentType);
        // A deployment can allow opaque binary content explicitly; it is then delivered as such.
        var opaque = FilesHost.Bytes(200);
        var binary = await host.Files(new FilesPolicy(allowedContentTypes: [.. FilesPolicy.DefaultAllowedContentTypes, FileContentTypes.Binary]))
            .UploadAsync(member.Context, member.Principal, FilesHost.Request(opaque, name: "photo.png"), new MemoryStream(opaque), Cancellation);
        Assert.Equal("application/octet-stream", binary.ContentType);
        await files.ReleaseAsync(member.Context, member.Principal, binary.FileId, binary.VersionId, Cancellation);
        await using var read = await files.ReadAsync(member.Context, member.Principal, binary.FileId, binary.VersionId, cancellationToken: Cancellation);
        Assert.Equal(new FileDelivery("application/octet-stream", FileDisposition.Attachment, "photo.png"), read.Delivery);
        Assert.Contains(new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"), read.Delivery.Headers());
    }
}
