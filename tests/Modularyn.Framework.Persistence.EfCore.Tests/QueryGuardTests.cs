using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

// TM-G02-029 on CODE-03's filter seam: EF Core's general IgnoreQueryFilters, every named form the adapter did not
// issue, raw SQL the adapter did not build and bulk LINQ writes to host-scoped data are refused on the EF and direct-SQL
// paths, against PostgreSQL. The sample model has the soft-delete filter and the module's Sample.NameVisibility filter.
[Trait("Requires", "PostgreSQL")]
[Collection(PostgreSqlCollection.Name)]
public sealed class QueryGuardTests(ITestOutputHelper output) : PostgreSqlTestDatabase
{
    private static readonly string[][] NamedForms =
        [["Modularyn.SoftDelete"], ["Sample.NameVisibility"], ["Modularyn.Tenant"], ["Modularyn.Scope"], ["Modularyn.SoftDelete", "Sample.NameVisibility"]];

    [Fact]
    public async Task TM_G02_029_General_and_unissued_named_IgnoreQueryFilters_are_refused_on_both_paths()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using (var insert = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            await insert.Sql.InsertAsync(new SampleRow { Id = "item-hidden", Name = "excluded", IsDeleted = true }, Cancellation);
            Assert.Equal(UnitOfWorkState.Committed, (await insert.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal(4L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items")));

        await using var read = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        var context = (SampleContext)read.Context;
        Assert.Equal(2, await context.Items.CountAsync(Cancellation));
        Assert.Equal(2, await read.Sql.Query<SampleRow>().CountAsync(Cancellation));

        Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items.IgnoreQueryFilters().CountAsync(Cancellation))).Code);
        Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => read.Sql.Query<SampleRow>().IgnoreQueryFilters().CountAsync(Cancellation))).Code);
        Assert.Equal("MPS033", Assert.Throws<PersistenceContractException>(() => context.Items.IgnoreQueryFilters().ToList()).Code);
        foreach (var names in NamedForms)
        {
            Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items.IgnoreQueryFilters(names).CountAsync(Cancellation))).Code);
            Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => read.Sql.Query<SampleRow>().IgnoreQueryFilters(names).CountAsync(Cancellation))).Code);
        }

        // Hidden in a subquery, behind a projection or in a compiled query, the call is still refused.
        Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items
            .Where(row => context.Items.IgnoreQueryFilters().Any(other => other.Id == row.Id)).CountAsync(Cancellation))).Code);
        Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items.IgnoreQueryFilters()
            .Select(row => row.Name).ToArrayAsync(Cancellation))).Code);
        var compiled = EF.CompileAsyncQuery((SampleContext owner) => owner.Items.IgnoreQueryFilters().Count());
        Assert.Equal("MPS033", (await Assert.ThrowsAsync<PersistenceContractException>(() => compiled(context))).Code);

        // The adapter's own bypass lifts only the soft-delete predicate; the module's filter remains.
        var bypass = new SoftDeleteBypass("Inspect soft-deleted items for recovery", "guard-test");
        Assert.Equal(3, await context.IncludingSoftDeleted<SampleRow>(bypass).Query.CountAsync(Cancellation));
        Assert.Equal(3, await read.Sql.IncludingSoftDeleted<SampleRow>(bypass).Query.CountAsync(Cancellation));
        Assert.Equal(2, await context.Items.CountAsync(Cancellation));
        output.WriteLine("Filtered count 2 on both paths; the soft-delete bypass gives 3; every IgnoreQueryFilters form was refused with MPS033.");
    }

    [Fact]
    public async Task TM_G02_029_Raw_SQL_that_the_adapter_did_not_build_is_refused_before_it_runs()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        var context = (SampleContext)work.Context;
        // A module-supplied FromSql text could forge any column a filter reads, so it is refused even when it is harmless.
        Assert.Equal("MPS034", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items
            .FromSqlRaw("SELECT 'forged' AS \"Id\", 'x' AS \"Name\", false AS \"IsDeleted\", gen_random_uuid() AS \"Version\", NULL::text AS \"Note\"")
            .CountAsync(Cancellation))).Code);
        Assert.Equal("MPS034", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items
            .Where(row => context.Items.FromSql($"SELECT * FROM unrelated").Any(other => other.Id == row.Id)).CountAsync(Cancellation))).Code);
        Assert.Equal("MPS034", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Database
            .SqlQueryRaw<int>("SELECT 1 AS \"Value\"").ToListAsync(Cancellation))).Code);
        var update = "UPDATE " + Table("Items") + " SET \"Name\" = 'raw'";
        Assert.Equal("MPS034", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Database
            .ExecuteSqlAsync(System.Runtime.CompilerServices.FormattableStringFactory.Create(update), Cancellation))).Code);
        Assert.Equal("MPS034", Assert.Throws<PersistenceContractException>(() => context.Database.ExecuteSql($"SELECT 1")).Code);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Name\" = 'raw'"));

        // The adapter's reads still run, with every filter composed over them.
        Assert.Equal(["item-1", "item-2"], await work.Sql.Query<SampleRow>().OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Cancellation));
        Assert.Equal("item-1", (await work.Sql.Query<SampleRow, string>(row => row.Name, "first").SingleAsync(Cancellation)).Id);
    }

    [Fact]
    public async Task TM_G02_029_ModuleSql_reads_run_while_module_code_calling_FromSqlRaw_itself_is_refused_even_with_the_same_text()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        var context = (SampleContext)work.Context;
        Assert.Equal(["item-1", "item-2"], await work.Sql.Query<SampleRow>().OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Cancellation));
        Assert.Equal("item-2", (await work.Sql.Query<SampleRow, string>(row => row.Name, "second").SingleAsync(Cancellation)).Id);
        // The text ModuleSql would build is still refused when module code supplies it: acceptance needs the adapter's read.
        var selectAll = "SELECT * FROM " + Table("Items");
        var selectWhere = selectAll + " WHERE \"Name\" IS NOT DISTINCT FROM @value";
#pragma warning disable EF1003 // The test supplies the adapter's own text from module code; the guard refuses it before it runs.
        Assert.Equal("MPS034", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items.FromSqlRaw(selectAll).CountAsync(Cancellation))).Code);
        Assert.Equal("MPS034", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items
            .FromSqlRaw(selectWhere, new Npgsql.NpgsqlParameter("value", "second")).CountAsync(Cancellation))).Code);
#pragma warning restore EF1003
        Assert.Equal(2, await work.Sql.Query<SampleRow>().CountAsync(Cancellation));
    }

    [Fact]
    public async Task TM_G02_029_Bulk_LINQ_writes_to_host_scoped_data_are_refused_and_ModuleSql_updates_still_apply()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            var context = (SampleContext)work.Context;
            Assert.Equal("MPS037", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items.ExecuteDeleteAsync(Cancellation))).Code);
            Assert.Equal("MPS037", (await Assert.ThrowsAsync<PersistenceContractException>(() => context.Items
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "bulk"), Cancellation))).Code);
            var changed = await work.Sql.UpdateAsync<SampleRow, string, string>(row => row.Id, "item-1", SampleInitial.InitialVersion,
                row => row.Name, "direct", Cancellation);
            Assert.True(changed.Succeeded);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items")));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Name\" = 'bulk'"));
        Assert.Equal("direct", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
    }
}
