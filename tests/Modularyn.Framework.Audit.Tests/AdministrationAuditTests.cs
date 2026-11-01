using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;
using Npgsql;
using Xunit;
using static Modularyn.Framework.Audit.Tests.AuditFixture;

namespace Modularyn.Framework.Audit.Tests;

// TM-G02-051 for the directory and identity, on PostgreSQL with the real stores and the real audit store: each tenant,
// account, session and delegation change commits with its record in the change's own transaction, each use of a
// delegation is recorded before it acts, and a change or use whose record cannot be written is refused and changes nothing.
// No record holds a password, a session token or a session identifier's secret.
[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class AdministrationAuditTests
{
    [Fact]
    public async Task TM_G02_051_Each_directory_and_identity_change_commits_with_its_record()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var created = Assert.Single(await database.AuditAsync(TenantDirectoryAdministration.TenantCreatedAction));
        Assert.Equal(tenant, created.Tenant);
        Assert.Equal(DurableHost.Service, created.Actor);
        Assert.Equal("tenant", created.ResourceType);
        Assert.Equal(tenant.ToString("D"), created.Resource);
        Assert.Equal(TenantDirectoryAdministration.OperationId, created.Operation);
        Assert.NotNull(created.Unit);
        Assert.Equal(clock.Now, created.At);
        await host.Get<TenantDirectoryAdministration>().SetStateAsync(await host.HostAsync(TenantDirectoryAdministration.OperationId), tenant,
            TenantState.Suspended, Token);
        Assert.Equal("state Active to Suspended", Assert.Single(await database.AuditAsync(TenantDirectoryAdministration.StateChangedAction)).Detail);
        await host.Get<TenantDirectoryAdministration>().SetStateAsync(await host.HostAsync(TenantDirectoryAdministration.OperationId), tenant,
            TenantState.Active, Token);

        var subject = await host.UserAsync(tenant);
        var account = Assert.Single(await database.AuditAsync(IdentityAuditActions.AccountCreated), row => row.Subject == subject.Id.ToString("D"));
        Assert.Equal(DurableHost.Service, account.Actor);
        Assert.Equal(IdentityHostOperations.AccountsId, account.Operation);
        Assert.Equal("account", account.ResourceType);
        var helper = await host.UserAsync(tenant);
        var accounts = host.Get<IAccountService>();
        Assert.True(await accounts.DisableAsync(IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.AccountsId)), helper.Id, Token));
        Assert.True(await accounts.EnableAsync(IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.AccountsId)), helper.Id, Token));
        Assert.Single(await database.AuditAsync(IdentityAuditActions.AccountDisabled), row => row.Subject == helper.Id.ToString("D"));
        Assert.Single(await database.AuditAsync(IdentityAuditActions.AccountEnabled), row => row.Subject == helper.Id.ToString("D"));

        // A delegation's issue, each use and its revocation, each with who acted, for whom and which delegation.
        await using var subjectUnit = await host.EnterAsync(subject, tenant);
        await using var helperUnit = await host.EnterAsync(helper, tenant);
        var issued = await host.Get<IDelegationService>().IssueAsync(new DelegationRequest(helper.Id, subject.Id, [DurableModel.Apply], "Support case",
            tenant, clock.Now.AddMinutes(30)), subjectUnit.Principal, Token);
        Assert.Equal(DelegationIssueStatus.Issued, issued.Status);
        var grant = issued.GrantId!.Value.ToString("D");
        var issue = Assert.Single(await database.AuditAsync(IdentityAuditActions.DelegationIssued));
        Assert.Equal(subject.Id.ToString("D"), issue.Actor);
        Assert.Equal(grant, issue.Resource);
        Assert.NotNull(await host.Get<IDelegationService>().ResolveAsync(issued.GrantId.Value, helperUnit.Principal, Token));
        var used = Assert.Single(await database.AuditAsync(IdentityAuditActions.DelegationUsed));
        Assert.Equal(helper.Id.ToString("D"), used.Actor);
        Assert.Equal(subject.Id.ToString("D"), used.Subject);
        Assert.Equal(tenant, used.Tenant);
        Assert.Equal(grant, used.Resource);
        Assert.True(await host.Get<IDelegationService>().RevokeAsync(issued.GrantId.Value, subjectUnit.Principal, Token));
        Assert.Equal(grant, Assert.Single(await database.AuditAsync(IdentityAuditActions.DelegationRevoked)).Resource);

        // A session revoked by an identity administrator, and an owner's password change, which also ends its sessions.
        var signedIn = await host.Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(helper.Name, new SecretText(helper.Password),
            "203.0.113.3"), Token);
        var session = signedIn.Session!;
        Assert.True(await host.Get<ISessionService>().RevokeAsync(IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.SessionsId)),
            session.SessionId, Token));
        var revoked = Assert.Single(await database.AuditAsync(IdentityAuditActions.SessionRevoked));
        Assert.Equal(DurableHost.Service, revoked.Actor);
        Assert.Equal(helper.Id.ToString("D"), revoked.Subject);
        Assert.Equal(IdentityHostOperations.SessionsId, revoked.Operation);
        var replacement = "pw-" + Guid.NewGuid().ToString("N");
        Assert.Equal(PasswordChangeStatus.Changed, (await host.Get<IAccountService>().ChangePasswordAsync(subject.Id, new SecretText(subject.Password),
            new SecretText(replacement), "203.0.113.3", Token)).Status);
        var changed = Assert.Single(await database.AuditAsync(IdentityAuditActions.PasswordChanged));
        Assert.Equal(subject.Id.ToString("D"), changed.Actor);
        Assert.Equal(subject.Id.ToString("D"), changed.Subject);

        // No record holds a password or any part of a session token.
        var everything = await EverythingAsync(database);
        foreach (var secret in new[] { subject.Password, helper.Password, replacement, session.Token.Reveal() })
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TM_G02_051_A_directory_or_identity_change_whose_record_cannot_be_written_changes_nothing()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var subject = await host.UserAsync(tenant);
        var helper = await host.UserAsync(tenant);
        await using var subjectUnit = await host.EnterAsync(subject, tenant);
        await using var helperUnit = await host.EnterAsync(helper, tenant);
        var issued = await host.Get<IDelegationService>().IssueAsync(new DelegationRequest(helper.Id, subject.Id, [DurableModel.Apply], "Support case",
            tenant, clock.Now.AddMinutes(30)), subjectUnit.Principal, Token);
        // Each host scope is entered, and its grant recorded, before the store becomes unavailable.
        var directoryScope = await host.HostAsync(TenantDirectoryAdministration.OperationId);
        var accountScope = IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.AccountsId));
        var sessionScope = IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.SessionsId));
        var tenants = await CountAsync(database, database.DirectorySchema, "Tenants");
        var accounts = await CountAsync(database, database.IdentitySchema, "Accounts");
        var records = (await database.AuditAsync()).Length;

        await BreakAsync(database);
        try
        {
            Assert.Equal("MTN023", (await Assert.ThrowsAsync<TenancyException>(() =>
                host.Get<TenantDirectoryAdministration>().CreateTenantAsync(directoryScope, "Unrecorded tenant", Token))).Code);
            Assert.Equal("MTN023", (await Assert.ThrowsAsync<TenancyException>(() =>
                host.Get<TenantDirectoryAdministration>().SetStateAsync(directoryScope, tenant, TenantState.Suspended, Token))).Code);
            Assert.Equal(IdentityAuthorityCodes.AuditUnavailable, (await Assert.ThrowsAsync<IdentityAuthorityException>(() =>
                host.Get<IAccountService>().CreateLocalAccountAsync(accountScope, "unrecorded-" + Guid.NewGuid().ToString("N"),
                    new SecretText("pw-" + Guid.NewGuid().ToString("N")), Token))).Code);
            Assert.Equal(IdentityAuthorityCodes.AuditUnavailable, (await Assert.ThrowsAsync<IdentityAuthorityException>(() =>
                host.Get<IAccountService>().DisableAsync(accountScope, helper.Id, Token))).Code);
            Assert.Equal(IdentityAuthorityCodes.AuditUnavailable, (await Assert.ThrowsAsync<IdentityAuthorityException>(() =>
                host.Get<ISessionService>().RevokeAllAsync(sessionScope, helper.Id, Token))).Code);
            Assert.Equal(IdentityAuthorityCodes.AuditUnavailable, (await Assert.ThrowsAsync<IdentityAuthorityException>(() =>
                host.Get<IDelegationService>().RevokeAsync(issued.GrantId!.Value, subjectUnit.Principal, Token))).Code);
            // A delegation whose use cannot be recorded does not resolve, so the request does not act under it.
            Assert.Null(await host.Get<IDelegationService>().ResolveAsync(issued.GrantId!.Value, helperUnit.Principal, Token));
        }
        finally
        {
            await RepairAsync(database);
        }

        Assert.Equal(tenants, await CountAsync(database, database.DirectorySchema, "Tenants"));
        Assert.Equal(accounts, await CountAsync(database, database.IdentitySchema, "Accounts"));
        Assert.Equal(records, (await database.AuditAsync()).Length);
        // The tenant is still active, the account still enabled with its sessions, and the delegation still resolves.
        await using (var again = await host.EnterAsync(helper, tenant))
            Assert.NotNull(await host.Get<IDelegationService>().ResolveAsync(issued.GrantId!.Value, again.Principal, Token));
        var session = await host.Get<ISessionService>().DescribeAsync(helperUnit.Principal.SessionId!.Value, Token);
        Assert.NotNull(session);
        Assert.Null(session.EndedAt);
    }

    private static Task<long> CountAsync(DurableDatabase database, string schema, string table) =>
        database.ScalarAsync<long>("SELECT count(*) FROM \"" + schema + "\".\"" + table + "\"");
}
