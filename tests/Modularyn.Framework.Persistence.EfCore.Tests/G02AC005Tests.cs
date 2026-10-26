using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(PostgreSqlCollection.Name)]
public sealed class G02AC005Tests(ITestOutputHelper output) : PostgreSqlTestDatabase
{
    [Fact]
    public async Task AC_005_P01_CommittedWorkAndSoftDeleteFiltersAgreeOnPostgreSql_Integration()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        var completions = new List<UnitOfWorkResult>();
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
        {
            work.OnCompleted(completions.Add);
            var context = (SampleContext)work.Context;
            var efIds = await context.Items.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Cancellation);
            var sqlIds = await work.Sql.Query<SampleRow>().OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(Cancellation);
            Assert.Equal(new[] { "item-1", "item-2" }, efIds);
            Assert.Equal(efIds, sqlIds);
            Assert.Empty(await work.Sql.Query<SampleRow, string>(row => row.Name, "first' OR TRUE --").ToArrayAsync(Cancellation));

            var first = await context.Items.SingleAsync(row => row.Id == "item-1", Cancellation);
            first.Name = "committed";
            await work.FlushAsync(Cancellation);
            await work.Sql.InsertAsync(new SampleRow { Id = "item-4", Name = "created" }, Cancellation);
            Assert.Equal("first", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'item-4'"));
            Assert.NotNull(work.TransactionId);
            output.WriteLine($"EF and direct SQL share transaction {work.TransactionId}; initial visible rows: {string.Join(", ", efIds)}.");
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal(UnitOfWorkState.Committed, Assert.Single(completions).State);
        Assert.Equal("committed", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'item-4'"));
        output.WriteLine("One committed callback; an independent connection sees the update and insert.");
        output.WriteLine($"EF Core {typeof(DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}; "
            + $"Npgsql {typeof(NpgsqlConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}; "
            + $"PostgreSQL {await ScalarAsync<string>("SHOW server_version")}.");
    }

    [Fact]
    public async Task AC_005_N01_StaleWritesConflictAndFailedTransactionsRollBack_Integration()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        await using (var editorA = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
        await using (var editorB = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
        {
            var first = await editorA.Context.Set<SampleRow>().SingleAsync(row => row.Id == "item-1", Cancellation);
            var stale = await editorB.Context.Set<SampleRow>().SingleAsync(row => row.Id == "item-1", Cancellation);
            Assert.Equal(first.Version, stale.Version);
            first.Name = "winner";
            Assert.Equal(UnitOfWorkState.Committed, (await editorA.CompleteAsync(Cancellation)).State);
            stale.Name = "proposed";
            var conflict = await editorB.CompleteAsync(Cancellation);
            Assert.Equal(UnitOfWorkState.Failed, conflict.State);
            var detail = Assert.Single(conflict.Conflicts);
            Assert.Equal(SampleInitial.InitialVersion, detail.ExpectedVersion);
            Assert.Equal("proposed", detail.ProposedValues[nameof(SampleRow.Name)]);
            Assert.Equal("proposed", stale.Name);
            output.WriteLine($"Stale version {detail.ExpectedVersion} reports {conflict.State}; the proposed name remains available.");
        }
        Assert.Equal("winner", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));

        var completions = new List<UnitOfWorkResult>();
        await using (var failed = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
        {
            failed.OnCompleted(completions.Add);
            var first = await failed.Context.Set<SampleRow>().SingleAsync(row => row.Id == "item-1", Cancellation);
            first.Name = "must roll back";
            await failed.FlushAsync(Cancellation);
            await failed.Sql.InsertAsync(new SampleRow { Id = "item-4", Name = "also rolls back" }, Cancellation);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => failed.Sql.InsertAsync(
                new SampleRow { Id = "item-2", Name = "duplicate key fault" }, Cancellation));
            Assert.Equal("23505", exception.SqlState);
            Assert.Equal(UnitOfWorkState.Failed, (await failed.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal(UnitOfWorkState.Failed, Assert.Single(completions).State);
        Assert.Equal("winner", await ScalarAsync<string>("SELECT \"Name\" FROM " + Table("Items") + " WHERE \"Id\" = 'item-1'"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = 'item-4'"));
        output.WriteLine("The injected duplicate insert follows a flushed update and a direct insert; independent reads retain neither change.");
    }

    [Fact]
    public async Task AC_005_B01_TransactionModesKeepTheirDeclaredCommitBoundaries_Integration()
    {
        var module = SampleModule();
        await MigrateAsync(module);
        foreach (var mode in new[] { UnitOfWorkMode.Join, UnitOfWorkMode.New, UnitOfWorkMode.Suppress })
        {
            var outerId = "outer-" + mode;
            var innerId = "inner-" + mode;
            var outerCompletions = new List<UnitOfWorkResult>();
            var innerCompletions = new List<UnitOfWorkResult>();
            await using (var outer = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: Cancellation))
            {
                outer.OnCompleted(outerCompletions.Add);
                await outer.Sql.InsertAsync(new SampleRow { Id = outerId, Name = "outer" }, Cancellation);
                await using (var inner = await EfCoreUnitOfWork.BeginAsync(module, mode, outer, Cancellation))
                {
                    inner.OnCompleted(innerCompletions.Add);
                    if (mode == UnitOfWorkMode.Join)
                    {
                        Assert.Same(outer.Context, inner.Context);
                        Assert.Equal(outer.TransactionId, inner.TransactionId);
                    }
                    else
                    {
                        Assert.NotSame(outer.Context, inner.Context);
                        if (mode == UnitOfWorkMode.Suppress) Assert.Null(inner.TransactionId);
                        else Assert.NotEqual(outer.TransactionId, inner.TransactionId);
                    }
                    await inner.Sql.InsertAsync(new SampleRow { Id = innerId, Name = "inner" }, Cancellation);
                    var innerResult = await inner.CompleteAsync(Cancellation);
                    Assert.Equal(mode switch
                    {
                        UnitOfWorkMode.Join => UnitOfWorkState.Joined,
                        UnitOfWorkMode.New => UnitOfWorkState.Committed,
                        _ => UnitOfWorkState.CompletedWithoutTransaction
                    }, innerResult.State);
                    output.WriteLine($"{mode}: outer transaction {outer.TransactionId}; inner transaction {inner.TransactionId}; inner completion {innerResult.State}.");
                }
                if (mode == UnitOfWorkMode.Join) Assert.Empty(innerCompletions);
                await outer.RollbackAsync(Cancellation);
            }
            Assert.Equal(UnitOfWorkState.RolledBack, Assert.Single(outerCompletions).State);
            Assert.Equal(mode switch
            {
                UnitOfWorkMode.Join => UnitOfWorkState.RolledBack,
                UnitOfWorkMode.New => UnitOfWorkState.Committed,
                _ => UnitOfWorkState.CompletedWithoutTransaction
            }, Assert.Single(innerCompletions).State);
            Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", outerId)));
            Assert.Equal(mode == UnitOfWorkMode.Join ? 0L : 1L,
                await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", innerId)));
        }
    }

    [Fact]
    public Task AC_005_R01_InterruptedMigrationResumesWithoutDuplicateSeeds_Integration() =>
        RecoverAsync(SampleAddNoteAndSeed.Id, cancel: false);

    [Fact]
    public Task AC_005_R01_InterruptedMigrationResumesWithoutDuplicateSeeds_Migration() =>
        RecoverAsync(SampleInitial.Id, cancel: false);

    [Fact]
    public Task AC_005_R01_InterruptedMigrationResumesWithoutDuplicateSeeds_Recovery() =>
        RecoverAsync(SampleAddNoteAndSeed.Id, cancel: true);

    private async Task RecoverAsync(string checkpointId, bool cancel)
    {
        var module = SampleModule();
        var registry = new PostgreSqlModuleRegistry([module]);
        var plan = await PlanAsync(Manifest(module));
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var observer = new CheckpointObserver((checkpoint, token) =>
        {
            if (checkpoint.MigrationId != checkpointId) return ValueTask.CompletedTask;
            if (cancel)
            {
                interruption.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new IOException("Simulated process interruption after the durable migration checkpoint.");
        });
        var coordinator = new PostgreSqlMigrationCoordinator(registry);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ApplyAsync(plan, observer, interruption.Token));
        else
        {
            var failed = await coordinator.ApplyAsync(plan, observer, Cancellation);
            Assert.Equal("MGP004", Assert.Single(failed.Diagnostics).Code);
        }
        Assert.Equal(cancel ? "Interrupted" : "Failed", await ProgressStateAsync(checkpointId));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items")));
        var expectedCheckpoints = checkpointId == SampleInitial.Id ? 1L : 2L;
        Assert.Equal(expectedCheckpoints, await ScalarAsync<long>("SELECT count(*) FROM " + Table(PostgreSqlModule.HistoryTable)));
        Assert.Equal(expectedCheckpoints - 1, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds")));
        Assert.Contains(await new MigrationActivationValidator(registry).ValidateAsync(plan.Selection, Cancellation),
            diagnostic => diagnostic.Code == MigrationActivationValidator.Incomplete);

        // A new coordinator and new connections reconcile committed history before considering replay.
        var resumed = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan, cancellationToken: Cancellation);
        Assert.Empty(resumed.Diagnostics);
        Assert.Contains(resumed.Completed, checkpoint => checkpoint.MigrationId == checkpointId && checkpoint.Recovered);
        Assert.Equal("Complete", await ProgressStateAsync(SampleInitial.Id));
        Assert.Equal("Complete", await ProgressStateAsync(SampleAddNoteAndSeed.Id));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items")));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds") + " WHERE \"Id\" = 'core'"));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM " + Table(PostgreSqlModule.HistoryTable)));
        Assert.Equal("YES:text", await ScalarAsync<string>(
            "SELECT is_nullable || ':' || data_type FROM information_schema.columns WHERE table_schema = @schema AND table_name = 'Items' AND column_name = 'Note'",
            new NpgsqlParameter("schema", Schema)));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("Items") + " WHERE \"Note\" IS NULL"));
        await AssertCompleteAsync(module);
        var repeated = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan, cancellationToken: Cancellation);
        Assert.Empty(repeated.Diagnostics);
        Assert.Empty(repeated.Completed);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds")));
        output.WriteLine($"Interrupted checkpoint {checkpointId}; resumed with {string.Join(", ", resumed.Completed.Select(item => item.MigrationId))}. "
            + "Existing rows: 3; nullable text column: present; system seed: 1; history: 2; progress: complete; repeated application: no work.");
    }
}
