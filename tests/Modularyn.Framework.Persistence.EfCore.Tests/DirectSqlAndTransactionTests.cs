using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(PostgreSqlCollection.Name)]
public sealed class DirectSqlAndTransactionTests : PostgreSqlTestDatabase
{
    [Fact]
    public async Task Named_filters_compose_on_both_paths_and_soft_delete_bypass_preserves_other_filters()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using (var insert = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            await insert.Sql.InsertAsync(new SampleRow { Id = "item-hidden", Name = "excluded", IsDeleted = true }, Cancellation);
            await insert.Sql.InsertAsync(new SampleRow { Id = "item-quoted", Name = "value'; SELECT 1; --" }, Cancellation);
            Assert.Equal(UnitOfWorkState.Committed, (await insert.CompleteAsync(Cancellation)).State);
        }
        var bypass = new SoftDeleteBypass("Inspect soft-deleted items for recovery", "recovery-query");
        await using var read = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        var ef = read.Context.IncludingSoftDeleted<SampleRow>(bypass);
        var sql = read.Sql.IncludingSoftDeleted<SampleRow>(bypass);
        Assert.Same(bypass, ef.Bypass);
        Assert.Same(bypass, sql.Bypass);
        Assert.Equal(bypass.OperationId, sql.Bypass.OperationId);
        var expected = new[] { "item-1", "item-2", "item-3", "item-quoted" };
        Assert.Equal(expected, await ef.Query.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Cancellation));
        Assert.Equal(expected, await sql.Query.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Cancellation));
        Assert.DoesNotContain(await read.Sql.Query<SampleRow>().Select(row => row.Id).ToArrayAsync(Cancellation), id => id == "item-3");
        Assert.Equal("item-quoted", (await read.Sql.Query<SampleRow, string>(row => row.Name, "value'; SELECT 1; --").SingleAsync(Cancellation)).Id);
        Assert.Equal(3, await read.Context.Set<SampleRow>().CountAsync(Cancellation));
    }

    [Fact]
    public async Task Removing_a_soft_deleted_entity_updates_its_flag_and_version_without_deleting_the_row()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            var row = await work.Context.Set<SampleRow>().SingleAsync(row => row.Id == "item-2", Cancellation);
            work.Context.Remove(row);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
            Assert.NotEqual(SampleInitial.InitialVersion, row.Version);
        }
        Assert.True(await ScalarAsync<bool>("SELECT \"IsDeleted\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-2'"));
        await using var read = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        Assert.Equal(new[] { "item-1" }, await read.Sql.Query<SampleRow>().Select(row => row.Id).ToArrayAsync(Cancellation));
    }

    [Fact]
    public async Task Direct_updates_require_versions_apply_filters_and_roll_back_other_work_on_conflict()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using (var winner = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            var changed = await winner.Sql.UpdateAsync<SampleRow, string, string>(row => row.Id, "item-1", SampleInitial.InitialVersion,
                row => row.Name, "direct winner", Cancellation);
            Assert.True(changed.Succeeded);
            Assert.NotNull(changed.Version);
            Assert.NotEqual(SampleInitial.InitialVersion, changed.Version);
            Assert.Equal(UnitOfWorkState.Committed, (await winner.CompleteAsync(Cancellation)).State);
        }
        await using (var stale = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            await stale.Sql.InsertAsync(new SampleRow { Id = "must-rollback", Name = "prior work" }, Cancellation);
            var result = await stale.Sql.UpdateAsync<SampleRow, string, string>(row => row.Id, "item-1", SampleInitial.InitialVersion,
                row => row.Name, "stale proposal", Cancellation);
            Assert.False(result.Succeeded);
            Assert.Equal("stale proposal", result.Conflict!.ProposedValues[nameof(SampleRow.Name)]);
            Assert.Equal(UnitOfWorkState.Failed, (await stale.CompleteAsync(Cancellation)).State);
        }
        await using (var hidden = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            var result = await hidden.Sql.UpdateAsync<SampleRow, string, string>(row => row.Id, "item-3", SampleInitial.InitialVersion,
                row => row.Name, "must remain hidden", Cancellation);
            Assert.False(result.Succeeded);
            Assert.Equal(UnitOfWorkState.Failed, (await hidden.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal("direct winner", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
        Assert.Equal("deleted", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-3'"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'must-rollback'"));
    }

    [Fact]
    public async Task Cancellation_before_commit_rolls_back_flushed_work_and_reports_one_rollback()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        var completions = new List<UnitOfWorkResult>();
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation))
        {
            work.OnCompleted(completions.Add);
            await work.Sql.InsertAsync(new SampleRow { Id = "cancelled", Name = "pending" }, Cancellation);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.CompleteAsync(cancelled.Token));
        }
        Assert.Equal(UnitOfWorkState.RolledBack, Assert.Single(completions).State);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'cancelled'"));
    }

    [Fact]
    public async Task An_abandoned_join_prevents_the_outer_transaction_from_committing()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using var outer = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        await using (var inner = await EfCoreUnitOfWork.BeginAsync(module, Deployment, UnitOfWorkMode.Join, outer, Cancellation))
            await inner.Sql.InsertAsync(new SampleRow { Id = "abandoned", Name = "joined" }, Cancellation);
        Assert.Equal(UnitOfWorkState.Failed, (await outer.CompleteAsync(Cancellation)).State);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'abandoned'"));
    }

    [Fact]
    public async Task Cancellation_interrupts_a_direct_command_waiting_on_PostgreSQL_and_prevents_commit()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        await using (var blocker = new NpgsqlConnection(Options.ConnectionString))
        {
            await blocker.OpenAsync(Cancellation);
            await using var transaction = await blocker.BeginTransactionAsync(Cancellation);
            await using var command = new NpgsqlCommand("LOCK TABLE " + Table("Items") + " IN ACCESS EXCLUSIVE MODE", blocker, transaction);
            await command.ExecuteNonQueryAsync(Cancellation);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            cancelled.CancelAfter(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Sql.InsertAsync(
                new SampleRow { Id = "blocked-insert", Name = "cancelled command" }, cancelled.Token));
            Assert.Equal(UnitOfWorkState.Failed, (await work.CompleteAsync(Cancellation)).State);
            await transaction.RollbackAsync(Cancellation);
        }
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'blocked-insert'"));
    }

    [Fact]
    public async Task A_disconnected_update_without_an_original_version_is_refused()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        work.Context.Update(new SampleRow { Id = "item-1", Name = "missing version" });
        var refusal = await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation));
        Assert.Equal("MPS010", refusal.Code);
        Assert.Equal(UnitOfWorkState.Failed, work.State);
        Assert.Equal("first", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
    }

    [Fact]
    public async Task A_callback_failure_keeps_the_committed_outcome_and_invokes_each_callback_once()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, Deployment, cancellationToken: Cancellation);
        var calls = 0;
        work.OnCompleted(_ => { calls++; throw new InvalidOperationException("Callback failed after commit."); });
        var observed = new List<UnitOfWorkResult>();
        work.OnCompleted(observed.Add);
        await work.Sql.InsertAsync(new SampleRow { Id = "committed-callback", Name = "durable" }, Cancellation);
        await Assert.ThrowsAsync<AggregateException>(() => work.CompleteAsync(Cancellation));
        Assert.Equal(UnitOfWorkState.Committed, work.State);
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        Assert.Equal(1, calls);
        Assert.Equal(UnitOfWorkState.Committed, Assert.Single(observed).State);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'committed-callback'"));
    }
}
