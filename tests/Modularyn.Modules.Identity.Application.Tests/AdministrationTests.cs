using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

// SEC-05's gate on identity's administrative writes, over in-memory stores: each needs an active, audited host scope
// granted for its own identity operation, or, where the write is the owner's, the owner's own current session.
public sealed class AdministrationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task<string> RefusedAsync(Func<Task> write) => (await Assert.ThrowsAsync<IdentityAuthorityException>(write)).Code;

    [Fact(DisplayName = "TM-G02-019 account and membership administration need an audited host scope for their own operation; nothing else authorizes them")]
    public async Task TM_G02_019_Account_and_membership_administration_need_host_scope_for_their_own_operation()
    {
        await using var identity = UnitIdentity.Create();
        var (account, password) = await identity.AccountAsync("member-1");
        var (session, principal) = await identity.SignInAsync("member-1", password);
        var owner = IdentityAuthority.Owner(principal);
        var accounts = identity.Get<IAccountService>();
        var memberships = identity.Get<ITenantMemberships>();
        var tenant = Guid.NewGuid();
        var refusals = new[]
        {
            await identity.Administrator.TenantMemberAsync(), await identity.Administrator.SessionsAsync(),
            await identity.Administrator.EndedHostAsync(IdentityHostOperations.AccountsId), owner
        };

        // A tenant context, a host scope for another operation, an ended host scope and the account's own owner are all refused.
        foreach (var authority in refusals)
        {
            Assert.Equal(IdentityAuthorityCodes.HostOperationRequired, await RefusedAsync(() =>
                accounts.CreateLocalAccountAsync(authority, "member-2", new SecretText(Generated.Password()), Cancellation)));
            Assert.Equal(IdentityAuthorityCodes.HostOperationRequired, await RefusedAsync(() => accounts.DisableAsync(authority, account, Cancellation)));
            Assert.Equal(IdentityAuthorityCodes.HostOperationRequired, await RefusedAsync(() => accounts.EnableAsync(authority, account, Cancellation)));
            Assert.Equal(IdentityAuthorityCodes.HostOperationRequired, await RefusedAsync(() => accounts.SecurityStateChangedAsync(authority, account, Cancellation)));
            Assert.Equal(IdentityAuthorityCodes.HostOperationRequired, await RefusedAsync(() => memberships.AddMemberAsync(authority, account, tenant, Cancellation)));
            Assert.Equal(IdentityAuthorityCodes.HostOperationRequired, await RefusedAsync(() => memberships.RemoveMemberAsync(authority, account, tenant, Cancellation)));
        }
        // The accounts operation does not reach memberships, and the memberships operation does not reach accounts.
        Assert.Equal(IdentityAuthorityCodes.HostOperationRequired, await RefusedAsync(async () =>
            await memberships.AddMemberAsync(await identity.Administrator.AccountsAsync(), account, tenant, Cancellation)));
        Assert.Equal(IdentityAuthorityCodes.HostOperationRequired, await RefusedAsync(async () =>
            await accounts.DisableAsync(await identity.Administrator.MembershipsAsync(), account, Cancellation)));
        // Nothing changed: the account is enabled, its session is current and it belongs to no tenant.
        Assert.False(await memberships.IsMemberAsync(account, tenant, Cancellation));
        Assert.NotNull(await identity.Get<ISessionService>().ValidateAsync(session.Token, Cancellation));

        // With the right operation each write is made.
        Assert.True(await memberships.AddMemberAsync(await identity.Administrator.MembershipsAsync(), account, tenant, Cancellation));
        Assert.True(await accounts.DisableAsync(await identity.Administrator.AccountsAsync(), account, Cancellation));
        Assert.False(await memberships.IsMemberAsync(account, tenant, Cancellation));
    }

    [Fact(DisplayName = "TM-G02-017 an owner revokes its own sessions, an identity administrator any account's, and nobody else any")]
    public async Task TM_G02_017_Sessions_are_revoked_by_their_owner_or_an_identity_administrator()
    {
        await using var identity = UnitIdentity.Create();
        var (first, firstPassword) = await identity.AccountAsync("member-1");
        var (second, secondPassword) = await identity.AccountAsync("member-2");
        var sessions = identity.Get<ISessionService>();
        var (firstSession, firstPrincipal) = await identity.SignInAsync("member-1", firstPassword);
        var (secondSession, secondPrincipal) = await identity.SignInAsync("member-2", secondPassword);
        var firstOwner = IdentityAuthority.Owner(firstPrincipal);

        // Another account's owner and a host scope for another operation are refused, before anything ends.
        Assert.Equal(IdentityAuthorityCodes.OwnerOrHostRequired, await RefusedAsync(() => sessions.RevokeAllAsync(firstOwner, second, Cancellation)));
        Assert.Equal(IdentityAuthorityCodes.OwnerOrHostRequired, await RefusedAsync(() => sessions.RevokeAsync(firstOwner, secondSession.SessionId, Cancellation)));
        Assert.Equal(IdentityAuthorityCodes.OwnerOrHostRequired, await RefusedAsync(async () =>
            await sessions.RevokeAllAsync(await identity.Administrator.AccountsAsync(), second, Cancellation)));
        Assert.NotNull(await sessions.ValidateAsync(secondSession.Token, Cancellation));
        // A delegated principal is not its subject's owner, whatever the delegation covers.
        var delegated = ExecutionPrincipal.ForDelegation(secondPrincipal.Actor, secondSession.SessionId, secondSession.AuthenticatedAt, firstPrincipal.Actor,
            new DelegatedAuthority(Guid.NewGuid(), ["identity.sessions"], "Help member-1", null, identity.Clock.Now.AddMinutes(-1), identity.Clock.Now.AddMinutes(10)));
        Assert.Equal(IdentityAuthorityCodes.OwnerOrHostRequired, await RefusedAsync(() => sessions.RevokeAllAsync(IdentityAuthority.Owner(delegated), first, Cancellation)));

        // The owner ends its own sessions; afterwards its principal is no longer an owner's.
        Assert.True(await sessions.RevokeAsync(firstOwner, firstSession.SessionId, Cancellation));
        Assert.Equal(IdentityAuthorityCodes.OwnerOrHostRequired, await RefusedAsync(() => sessions.RevokeAllAsync(firstOwner, first, Cancellation)));
        // An identity administrator in host scope ends any account's sessions.
        Assert.Equal(1, await sessions.RevokeAllAsync(await identity.Administrator.SessionsAsync(), second, Cancellation));
        Assert.Null(await sessions.ValidateAsync(secondSession.Token, Cancellation));
    }
}
