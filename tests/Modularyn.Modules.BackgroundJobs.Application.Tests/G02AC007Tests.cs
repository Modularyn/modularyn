using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Modules.BackgroundJobs.Application.Tests;

[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class G02AC007Tests(ITestOutputHelper output)
{
    private static CancellationToken Token => DurableDatabase.Token;

    [Fact]
    public Task AC_007_P01_DueJobRunsOnceWithVisibleState_Integration() => DueAsync(false);
    [Fact]
    public Task AC_007_P01_DueJobRunsOnceWithVisibleState_Recovery() => DueAsync(true);
    [Fact]
    public Task AC_007_N01_RetryExhaustionAndCancellationHaveDistinctOutcomes_Integration() => OutcomesAsync(false);
    [Fact]
    public Task AC_007_N01_RetryExhaustionAndCancellationHaveDistinctOutcomes_Recovery() => OutcomesAsync(true);
    [Fact]
    public Task AC_007_R01_LeaseLossAndRestartLeaveOneCommittedEffect_Integration() => LeaseAsync(false);
    [Fact]
    public Task AC_007_R01_LeaseLossAndRestartLeaveOneCommittedEffect_Recovery() => LeaseAsync(true);
    [Fact]
    public Task AC_007_B01_ScheduleEdgesAndMisfiresFollowDeclaredPolicy_Integration() => SchedulesAsync(false);
    [Fact]
    public Task AC_007_B01_ScheduleEdgesAndMisfiresFollowDeclaredPolicy_Recovery() => SchedulesAsync(true);

    private async Task DueAsync(bool restart)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        clock.Set(new DateTimeOffset(2026, 11, 1, 11, 59, 59, TimeSpan.Zero).AddTicks(7));
        Guid tenant;
        Guid id;
        var due = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        await using (var host = DurableHost.Create(database, clock))
        {
            tenant = await host.TenantAsync();
            await using var unit = await host.EnterServiceAsync(tenant);
            id = await host.QueueAsync(unit, "counter", due.AddTicks(7));
            var dispatch = await host.HostAsync();
            Assert.Equal(0, await host.Worker().RunDueAsync(dispatch, host.WorkerPrincipal(), cancellationToken: Token));
            Assert.Equal(JobState.Queued, Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, unit.Context)).State);
            Assert.Empty(await host.RowsAsync<JobAttempt>(database.Producer, unit.Context));
            if (!restart)
            {
                clock.Set(due.AddTicks(7));
                Assert.Equal(1, await host.Worker().RunDueAsync(dispatch, host.WorkerPrincipal(), cancellationToken: Token));
            }
        }
        await using var recovered = DurableHost.Create(database, clock);
        await using var read = await recovered.EnterServiceAsync(tenant);
        clock.Set(due.AddTicks(7));
        var hostContext = await recovered.HostAsync();
        Assert.Equal(restart ? 1 : 0, await recovered.Worker().RunDueAsync(hostContext, recovered.WorkerPrincipal(), cancellationToken: Token));
        Assert.Equal(0, await recovered.Worker().RunDueAsync(hostContext, recovered.WorkerPrincipal(), cancellationToken: Token));
        var row = Assert.Single(await recovered.RowsAsync<JobRecord>(database.Producer, read.Context));
        Assert.Equal(id, row.Id);
        Assert.Equal(due, row.DueAt);
        Assert.Equal(JobState.Completed, row.State);
        Assert.Equal(1, row.Attempts);
        var history = Assert.Single(await recovered.RowsAsync<JobAttempt>(database.Producer, read.Context));
        Assert.Equal(JobAttemptState.Completed, history.State);
        Assert.Equal(due, history.StartedAt);
        Assert.Equal(due, history.EndedAt);
        Assert.Equal(1, Assert.Single(await recovered.RowsAsync<DurableEffect>(database.Producer, read.Context)).Count);
        output.WriteLine($"Due job {id}: due={row.DueAt:O}, state={row.State}, attempts={row.Attempts}, completed={history.EndedAt:O}.");
    }

    private async Task OutcomesAsync(bool restart)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var unit = await host.EnterServiceAsync(tenant);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        var failed = await host.QueueAsync(unit, "failed");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var running = DurableHost.Create(database, clock);
            var worker = (restart ? running : host).Worker(async (execution, token) =>
            {
                await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
                await execution.Work.FlushAsync(token);
                throw new InvalidOperationException("The dependency failed.");
            });
            Assert.Equal(0, await worker.RunDueAsync(await running.HostAsync(), running.WorkerPrincipal(), cancellationToken: Token));
            var row = Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, unit.Context));
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal(attempt == 3 ? JobState.Failed : JobState.Retry, row.State);
            Assert.Equal("HandlerFailed", row.Code);
            Assert.Empty(await host.RowsAsync<DurableEffect>(database.Producer, unit.Context));
            var delay = TimeSpan.FromSeconds(1 << (attempt - 1));
            clock.Advance(delay - TimeSpan.FromMicroseconds(1));
            Assert.Equal(0, await worker.RunDueAsync(dispatch, principal, cancellationToken: Token));
            clock.Advance(TimeSpan.FromMicroseconds(1));
        }
        var queued = await host.QueueAsync(unit, "cancelled-before");
        Assert.True(await host.JobStore.CancelAsync(unit.Context, unit.Principal, host.Get<IPermissionEvaluator>(), queued, Token));
        Assert.Equal(0, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
        var runningId = await host.QueueAsync(unit, "cancelled-during");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var task = host.Worker(async (execution, token) =>
        {
            await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
            await execution.Work.FlushAsync(token);
            reached.TrySetResult();
            await release.Task.WaitAsync(token);
        }).RunDueAsync(dispatch, principal, cancellationToken: shutdown.Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            Assert.True(await host.JobStore.CancelAsync(unit.Context, unit.Principal, host.Get<IPermissionEvaluator>(), runningId, Token));
            if (restart)
            {
                shutdown.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                clock.Advance(host.JobStore.Policy.Lease + TimeSpan.FromMicroseconds(1));
                await using var recovered = DurableHost.Create(database, clock);
                Assert.Equal(0, await recovered.Worker().RunDueAsync(await recovered.HostAsync(), recovered.WorkerPrincipal(), cancellationToken: Token));
            }
            else
            {
                release.TrySetResult();
                Assert.Equal(0, await task);
            }
        }
        finally { release.TrySetResult(); }
        var jobs = await host.RowsAsync<JobRecord>(database.Producer, unit.Context);
        Assert.Equal(JobState.Failed, Assert.Single(jobs, row => row.Id == failed).State);
        Assert.Equal(JobState.CancelledBeforeRun, Assert.Single(jobs, row => row.Id == queued).State);
        Assert.Equal(JobState.CancelledDuringRun, Assert.Single(jobs, row => row.Id == runningId).State);
        var attempts = await host.RowsAsync<JobAttempt>(database.Producer, unit.Context);
        Assert.Equal(3, attempts.Count(row => row.JobId == failed && row.State == JobAttemptState.Failed));
        Assert.DoesNotContain(attempts, row => row.JobId == queued);
        Assert.Equal(JobAttemptState.Cancelled, Assert.Single(attempts, row => row.JobId == runningId).State);
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Producer, unit.Context));
        foreach (var job in jobs) output.WriteLine($"Job {job.Id}: state={job.State}, attempts={job.Attempts}, code={job.Code}.");
        foreach (var attempt in attempts) output.WriteLine($"Attempt {attempt.Number}: job={attempt.JobId}, state={attempt.State}, "
            + $"started={attempt.StartedAt:O}, ended={attempt.EndedAt:O}, code={attempt.Code}.");
    }

    private async Task LeaseAsync(bool restart)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var first = DurableHost.Create(database, clock);
        await using var replacement = DurableHost.Create(database, clock);
        var tenant = await first.TenantAsync();
        await using var unit = await first.EnterServiceAsync(tenant);
        var id = await first.QueueAsync(unit, "counter");
        var oldReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTask = first.Worker(async (execution, token) =>
        {
            await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
            await execution.Work.FlushAsync(token);
            oldReached.TrySetResult();
            await oldRelease.Task.WaitAsync(token);
        }).RunDueAsync(await first.HostAsync(), first.WorkerPrincipal(), cancellationToken: Token);
        Task<int>? newTask = null;
        try
        {
            await oldReached.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            var running = Assert.Single(await first.RowsAsync<JobRecord>(database.Producer, unit.Context));
            Assert.Equal(JobState.Running, running.State);
            clock.Set(running.LeaseUntil!.Value.AddTicks(7));
            var newHost = restart ? replacement : first;
            newTask = newHost.Worker(async (execution, token) =>
            {
                newReached.TrySetResult();
                await newRelease.Task.WaitAsync(token);
                await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
            }).RunDueAsync(await newHost.HostAsync(), newHost.WorkerPrincipal(), cancellationToken: Token);
            await newReached.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            oldRelease.TrySetResult();
            Assert.Equal(0, await oldTask);
            Assert.Empty(await first.RowsAsync<DurableEffect>(database.Producer, unit.Context));
            newRelease.TrySetResult();
            Assert.Equal(1, await newTask);
        }
        finally
        {
            oldRelease.TrySetResult();
            newRelease.TrySetResult();
            await oldTask;
            if (newTask is not null) await newTask;
        }
        var job = Assert.Single(await first.RowsAsync<JobRecord>(database.Producer, unit.Context));
        Assert.Equal(id, job.Id);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.Attempts);
        var history = (await first.RowsAsync<JobAttempt>(database.Producer, unit.Context)).OrderBy(row => row.Number).ToArray();
        Assert.Equal([JobAttemptState.LeaseLost, JobAttemptState.Completed], history.Select(row => row.State));
        Assert.NotEqual(history[0].Lease, history[1].Lease);
        Assert.Equal(1, Assert.Single(await first.RowsAsync<DurableEffect>(database.Producer, unit.Context)).Count);
        foreach (var attempt in history) output.WriteLine($"Lease {attempt.Lease}: attempt={attempt.Number}, state={attempt.State}, "
            + $"started={attempt.StartedAt:O}, ended={attempt.EndedAt:O}, code={attempt.Code}.");
    }

    private async Task SchedulesAsync(bool restart)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        clock.Set(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero));
        Guid tenant;
        Guid autumn;
        await using (var host = DurableHost.Create(database, clock))
        {
            tenant = await host.TenantAsync();
            await using var unit = await host.EnterServiceAsync(tenant);
            var spring = await ScheduleAsync(host, unit.Context, new("America/Chicago", new(2, 30)), new(2026, 3, 8));
            var scheduler = new ScheduleWorker(host.JobStore, host.Get<AuthorizedWorkRunner>());
            Assert.Equal(0, await scheduler.MaterializeAsync(await host.HostAsync(), host.WorkerPrincipal(), cancellationToken: Token));
            var skipped = Assert.Single(await host.RowsAsync<ScheduleOccurrence>(database.Producer, unit.Context));
            Assert.Equal(OccurrenceState.NonexistentLocalTime, skipped.State);
            Assert.Null(skipped.DueAt);
            Assert.True(await host.JobStore.StopScheduleAsync(unit.Context, unit.Principal, host.Get<IPermissionEvaluator>(), spring, Token));
            autumn = await ScheduleAsync(host, unit.Context, new("America/Chicago", new(1, 30)), new(2026, 10, 30));
            clock.Set(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(7));
            if (!restart) Assert.Equal(1, await scheduler.MaterializeAsync(await host.HostAsync(), host.WorkerPrincipal(), cancellationToken: Token));
        }
        await using var recovered = DurableHost.Create(database, clock);
        await using var read = await recovered.EnterServiceAsync(tenant);
        var worker = new ScheduleWorker(recovered.JobStore, recovered.Get<AuthorizedWorkRunner>());
        var dispatch = await recovered.HostAsync();
        var principal = recovered.WorkerPrincipal();
        Assert.Equal(restart ? 1 : 0, await worker.MaterializeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await worker.MaterializeAsync(dispatch, principal, cancellationToken: Token));
        var history = (await recovered.RowsAsync<ScheduleOccurrence>(database.Producer, read.Context)).Where(row => row.ScheduleId == autumn)
            .OrderBy(row => row.LocalDate).ToArray();
        Assert.Equal(3, history.Length);
        Assert.Equal([OccurrenceState.Missed, OccurrenceState.Missed, OccurrenceState.CatchUp], history.Select(row => row.State));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), history[2].DueAt);
        var due = Assert.Single(await recovered.RowsAsync<JobRecord>(database.Producer, read.Context));
        Assert.Equal(history[2].DueAt, due.DueAt);
        Assert.Equal(autumn, due.ScheduleId);
        Assert.Equal(1, await recovered.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await recovered.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, Assert.Single(await recovered.RowsAsync<DurableEffect>(database.Producer, read.Context)).Count);
        foreach (var occurrence in history) output.WriteLine($"Occurrence {occurrence.LocalDate:O}: due={occurrence.DueAt:O}, state={occurrence.State}.");
    }

    private static async Task<Guid> ScheduleAsync(DurableHost host, TenantContext context, DailySchedule schedule, DateOnly date)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(host.Database.Producer, context, cancellationToken: Token);
        var id = host.JobStore.Schedule(work, "durable.counter", "scheduled-counter", schedule, date);
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        return id;
    }
}
