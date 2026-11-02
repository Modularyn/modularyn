using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

/// <summary>
/// The opt-in allowlist release policy through the Files service, over real PostgreSQL, the filesystem provider and the real
/// evaluator: it releases only what its allowlist and maximum admit, and nothing is released without it.
/// </summary>
[Collection(FilesPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class AllowlistReleaseTests
{
    private static CancellationToken Cancellation => FilesHost.Cancellation;

    // Uploads admit more than the allowlist releases, so each refusal below is the release policy's own.
    private static readonly FilesPolicy Uploads = new(allowedContentTypes: [.. FilesPolicy.DefaultAllowedContentTypes, FileContentTypes.Binary]);

    [Fact]
    public async Task TM_G02_042_The_allowlist_policy_releases_allowlisted_content_and_keeps_the_rest_quarantined()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        await using var member = await host.EnterAsync(author, tenant.Id);
        var files = host.Files(Uploads, new AllowlistFileReleasePolicy([FileContentTypes.Png, FileContentTypes.PlainText], maximumLength: 4096));

        // Allowlisted types within the maximum are released, readable and grantable.
        foreach (var (content, name) in new[]
        {
            (AllowlistReleasePolicyTests.Png(1000), "photo.png"), (AllowlistReleasePolicyTests.Png(4096), "largest.png"), (FilesHost.Text(300), "notes.txt")
        })
        {
            var released = await UploadAndReleaseAsync(files, member, content, name);
            Assert.Equal(FileQuarantineState.Released, released.Quarantine);
            Assert.Equal(content, await FilesHost.ReadAllAsync(await files.ReadAsync(member.Context, member.Principal, released.FileId,
                released.VersionId, cancellationToken: Cancellation)));
            var issued = await files.IssueGrantAsync(member.Context, member.Principal, released.FileId, released.VersionId,
                FileGrantPurpose.Download, TimeSpan.FromMinutes(5), Cancellation);
            Assert.Equal(released.VersionId, issued.Grant.VersionId);
        }

        // A type off the allowlist, a version over its maximum, bytes that detection does not recognize and text with a binary
        // data byte past its header are rejected, and stay unreadable and ungrantable.
        foreach (var (content, name) in new[]
        {
            (AllowlistReleasePolicyTests.Pdf(400), "report.pdf"), (AllowlistReleasePolicyTests.Png(4097), "large.png"), (FilesHost.Bytes(200), "photo.png"),
            ([.. FilesHost.Text(FileContentTypes.SniffLength), 0x00, .. FilesHost.Text(10)], "notes.txt")
        })
        {
            var rejected = await UploadAndReleaseAsync(files, member, content, name);
            Assert.Equal(FileQuarantineState.Rejected, rejected.Quarantine);
            await AssertQuarantinedAsync(files, member, rejected);
        }
    }

    [Fact]
    public async Task TM_G02_042_The_allowlist_policy_detects_the_type_from_the_bytes_never_from_the_stored_record()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        await using var member = await host.EnterAsync(author, tenant.Id);
        var content = FilesHost.Text(300);
        var version = await host.Files().UploadAsync(member.Context, member.Principal, FilesHost.Request(content, name: "photo.png"),
            new MemoryStream(content), Cancellation);
        Assert.Equal(FileContentTypes.PlainText, version.ContentType);

        // A writer with direct database access relabels the text as an allowlisted image; the bytes still decide.
        Assert.Equal(1, await host.ScalarAsync<int>("UPDATE " + host.FilesTable("FileRevisions")
            + " SET \"ContentType\" = @type WHERE \"TenantId\" = @tenant AND \"Id\" = @id RETURNING 1",
            new NpgsqlParameter("type", FileContentTypes.Png), new NpgsqlParameter("tenant", tenant.Id), new NpgsqlParameter("id", version.VersionId)));
        var files = host.Files(release: new AllowlistFileReleasePolicy([FileContentTypes.Png], maximumLength: 4096));
        var rejected = await files.ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId, Cancellation);
        Assert.Equal(FileQuarantineState.Rejected, rejected.Quarantine);
        Assert.Equal(FileContentTypes.Png, rejected.ContentType);
        await AssertQuarantinedAsync(files, member, rejected);
    }

    [Fact]
    public async Task TM_G02_042_Nothing_is_released_without_a_registered_policy()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        await using var member = await host.EnterAsync(author, tenant.Id);

        // Content the allowlist would release stays pending and unreadable through a service composed without it.
        var unregistered = host.Files(Uploads);
        var pending = await UploadAndReleaseAsync(unregistered, member, AllowlistReleasePolicyTests.Png(1000), "photo.png");
        Assert.Equal(FileQuarantineState.Pending, pending.Quarantine);
        await AssertQuarantinedAsync(unregistered, member, pending);
        Assert.Equal(pending, Assert.Single(await unregistered.ListAsync(member.Context, member.Principal, pending.FileId, Cancellation)));

        // Only a service the host composes with the policy, named explicitly, releases it.
        var registered = host.Files(Uploads, new AllowlistFileReleasePolicy([FileContentTypes.Png], maximumLength: 4096));
        var released = await registered.ReleaseAsync(member.Context, member.Principal, pending.FileId, pending.VersionId, Cancellation);
        Assert.Equal(FileQuarantineState.Released, released.Quarantine);
    }

    private static async Task<FileVersionInfo> UploadAndReleaseAsync(FileService files, Member member, byte[] content, string name)
    {
        var version = await files.UploadAsync(member.Context, member.Principal, FilesHost.Request(content, name: name), new MemoryStream(content),
            Cancellation);
        Assert.Equal(FileQuarantineState.Pending, version.Quarantine);
        return await files.ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId, Cancellation);
    }

    private static async Task AssertQuarantinedAsync(FileService files, Member member, FileVersionInfo version)
    {
        Assert.Equal(FileStorageError.Quarantined, (await Assert.ThrowsAsync<FileStorageException>(() => files.ReadAsync(member.Context,
            member.Principal, version.FileId, version.VersionId, cancellationToken: Cancellation))).Code);
        Assert.Equal(FileStorageError.Quarantined, (await Assert.ThrowsAsync<FileStorageException>(() => files.IssueGrantAsync(member.Context,
            member.Principal, version.FileId, version.VersionId, FileGrantPurpose.Download, TimeSpan.FromMinutes(5), Cancellation))).Code);
    }
}
