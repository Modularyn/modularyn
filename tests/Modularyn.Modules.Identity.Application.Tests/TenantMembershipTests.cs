using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

// The membership answer tenancy's seam asks for (TM-G02-025, SEC-04's control): a current record, never a cached claim.
public sealed class TenantMembershipTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "TM-G02-025 membership is a current record of an enabled account, and removing it never removes the account")]
    public async Task TM_G02_025_Membership_is_a_current_record_of_an_enabled_account()
    {
        await using var identity = UnitIdentity.Create();
        var (account, password) = await identity.AccountAsync("member-1");
        var memberships = identity.Get<ITenantMemberships>();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        Assert.False(await memberships.IsMemberAsync(account, tenantA, Cancellation));
        Assert.True(await memberships.AddMemberAsync(account, tenantA, Cancellation));
        Assert.False(await memberships.AddMemberAsync(account, tenantA, Cancellation));
        Assert.False(await memberships.AddMemberAsync(Guid.NewGuid(), tenantA, Cancellation));
        Assert.True(await memberships.IsMemberAsync(account, tenantA, Cancellation));
        Assert.False(await memberships.IsMemberAsync(account, tenantB, Cancellation));
        Assert.False(await memberships.IsMemberAsync(account, Guid.Empty, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => memberships.AddMemberAsync(account, Guid.Empty, Cancellation));

        Assert.True(await identity.Get<IAccountService>().DisableAsync(account, Cancellation));
        Assert.False(await memberships.IsMemberAsync(account, tenantA, Cancellation));
        Assert.True(await identity.Get<IAccountService>().EnableAsync(account, Cancellation));
        Assert.True(await memberships.IsMemberAsync(account, tenantA, Cancellation));
        Assert.True(await memberships.RemoveMemberAsync(account, tenantA, Cancellation));
        Assert.False(await memberships.IsMemberAsync(account, tenantA, Cancellation));
        Assert.False(await memberships.RemoveMemberAsync(account, tenantA, Cancellation));
        Assert.NotNull((await identity.SignInAsync("member-1", password)).Principal);
    }
}
