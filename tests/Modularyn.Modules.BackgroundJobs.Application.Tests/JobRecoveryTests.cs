using Modularyn.Framework.Authorization;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.BackgroundJobs.Application.Tests;

[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class JobRecoveryTests
{
    private static CancellationToken Token => DurableDatabase.Token;

    [Fact]
    public async Task Lost_completion_acknowledgement_reconciles_without_another_attempt()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        Guid tenant;
        await using (var host = DurableHost.Create(database, clock))
        {
            tenant = await host.TenantAsync();
            await using var source = await host.EnterServiceAsync(tenant);
            await host.QueueAsync(source, "acknowledgement");
            var worker = host.Worker(async (execution, token) =>
            {
                await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
                execution.Work.OnCompleted(result =>
                {
                    if (result.State == UnitOfWorkState.Committed) throw new InvalidOperationException("Completion acknowledgement was lost.");
                });
            });
            Assert.Equal(0, await worker.RunDueAsync(await host.HostAsync(), host.WorkerPrincipal(), cancellationToken: Token));
        }
        await using var recovered = DurableHost.Create(database, clock);
        await using var read = await recovered.EnterServiceAsync(tenant);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await recovered.Worker().RunDueAsync(await recovered.HostAsync(), recovered.WorkerPrincipal(), cancellationToken: Token));
        Assert.Equal(JobState.Completed, Assert.Single(await recovered.RowsAsync<JobRecord>(database.Producer, read.Context)).State);
        Assert.Equal(JobAttemptState.Completed, Assert.Single(await recovered.RowsAsync<JobAttempt>(database.Producer, read.Context)).State);
        Assert.Equal(1, Assert.Single(await recovered.RowsAsync<DurableEffect>(database.Producer, read.Context)).Count);
    }

    [Fact]
    public async Task Competing_workers_do_not_claim_an_unexpired_attempt()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var first = DurableHost.Create(database, clock);
        await using var second = DurableHost.Create(database, clock);
        await using var source = await first.EnterServiceAsync(await first.TenantAsync());
        await first.QueueAsync(source, "owned");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = first.Worker(async (execution, token) =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
            await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
        }).RunDueAsync(await first.HostAsync(), first.WorkerPrincipal(), cancellationToken: Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            Assert.Equal(0, await second.Worker().RunDueAsync(await second.HostAsync(), second.WorkerPrincipal(), cancellationToken: Token));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await running);
        Assert.Single(await first.RowsAsync<JobAttempt>(database.Producer, source.Context));
        Assert.Equal(1, Assert.Single(await first.RowsAsync<DurableEffect>(database.Producer, source.Context)).Count);
    }

    [Theory]
    [InlineData(MisfirePolicy.Skip, 0)]
    [InlineData(MisfirePolicy.CatchUpOnce, 1)]
    [InlineData(MisfirePolicy.CatchUpAll, 3)]
    public async Task Every_misfire_policy_persists_each_date_once_under_competing_materialization(MisfirePolicy policy, int expected)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        clock.Set(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        await using var first = DurableHost.Create(database, clock);
        await using var second = DurableHost.Create(database, clock);
        await using var source = await first.EnterServiceAsync(await first.TenantAsync());
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, cancellationToken: Token))
        {
            first.JobStore.Schedule(work, "durable.counter", "misfire", new("America/Chicago", new(1, 30), policy), new(2026, 10, 30));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        var scheduler = new ScheduleWorker(first.JobStore, first.Get<AuthorizedWorkRunner>());
        var competing = new ScheduleWorker(second.JobStore, second.Get<AuthorizedWorkRunner>());
        var one = scheduler.MaterializeAsync(await first.HostAsync(), first.WorkerPrincipal(), cancellationToken: Token);
        var two = competing.MaterializeAsync(await second.HostAsync(), second.WorkerPrincipal(), cancellationToken: Token);
        Assert.Equal(expected, (await Task.WhenAll(one, two)).Sum());
        var dates = await first.RowsAsync<ScheduleOccurrence>(database.Producer, source.Context);
        Assert.Equal(3, dates.Length);
        Assert.Equal(expected, dates.Count(row => row.State == OccurrenceState.CatchUp));
        Assert.Equal(3 - expected, dates.Count(row => row.State == OccurrenceState.Missed));
        Assert.Equal(expected, (await first.RowsAsync<JobRecord>(database.Producer, source.Context)).Length);
        await using var restarted = DurableHost.Create(database, clock);
        Assert.Equal(0, await new ScheduleWorker(restarted.JobStore, restarted.Get<AuthorizedWorkRunner>())
            .MaterializeAsync(await restarted.HostAsync(), restarted.WorkerPrincipal(), cancellationToken: Token));
        Assert.Equal(expected, await restarted.Worker().RunDueAsync(await restarted.HostAsync(), restarted.WorkerPrincipal(), cancellationToken: Token));
        var effects = await restarted.RowsAsync<DurableEffect>(database.Producer, source.Context);
        if (expected == 0) Assert.Empty(effects);
        else Assert.Equal(expected, Assert.Single(effects).Count);
    }

    [Fact]
    public async Task Future_schedules_and_excess_backlog_do_not_starve_a_due_schedule()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        clock.Set(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        await using var host = DurableHost.Create(database, clock);
        await using var source = await host.EnterServiceAsync(await host.TenantAsync());
        var scheduler = new ScheduleWorker(host.JobStore, host.Get<AuthorizedWorkRunner>());
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, cancellationToken: Token))
        {
            host.JobStore.Schedule(work, "durable.counter", "future", new("America/Chicago", new(23, 0)), new(2026, 11, 1));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        Assert.Equal(0, await scheduler.MaterializeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, cancellationToken: Token))
        {
            host.JobStore.Schedule(work, "durable.counter", "backlog", new("America/Chicago", new(1, 0)), new(2000, 1, 1));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        Assert.Equal(0, await scheduler.MaterializeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        var backlog = Assert.Single(await host.RowsAsync<JobSchedule>(database.Producer, source.Context), row => row.Code == "BacklogLimit");
        Assert.False(backlog.Enabled);
        // A disabled schedule never runs again, so it keeps no payload.
        Assert.Equal(Framework.Audit.DurablePayloads.Redacted, backlog.Payload);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, cancellationToken: Token))
        {
            host.JobStore.Schedule(work, "durable.counter", "due", new("America/Chicago", new(6, 0)), new(2026, 11, 1));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        Assert.Equal(1, await scheduler.MaterializeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        Assert.Equal("due", Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, source.Context)).Payload);
    }

    [Fact]
    public async Task A_schedule_whose_zone_no_longer_resolves_defers_without_stopping_other_tenants_schedules()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        clock.Set(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        await using var host = DurableHost.Create(database, clock);
        await using var broken = await host.EnterServiceAsync(await host.TenantAsync());
        await using var healthy = await host.EnterServiceAsync(await host.TenantAsync());
        var brokenId = await ScheduleAsync(host, broken, "broken");
        clock.Advance(TimeSpan.FromSeconds(1));
        var healthyId = await ScheduleAsync(host, healthy, "healthy");
        // The host's time-zone database no longer knows the stored zone, as after a time-zone data change or a move to
        // another host. The broken schedule is due first, so a failure that escaped its own evaluation would end the pass.
        await database.ExecuteAsync("UPDATE \"" + database.Producer.Schema + "\".\"JobSchedules\" SET \"TimeZone\" = 'Retired/Zone' WHERE \"Id\" = @id",
            new NpgsqlParameter("id", brokenId));
        var scheduler = new ScheduleWorker(host.JobStore, host.Get<AuthorizedWorkRunner>());
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        Assert.Equal(1, await scheduler.MaterializeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(healthyId, Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, healthy.Context)).ScheduleId);
        Assert.Equal(1, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, Assert.Single(await host.RowsAsync<DurableEffect>(database.Producer, healthy.Context)).Count);
        for (var deferral = 1; deferral <= host.JobStore.Policy.MaxAttempts; deferral++)
        {
            if (deferral > 1) Assert.Equal(0, await scheduler.MaterializeAsync(dispatch, principal, cancellationToken: Token));
            var row = Assert.Single(await host.RowsAsync<JobSchedule>(database.Producer, broken.Context));
            Assert.Equal("ScheduleFailed", row.Code);
            Assert.Equal(deferral, row.Deferrals);
            Assert.Equal(deferral < host.JobStore.Policy.MaxAttempts, row.Enabled);
            Assert.Equal(new DateOnly(2026, 11, 1), row.NextDate);
            clock.Advance(TimeSpan.FromSeconds(1 << (deferral - 1)));
        }
        Assert.Equal(0, await scheduler.MaterializeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Empty(await host.RowsAsync<ScheduleOccurrence>(database.Producer, broken.Context));
        Assert.Empty(await host.RowsAsync<JobRecord>(database.Producer, broken.Context));
    }

    [Fact]
    public async Task A_job_whose_stored_reference_was_copied_from_another_row_is_denied_without_stopping_other_jobs()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        await using var swapped = await host.EnterServiceAsync(await host.TenantAsync());
        await using var healthy = await host.EnterServiceAsync(await host.TenantAsync());
        var swappedId = await host.QueueAsync(swapped, "swapped");
        clock.Advance(TimeSpan.FromSeconds(1));
        var healthyId = await host.QueueAsync(healthy, "healthy");
        // A stored reference copied from another tenant's row is sealed for that row, so it never opens for this one
        // (SEC-07); the worker's own tenant check stays behind the seal. The swapped job is due first.
        var jobs = "\"" + database.Producer.Schema + "\".\"Jobs\"";
        await database.ExecuteAsync("UPDATE " + jobs + " SET \"Propagated\" = (SELECT \"Propagated\" FROM " + jobs + " WHERE \"Id\" = @healthy) WHERE \"Id\" = @swapped",
            new NpgsqlParameter("healthy", healthyId), new NpgsqlParameter("swapped", swappedId));
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        Assert.Equal(1, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(JobState.Completed, Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, healthy.Context)).State);
        var effect = Assert.Single(await host.RowsAsync<DurableEffect>(database.Producer, healthy.Context));
        Assert.Equal(("healthy", 1), (effect.Key, effect.Count));
        var row = Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, swapped.Context));
        Assert.Equal((JobState.Denied, Framework.Tenancy.TenancyCodes.Unsealed, 0, Framework.Audit.DurablePayloads.Redacted), (row.State, row.Code, row.Attempts, row.Payload));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
        Assert.Empty(await host.RowsAsync<JobAttempt>(database.Producer, swapped.Context));
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Producer, swapped.Context));
        Assert.Single(await host.RowsAsync<DurableEffect>(database.Producer, healthy.Context));
    }

    private static async Task<Guid> ScheduleAsync(DurableHost host, DurableUnit unit, string payload)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(host.Database.Producer, unit.Context, cancellationToken: Token);
        var id = host.JobStore.Schedule(work, "durable.counter", payload, new("America/Chicago", new(6, 0)), new(2026, 11, 1));
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        return id;
    }
}
