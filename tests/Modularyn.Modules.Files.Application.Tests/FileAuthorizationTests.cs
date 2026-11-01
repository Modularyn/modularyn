using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

/// <summary>Every file action through SEC-05's evaluator, over the real authorization, identity and tenancy runtime.</summary>
[Collection(FilesPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class FileAuthorizationTests
{
    private static CancellationToken Cancellation => FilesHost.Cancellation;

    /// <summary>The service's every action, on one released version, for one unit.</summary>
    private static IEnumerable<(string Action, Func<Task> Run)> Actions(FileService files, TenantContext context, ExecutionPrincipal? principal,
        FileVersionInfo version, FileGrantToken token, Guid grantId)
    {
        var content = FilesHost.Text(10);
        yield return ("upload", () => files.UploadAsync(context, principal, FilesHost.Request(content), new MemoryStream(content), Cancellation));
        yield return ("upload version", () => files.UploadAsync(context, principal, FilesHost.Request(content, version.FileId), new MemoryStream(content),
            Cancellation));
        yield return ("list files", () => files.ListFilesAsync(context, principal, "documents", cancellationToken: Cancellation));
        yield return ("list", () => files.ListAsync(context, principal, version.FileId, Cancellation));
        yield return ("list deleted", () => files.ListDeletedAsync(context, principal, version.FileId, Cancellation));
        yield return ("read", () => files.ReadAsync(context, principal, version.FileId, version.VersionId, cancellationToken: Cancellation));
        yield return ("delete", () => files.DeleteAsync(context, principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation));
        yield return ("restore", () => files.RestoreAsync(context, principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation));
        yield return ("release", () => files.ReleaseAsync(context, principal, version.FileId, version.VersionId, Cancellation));
        yield return ("issue grant", () => files.IssueGrantAsync(context, principal, version.FileId, version.VersionId, FileGrantPurpose.Download,
            TimeSpan.FromMinutes(5), Cancellation));
        yield return ("list grants", () => files.ListGrantsAsync(context, principal, version.FileId, Cancellation));
        yield return ("revoke grant", () => files.RevokeGrantAsync(context, principal, grantId, Cancellation));
        yield return ("redeem grant", () => files.RedeemGrantAsync(context, principal, token, FileGrantPurpose.Download, cancellationToken: Cancellation));
    }

    private static async Task<(FileVersionInfo Version, IssuedFileGrant Grant)> ReleasedAsync(FileService files, Member author, string name = "notes.txt")
    {
        var content = FilesHost.Text(100);
        var version = await files.UploadAsync(author.Context, author.Principal, FilesHost.Request(content, name: name), new MemoryStream(content), Cancellation);
        version = await files.ReleaseAsync(author.Context, author.Principal, version.FileId, version.VersionId, Cancellation);
        Assert.Equal(FileQuarantineState.Released, version.Quarantine);
        var grant = await files.IssueGrantAsync(author.Context, author.Principal, version.FileId, version.VersionId, FileGrantPurpose.Download,
            TimeSpan.FromMinutes(5), Cancellation);
        return (version, grant);
    }

    [Fact]
    public async Task TM_G02_044_Every_file_action_asks_the_evaluator_and_refuses_without_the_permission_whether_or_not_the_file_exists()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        var bystander = await host.UserAsync("bystander-1", tenant.Id);
        var files = host.Files(release: new TestReleasePolicy());
        await using var writer = await host.EnterAsync(author, tenant.Id);
        var (version, grant) = await ReleasedAsync(files, writer);
        var missing = version with { FileId = Guid.NewGuid(), VersionId = Guid.NewGuid() };
        var unknown = FileGrantToken.Create(Guid.NewGuid());

        // A member of the tenant who holds no Files permission is refused every action with the evaluator's code, for an
        // existing file and a missing one alike, so a refusal reveals nothing about which files exist.
        await using (var member = await host.EnterAsync(bystander, tenant.Id))
        {
            foreach (var target in new[] { (version, grant.Token, grant.Grant.GrantId), (missing, unknown, unknown.GrantId) })
                foreach (var (action, run) in Actions(files, member.Context, member.Principal, target.Item1, target.Item2, target.Item3))
                    Assert.True(AuthorizationCodes.NotHeld == (await Assert.ThrowsAsync<FileAccessDeniedException>(run)).Code, action);
        }
        // Without a principal nothing is permitted, and no grant is served without a session.
        foreach (var (action, run) in Actions(files, writer.Context, null, version, grant.Token, grant.Grant.GrantId))
            Assert.True(AuthorizationCodes.Unauthenticated == (await Assert.ThrowsAsync<FileAccessDeniedException>(run)).Code, action);
        // A principal whose session ended is refused at the next current-profile decision; upload decides at the bounded profile.
        var (session, principal) = await host.SignInAsync(author);
        Assert.True(await host.Get<ISessionService>().SignOutAsync(session.Token, Cancellation));
        await using (var ended = await host.EnterAsync(author, tenant.Id, principal))
            foreach (var (action, run) in Actions(files, ended.Context, ended.Principal, version, grant.Token, grant.Grant.GrantId)
                         .Where(item => item.Action is not ("upload" or "upload version")))
                Assert.True(AuthorizationCodes.PrincipalNotCurrent == (await Assert.ThrowsAsync<FileAccessDeniedException>(run)).Code, action);
        // The owner's own actions are permitted; nothing above changed the file.
        Assert.Equal(version, Assert.Single(await files.ListAsync(writer.Context, writer.Principal, version.FileId, Cancellation)));
        Assert.Equal(FilesHost.Text(100), await FilesHost.ReadAllAsync(await files.ReadAsync(writer.Context, writer.Principal, version.FileId,
            version.VersionId, cancellationToken: Cancellation)));
    }

    [Fact]
    public async Task TM_G02_044_A_file_resolves_in_the_effective_tenant_before_its_owner_or_a_current_grant_is_checked()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenantA = await host.TenantAsync("Tenant A");
        var tenantB = await host.TenantAsync("Tenant B");
        var author = await host.MemberAsync(tenantA, "author-1");
        await host.GrantAsync(tenantA, author, FilePermissions.Release);
        var peer = await host.MemberAsync(tenantA, "peer-1");
        await host.GrantAsync(tenantA, peer, FilePermissions.Release);
        await host.AddMemberAsync(author, tenantB.Id);
        await host.Get<AuthorityAdministration>().AssignRoleAsync(tenantB.OwnerUnit.Context, tenantB.OwnerUnit.Principal,
            author.Actor, FilesHost.MemberRole, Cancellation);
        var files = host.Files(release: new TestReleasePolicy());
        await using var writer = await host.EnterAsync(author, tenantA.Id);
        var (version, grant) = await ReleasedAsync(files, writer);
        var missing = version with { FileId = Guid.NewGuid(), VersionId = Guid.NewGuid() };
        var unknown = FileGrantToken.Create(Guid.NewGuid());

        // Another member holds every Files permission, but neither owns the file nor holds a grant on it: each action on it is
        // answered exactly as for a file that does not exist.
        await using var reader = await host.EnterAsync(peer, tenantA.Id);
        foreach (var target in new[] { (version, grant.Token, grant.Grant.GrantId), (missing, unknown, unknown.GrantId) })
            foreach (var (action, run) in Actions(files, reader.Context, reader.Principal, target.Item1, target.Item2, target.Item3)
                         .Where(item => item.Action is not ("upload" or "list files")))
            {
                var refused = await Assert.ThrowsAsync<FileStorageException>(run);
                Assert.True(refused.Code is FileStorageError.NotFound or FileStorageError.GrantRefused, action);
            }
        Assert.Empty(await files.ListFilesAsync(reader.Context, reader.Principal, "documents", cancellationToken: Cancellation));

        // Shared for reading through SEC-05's resource grant, the file is readable and listed, and nothing else is permitted.
        await host.ShareAsync(writer, peer, version.FileId);
        Assert.Equal(version, Assert.Single(await files.ListAsync(reader.Context, reader.Principal, version.FileId, Cancellation)));
        Assert.Equal(FilesHost.Text(100), await FilesHost.ReadAllAsync(await files.ReadAsync(reader.Context, reader.Principal, version.FileId,
            version.VersionId, cancellationToken: Cancellation)));
        Assert.Equal(FilesHost.Text(100), await FilesHost.ReadAllAsync(await files.RedeemGrantAsync(reader.Context, reader.Principal, grant.Token,
            FileGrantPurpose.Download, cancellationToken: Cancellation)));
        Assert.Single(await files.ListFilesAsync(reader.Context, reader.Principal, "documents", cancellationToken: Cancellation));
        Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() => files.DeleteAsync(reader.Context, reader.Principal,
            version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation))).Code);
        Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() => files.IssueGrantAsync(reader.Context,
            reader.Principal, version.FileId, version.VersionId, FileGrantPurpose.Download, TimeSpan.FromMinutes(1), Cancellation))).Code);
        // The revoked share stops at once.
        await host.UnshareAsync(writer, peer, version.FileId);
        Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() => files.ReadAsync(reader.Context, reader.Principal,
            version.FileId, version.VersionId, cancellationToken: Cancellation))).Code);

        // In another tenant the same author, who holds the same role there, finds nothing of tenant A's file.
        await using var elsewhere = await host.EnterAsync(author, tenantB.Id);
        foreach (var (action, run) in Actions(files, elsewhere.Context, elsewhere.Principal, version, grant.Token, grant.Grant.GrantId)
                     .Where(item => item.Action is not ("upload" or "list files" or "release")))
        {
            var refused = await Assert.ThrowsAsync<FileStorageException>(run);
            Assert.True(refused.Code is FileStorageError.NotFound or FileStorageError.GrantRefused, action);
        }
        Assert.Empty(await files.ListFilesAsync(elsewhere.Context, elsewhere.Principal, "documents", cancellationToken: Cancellation));
        Assert.Equal(0, await host.CountAsync("Files", tenantB.Id));
    }

    [Fact]
    public async Task TM_G02_044_File_lists_show_only_what_the_subject_may_read_filtered_in_the_query()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        var peer = await host.MemberAsync(tenant, "peer-1");
        var files = host.Files();
        await using var writer = await host.EnterAsync(author, tenant.Id);
        await using var reader = await host.EnterAsync(peer, tenant.Id);
        var own = new List<FileVersionInfo>();
        for (var index = 0; index < 4; index++)
        {
            // Each file is created a second after the last, so the list's creation order is the order of these uploads.
            host.Clock.Advance(TimeSpan.FromSeconds(1));
            var content = FilesHost.Text(10 + index);
            own.Add(await files.UploadAsync(writer.Context, writer.Principal, FilesHost.Request(content, name: "author-" + index + ".txt",
                container: index == 3 ? "projects/p-1" : "documents"), new MemoryStream(content), Cancellation));
        }
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var theirs = await files.UploadAsync(reader.Context, reader.Principal, FilesHost.Request(FilesHost.Text(5), name: "peer.txt"),
            new MemoryStream(FilesHost.Text(5)), Cancellation);
        await host.ShareAsync(writer, peer, own[1].FileId);

        var authorView = await files.ListFilesAsync(writer.Context, writer.Principal, "documents", cancellationToken: Cancellation);
        Assert.Equal(new[] { "author-0.txt", "author-1.txt", "author-2.txt" }, authorView.Select(file => file.Name).ToArray());
        Assert.All(authorView, file => Assert.Equal(author.Actor, file.CreatedBy));
        Assert.All(authorView, file => Assert.Equal("project-1", file.Owner));
        var peerView = await files.ListFilesAsync(reader.Context, reader.Principal, "documents", cancellationToken: Cancellation);
        Assert.Equal(new[] { own[1].FileId, theirs.FileId }, peerView.Select(file => file.FileId).ToArray());
        // Paging applies to the visible rows only: hidden files take no place in the order.
        Assert.Equal(theirs.FileId, Assert.Single(await files.ListFilesAsync(reader.Context, reader.Principal, "documents", 1, 1, Cancellation)).FileId);
        Assert.Equal(own[3].FileId, Assert.Single(await files.ListFilesAsync(writer.Context, writer.Principal, "projects/p-1",
            cancellationToken: Cancellation)).FileId);
        Assert.Equal(FileStorageError.InvalidName, (await Assert.ThrowsAsync<FileStorageException>(() =>
            files.ListFilesAsync(reader.Context, reader.Principal, "../documents", cancellationToken: Cancellation))).Code);
        foreach (var (offset, limit) in new[] { (-1, 10), (0, 0), (0, FileService.MaxListLimit + 1), (FileService.MaxListOffset + 1, 10) })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.ListFilesAsync(reader.Context, reader.Principal, "documents", offset, limit,
                Cancellation));
    }
}
