using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events.Tests;
using Xunit;
using static Modularyn.Framework.Audit.Tests.AuditFixture;

namespace Modularyn.Framework.Audit.Tests;

// TM-G02-052 on PostgreSQL: audit is read only under its own permission, decided by the evaluator at the current profile
// and separate from the administration permission, or in a host scope granted for its own host operation; a tenant's read
// returns only that tenant's records; and every read is recorded before any record is returned.
[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class AuditReadTests
{
    [Fact]
    public async Task TM_G02_052_Audit_reads_need_their_own_permission_and_are_themselves_audited()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new DurableClock());
        var tenant = await host.TenantAsync();
        var other = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var administrator = await host.UserAsync(tenant);
        var member = await host.UserAsync(tenant);
        await host.UserAsync(other, owner: true);
        var reader = host.Get<AuditReader>();
        await using (var ownerUnit = await host.EnterAsync(owner, tenant))
            await host.GrantAsync(ownerUnit, administrator, AuthorizationModule.ManagePermission);

        // Neither a plain member nor an administrator of permissions reads audit.
        foreach (var user in new[] { member, administrator })
        {
            await using var unit = await host.EnterAsync(user, tenant);
            Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<AuditAccessException>(() => reader.ReadAsync(unit.Context, unit.Principal, new(), Token)))
                .Code);
        }
        Assert.Empty(await database.AuditAsync(AuditActions.AuditRead));

        await using (var ownerUnit = await host.EnterAsync(owner, tenant))
        {
            var records = await reader.ReadAsync(ownerUnit.Context, ownerUnit.Principal, new() { Limit = AuditQuery.MaximumLimit }, Token);
            Assert.NotEmpty(records);
            Assert.All(records, record => Assert.Equal(tenant, record.TenantId));
            // The read was on the record before anything was read, so it is among what it read.
            var read = Assert.Single(records, record => record.Action == AuditActions.AuditRead);
            Assert.Equal(ownerUnit.Context.UnitId, read.UnitId);
            Assert.Equal(owner.Id.ToString("D"), read.Actor.Subject);
            Assert.Equal(["authority.permission-granted"], (await reader.ReadAsync(ownerUnit.Context, ownerUnit.Principal,
                new() { Action = "authority.permission-granted" }, Token)).Select(record => record.Action));
            await host.GrantAsync(ownerUnit, member, AuditModule.ReadPermission);
        }
        await using (var memberUnit = await host.EnterAsync(member, tenant))
            Assert.NotEmpty(await reader.ReadAsync(memberUnit.Context, memberUnit.Principal, new(), Token));
        Assert.Equal(3, (await database.AuditAsync(AuditActions.AuditRead)).Length);

        // Every tenant's records, and the host's own, need a host scope granted for the audit's own host operation.
        var dispatch = await host.HostAsync();
        Assert.Equal(AuditCodes.HostReadRequired, (await Assert.ThrowsAsync<AuditAccessException>(() => reader.ReadAsync(dispatch, null, new(), Token))).Code);
        var auditor = await host.HostAsync(AuditModule.HostReadOperation.Id);
        var all = await reader.ReadAsync(auditor, null, new() { Limit = AuditQuery.MaximumLimit }, Token);
        Assert.Contains(all, record => record.TenantId == tenant);
        Assert.Contains(all, record => record.TenantId == other);
        Assert.Contains(all, record => record.TenantId is null && record.Action == AuditActions.HostScopeGranted);
        Assert.Single(all, record => record.Action == AuditActions.AuditHostRead && record.UnitId == auditor.UnitId);

        // A read that cannot be recorded returns nothing.
        await BreakAsync(database);
        await using (var ownerUnit = await host.EnterAsync(owner, tenant))
            await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync(ownerUnit.Context, ownerUnit.Principal, new(), Token));
        await RepairAsync(database);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(auditor, null, new() { Limit = AuditQuery.MaximumLimit + 1 }, Token));
    }
}
