using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Npgsql;
using Xunit;
using static Modularyn.Framework.Audit.Tests.AuditFixture;

namespace Modularyn.Framework.Audit.Tests;

// TM-G02-051 for authority, on PostgreSQL with the real tenancy, identity, authorization and audit store: every grant,
// role, membership, tenant owner and host-authority change commits with exactly one record in its own transaction, a
// change whose record cannot be written is refused and changes nothing, and a change that retries leaves one record.
// TM-G02-017: removing a membership ends the grants made during it, and adding the member again revives none of them.
[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class AuthorityAuditTests
{
    [Fact]
    public async Task TM_G02_051_Each_authority_change_commits_with_exactly_one_record()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var member = await host.UserAsync(tenant);
        await using var ownerUnit = await host.EnterAsync(owner, tenant);
        var administration = host.Get<AuthorityAdministration>();

        Assert.True(await administration.GrantPermissionAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, DurableModel.Apply, Token));
        // A change that changes nothing is not made and leaves no record.
        Assert.False(await administration.GrantPermissionAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, DurableModel.Apply, Token));
        Assert.True(await administration.DefineRoleAsync(ownerUnit.Context, ownerUnit.Principal, "appliers", [DurableModel.Apply], [], Token));
        Assert.True(await administration.AssignRoleAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, "appliers", Token));
        Assert.True(await administration.UnassignRoleAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, "appliers", Token));
        Assert.True(await administration.RevokePermissionAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, DurableModel.Apply, Token));
        var hostAuthority = host.Get<HostAuthorityAdministration>();
        Assert.True(await hostAuthority.GrantAsync(await host.HostAsync(AuthorizationModule.HostAuthorityOperation.Id), member.Actor,
            TenantDirectoryAdministrationOperation, Token));

        var records = await database.AuditAsync();
        Assert.Equal(2, records.Count(row => row.Action == "authority.membership-added" && row.Tenant == tenant));
        Assert.Single(records, row => row.Action == "authority.tenant-owner-added" && row.Subject == owner.Id.ToString("D"));
        foreach (var action in new[] { "authority.permission-granted", "authority.role-defined", "authority.role-assigned", "authority.role-unassigned",
            "authority.permission-revoked", "authority.host-authority-granted" })
            Assert.Single(records, row => row.Action == action);
        var grant = Assert.Single(records, row => row.Action == "authority.permission-granted");
        Assert.Equal(owner.Id.ToString("D"), grant.Actor);
        Assert.Equal(member.Id.ToString("D"), grant.Subject);
        Assert.Equal(tenant, grant.Tenant);
        Assert.Equal("permission", grant.ResourceType);
        Assert.Equal(DurableModel.Apply, grant.Detail);
        Assert.Equal(clock.Now, grant.At);
        var granted = Assert.Single(records, row => row.Action == "authority.host-authority-granted");
        Assert.Null(granted.Tenant);
        Assert.Equal(DurableHost.Service, granted.Actor);
        Assert.Equal("host-operation", granted.ResourceType);
        Assert.Equal(TenantDirectoryAdministrationOperation, granted.Resource);
        Assert.Equal(records.Length, records.Select(row => row.Id).Distinct().Count());
    }

    [Fact]
    public async Task TM_G02_051_An_authority_change_whose_record_cannot_be_written_is_refused_and_changes_nothing()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new DurableClock());
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var member = await host.UserAsync(tenant);
        var stranger = await host.UserAsync(await host.TenantAsync());
        await using var ownerUnit = await host.EnterAsync(owner, tenant);
        var memberships = host.Get<ITenantMemberships>();
        var membershipScope = IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.MembershipsId));
        var before = (await database.AuditAsync()).Length;

        await BreakAsync(database);
        Assert.Equal(AuthorizationCodes.AuditUnavailable, (await Assert.ThrowsAsync<AuthorityChangeException>(() =>
            host.Get<AuthorityAdministration>().GrantPermissionAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, DurableModel.Apply, Token))).Code);
        Assert.Equal(IdentityAuthorityCodes.AuditUnavailable, (await Assert.ThrowsAsync<IdentityAuthorityException>(() =>
            memberships.AddMemberAsync(membershipScope, stranger.Id, tenant, Token))).Code);
        await RepairAsync(database);

        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM \"" + database.AuthoritySchema + "\".\"PermissionGrants\""));
        Assert.False(await memberships.IsMemberAsync(stranger.Id, tenant, Token));
        Assert.Equal(before, (await database.AuditAsync()).Length);
        await using var memberUnit = await host.EnterAsync(member, tenant);
        var refused = await host.Get<IPermissionEvaluator>().AuthorizeAsync(new(memberUnit.Context, memberUnit.Principal, DurableModel.Apply) { RequireCurrent = true }, Token);
        Assert.Equal(AuthorizationCodes.NotHeld, refused.Code);
    }

    [Fact]
    public async Task TM_G02_051_Concurrent_authority_changes_that_retry_leave_one_record_each()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new DurableClock());
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var members = new[] { await host.UserAsync(tenant), await host.UserAsync(tenant), await host.UserAsync(tenant) };
        var units = new List<DurableUnit>();
        foreach (var _ in members) units.Add(await host.EnterAsync(owner, tenant));
        try
        {
            // The changes serialize on the tenant's authority row; each that loses reads again and retries in a new transaction.
            var results = await Task.WhenAll(members.Select((member, index) => Task.Run(() => host.Get<AuthorityAdministration>()
                .GrantPermissionAsync(units[index].Context, units[index].Principal, member.Actor, DurableModel.Apply, Token))));
            Assert.All(results, Assert.True);
        }
        finally
        {
            foreach (var unit in units) await unit.DisposeAsync();
        }
        var grants = await database.AuditAsync("authority.permission-granted");
        Assert.Equal(members.Length, grants.Length);
        Assert.Equal(members.Select(member => member.Id.ToString("D")).Order(), grants.Select(row => row.Subject!).Order());
        Assert.Equal((long)members.Length, await database.ScalarAsync<long>("SELECT count(*) FROM \"" + database.AuthoritySchema + "\".\"PermissionGrants\""));
    }

    [Fact]
    public async Task TM_G02_017_Removing_a_membership_ends_the_grants_made_during_it()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var member = await host.UserAsync(tenant);
        var administration = host.Get<AuthorityAdministration>();
        var evaluator = host.Get<IPermissionEvaluator>();
        var resource = new ResourceReference(tenant, "durable-item", "item-1", owner.Actor);
        await using (var ownerUnit = await host.EnterAsync(owner, tenant))
        {
            Assert.True(await administration.GrantPermissionAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, DurableModel.Apply, Token));
            Assert.True(await administration.DefineRoleAsync(ownerUnit.Context, ownerUnit.Principal, "appliers", [DurableModel.Apply], [], Token));
            Assert.True(await administration.AssignRoleAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, "appliers", Token));
            Assert.True(await administration.GrantResourceAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, resource, DurableModel.Apply, null, Token));
        }
        await using (var memberUnit = await host.EnterAsync(member, tenant))
        {
            Assert.True((await evaluator.AuthorizeAsync(new(memberUnit.Context, memberUnit.Principal, DurableModel.Apply) { Resource = resource, RequireCurrent = true },
                Token)).Permitted);
        }

        // The member is removed, then added again later: a new membership, which none of the earlier grants belongs to.
        clock.Advance(TimeSpan.FromSeconds(1));
        var memberships = host.Get<ITenantMemberships>();
        Assert.True(await memberships.RemoveMemberAsync(IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.MembershipsId)), member.Id, tenant, Token));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await memberships.AddMemberAsync(IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.MembershipsId)), member.Id, tenant, Token));
        Assert.Single(await database.AuditAsync("authority.membership-removed"), row => row.Subject == member.Id.ToString("D") && row.Tenant == tenant);

        await using (var memberUnit = await host.EnterAsync(member, tenant))
        {
            // Neither the direct grant nor the role assignment counts, at either profile, and the share is not visible.
            Assert.Equal(AuthorizationCodes.NotHeld, (await evaluator.AuthorizeAsync(new(memberUnit.Context, memberUnit.Principal, DurableModel.Apply)
                { RequireCurrent = true }, Token)).Code);
            Assert.Equal(AuthorizationCodes.NotHeld, (await evaluator.AuthorizeAsync(new(memberUnit.Context, memberUnit.Principal, DurableModel.Apply), Token)).Code);
            Assert.Empty((await evaluator.DescribeAsync(memberUnit.Context, memberUnit.Principal, Token))!.Permissions);
        }

        // Granting again makes the grant anew, rather than finding the earlier one and changing nothing; only the new one counts.
        await using (var ownerUnit = await host.EnterAsync(owner, tenant))
            Assert.True(await administration.GrantPermissionAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, DurableModel.Apply, Token));
        await using (var memberUnit = await host.EnterAsync(member, tenant))
        {
            Assert.True((await evaluator.AuthorizeAsync(new(memberUnit.Context, memberUnit.Principal, DurableModel.Apply) { RequireCurrent = true }, Token)).Permitted);
            Assert.Empty((await evaluator.VisibleAsync(new(memberUnit.Context, memberUnit.Principal, DurableModel.Apply) { RequireCurrent = true }, "durable-item",
                Token)).GrantedIds);
            Assert.Equal(AuthorizationCodes.ResourceNotFound, (await evaluator.AuthorizeAsync(new(memberUnit.Context, memberUnit.Principal, DurableModel.Apply)
                { Resource = resource, RequireCurrent = true }, Token)).Code);
        }
        await using (var ownerUnit = await host.EnterAsync(owner, tenant))
            Assert.True(await administration.GrantResourceAsync(ownerUnit.Context, ownerUnit.Principal, member.Actor, resource, DurableModel.Apply, null, Token));
        await using (var memberUnit = await host.EnterAsync(member, tenant))
            Assert.Equal(["item-1"], (await evaluator.VisibleAsync(new(memberUnit.Context, memberUnit.Principal, DurableModel.Apply) { RequireCurrent = true },
                "durable-item", Token)).GrantedIds);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM \"" + database.AuthoritySchema + "\".\"PermissionGrants\" WHERE \"Member\" = @member",
            new NpgsqlParameter("member", member.Id.ToString("D"))));
        Assert.Equal(2, (await database.AuditAsync("authority.permission-granted")).Length);
    }

    private const string TenantDirectoryAdministrationOperation = Tenancy.TenantDirectoryAdministration.OperationId;
}
