using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Xunit;
using static Modularyn.Framework.Audit.Tests.AuditFixture;

namespace Modularyn.Framework.Audit.Tests;

// TM-G02-029 and TM-G02-051, FND-010 on PostgreSQL: every privileged filter bypass, a cross-tenant read in host scope or
// a read that includes soft-deleted rows, is recorded in audit before its query exists, naming the unit, its actor and
// tenant, the entity and the operation and reason; a bypass that cannot be recorded, or that no audited context makes,
// is refused and reads nothing.
[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class BypassAuditTests
{
    [Fact]
    public async Task TM_G02_029_Every_cross_tenant_read_is_recorded_before_its_query_exists()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new DurableClock());
        foreach (var tenant in new[] { await host.TenantAsync(), await host.TenantAsync() })
            await AddNotesAsync(host, tenant);
        var scope = await host.HostAsync();

        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, scope, cancellationToken: Token))
        {
            // Both tenants' kept notes: only the tenant predicate is lifted, and soft delete still applies.
            Assert.Equal(2, await (await work.Context.AcrossTenantsAsync<DurableNote>(Token)).Query.CountAsync(Token));
            Assert.Equal(2, await (await work.Sql.AcrossTenantsAsync<DurableNote>(Token)).Query.CountAsync(Token));
        }
        var reads = (await database.AuditAsync(AuditActions.CrossTenantRead)).Where(row => row.Resource == DurableModel.ProducerId + "/Notes").ToArray();
        Assert.Equal(2, reads.Length);
        Assert.All(reads, read =>
        {
            Assert.Equal(scope.UnitId, read.Unit);
            Assert.Null(read.Tenant);
            Assert.Equal(DurableHost.Service, read.Actor);
            Assert.Equal(DurableModel.Dispatch.Id, read.Operation);
            Assert.Equal(scope.HostGrant!.Reason, read.Reason);
            Assert.Equal("scope host", read.Detail);
            Assert.Equal("entity", read.ResourceType);
        });
        // The dispatcher's own scan is recorded the same way.
        await host.Pump().DispatchAsync(scope, host.WorkerPrincipal(), cancellationToken: Token);
        Assert.Single(await database.AuditAsync(AuditActions.CrossTenantRead), row => row.Resource == DurableModel.ProducerId + "/OutboxEvents");

        // A read that cannot be recorded is refused before any query exists.
        await BreakAsync(database);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, scope, cancellationToken: Token))
        {
            Assert.Equal("MPS047", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Context.AcrossTenantsAsync<DurableNote>(Token))).Code);
            Assert.Equal("MPS047", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.AcrossTenantsAsync<DurableNote>(Token))).Code);
        }
        await RepairAsync(database);
    }

    [Fact]
    public async Task TM_G02_029_Every_soft_deleted_read_is_recorded_and_refused_without_an_audited_context()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new DurableClock());
        var tenant = await host.TenantAsync();
        await AddNotesAsync(host, tenant);
        await using var unit = await host.EnterServiceAsync(tenant);
        var bypass = new SoftDeleteBypass("Restore a deleted note", "durable.notes-restore");

        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            Assert.Equal(1, await work.Context.Set<DurableNote>().CountAsync(Token));
            Assert.Equal(2, await (await work.Context.IncludingSoftDeletedAsync<DurableNote>(bypass, Token)).Query.CountAsync(Token));
            Assert.Equal(2, await (await work.Sql.IncludingSoftDeletedAsync<DurableNote>(bypass, Token)).Query.CountAsync(Token));
        }
        var reads = await database.AuditAsync(AuditActions.SoftDeletedRead);
        Assert.Equal(2, reads.Length);
        Assert.All(reads, read =>
        {
            Assert.Equal(unit.Context.UnitId, read.Unit);
            Assert.Equal(tenant, read.Tenant);
            Assert.Equal(DurableHost.Service, read.Actor);
            Assert.Equal(bypass.OperationId, read.Operation);
            Assert.Equal(bypass.Reason, read.Reason);
            Assert.Equal(DurableModel.ProducerId + "/Notes", read.Resource);
            Assert.Equal("scope tenant", read.Detail);
        });

        // A unit with no tenant or host context has no audit to record a bypass, so it is refused.
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, cancellationToken: Token))
            Assert.Equal("MPS047", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                work.Context.IncludingSoftDeletedAsync<DurableNote>(bypass, Token))).Code);
        await using (var work = await EfCoreUnitOfWork.BeginOwnedAsync(database.Producer, ModuleOwnerScope.For<DurableProducerContext>("durable.owner"),
            cancellationToken: Token))
            Assert.Equal("MPS047", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.IncludingSoftDeletedAsync<DurableNote>(bypass, Token))).Code);
        await BreakAsync(database);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
            Assert.Equal("MPS047", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                work.Context.IncludingSoftDeletedAsync<DurableNote>(bypass, Token))).Code);
        await RepairAsync(database);
        Assert.Equal(2, (await database.AuditAsync(AuditActions.SoftDeletedRead)).Length);
    }

    /// <summary>One kept and one soft-deleted note in the tenant.</summary>
    private static async Task AddNotesAsync(DurableHost host, Guid tenant)
    {
        await using var unit = await host.EnterServiceAsync(tenant);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(host.Database.Producer, unit.Context, cancellationToken: Token))
        {
            work.Context.Add(new DurableNote { Key = "kept" });
            work.Context.Add(new DurableNote { Key = "deleted" });
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(host.Database.Producer, unit.Context, cancellationToken: Token))
        {
            work.Context.Remove(await work.Context.Set<DurableNote>().SingleAsync(row => row.Key == "deleted", Token));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
    }
}
