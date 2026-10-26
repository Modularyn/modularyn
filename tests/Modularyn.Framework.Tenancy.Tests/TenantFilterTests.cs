using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-029 for the tenant filter on CODE-03's seam, against PostgreSQL: every supported read path composes the tenant
// predicate, writes stay in the unit's tenant, references and unique keys are per tenant, and a unit without an active
// tenant context reaches no tenant data.
[Collection(TenancyPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class TenantFilterTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private sealed record Fixture(TenancyDatabase Database, ServiceProvider Services, Guid A1, Guid A2, Guid B1) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private static async Task<Fixture> CreateAsync()
    {
        var database = await TenancyDatabase.CreateAsync();
        var services = database.Services();
        await database.SeedAsync(services);
        var a1 = await AddAsync(database, services, "user-a", database.TenantA, "a-1");
        var a2 = await AddAsync(database, services, "user-a", database.TenantA, "a-2");
        var b1 = await AddAsync(database, services, "user-b", database.TenantB, "b-1");
        await InUnitAsync(database, services, "user-a", database.TenantA, items => items.Notes.Add(new ItemNote { Id = Guid.NewGuid(), ItemId = a1, Text = "note a" }));
        await InUnitAsync(database, services, "user-b", database.TenantB, items => items.Notes.Add(new ItemNote { Id = Guid.NewGuid(), ItemId = b1, Text = "note b" }));
        return new(database, services, a1, a2, b1);
    }

    [Fact]
    public async Task TM_G02_029_Every_supported_read_path_composes_the_tenant_predicate()
    {
        await using var fixture = await CreateAsync();
        var (database, services) = (fixture.Database, fixture.Services);
        var (scope, context) = await Units.EnterAsync(services, "user-a", database.TenantA);
        await using (scope)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation);
            var items = (ItemsContext)work.Context;
            Assert.Equal(["a-1", "a-2"], await items.Items.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(Cancellation));
            Assert.Equal(2, await items.Items.CountAsync(Cancellation));
            Assert.False(await items.Items.AnyAsync(row => row.Id == fixture.B1, Cancellation));
            Assert.Null(await items.Items.FindAsync([database.TenantB, fixture.B1], Cancellation));
            Assert.Null(await items.Items.SingleOrDefaultAsync(row => row.Code == "b-1", Cancellation));
            Assert.Equal([(database.TenantA, 2)], (await items.Items.GroupBy(row => row.TenantId).Select(group => new { group.Key, Count = group.Count() })
                .ToArrayAsync(Cancellation)).Select(group => (group.Key, group.Count)));
            // Joins and subqueries filter both sides.
            Assert.Equal(["note a"], await items.Notes.Join(items.Items, note => new { note.TenantId, note.ItemId }, item => new { item.TenantId, ItemId = item.Id },
                (note, item) => note.Text).ToArrayAsync(Cancellation));
            Assert.Equal(["a-1"], await items.Items.Where(item => items.Notes.Any(note => note.ItemId == item.Id)).Select(item => item.Code).ToArrayAsync(Cancellation));
            // The direct-SQL reads compose the same predicate over the adapter's SELECT.
            Assert.Equal(["a-1", "a-2"], await work.Sql.Query<Item>().OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(Cancellation));
            Assert.Empty(await work.Sql.Query<Item, string>(row => row.Code, "b-1").ToArrayAsync(Cancellation));
            Assert.Empty(await work.Sql.Query<Item, Guid>(row => row.Id, fixture.B1).ToArrayAsync(Cancellation));
            // The soft-delete bypass keeps the tenant predicate.
            var bypass = new SoftDeleteBypass("Inspect deleted items", "filter-test");
            Assert.Equal(["a-1", "a-2"], await items.IncludingSoftDeleted<Item>(bypass).Query.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(Cancellation));
            Assert.Equal(["a-1", "a-2"], await work.Sql.IncludingSoftDeleted<Item>(bypass).Query.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(Cancellation));
            // The tenant and scope filters cannot be lifted by EF Core's own bypass, on either path, or by raw SQL that
            // forges the tenant column.
            foreach (var query in new[] { items.Items, work.Sql.Query<Item>() })
            {
                Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => query.IgnoreQueryFilters().CountAsync(Cancellation))).Code);
                foreach (var name in new[] { "Modularyn.Tenant", "Modularyn.Scope", "Modularyn.SoftDelete" })
                    Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => query.IgnoreQueryFilters([name]).CountAsync(Cancellation))).Code);
            }
            var forged = "SELECT '" + TenantIdentifier.Format(database.TenantA) + "'::uuid AS \"TenantId\", \"Id\", \"Code\", \"Name\", \"IsDeleted\", \"Version\" FROM "
                + database.Table("Items");
#pragma warning disable EF1003 // The forged text is the point of the test; the guard refuses it before it runs.
            Assert.Equal("MPS034", (await Assert.ThrowsAsync<PersistenceContractException>(() => items.Items.FromSqlRaw(forged).CountAsync(Cancellation))).Code);
#pragma warning restore EF1003
            // The executed statements carry the tenant parameter, evaluated for this unit.
            foreach (var sql in new[] { items.Items.ToQueryString(), work.Sql.Query<Item>().ToQueryString(), items.Notes.ToQueryString() })
            {
                TestContext.Current.TestOutputHelper?.WriteLine(sql);
                Assert.Contains("\"TenantId\" = @ef_filter__TenantFilterValue", sql, StringComparison.Ordinal);
                // The scope predicate compares the tenant again, so lifting the tenant predicate alone cannot widen a tenant unit.
                Assert.Matches(@"\(@ef_filter__ScopeHostValue\d* OR \w+\.""TenantId"" = @ef_filter__ScopeTenantValue\d*\)", sql);
                Assert.Contains(TenantIdentifier.Format(database.TenantA), sql, StringComparison.Ordinal);
                Assert.DoesNotContain(TenantIdentifier.Format(database.TenantB), sql, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task TM_G02_029_Writes_stay_in_the_unit_s_tenant_on_the_EF_and_direct_SQL_paths()
    {
        await using var fixture = await CreateAsync();
        var (database, services) = (fixture.Database, fixture.Services);
        var bVersion = await database.ScalarAsync<Guid>("SELECT \"Version\" FROM " + database.Table("Items") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", fixture.B1));
        var (scope, context) = await Units.EnterAsync(services, "user-a", database.TenantA);
        await using (scope)
        {
            // The tenant of a new row is the unit's, never the caller's.
            var derived = new Item { Id = Guid.NewGuid(), Code = "a-derived", Name = "derived" };
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation))
            {
                work.Context.Add(derived);
                Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
            }
            Assert.Equal(database.TenantA, derived.TenantId);
            await Refused("MPS036", work => work.Context.Add(new Item { TenantId = database.TenantB, Id = Guid.NewGuid(), Code = "forged", Name = "forged" }));
            // A row attached with another tenant's identity cannot be updated or deleted.
            await Refused("MPS036", work => work.Context.Update(new Item { TenantId = database.TenantB, Id = fixture.B1, Code = "hijacked", Name = "hijacked", Version = bVersion }));
            await Refused("MPS036", work => work.Context.Remove(new Item { TenantId = database.TenantB, Id = fixture.B1, Code = "b-1", Version = bVersion }));

            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation))
            {
                Assert.Equal("MPS036", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.InsertAsync(
                    new Item { TenantId = database.TenantB, Id = Guid.NewGuid(), Code = "forged-sql", Name = "forged" }, Cancellation))).Code);
                Assert.Equal("MPS036", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.UpdateAsync<Item, Guid, Guid>(
                    row => row.Id, fixture.A1, Guid.NewGuid(), row => row.TenantId, database.TenantB, Cancellation))).Code);
                Assert.Equal("MPS036", (await Assert.ThrowsAsync<PersistenceContractException>(() => ((ItemsContext)work.Context).Items
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TenantId, database.TenantB), Cancellation))).Code);
                Assert.Equal("MPS037", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.InsertAsync(
                    new Lookup { Id = "tenant-written", Name = "x" }, Cancellation))).Code);
                // Another tenant's key reports a conflict, as a missing row does, and changes nothing.
                var foreign = await work.Sql.UpdateAsync<Item, Guid, string>(row => row.Id, fixture.B1, bVersion, row => row.Name, "from A", Cancellation);
                Assert.False(foreign.Succeeded);
            }
            // Bulk writes compose the tenant predicate, so they reach only the unit's own rows. They are refused where they
            // would skip a version token or physically delete soft-deleted rows; the versioned direct update remains.
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation))
            {
                var items = (ItemsContext)work.Context;
                Assert.Equal("MPS010", (await Assert.ThrowsAsync<PersistenceContractException>(() => items.Items
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "bulk A"), Cancellation))).Code);
                Assert.Equal("MPS041", (await Assert.ThrowsAsync<PersistenceContractException>(() => items.Items.ExecuteDeleteAsync(Cancellation))).Code);
                Assert.Equal(1, await items.Notes.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Text, "bulk A"), Cancellation));
                Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
            }
            Assert.Equal([(database.TenantA, "bulk A"), (database.TenantB, "note b")],
                (await database.InventoryAsync("ItemNotes", "Text")).OrderBy(row => row.Tenant == database.TenantA ? 0 : 1));
            var a2Version = await database.ScalarAsync<Guid>("SELECT \"Version\" FROM " + database.Table("Items") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", fixture.A2));
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation))
            {
                Assert.Equal(1, await ((ItemsContext)work.Context).Notes.ExecuteDeleteAsync(Cancellation));
                // A soft delete through the versioned direct update keeps the row and its tenant.
                Assert.True((await work.Sql.UpdateAsync<Item, Guid, bool>(row => row.Id, fixture.A2, a2Version, row => row.IsDeleted, true, Cancellation)).Succeeded);
                Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
            }
            Assert.True(await database.ScalarAsync<bool>("SELECT \"IsDeleted\" FROM " + database.Table("Items") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", fixture.A2)));
            // Host data is readable from a tenant unit but cannot be changed by it.
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation))
            {
                var items = (ItemsContext)work.Context;
                Assert.Equal(["core"], await items.Lookups.Select(row => row.Id).ToArrayAsync(Cancellation));
                items.Lookups.Add(new Lookup { Id = "tenant-written", Name = "x" });
                Assert.Equal("MPS037", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
            }
        }
        Assert.Equal("b-1", await database.ScalarAsync<string>("SELECT \"Name\" FROM " + database.Table("Items") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", fixture.B1)));
        Assert.Equal(bVersion, await database.ScalarAsync<Guid>("SELECT \"Version\" FROM " + database.Table("Items") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", fixture.B1)));
        Assert.Equal([(database.TenantB, "note b")], await database.InventoryAsync("ItemNotes", "Text"));
        Assert.DoesNotContain(await database.InventoryAsync("Items", "Name"), row => row.Value == "bulk A");
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Items") + " WHERE \"TenantId\" = @tenant AND \"Code\" LIKE 'forged%'",
            new NpgsqlParameter("tenant", database.TenantB)));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Lookups")));

        async Task Refused(string code, Action<EfCoreUnitOfWork> change)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation);
            change(work);
            Assert.Equal(code, (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
            Assert.Equal(UnitOfWorkState.Failed, work.State);
        }
    }

    [Fact]
    public async Task TM_G02_029_References_and_unique_keys_are_scoped_to_one_tenant()
    {
        await using var fixture = await CreateAsync();
        var (database, services) = (fixture.Database, fixture.Services);
        // A reference to another tenant's item fails exactly as a reference to an item that does not exist.
        var foreign = await FailureAsync(database, services, "user-a", database.TenantA, items => items.Notes.Add(new ItemNote { Id = Guid.NewGuid(), ItemId = fixture.B1, Text = "x" }));
        var missing = await FailureAsync(database, services, "user-a", database.TenantA, items => items.Notes.Add(new ItemNote { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Text = "x" }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign);
        Assert.Equal(missing, foreign);
        // code-1 is unique within each tenant, and each tenant can hold it.
        await InUnitAsync(database, services, "user-a", database.TenantA, items => items.Items.Add(new Item { Id = Guid.NewGuid(), Code = "code-1", Name = "A" }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await FailureAsync(database, services, "user-a", database.TenantA,
            items => items.Items.Add(new Item { Id = Guid.NewGuid(), Code = "code-1", Name = "A again" })));
        await InUnitAsync(database, services, "user-b", database.TenantB, items => items.Items.Add(new Item { Id = Guid.NewGuid(), Code = "code-1", Name = "B" }));
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Items") + " WHERE \"Code\" = 'code-1'"));
        // The constraints exist in the database with the tenant column first.
        var index = await database.ScalarAsync<string>("SELECT indexdef FROM pg_indexes WHERE schemaname = @schema AND indexname = 'IX_Items_TenantId_Code'",
            new NpgsqlParameter("schema", database.ItemsSchema));
        Assert.StartsWith("CREATE UNIQUE INDEX", index, StringComparison.Ordinal);
        Assert.EndsWith("USING btree (\"TenantId\", \"Code\")", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TM_G02_029_A_unit_without_an_active_tenant_context_reaches_no_tenant_data()
    {
        await using var fixture = await CreateAsync();
        var (database, services) = (fixture.Database, fixture.Services);
        await using (var none = await EfCoreUnitOfWork.BeginAsync(database.Items, cancellationToken: Cancellation))
        {
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => ((ItemsContext)none.Context).Items.CountAsync(Cancellation))).Code);
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => none.Sql.Query<Item>().CountAsync(Cancellation))).Code);
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => none.Sql.InsertAsync(
                new Item { Id = Guid.NewGuid(), Code = "none", Name = "none" }, Cancellation))).Code);
            Assert.Equal(["core"], await ((ItemsContext)none.Context).Lookups.Select(row => row.Id).ToArrayAsync(Cancellation));
        }
        var (scope, context) = await Units.EnterAsync(services, "user-a", database.TenantA);
        var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation);
        await using (work)
        {
            var items = (ItemsContext)work.Context;
            Assert.Equal(2, await items.Items.CountAsync(Cancellation));
            items.Items.Add(new Item { Id = Guid.NewGuid(), Code = "after-end", Name = "x" });
            // The unit's scope ends: the bound context ends with it, and the unit can no longer read or write tenant data.
            await scope.DisposeAsync();
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => items.Items.CountAsync(Cancellation))).Code);
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
        }
        Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
            EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation))).Code);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Items") + " WHERE \"Code\" = 'after-end'"));

        // A joined scope cannot switch tenant contexts.
        var (scopeA, contextA) = await Units.EnterAsync(services, "user-a", database.TenantA);
        var (scopeB, contextB) = await Units.EnterAsync(services, "user-b", database.TenantB);
        await using (scopeA)
        await using (scopeB)
        await using (var outer = await EfCoreUnitOfWork.BeginAsync(database.Items, contextA, cancellationToken: Cancellation))
        {
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                EfCoreUnitOfWork.BeginAsync(database.Items, contextB, UnitOfWorkMode.Join, outer, Cancellation))).Code);
            await using var joined = await EfCoreUnitOfWork.BeginAsync(database.Items, UnitOfWorkMode.Join, outer, Cancellation);
            Assert.Same(contextA, joined.Context.Tenant);
            // A new scope begun beside an ambient unit inherits nothing from it.
            await using var separate = await EfCoreUnitOfWork.BeginAsync(database.Items, UnitOfWorkMode.New, outer, Cancellation);
            Assert.Null(separate.Context.Tenant);
        }
    }

    private static async Task<Guid> AddAsync(TenancyDatabase database, IServiceProvider services, string subject, Guid tenant, string code)
    {
        var item = new Item { Id = Guid.NewGuid(), Code = code, Name = code };
        await InUnitAsync(database, services, subject, tenant, items => items.Items.Add(item));
        return item.Id;
    }

    private static async Task InUnitAsync(TenancyDatabase database, IServiceProvider services, string subject, Guid tenant, Action<ItemsContext> change)
    {
        var (scope, context) = await Units.EnterAsync(services, subject, tenant);
        await using (scope)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation);
            change((ItemsContext)work.Context);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
    }

    private static async Task<string> FailureAsync(TenancyDatabase database, IServiceProvider services, string subject, Guid tenant, Action<ItemsContext> change)
    {
        var (scope, context) = await Units.EnterAsync(services, subject, tenant);
        await using (scope)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation);
            change((ItemsContext)work.Context);
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => work.CompleteAsync(Cancellation));
            return Assert.IsType<PostgresException>(failure.InnerException).SqlState;
        }
    }
}
