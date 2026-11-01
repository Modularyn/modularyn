using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;
using Xunit;
using static Modularyn.Framework.Audit.Tests.AuditFixture;

namespace Modularyn.Framework.Audit.Tests;

// TH-G02-045 and TM-G02-037 on PostgreSQL: each operator replay, dispatch replay, job cancellation and schedule stop is
// recorded, with who did it, when, on which item, what it reset and the item's original producer, in the transaction of
// the change itself; an action whose record cannot be written, or a store without the audit trail, changes nothing.
[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class OperatorAuditTests
{
    [Fact]
    public async Task TM_G02_037_Each_replay_cancellation_and_schedule_stop_is_recorded_with_its_change()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var source = await host.EnterServiceAsync(tenant);
        var id = await PoisonAsync(host, source, clock);
        var owner = await host.UserAsync(tenant, owner: true);
        var operatorUser = await host.UserAsync(tenant);
        var evaluator = host.Get<IPermissionEvaluator>();
        await using (var admin = await host.EnterAsync(owner, tenant))
            await host.GrantAsync(admin, operatorUser, EventStore.ReplayPermission, JobStore.CancelPermission);
        var producer = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context));
        Assert.True(PropagatedTenantContext.TryParse(producer.Propagated, out var captured));

        await using var operation = await host.EnterAsync(operatorUser, tenant);
        Assert.True(await host.Inbox.ReplayAsync(operation.Context, operation.Principal, evaluator, id, Token));
        var replayed = Assert.Single(await database.AuditAsync(EventStore.ReplayedAction));
        Assert.Equal(operatorUser.Id.ToString("D"), replayed.Actor);
        Assert.Equal(tenant, replayed.Tenant);
        Assert.Equal(operation.Context.UnitId, replayed.Unit);
        Assert.Equal("event.inbox", replayed.ResourceType);
        Assert.Equal(id.ToString("D"), replayed.Resource);
        Assert.Equal(clock.Now, replayed.At);
        Assert.Equal("code UnsupportedVersion attempts 3 gap-waits 0 conflicts 0 producer service:" + DurableHost.Service
            + " unit " + captured!.ProducerUnitId.ToString("D"), replayed.Detail);
        Assert.Equal(EventState.Pending, Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context)).State);

        // A quarantined dispatch is replayed and recorded the same way.
        await database.ExecuteAsync("UPDATE \"" + database.ProducerSchema + "\".\"OutboxEvents\" SET \"State\" = 2, \"Code\" = 'DeliveryFailed', \"Attempts\" = 3 "
            + "WHERE \"Id\" = @id", new NpgsqlParameter("id", id));
        Assert.True(await host.Outbox.ReplayDispatchAsync(operation.Context, operation.Principal, evaluator, id, Token));
        var dispatched = Assert.Single(await database.AuditAsync(EventStore.DispatchReplayedAction));
        Assert.Equal("event.outbox", dispatched.ResourceType);
        Assert.StartsWith("code DeliveryFailed attempts 3 conflicts 0 producer service:" + DurableHost.Service, dispatched.Detail, StringComparison.Ordinal);

        // A cancelled job and a stopped schedule, each with the state it was in.
        var job = await host.QueueAsync(source, "cancelled", clock.Now.AddHours(1));
        Assert.True(await host.JobStore.CancelAsync(operation.Context, operation.Principal, evaluator, job, Token));
        var cancelled = Assert.Single(await database.AuditAsync(JobStore.CancelledAction));
        Assert.Equal(job.ToString("D"), cancelled.Resource);
        Assert.Equal("kind durable.counter state Queued attempts 0", cancelled.Detail);
        Guid schedule;
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, cancellationToken: Token))
        {
            schedule = host.JobStore.Schedule(work, "durable.counter", "scheduled", new("Etc/UTC", new(9, 0)), new(2026, 1, 2));
            await work.CompleteAsync(Token);
        }
        Assert.True(await host.JobStore.StopScheduleAsync(source.Context, source.Principal, evaluator, schedule, Token));
        var stopped = Assert.Single(await database.AuditAsync(JobStore.ScheduleStoppedAction));
        Assert.Equal(DurableHost.Service, stopped.Actor);
        Assert.Equal("kind durable.counter next 2026-01-02", stopped.Detail);

        // A refused action leaves no record: the operator lacks the schedule permission.
        Assert.False(await host.JobStore.StopScheduleAsync(operation.Context, operation.Principal, evaluator, schedule, Token));
        Assert.Single(await database.AuditAsync(JobStore.ScheduleStoppedAction));
    }

    [Fact]
    public async Task TM_G02_037_An_operator_action_whose_record_cannot_be_written_changes_nothing()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var source = await host.EnterServiceAsync(tenant);
        var id = await PoisonAsync(host, source, clock);
        var job = await host.QueueAsync(source, "kept", clock.Now.AddHours(1));
        var evaluator = host.Get<IPermissionEvaluator>();

        await BreakAsync(database);
        await Assert.ThrowsAnyAsync<Exception>(() => host.Inbox.ReplayAsync(source.Context, source.Principal, evaluator, id, Token));
        await Assert.ThrowsAnyAsync<Exception>(() => host.JobStore.CancelAsync(source.Context, source.Principal, evaluator, job, Token));
        await RepairAsync(database);
        // Without an audit trail the stores make no operator change at all.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EventStore(database.Consumer, clock).ReplayAsync(source.Context, source.Principal, evaluator, id, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new JobStore(database.Producer, clock, new JobPolicy()).CancelAsync(source.Context, source.Principal,
            evaluator, job, Token));

        var receipt = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context));
        Assert.Equal(EventState.Poison, receipt.State);
        Assert.Equal(3, receipt.Attempts);
        var queued = Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, source.Context));
        Assert.Equal(JobState.Queued, queued.State);
        Assert.False(queued.CancellationRequested);
        Assert.Empty(await database.AuditAsync(EventStore.ReplayedAction));
        Assert.Empty(await database.AuditAsync(JobStore.CancelledAction));
    }

    /// <summary>An event of a version no handler supports, quarantined after its three attempts.</summary>
    private static async Task<Guid> PoisonAsync(DurableHost host, DurableUnit source, DurableClock clock)
    {
        var id = await host.ProduceAsync(source, "unsupported", schema: 2);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token);
            clock.Advance(TimeSpan.FromSeconds(1 << attempt));
        }
        Assert.Equal(EventState.Poison, Assert.Single(await host.RowsAsync<InboxEvent>(host.Database.Consumer, source.Context)).State);
        return id;
    }
}
