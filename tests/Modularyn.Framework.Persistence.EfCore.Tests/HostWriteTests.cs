using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

// TM-G02-028 for host-scoped data: host scope is never applied implicitly. A unit without a tenant or host context can
// read host-scoped data but change none of it; a change needs an audited host scope, which the tenancy tests exercise,
// or a named deployment scope for framework metadata, seeds and tests.
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
}
