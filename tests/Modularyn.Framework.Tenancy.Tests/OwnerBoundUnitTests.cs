using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-028 for the owner-bound unit of work, against PostgreSQL: a module changes its own host-scoped data through its
// own context without host scope, and gains nothing else. It reaches no tenant-scoped row, no other module's data and no
// cross-tenant read or update, which still need an active, audited host scope; a tenant unit and an unbound unit still
// cannot change host-scoped data.
[Collection(TenancyPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class OwnerBoundUnitTests
{
    private static readonly ModuleOwnerScope ItemsOwner = ModuleOwnerScope.For<ItemsContext>("items.lookups");
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TM_G02_028_An_owner_bound_unit_writes_only_its_own_module_s_host_scoped_data()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        var (seeding, tenantA) = await Units.EnterAsync(services, "user-a", database.TenantA);
        Guid item;
        await using (seeding)
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, tenantA, cancellationToken: Cancellation))
        {
            item = Guid.NewGuid();
            ((ItemsContext)work.Context).Items.Add(new Item { Id = item, Code = "a-1", Name = "a-1" });
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }

        // The allowed write: the module's own host-scoped row, tracked and direct, with no tenant or host context.
        await using (var work = await EfCoreUnitOfWork.BeginOwnedAsync(database.Items, ItemsOwner, cancellationToken: Cancellation))
        {
            var items = (ItemsContext)work.Context;
            items.Lookups.Add(new Lookup { Id = "colour", Name = "Colour" });
            await work.Sql.InsertAsync(new Lookup { Id = "size", Name = "Size" }, Cancellation);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Lookups") + " WHERE \"Id\" <> 'core'"));

        // Tenant-scoped rows are refused on every path, and so is the cross-tenant bypass and the named-tenant update.
        await using (var work = await EfCoreUnitOfWork.BeginOwnedAsync(database.Items, ItemsOwner, cancellationToken: Cancellation))
        {
            var items = (ItemsContext)work.Context;
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => items.Items.CountAsync(Cancellation))).Code);
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.Query<Item>().CountAsync(Cancellation))).Code);
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.InsertAsync(
                new ItemNote { Id = Guid.NewGuid(), TenantId = database.TenantA, ItemId = item, Text = "from owner" }, Cancellation))).Code);
            Assert.Equal("MPS038", Assert.Throws<PersistenceContractException>(() => items.AcrossTenants<Item>()).Code);
            Assert.Equal("MPS038", Assert.Throws<PersistenceContractException>(() => work.Sql.AcrossTenants<Item>()).Code);
            Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.UpdateForTenantAsync<Item, Guid, string>(
                database.TenantA, row => row.Id, item, Guid.NewGuid(), row => row.Name, "from owner", Cancellation))).Code);
        }
        await using (var work = await EfCoreUnitOfWork.BeginOwnedAsync(database.Items, ItemsOwner, cancellationToken: Cancellation))
        {
            ((ItemsContext)work.Context).Notes.Add(new ItemNote { Id = Guid.NewGuid(), TenantId = database.TenantA, ItemId = item, Text = "tracked" });
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
        }

        // Another module's data: an owner scope opens only its own module's context, and that context maps no other
        // module's entity.
        Assert.Equal("MPS042", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
            EfCoreUnitOfWork.BeginOwnedAsync(database.Directory, ItemsOwner, cancellationToken: Cancellation))).Code);
        await using (var work = await EfCoreUnitOfWork.BeginOwnedAsync(database.Items, ItemsOwner, cancellationToken: Cancellation))
            Assert.Throws<InvalidOperationException>(() => work.Context.Add(new TenantRecord { Id = Guid.NewGuid(), Name = "Tenant C" }));
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM \"" + database.DirectorySchema + "\".\"Tenants\""));

        // Without the owner scope, a unit with no context and a tenant unit still cannot change host-scoped data.
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, cancellationToken: Cancellation))
            Assert.Equal("MPS040", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.InsertAsync(
                new Lookup { Id = "unbound", Name = "Unbound" }, Cancellation))).Code);
        var (tenantScope, tenant) = await Units.EnterAsync(services, "ta-admin", database.TenantA);
        await using (tenantScope)
        {
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, tenant, cancellationToken: Cancellation))
            {
                ((ItemsContext)work.Context).Lookups.Add(new Lookup { Id = "tenant", Name = "From a tenant" });
                Assert.Equal("MPS037", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
            }
            // A tenant unit cannot take on an owner scope by joining, and an owner-bound unit cannot take on a tenant.
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, tenant, cancellationToken: Cancellation))
                Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                    EfCoreUnitOfWork.BeginOwnedAsync(database.Items, ItemsOwner, UnitOfWorkMode.Join, work, Cancellation))).Code);
            await using (var owned = await EfCoreUnitOfWork.BeginOwnedAsync(database.Items, ItemsOwner, cancellationToken: Cancellation))
                Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                    EfCoreUnitOfWork.BeginAsync(database.Items, tenant, UnitOfWorkMode.Join, owned, Cancellation))).Code);
        }
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Lookups") + " WHERE \"Id\" <> 'core'"));
    }

    [Fact]
    public async Task TM_G02_028_Cross_tenant_work_still_needs_an_audited_host_scope_beside_owner_bound_units()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        foreach (var (subject, tenant, code) in new[] { ("user-a", database.TenantA, "a-1"), ("user-b", database.TenantB, "b-1") })
        {
            var (scope, context) = await Units.EnterAsync(services, subject, tenant);
            await using (scope)
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation))
            {
                ((ItemsContext)work.Context).Items.Add(new Item { Id = Guid.NewGuid(), Code = code, Name = code });
                Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
            }
        }

        // The owner-bound unit sees no tenant's rows; only the audited host scope reads across tenants.
        await using (var owned = await EfCoreUnitOfWork.BeginOwnedAsync(database.Items, ItemsOwner, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
            Assert.Equal("MPS038", Assert.Throws<PersistenceContractException>(() => owned.Context.AcrossTenants<Item>()).Code);
        database.Seams.Authority.Grant("host-1", TenancyServer.HostReport);
        var audited = database.Seams.Audit.Records.Count;
        var (hostScope, host) = await Units.EnterHostAsync(services, "host-1", TenancyServer.HostReport);
        await using (hostScope)
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, host, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
            Assert.Equal(["a-1", "b-1"], await work.Context.AcrossTenants<Item>().Query.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(Cancellation));
        Assert.Equal(audited + 1, database.Seams.Audit.Records.Count);
    }
}
