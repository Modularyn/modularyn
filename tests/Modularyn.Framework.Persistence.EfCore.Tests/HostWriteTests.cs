using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

// TM-G02-028 for host-scoped data: host scope is never applied implicitly. A unit without a tenant or host context can
// read host-scoped data but change none of it; a change needs an audited host scope, which the tenancy tests exercise,
// a named deployment scope for framework metadata, seeds and tests, or the owning module's owner-bound unit, which
// reaches only that module's own host-scoped data.
[Trait("Requires", "PostgreSQL")]
[Collection(PostgreSqlCollection.Name)]
public sealed class HostWriteTests : PostgreSqlTestDatabase
{
    [Fact]
    public async Task TM_G02_028_A_unit_without_a_context_reads_host_scoped_data_and_changes_none_of_it()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
        {
            Assert.Null(work.Context.Deployment);
            Assert.Equal(2, await work.Context.Set<SampleRow>().CountAsync(Cancellation));
            Assert.Equal(2, await work.Sql.Query<SampleRow>().CountAsync(Cancellation));
            Assert.Equal("MPS040", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.InsertAsync(
                new SampleRow { Id = "implicit", Name = "no scope" }, Cancellation))).Code);
            Assert.Equal("MPS040", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.UpdateAsync<SampleRow, string, string>(
                row => row.Id, "item-1", SampleInitial.InitialVersion, row => row.Name, "no scope", Cancellation))).Code);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
        {
            work.Context.Add(new SampleRow { Id = "implicit", Name = "no scope" });
            Assert.Equal("MPS040", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
        {
            var row = await work.Context.Set<SampleRow>().SingleAsync(row => row.Id == "item-2", Cancellation);
            row.Name = "changed without scope";
            Assert.Equal("MPS040", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
        {
            work.Context.Remove(await work.Context.Set<SampleRow>().SingleAsync(row => row.Id == "item-1", Cancellation));
            Assert.Equal("MPS040", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
        }
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'implicit'"));
        Assert.Equal("first", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
        Assert.Equal("second", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-2'"));
        Assert.False(await ScalarAsync<bool>("SELECT \"IsDeleted\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
    }

    [Fact]
    public async Task TM_G02_028_A_named_deployment_scope_changes_host_scoped_data_and_joins_only_its_own_scope()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            Assert.Same(Deployment, work.Context.Deployment);
            Assert.Null(work.Context.Tenant);
            await work.Sql.InsertAsync(new SampleRow { Id = "deployed", Name = "seeded" }, Cancellation);
            // A join shares the ambient scope; naming another scope, or another context, is refused.
            await using (var joined = await EfCoreUnitOfWork.BeginAsync(module, UnitOfWorkMode.Join, work, Cancellation))
            {
                Assert.Same(Deployment, joined.Context.Deployment);
                await joined.CompleteAsync(Cancellation);
            }
            var other = new DeploymentScope("test.other-fixture", "Another scope.");
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                EfCoreUnitOfWork.BeginAsync(module, other, UnitOfWorkMode.Join, work, Cancellation))).Code);
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                EfCoreUnitOfWork.BeginAsync(module, (Modularyn.Framework.Tenancy.Abstractions.TenantContext?)null, UnitOfWorkMode.Join, work, Cancellation))).Code);
            // A new scope begun beside it inherits nothing, so it cannot write.
            await using (var separate = await EfCoreUnitOfWork.BeginAsync(module, UnitOfWorkMode.New, work, Cancellation))
                Assert.Equal("MPS040", (await Assert.ThrowsAsync<PersistenceContractException>(() => separate.Sql.InsertAsync(
                    new SampleRow { Id = "separate", Name = "no scope" }, Cancellation))).Code);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'deployed'"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'separate'"));
        Assert.Throws<ArgumentException>(() => new DeploymentScope("seed data", "Spaces are not an operation identity."));
        Assert.Throws<ArgumentException>(() => new DeploymentScope("test.seed", " padded"));
    }

    [Fact]
    public async Task TM_G02_028_An_owner_bound_unit_changes_its_own_module_host_scoped_data_and_joins_only_its_own_scope()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        var owner = ModuleOwnerScope.For<SampleContext>("sample.items");
        await using (var work = await EfCoreUnitOfWork.BeginOwnedAsync(module, owner, cancellationToken: Cancellation))
        {
            Assert.Same(owner, work.Context.Owner);
            Assert.Null(work.Context.Tenant);
            Assert.Null(work.Context.Deployment);
            // Tracked and direct writes of the module's own host-scoped rows are allowed, with every other rule intact.
            work.Context.Add(new SampleRow { Id = "owned", Name = "added" });
            work.Context.Add(new SystemSeed { Id = "owned-seed", Name = "added" });
            var second = await work.Context.Set<SampleRow>().SingleAsync(row => row.Id == "item-2", Cancellation);
            second.Name = "renamed";
            work.Context.Remove(await work.Context.Set<SampleRow>().SingleAsync(row => row.Id == "item-1", Cancellation));
            await work.Sql.InsertAsync(new SampleRow { Id = "owned-direct", Name = "inserted" }, Cancellation);
            Assert.Equal("MPS037", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                work.Context.Set<SystemSeed>().Where(row => row.Id == "core").ExecuteDeleteAsync(Cancellation))).Code);
            // A join shares the ambient owner scope; naming another scope, or none, is refused.
            await using (var joined = await EfCoreUnitOfWork.BeginAsync(module, UnitOfWorkMode.Join, work, Cancellation))
            {
                Assert.Same(owner, joined.Context.Owner);
                await joined.CompleteAsync(Cancellation);
            }
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                EfCoreUnitOfWork.BeginOwnedAsync(module, ModuleOwnerScope.For<SampleContext>("sample.other"), UnitOfWorkMode.Join, work, Cancellation))).Code);
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                EfCoreUnitOfWork.BeginAsync(module, (Modularyn.Framework.Tenancy.Abstractions.TenantContext?)null, UnitOfWorkMode.Join, work, Cancellation))).Code);
            Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                EfCoreUnitOfWork.BeginAsync(module, Deployment, UnitOfWorkMode.Join, work, Cancellation))).Code);
            // A new unit begun beside it inherits nothing, so it cannot write.
            await using (var separate = await EfCoreUnitOfWork.BeginAsync(module, UnitOfWorkMode.New, work, Cancellation))
                Assert.Equal("MPS040", (await Assert.ThrowsAsync<PersistenceContractException>(() => separate.Sql.InsertAsync(
                    new SampleRow { Id = "separate", Name = "no scope" }, Cancellation))).Code);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        await using (var work = await EfCoreUnitOfWork.BeginOwnedAsync(module, owner, cancellationToken: Cancellation))
        {
            var version = await work.Context.Set<SampleRow>().Where(row => row.Id == "owned").Select(row => row.Version).SingleAsync(Cancellation);
            Assert.True((await work.Sql.UpdateAsync<SampleRow, string, string>(row => row.Id, "owned", version, row => row.Name, "updated", Cancellation)).Succeeded);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal("updated", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'owned'"));
        Assert.Equal("inserted", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'owned-direct'"));
        Assert.Equal("renamed", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-2'"));
        Assert.True(await ScalarAsync<bool>("SELECT \"IsDeleted\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds") + " WHERE \"Id\" = 'owned-seed'"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'separate'"));
    }
}

// TM-G02-028: an owner scope binds a unit to the module whose context type it names, and to nothing else.
public sealed class OwnerScopeTests
{
    private static PostgreSqlModule Sample() => PostgreSqlModule.Create("Sample", "sample_owner", new PostgreSqlPersistenceOptions("Host=localhost"),
        module => new SampleContext(module));

    [Fact]
    public async Task TM_G02_028_An_owner_scope_naming_another_module_s_context_is_refused_before_any_connection()
    {
        var foreign = ModuleOwnerScope.For<ProbeContext>("probe.items");
        Assert.Equal("MPS042", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
            EfCoreUnitOfWork.BeginOwnedAsync(Sample(), foreign, cancellationToken: TestContext.Current.CancellationToken))).Code);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            EfCoreUnitOfWork.BeginOwnedAsync(Sample(), null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TM_G02_028_An_owner_scope_names_its_operation()
    {
        var scope = ModuleOwnerScope.For<SampleContext>("sample.items-1_a");
        Assert.Equal((typeof(SampleContext), "sample.items-1_a"), (scope.ContextType, scope.OperationId));
        foreach (var invalid in new[] { "", "sample items", "sample/items", new string('a', 201) })
            Assert.Throws<ArgumentException>(() => ModuleOwnerScope.For<SampleContext>(invalid));
    }
}
