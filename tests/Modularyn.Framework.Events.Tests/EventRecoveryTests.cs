using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Events.Tests;

[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class EventRecoveryTests
{
    private static CancellationToken Token => DurableDatabase.Token;

    [Theory]
    [InlineData("kind")]
    [InlineData("schema")]
    [InlineData("key")]
    [InlineData("position")]
    [InlineData("payload")]
    public async Task An_accepted_event_identity_cannot_be_delivered_with_different_content(string mutation)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        await using var unit = await host.EnterServiceAsync(await host.TenantAsync());
        var id = await host.ProduceAsync(unit, "identity-content");
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        await Assert.ThrowsAsync<EventInterruptedException>(() => host.Pump(new G02AC006Tests.StopAt(EventCheckpoint.ReceivedBeforeDispatchAcknowledgement))
            .DispatchAsync(dispatch, principal, cancellationToken: Token));
        var accepted = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context));
        Assert.Equal(id, accepted.Id);
        (string Column, object Value) changed = mutation switch
        {
            "kind" => ("Kind", "item.other"),
            "schema" => ("SchemaVersion", accepted.SchemaVersion + 1),
            "key" => ("OrderingKey", "different-item"),
            "position" => ("Position", accepted.Position + 1),
            "payload" => ("Payload", "different-content"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        // Simulate a transport retry carrying different content while preserving the previously accepted identity.
        await database.ExecuteAsync("UPDATE \"" + database.Producer.Schema + "\".\"OutboxEvents\" SET \"" + changed.Column
            + "\" = @value WHERE \"Id\" = @id", new NpgsqlParameter("value", changed.Value), new NpgsqlParameter("id", id));
        Assert.Equal(0, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        var refused = Assert.Single(await host.RowsAsync<OutboxEvent>(database.Producer, unit.Context));
        Assert.Equal("DeliveryFailed", refused.Code);
        Assert.Equal(EventState.Pending, refused.State);
        var receipt = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context));
        Assert.Equal((accepted.Id, accepted.Kind, accepted.SchemaVersion, accepted.OrderingKey, accepted.Position, accepted.Payload, accepted.Version),
            (receipt.Id, receipt.Kind, receipt.SchemaVersion, receipt.OrderingKey, receipt.Position, receipt.Payload, receipt.Version));
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context));
    }

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
    public async Task A_contiguous_gap_backs_off_then_quarantines_for_replay_once_the_predecessor_arrives()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var source = await host.EnterServiceAsync(tenant);
        var waiting = await host.ProduceAsync(source, "ordered", 2);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        var policy = new EventRetryPolicy(gapWaits: 3);
        var pump = host.Pump(ordering: EventOrdering.Contiguous, retry: policy);
        Assert.Equal(1, await pump.DispatchAsync(dispatch, principal, cancellationToken: Token));
        for (var wait = 1; wait <= policy.GapWaits; wait++)
        {
            Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
            var row = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context));
            Assert.Equal((wait, 0), (row.GapWaits, row.Attempts));
            if (wait == policy.GapWaits)
            {
                Assert.Equal((EventState.Poison, EventPump.GapExhaustedCode), (row.State, row.Code));
                break;
            }
            Assert.Equal((EventState.Pending, EventPump.GapCode, clock.Now + policy.Delay(wait)), (row.State, row.Code, row.DueAt));
            // Not due before its backed-off delay: the wait is not counted again.
            clock.Advance(policy.Delay(wait) - TimeSpan.FromMicroseconds(1));
            Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
            Assert.Equal(wait, Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context)).GapWaits);
            clock.Advance(TimeSpan.FromMicroseconds(1));
        }
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, source.Context));
        // The predecessor arrives and applies; an operator then replays the quarantined receipt with a fresh budget.
        await host.ProduceAsync(source, "ordered", 1);
        Assert.Equal(1, await pump.DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
        await using var operation = await host.EnterServiceAsync(tenant);
        Assert.True(await host.Inbox.ReplayAsync(operation.Context, operation.Principal, host.Get<IPermissionEvaluator>(), waiting, Token));
        var replayed = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context), row => row.Id == waiting);
        Assert.Equal((EventState.Pending, 0, 0), (replayed.State, replayed.GapWaits, replayed.Attempts));
        Assert.Equal(1, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
        var effect = Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, source.Context));
        Assert.Equal((2, 2L), (effect.Count, effect.Position));
    }

    [Fact]
    public async Task A_conflicting_receipt_backs_off_on_its_own_budget_then_quarantines()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        await using var source = await host.EnterServiceAsync(await host.TenantAsync());
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        await host.ProduceAsync(source, "conflicting");
        Assert.Equal(1, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        clock.Advance(TimeSpan.FromSeconds(1));
        await host.ProduceAsync(source, "other");
        Assert.Equal(1, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        // A handler defect: it removes a row by a version it never read, so its own write always loses the concurrency check.
        var consumer = new EventConsumer(DurableModel.Consumer, DurableModel.Apply, [new("item.changed", 1, async (execution, token) =>
        {
            if (execution.Event.OrderingKey == "conflicting")
                execution.Work.Context.Remove(new DurableEffect
                    { TenantId = execution.Work.Context.Tenant!.TenantId!.Value, Key = "never-read", Version = Guid.NewGuid() });
            else await DurableModel.EffectAsync(execution.Work, execution.Event.OrderingKey, execution.Event.Position, token);
        })]);
        var policy = new EventRetryPolicy(conflicts: 3);
        var pump = new EventPump(host.Outbox, host.Inbox, consumer, host.Get<AuthorizedWorkRunner>(), clock, policy);
        // The conflicting receipt is due first. Once its conflict is recorded it leaves the head of the scan, so the next
        // pass of one reaches the other key.
        Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        Assert.Equal(1, await pump.ConsumeAsync(dispatch, principal, limit: 1, cancellationToken: Token));
        for (var conflict = 1; conflict <= policy.Conflicts; conflict++)
        {
            if (conflict > 1) Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
            var row = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context), row => row.OrderingKey == "conflicting");
            Assert.Equal((conflict, 0), (row.Conflicts, row.Attempts));
            if (conflict == policy.Conflicts)
            {
                Assert.Equal((EventState.Poison, EventPump.ConflictExhaustedCode), (row.State, row.Code));
                break;
            }
            Assert.Equal((EventState.Pending, EventPump.ConflictCode), (row.State, row.Code));
            Assert.True(row.DueAt > clock.Now);
            clock.Advance(policy.Delay(conflict));
        }
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
        var effect = Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, source.Context));
        Assert.Equal(("other", 1), (effect.Key, effect.Count));
        Assert.DoesNotContain(await host.RowsAsync<EventPosition>(database.Consumer, source.Context), entry => entry.OrderingKey == "conflicting");
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
    public async Task A_receipt_whose_stored_reference_was_copied_from_another_row_is_denied_without_an_effect()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        await using var swapped = await host.EnterServiceAsync(await host.TenantAsync());
        await using var other = await host.EnterServiceAsync(await host.TenantAsync());
        var swappedId = await host.ProduceAsync(swapped, "swapped");
        var sameId = await host.ProduceAsync(other, "same");
        var otherId = await host.ProduceAsync(other, "other");
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        Assert.Equal(3, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        // A receipt's stored reference copied from another tenant's receipt, and from another receipt of the same tenant:
        // each is sealed for the row it came from, so neither opens for the row it was copied to (SEC-07). The consumer's
        // own tenant check stays behind the seal.
        var inbox = "\"" + database.Consumer.Schema + "\".\"InboxEvents\"";
        foreach (var copy in new[] { swappedId, sameId })
            await database.ExecuteAsync("UPDATE " + inbox + " SET \"Propagated\" = (SELECT \"Propagated\" FROM " + inbox + " WHERE \"Id\" = @other) WHERE \"Id\" = @copy",
                new NpgsqlParameter("other", otherId), new NpgsqlParameter("copy", copy));
        Assert.Equal(1, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        foreach (var (unit, id) in new[] { (swapped, swappedId), (other, sameId) })
        {
            var row = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context), row => row.Id == id);
            Assert.Equal((EventState.Denied, TenancyCodes.Unsealed, 1, Audit.DurablePayloads.Redacted), (row.State, row.Code, row.Attempts, row.Payload));
        }
        Assert.Equal(0, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, swapped.Context));
        Assert.Empty(await host.RowsAsync<EventPosition>(database.Consumer, swapped.Context));
        var effect = Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, other.Context));
        Assert.Equal(("other", 1), (effect.Key, effect.Count));
        Assert.Equal(EventState.Completed, Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, other.Context), row => row.Id == otherId).State);
    }

    [Fact]
    public async Task A_validly_sealed_reference_naming_another_tenant_than_its_row_never_acts_in_either_tenant()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        await using var owner = await host.EnterServiceAsync(await host.TenantAsync());
        await using var other = await host.EnterServiceAsync(await host.TenantAsync());
        var sent = await host.ProduceAsync(owner, "sent");
        var received = await host.ProduceAsync(owner, "received");
        // References sealed with the host's own key ring and bound to the very rows they are stored on, but captured in
        // another tenant: they open, and revalidate for that tenant, so only the tenant checks behind the seal stand between
        // them and the rows. Receipt refuses one on the outbox and consumption one on the inbox, each before any read.
        await ForgeAsync(database, database.ProducerSchema, "OutboxEvents", sent, PropagatedTenantContext.Capture(other.Context, clock).ToJson(host.Outbox.OutboxBinding(sent)));
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(attempt == 1 ? 1 : 0, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
            clock.Advance(TimeSpan.FromSeconds(1 << (attempt - 1)));
        }
        var outbox = Assert.Single(await host.RowsAsync<OutboxEvent>(database.Producer, owner.Context), row => row.Id == sent);
        Assert.Equal((EventState.Poison, "DeliveryFailed", 3), (outbox.State, outbox.Code, outbox.Attempts));
        Assert.DoesNotContain(await host.RowsAsync<InboxEvent>(database.Consumer, other.Context), row => row.Id == sent);
        Assert.DoesNotContain(await host.RowsAsync<InboxEvent>(database.Consumer, owner.Context), row => row.Id == sent);

        await ForgeAsync(database, database.Consumer.Schema, "InboxEvents", received,
            PropagatedTenantContext.Capture(other.Context, clock).ToJson(host.Inbox.InboxBinding(received)));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(0, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
            clock.Advance(TimeSpan.FromSeconds(1 << (attempt - 1)));
        }
        var inbox = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, owner.Context), row => row.Id == received);
        Assert.Equal((EventState.Poison, "HandlerFailed", 3), (inbox.State, inbox.Code, inbox.Attempts));
        foreach (var unit in new[] { owner, other })
        {
            Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context));
            Assert.Empty(await host.RowsAsync<EventPosition>(database.Consumer, unit.Context));
        }
        Assert.Empty(await host.RowsAsync<InboxEvent>(database.Consumer, other.Context));
    }

    // Replaces a row's stored reference directly, as only a database writer could.
    private static Task ForgeAsync(DurableDatabase database, string schema, string table, Guid id, string propagated) =>
        database.ExecuteAsync("UPDATE \"" + schema + "\".\"" + table + "\" SET \"Propagated\" = @propagated WHERE \"Id\" = @id",
            new NpgsqlParameter("propagated", propagated), new NpgsqlParameter("id", id));

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
