using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Events.Tests;

[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class EventRecoveryTests
{
    private static CancellationToken Token => DurableDatabase.Token;

    [Fact]
    public async Task PostgreSQL_session_loss_before_commit_removes_flushed_business_and_intent_rows()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        Guid tenant;
        await using (var host = DurableHost.Create(database, clock))
        {
            tenant = await host.TenantAsync();
            await using var source = await host.EnterServiceAsync(tenant);
            var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, cancellationToken: Token);
            var disconnected = false;
            try
            {
                await DurableModel.EffectAsync(work, "disconnected", 1, Token);
                host.Outbox.Append(work, new(DurableModel.Consumer, "item.changed", 1, "disconnected", 1, "value"));
                Assert.True(await work.FlushAsync(Token) > 0);
                var connection = (NpgsqlConnection)work.Context.Database.GetDbConnection();
                await database.TerminateSessionAsync(connection.ProcessID);
                disconnected = true;
                await Assert.ThrowsAnyAsync<Exception>(() => work.CompleteAsync(Token));
            }
            finally
            {
                try { await work.DisposeAsync(); }
                catch (Exception) when (disconnected) { }
            }
        }
        await using var restarted = DurableHost.Create(database, clock);
        await using var read = await restarted.EnterServiceAsync(tenant);
        var dispatch = await restarted.HostAsync();
        var principal = restarted.WorkerPrincipal();
        Assert.Equal(0, await restarted.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await restarted.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Empty(await restarted.RowsAsync<OutboxEvent>(database.Producer, read.Context));
        Assert.Empty(await restarted.RowsAsync<InboxEvent>(database.Consumer, read.Context));
        Assert.Empty(await restarted.RowsAsync<DurableEffect>(database.Producer, read.Context));
        Assert.Empty(await restarted.RowsAsync<DurableEffect>(database.Consumer, read.Context));
    }

    [Fact]
    public async Task Concurrent_delivery_and_consumers_commit_one_effect()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var first = DurableHost.Create(database, clock);
        await using var second = DurableHost.Create(database, clock);
        var tenant = await first.TenantAsync();
        await using var source = await first.EnterServiceAsync(tenant);
        await first.ProduceAsync(source, "concurrent");
        var firstHost = await first.HostAsync();
        var secondHost = await second.HostAsync();
        var firstPrincipal = first.WorkerPrincipal();
        var secondPrincipal = second.WorkerPrincipal();
        await Task.WhenAll(first.Pump().DispatchAsync(firstHost, firstPrincipal, cancellationToken: Token),
            second.Pump().DispatchAsync(secondHost, secondPrincipal, cancellationToken: Token));
        Assert.Single(await first.RowsAsync<InboxEvent>(database.Consumer, source.Context));
        var arrivals = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new EventConsumer(DurableModel.Consumer, DurableModel.Apply, [new("item.changed", 1, async (execution, token) =>
        {
            if (Interlocked.Increment(ref arrivals) == 2) ready.TrySetResult();
            await release.Task.WaitAsync(token);
            await DurableModel.EffectAsync(execution.Work, execution.Event.OrderingKey, execution.Event.Position, token);
        })]);
        EventPump Pump(DurableHost host) => new(host.Outbox, host.Inbox, consumer, host.Get<AuthorizedWorkRunner>(), clock, new());
        var one = Pump(first).ConsumeAsync(firstHost, firstPrincipal, cancellationToken: Token);
        var two = Pump(second).ConsumeAsync(secondHost, secondPrincipal, cancellationToken: Token);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), Token); }
        finally { release.TrySetResult(); }
        Assert.Equal(1, (await Task.WhenAll(one, two)).Sum());
        Assert.Equal(1, Assert.Single(await first.RowsAsync<DurableEffect>(database.Consumer, source.Context)).Count);
        Assert.Equal(EventState.Completed, Assert.Single(await first.RowsAsync<InboxEvent>(database.Consumer, source.Context)).State);
    }

    [Fact]
    public async Task A_contiguous_gap_yields_the_scan_to_its_missing_predecessor_and_other_keys()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        await using var source = await host.EnterServiceAsync(await host.TenantAsync());
        await host.ProduceAsync(source, "ordered", 2);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        var pump = host.Pump(ordering: EventOrdering.Contiguous);
        await pump.DispatchAsync(dispatch, principal, cancellationToken: Token);
        Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, source.Context));
        var gap = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context));
        Assert.Equal("OrderingGap", gap.Code);
        Assert.Equal(0, gap.Attempts);
        await host.ProduceAsync(source, "ordered", 1);
        await host.ProduceAsync(source, "independent", 1);
        await pump.DispatchAsync(dispatch, principal, cancellationToken: Token);
        Assert.Equal(1, await pump.ConsumeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        Assert.Equal(1, await pump.ConsumeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await pump.ConsumeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        var effects = await host.RowsAsync<DurableEffect>(database.Consumer, source.Context);
        var ordered = Assert.Single(effects, row => row.Key == "ordered");
        Assert.Equal(2, ordered.Count);
        Assert.Equal(2, ordered.Position);
        Assert.Equal(1, Assert.Single(effects, row => row.Key == "independent").Count);
    }

    [Fact]
    public async Task Handler_failure_rolls_back_flushed_effects_before_quarantine()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        await using var source = await host.EnterServiceAsync(await host.TenantAsync());
        await host.ProduceAsync(source, "failing");
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
        var consumer = new EventConsumer(DurableModel.Consumer, DurableModel.Apply, [new("item.changed", 1, async (execution, token) =>
        {
            await DurableModel.EffectAsync(execution.Work, execution.Event.OrderingKey, execution.Event.Position, token);
            await execution.Work.FlushAsync(token);
            throw new InvalidOperationException("The consumer failed after its database write.");
        })]);
        var pump = new EventPump(host.Outbox, host.Inbox, consumer, host.Get<AuthorizedWorkRunner>(), clock, new());
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
            Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, source.Context));
            Assert.Empty(await host.RowsAsync<EventPosition>(database.Consumer, source.Context));
            clock.Advance(TimeSpan.FromSeconds(1 << attempt));
        }
        var poison = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context));
        Assert.Equal(EventState.Poison, poison.State);
        Assert.Equal("HandlerFailed", poison.Code);
        Assert.Equal(3, poison.Attempts);
    }

    [Fact]
    public async Task A_receipt_whose_stored_context_names_another_tenant_is_quarantined_without_an_effect()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        await using var swapped = await host.EnterServiceAsync(await host.TenantAsync());
        await using var other = await host.EnterServiceAsync(await host.TenantAsync());
        var swappedId = await host.ProduceAsync(swapped, "swapped");
        var otherId = await host.ProduceAsync(other, "other");
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        Assert.Equal(2, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        // A receipt's stored reference copied from another tenant's receipt: revalidation succeeds for that other tenant,
        // so only the consumer's own tenant check stands between the reference and the row.
        var inbox = "\"" + database.Consumer.Schema + "\".\"InboxEvents\"";
        await database.ExecuteAsync("UPDATE " + inbox + " SET \"Propagated\" = (SELECT \"Propagated\" FROM " + inbox + " WHERE \"Id\" = @other) WHERE \"Id\" = @swapped",
            new NpgsqlParameter("other", otherId), new NpgsqlParameter("swapped", swappedId));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(attempt == 1 ? 1 : 0, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
            var row = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, swapped.Context));
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal(attempt == 3 ? EventState.Poison : EventState.Pending, row.State);
            clock.Advance(TimeSpan.FromSeconds(1 << (attempt - 1)));
        }
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, swapped.Context));
        Assert.Empty(await host.RowsAsync<EventPosition>(database.Consumer, swapped.Context));
        var effect = Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, other.Context));
        Assert.Equal(("other", 1), (effect.Key, effect.Count));
        Assert.Equal(EventState.Completed, Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, other.Context)).State);
    }

    [Fact]
    public async Task Stores_refuse_suppressed_units_and_another_modules_transaction()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new());
        await using var source = await host.EnterServiceAsync(await host.TenantAsync());
        var intent = new EventIntent(DurableModel.Consumer, "item.changed", 1, "atomic", 1, "value");
        await using (var suppressed = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, UnitOfWorkMode.Suppress, cancellationToken: Token))
        {
            Assert.Throws<InvalidOperationException>(() => host.Outbox.Append(suppressed, intent));
            Assert.Throws<InvalidOperationException>(() => host.JobStore.Enqueue(suppressed, new("durable.counter", "atomic", host.Clock.Now)));
        }
        await using var foreign = await EfCoreUnitOfWork.BeginAsync(database.Consumer, source.Context, cancellationToken: Token);
        Assert.Throws<InvalidOperationException>(() => host.Outbox.Append(foreign, intent));
        Assert.Throws<InvalidOperationException>(() => host.JobStore.Enqueue(foreign, new("durable.counter", "atomic", host.Clock.Now)));
        Assert.Empty(await host.RowsAsync<OutboxEvent>(database.Producer, source.Context));
    }
}
