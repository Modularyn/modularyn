using System.Text;
using System.Text.Json;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

/// <summary>Download grants over real PostgreSQL, the filesystem provider and the real evaluator.</summary>
[Collection(FilesPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class DownloadGrantTests
{
    private static CancellationToken Cancellation => FilesHost.Cancellation;

    private static async Task<FileVersionInfo> ReleasedAsync(FileService files, Member member, byte[] content, Guid? fileId = null, string name = "notes.txt")
    {
        var version = await files.UploadAsync(member.Context, member.Principal, FilesHost.Request(content, fileId, name), new MemoryStream(content), Cancellation);
        return await files.ReleaseAsync(member.Context, member.Principal, version.FileId, version.VersionId, Cancellation);
    }

    private static Task<FileReadResult> RedeemAsync(FileService files, Member member, FileGrantToken token, FileGrantPurpose purpose = FileGrantPurpose.Download) =>
        files.RedeemGrantAsync(member.Context, member.Principal, token, purpose, cancellationToken: Cancellation);

    private static async Task RefusedAsync(FileService files, Member member, FileGrantToken token, FileGrantPurpose purpose = FileGrantPurpose.Download) =>
        Assert.Equal(FileStorageError.GrantRefused, (await Assert.ThrowsAsync<FileStorageException>(() => RedeemAsync(files, member, token, purpose))).Code);

    [Fact]
    public async Task TM_G02_044_A_download_grant_is_bound_to_its_tenant_version_purpose_and_expiry_and_stored_only_as_a_verifier()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenantA = await host.TenantAsync("Tenant A");
        var tenantB = await host.TenantAsync("Tenant B");
        var author = await host.MemberAsync(tenantA, "author-1");
        await host.GrantAsync(tenantA, author, FilePermissions.Release);
        await host.AddMemberAsync(author, tenantB.Id);
        await host.Get<AuthorityAdministration>().AssignRoleAsync(tenantB.OwnerUnit.Context, tenantB.OwnerUnit.Principal,
            author.Actor, FilesHost.MemberRole, Cancellation);
        var files = host.Files(release: new TestReleasePolicy());
        await using var member = await host.EnterAsync(author, tenantA.Id);
        var first = FilesHost.Text(120, 'f');
        var version = await ReleasedAsync(files, member, first);
        var issued = await files.IssueGrantAsync(member.Context, member.Principal, version.FileId, version.VersionId, FileGrantPurpose.Download,
            TimeSpan.FromMinutes(10), Cancellation);
        var grant = issued.Grant;
        Assert.Equal(new FileGrantInfo(grant.GrantId, version.FileId, version.VersionId, FileGrantPurpose.Download, author.Actor, host.Clock.Now,
            host.Clock.Now.AddMinutes(10), FileGrantState.Active), grant);
        Assert.Equal(issued.Token.GrantId, grant.GrantId);

        // Only the verifier is stored: no column holds the token, its secret or its text, and the verifier binds the tenant.
        var token = issued.Token.Reveal();
        var secret = Encoding.ASCII.GetBytes(token[(FileGrantToken.Prefix.Length + 32)..]);
        var stored = await host.StoredValuesAsync();
        Assert.DoesNotContain(stored, value => value is string text && text.Contains(token[(FileGrantToken.Prefix.Length + 32)..], StringComparison.Ordinal));
        Assert.DoesNotContain(stored, value => value is byte[] bytes && (bytes.AsSpan().IndexOf(secret) >= 0
            || bytes.AsSpan().IndexOf(System.Buffers.Text.Base64Url.DecodeFromChars(token.AsSpan(FileGrantToken.Prefix.Length + 32))) >= 0));
        Assert.Contains(stored, value => value is byte[] bytes && bytes.SequenceEqual(issued.Token.ComputeVerifier(tenantA.Id)));
        Assert.DoesNotContain(stored, value => value is byte[] bytes && bytes.SequenceEqual(issued.Token.ComputeVerifier(tenantB.Id)));
        // The token never appears in what is formatted, serialized or thrown.
        Assert.Equal(FileGrantToken.Redacted, issued.Token.ToString());
        Assert.DoesNotContain(token, issued.ToString(), StringComparison.Ordinal);
        Assert.Equal("\"" + FileGrantToken.Redacted + "\"", JsonSerializer.Serialize(issued.Token));

        // It delivers its own version, for its own purpose, in its own tenant, until it expires.
        var delivered = await RedeemAsync(files, member, issued.Token);
        Assert.Equal(version, delivered.Version);
        Assert.Equal(new FileDelivery("text/plain", FileDisposition.Attachment, "notes.txt"), delivered.Delivery);
        Assert.Equal(first, await FilesHost.ReadAllAsync(delivered));
        // A later version of the same file, created a second after the grant, is not what the grant delivers.
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        var later = await ReleasedAsync(files, member, FilesHost.Text(80, 's'), version.FileId);
        Assert.Equal(first, await FilesHost.ReadAllAsync(await RedeemAsync(files, member, issued.Token)));
        Assert.Equal(FilesHost.Text(10, 'f'), await FilesHost.ReadAllAsync(await files.RedeemGrantAsync(member.Context, member.Principal, issued.Token,
            FileGrantPurpose.Download, new FileReadRange(0, 10), Cancellation)));
        Assert.NotEqual(version.VersionId, later.VersionId);
        await RefusedAsync(files, member, issued.Token, FileGrantPurpose.Preview);
        await RefusedAsync(files, member, issued.Token, FileGrantPurpose.SingleDownload);
        // A different secret for the same grant, a token that names no grant, and the grant presented in another tenant fail alike.
        await RefusedAsync(files, member, FileGrantToken.Create(grant.GrantId));
        await RefusedAsync(files, member, FileGrantToken.Create(Guid.NewGuid()));
        await using (var elsewhere = await host.EnterAsync(author, tenantB.Id)) await RefusedAsync(files, elsewhere, issued.Token);
        Assert.False(FileGrantToken.TryParse(token[..^1] + (token[^1] == 'A' ? 'B' : 'A') + "x", out _));
        var refusal = await Assert.ThrowsAsync<FileStorageException>(() => RedeemAsync(files, member, issued.Token, FileGrantPurpose.Preview));
        Assert.DoesNotContain(token[FileGrantToken.Prefix.Length..], refusal.ToString(), StringComparison.Ordinal);

        // A lifetime beyond the policy's maximum, or none, is refused; at the expiry instant the grant ends.
        foreach (var lifetime in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), FilesPolicy.DefaultMaximumGrantLifetime + TimeSpan.FromTicks(10) })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.IssueGrantAsync(member.Context, member.Principal, version.FileId,
                version.VersionId, FileGrantPurpose.Download, lifetime, Cancellation));
        host.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(10));
        await using (var still = await host.EnterAsync(author, tenantA.Id)) Assert.Equal(first, await FilesHost.ReadAllAsync(await RedeemAsync(files, still, issued.Token)));
        host.Clock.Advance(TimeSpan.FromTicks(10));
        await using var expired = await host.EnterAsync(author, tenantA.Id);
        await RefusedAsync(files, expired, issued.Token);
        Assert.Equal(FileGrantState.Expired, Assert.Single(await files.ListGrantsAsync(expired.Context, expired.Principal, version.FileId, Cancellation)).State);
        // Recovery removes the expired grant; its token stays refused.
        Assert.Equal(0, await files.RecoverAsync(expired.Context, Cancellation));
        Assert.Equal(0, await host.CountAsync("FileGrants", tenantA.Id));
        await RefusedAsync(files, expired, issued.Token);
    }

    [Fact]
    public async Task TM_G02_044_Revocation_deletion_and_single_use_end_a_grant_and_nothing_revives_it()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        var peer = await host.MemberAsync(tenant, "peer-1");
        var bystander = await host.UserAsync("bystander-1", tenant.Id);
        var files = host.Files(release: new TestReleasePolicy());
        await using var member = await host.EnterAsync(author, tenant.Id);
        var content = FilesHost.Text(64);
        var version = await ReleasedAsync(files, member, content);
        async Task<IssuedFileGrant> IssueAsync(FileGrantPurpose purpose = FileGrantPurpose.Download) =>
            await files.IssueGrantAsync(member.Context, member.Principal, version.FileId, version.VersionId, purpose, TimeSpan.FromMinutes(5), Cancellation);

        // Revoked individually, a grant stops at once; revoking again changes nothing, and only its file's grant holders may.
        var revoked = await IssueAsync();
        var kept = await IssueAsync();
        await using (var other = await host.EnterAsync(peer, tenant.Id))
        {
            Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() =>
                files.RevokeGrantAsync(other.Context, other.Principal, revoked.Grant.GrantId, Cancellation))).Code);
            Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() =>
                files.RevokeGrantAsync(other.Context, other.Principal, Guid.NewGuid(), Cancellation))).Code);
        }
        await using (var outsider = await host.EnterAsync(bystander, tenant.Id))
            Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<FileAccessDeniedException>(() =>
                files.RevokeGrantAsync(outsider.Context, outsider.Principal, revoked.Grant.GrantId, Cancellation))).Code);
        var ended = await files.RevokeGrantAsync(member.Context, member.Principal, revoked.Grant.GrantId, Cancellation);
        Assert.Equal(FileGrantState.Revoked, ended.State);
        Assert.Equal(ended, await files.RevokeGrantAsync(member.Context, member.Principal, revoked.Grant.GrantId, Cancellation));
        await RefusedAsync(files, member, revoked.Token);
        Assert.Equal(content, await FilesHost.ReadAllAsync(await RedeemAsync(files, member, kept.Token)));
        // A new grant has a new token; the revoked token stays refused.
        var renewed = await IssueAsync();
        Assert.NotEqual(revoked.Token.Reveal(), renewed.Token.Reveal());
        await RefusedAsync(files, member, revoked.Token);

        // A single download is consumed by its first delivery, and of two concurrent deliveries exactly one succeeds.
        var single = await IssueAsync(FileGrantPurpose.SingleDownload);
        Assert.Equal(content, await FilesHost.ReadAllAsync(await RedeemAsync(files, member, single.Token, FileGrantPurpose.SingleDownload)));
        await RefusedAsync(files, member, single.Token, FileGrantPurpose.SingleDownload);
        var contested = await IssueAsync(FileGrantPurpose.SingleDownload);
        await using var second = await host.EnterAsync(author, tenant.Id);
        var outcomes = await Task.WhenAll(new[] { member, second }.Select(async unit =>
        {
            try { return await FilesHost.ReadAllAsync(await RedeemAsync(files, unit, contested.Token, FileGrantPurpose.SingleDownload)); }
            catch (FileStorageException exception) when (exception.Code == FileStorageError.GrantRefused) { return null; }
        }));
        Assert.Single(outcomes, outcome => outcome is not null);
        Assert.Equal(content, outcomes.Single(outcome => outcome is not null));
        var states = (await files.ListGrantsAsync(member.Context, member.Principal, version.FileId, Cancellation)).ToDictionary(row => row.GrantId, row => row.State);
        Assert.Equal(FileGrantState.Consumed, states[single.Grant.GrantId]);
        Assert.Equal(FileGrantState.Consumed, states[contested.Grant.GrantId]);

        // Deleting the version revokes its open grants in the same change, and restoring it revives none of them.
        var deleted = await files.DeleteAsync(member.Context, member.Principal, version.FileId, version.VersionId, version.ConcurrencyVersion, Cancellation);
        await RefusedAsync(files, member, kept.Token);
        var restored = await files.RestoreAsync(member.Context, member.Principal, version.FileId, version.VersionId, deleted.ConcurrencyVersion, Cancellation);
        Assert.False(restored.IsDeleted);
        await RefusedAsync(files, member, kept.Token);
        await RefusedAsync(files, member, renewed.Token);
        states = (await files.ListGrantsAsync(member.Context, member.Principal, version.FileId, Cancellation)).ToDictionary(row => row.GrantId, row => row.State);
        Assert.Equal(FileGrantState.Revoked, states[kept.Grant.GrantId]);
        Assert.Equal(FileGrantState.Revoked, states[renewed.Grant.GrantId]);
        Assert.Equal(FileGrantState.Consumed, states[single.Grant.GrantId]);
    }

    [Fact]
    public async Task TM_G02_044_A_grant_is_served_only_with_its_holder_s_current_authorization_and_never_without_a_session()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        var peer = await host.MemberAsync(tenant, "peer-1");
        var files = host.Files(release: new TestReleasePolicy());
        await using var member = await host.EnterAsync(author, tenant.Id);
        var content = FilesHost.Text(50);
        var version = await ReleasedAsync(files, member, content);
        var issued = await files.IssueGrantAsync(member.Context, member.Principal, version.FileId, version.VersionId, FileGrantPurpose.Download,
            TimeSpan.FromMinutes(5), Cancellation);

        // The token alone confers nothing: a member who may not read the file is refused, as for an unusable grant.
        await using var other = await host.EnterAsync(peer, tenant.Id);
        await RefusedAsync(files, other, issued.Token);
        await host.ShareAsync(member, peer, version.FileId);
        Assert.Equal(content, await FilesHost.ReadAllAsync(await RedeemAsync(files, other, issued.Token)));
        // Revoking the holder's access stops the grant at its next delivery, though the grant itself is still active.
        await host.UnshareAsync(member, peer, version.FileId);
        await RefusedAsync(files, other, issued.Token);
        Assert.Equal(FileGrantState.Active, Assert.Single(await files.ListGrantsAsync(member.Context, member.Principal, version.FileId, Cancellation)).State);
        // An ended session, a removed membership and no session at all are each refused by the evaluator.
        await host.ShareAsync(member, peer, version.FileId);
        var (session, principal) = await host.SignInAsync(peer);
        await using (var signedOut = await host.EnterAsync(peer, tenant.Id, principal))
        {
            Assert.True(await host.Get<ISessionService>().SignOutAsync(session.Token, Cancellation));
            Assert.Equal(AuthorizationCodes.PrincipalNotCurrent, (await Assert.ThrowsAsync<FileAccessDeniedException>(() =>
                RedeemAsync(files, signedOut, issued.Token))).Code);
        }
        Assert.Equal(AuthorizationCodes.Unauthenticated, (await Assert.ThrowsAsync<FileAccessDeniedException>(() =>
            files.RedeemGrantAsync(other.Context, null, issued.Token, FileGrantPurpose.Download, cancellationToken: Cancellation))).Code);
        await host.RemoveMemberAsync(peer, tenant.Id);
        Assert.Equal(AuthorizationCodes.SubjectNotMember, (await Assert.ThrowsAsync<FileAccessDeniedException>(() =>
            RedeemAsync(files, other, issued.Token))).Code);
    }

    [Fact]
    public async Task TM_G02_044_Previews_are_inline_only_for_approved_types_and_every_delivery_disables_sniffing()
    {
        await using var host = await FilesHost.CreateAsync();
        var tenant = await host.TenantAsync("Tenant A");
        var author = await host.MemberAsync(tenant, "author-1");
        await host.GrantAsync(tenant, author, FilePermissions.Release);
        var files = host.Files(release: new TestReleasePolicy());
        await using var member = await host.EnterAsync(author, tenant.Id);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. FilesHost.Bytes(32)];
        var image = await ReleasedAsync(files, member, png, name: "r\u00e9sum\u00e9 (final).png");
        var text = await ReleasedAsync(files, member, FilesHost.Text(20), name: "notes.txt");
        async Task<FileDelivery> DeliveryAsync(FileVersionInfo version, FileGrantPurpose purpose)
        {
            var grant = await files.IssueGrantAsync(member.Context, member.Principal, version.FileId, version.VersionId, purpose, TimeSpan.FromMinutes(1),
                Cancellation);
            await using var read = await RedeemAsync(files, member, grant.Token, purpose);
            return read.Delivery;
        }
        var preview = await DeliveryAsync(image, FileGrantPurpose.Preview);
        Assert.Equal(new FileDelivery("image/png", FileDisposition.Inline, "r\u00e9sum\u00e9 (final).png"), preview);
        Assert.Equal(new KeyValuePair<string, string>[]
        {
            new("Content-Type", "image/png"),
            new("X-Content-Type-Options", "nosniff"),
            new("Content-Disposition", "inline; filename=\"r_sum_ (final).png\"; filename*=UTF-8''r%C3%A9sum%C3%A9%20%28final%29.png"),
            new("Content-Security-Policy", "default-src 'none'; sandbox"),
            new("Cross-Origin-Resource-Policy", "same-origin"),
            new("Cache-Control", "private, no-store")
        }, preview.Headers());
        Assert.Equal(FileDisposition.Attachment, (await DeliveryAsync(image, FileGrantPurpose.Download)).Disposition);
        Assert.Equal(FileDisposition.Attachment, (await DeliveryAsync(text, FileGrantPurpose.Preview)).Disposition);
        // A deployment that approves no type for inline display delivers previews as attachments too.
        var strict = host.Files(new FilesPolicy(inlineContentTypes: []));
        var grant = await strict.IssueGrantAsync(member.Context, member.Principal, image.FileId, image.VersionId, FileGrantPurpose.Preview,
            TimeSpan.FromMinutes(1), Cancellation);
        await using var read = await RedeemAsync(strict, member, grant.Token, FileGrantPurpose.Preview);
        Assert.Equal(FileDisposition.Attachment, read.Delivery.Disposition);
        Assert.StartsWith("attachment;", read.Delivery.Headers().Single(header => header.Key == "Content-Disposition").Value, StringComparison.Ordinal);
    }
}
