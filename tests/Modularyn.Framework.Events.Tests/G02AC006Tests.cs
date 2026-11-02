using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Events.Tests;

[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class G02AC006Tests(ITestOutputHelper output)
{
    private static CancellationToken Token => DurableDatabase.Token;

    [Fact]
    public Task AC_006_P01_CommittedIntentProducesOneProjectionEffect_Integration() => CommittedAsync(false);
    [Fact]
    public Task AC_006_P01_CommittedIntentProducesOneProjectionEffect_Contract() => CommittedAsync(true);
    [Fact]
    public Task AC_006_B01_DuplicateAndReorderedEventsKeepProjectionVersion_Integration() => ReorderedAsync(false);
    [Fact]
    public Task AC_006_B01_DuplicateAndReorderedEventsKeepProjectionVersion_Contract() => ReorderedAsync(true);
    [Fact]
    public Task AC_006_N01_UnsupportedEventsRemainVisibleWithoutMutation_Integration() => UnsupportedAsync(false);
    [Fact]
    public Task AC_006_N01_UnsupportedEventsRemainVisibleWithoutMutation_Contract() => UnsupportedAsync(true);
    [Fact]
    public Task AC_006_R01_AllFiveCrashWindowsReconcileCommittedIntent_Integration() => CrashesAsync(false, false);
    [Fact]
    public Task AC_006_R01_AllFiveCrashWindowsReconcileCommittedIntent_Contract() => CrashesAsync(true, false);
    [Fact]
    public Task AC_006_R01_AllFiveCrashWindowsReconcileCommittedIntent_Recovery() => CrashesAsync(false, true);

    private static async Task CommittedAsync(bool contract)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var unit = await host.EnterServiceAsync(tenant);
        var id = await host.ProduceAsync(unit, "item");
        var outbox = Assert.Single(await host.RowsAsync<OutboxEvent>(database.Producer, unit.Context));
        Assert.Equal(id, outbox.Id);
        Assert.Equal(clock.Now, outbox.CreatedAt);
        Assert.True(PropagatedTenantContext.TryParse(outbox.Propagated, out var captured));
        Assert.Equal(unit.Context.UnitId, captured!.ProducerUnitId);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        var pump = host.Pump();
        Assert.Equal(1, await pump.DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Token));
        var effect = Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context));
        Assert.Equal(1, effect.Count);
        Assert.Equal(1, effect.Position);
        var receipt = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context));
        Assert.Equal(id, receipt.Id);
        Assert.Equal(EventState.Completed, receipt.State);
        Assert.True(PropagatedTenantContext.TryParse(receipt.Propagated, out var received));
        Assert.NotEqual(captured.ProducerUnitId, received!.ProducerUnitId);
        Assert.Equal(captured.Actor, received.Actor);
        if (contract) AssertEnvelope(outbox, receipt);
        var other = await host.TenantAsync();
        await using var otherUnit = await host.EnterServiceAsync(other);
        Assert.Empty(await host.RowsAsync<InboxEvent>(database.Consumer, otherUnit.Context));
        Assert.NotEmpty(await database.AuditAsync(AuditActions.HostScopeGranted));
    }

    private static async Task ReorderedAsync(bool contract)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var unit = await host.EnterServiceAsync(tenant);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        var id = await host.ProduceAsync(unit, "item", 5);
        var interrupted = host.Pump(new StopAt(EventCheckpoint.ReceivedBeforeDispatchAcknowledgement));
        await Assert.ThrowsAsync<EventInterruptedException>(() => interrupted.DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        await Assert.ThrowsAsync<EventInterruptedException>(() => interrupted.DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<EventInterruptedException>(() => interrupted.DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        clock.Advance(TimeSpan.FromSeconds(2));
        await using (var prune = await EfCoreUnitOfWork.BeginAsync(database.Consumer, unit.Context, cancellationToken: Token))
        {
            Assert.Equal(1, await host.Inbox.PruneReceiptsAsync(prune, cancellationToken: Token));
            Assert.Equal(UnitOfWorkState.Committed, (await prune.CompleteAsync(Token)).State);
        }
        Assert.Empty(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context));
        Assert.Single(await host.RowsAsync<EventPosition>(database.Consumer, unit.Context));
        Assert.Equal(1, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        var reconciled = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context));
        Assert.Equal(id, reconciled.Id);
        Assert.Equal("PositionAlreadyApplied", reconciled.Code);
        await host.ProduceAsync(unit, "item", 4);
        Assert.Equal(1, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        var projection = Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context));
        Assert.Equal(5, projection.Position);
        Assert.Equal(1, projection.Count);
        if (contract)
        {
            var receipts = await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context);
            foreach (var source in await host.RowsAsync<OutboxEvent>(database.Producer, unit.Context))
                AssertEnvelope(source, Assert.Single(receipts, row => row.Id == source.Id));
        }
    }

    private static async Task UnsupportedAsync(bool contract)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var unit = await host.EnterServiceAsync(tenant);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        var id = await host.ProduceAsync(unit, "item", 2, 2);
        await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(0, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
            var failed = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context));
            Assert.Equal(attempt, failed.Attempts);
            Assert.Equal("UnsupportedVersion", failed.Code);
            Assert.Equal(attempt == 3 ? EventState.Poison : EventState.Pending, failed.State);
            Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context));
            clock.Advance(TimeSpan.FromSeconds(1 << (attempt - 1)));
        }
        await host.ProduceAsync(unit, "item", 3);
        await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
        Assert.Equal(1, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        var poison = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context), row => row.Id == id);
        Assert.Equal(EventState.Poison, poison.State);
        Assert.Equal(3, Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context)).Position);
        var late = await host.ProduceAsync(unit, "item", 1, 2);
        await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(0, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
            clock.Advance(TimeSpan.FromSeconds(1 << attempt));
        }
        var latePoison = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context), row => row.Id == late);
        Assert.Equal(EventState.Poison, latePoison.State);
        Assert.Equal("UnsupportedVersion", latePoison.Code);
        Assert.Equal(1, Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context)).Count);
        if (contract)
        {
            var source = Assert.Single(await host.RowsAsync<OutboxEvent>(database.Producer, unit.Context), row => row.Id == id);
            AssertEnvelope(source, poison);
            var status = JsonSerializer.Deserialize<EventStatus>(JsonSerializer.Serialize(new EventStatus(poison.Id, poison.State, poison.Attempts, poison.Code, poison.DueAt)));
            Assert.Equal(EventState.Poison, status!.State);
            Assert.Equal("UnsupportedVersion", status.Code);
        }
    }

    private async Task CrashesAsync(bool contract, bool repeatRestart)
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        for (var window = 0; window < 5; window++)
        {
            Guid tenant;
            Guid id;
            await using (var host = DurableHost.Create(database, clock))
            {
                tenant = await host.TenantAsync();
                await using var unit = await host.EnterServiceAsync(tenant);
                id = await host.ProduceAsync(unit, "item", commit: window != 0);
                var dispatch = await host.HostAsync();
                var principal = host.WorkerPrincipal();
                if (window == 2)
                    await Assert.ThrowsAsync<EventInterruptedException>(() => host.Pump(new StopAt(EventCheckpoint.ReceivedBeforeDispatchAcknowledgement))
                        .DispatchAsync(dispatch, principal, cancellationToken: Token));
                if (window >= 3)
                {
                    await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
                    await Assert.ThrowsAsync<EventInterruptedException>(() => host.Pump(new StopAt(window == 3
                        ? EventCheckpoint.EffectBeforeCommit : EventCheckpoint.CommittedBeforeAcknowledgement))
                        .ConsumeAsync(dispatch, principal, cancellationToken: Token));
                    Assert.Equal(window == 4 ? 1 : 0, (await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context)).Length);
                }
            }
            for (var restart = 0; restart < (repeatRestart ? 2 : 1); restart++)
            {
                await using var restarted = DurableHost.Create(database, clock);
                await using var unit = await restarted.EnterServiceAsync(tenant);
                var dispatch = await restarted.HostAsync();
                var principal = restarted.WorkerPrincipal();
                await restarted.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
                await restarted.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token);
                var effects = await restarted.RowsAsync<DurableEffect>(database.Consumer, unit.Context);
                var outbox = await restarted.RowsAsync<OutboxEvent>(database.Producer, unit.Context);
                var inbox = await restarted.RowsAsync<InboxEvent>(database.Consumer, unit.Context);
                output.WriteLine($"Crash window {window}, restart {restart}: outbox={outbox.Length}, inbox={inbox.Length}, "
                    + $"effects={effects.Sum(row => row.Count)}, outbox states={string.Join(",", outbox.Select(row => row.State))}, "
                    + $"inbox states={string.Join(",", inbox.Select(row => row.State))}.");
                if (window == 0)
                {
                    Assert.Empty(effects);
                    Assert.Empty(outbox);
                    Assert.Empty(inbox);
                    Assert.Empty(await restarted.RowsAsync<DurableEffect>(database.Producer, unit.Context));
                }
                else
                {
                    Assert.Equal(1, Assert.Single(effects).Count);
                    Assert.Equal(1, Assert.Single(await restarted.RowsAsync<DurableEffect>(database.Producer, unit.Context)).Count);
                    Assert.Equal(id, Assert.Single(outbox).Id);
                    Assert.Equal(EventState.Completed, Assert.Single(outbox).State);
                    Assert.Equal(id, Assert.Single(inbox).Id);
                    Assert.Equal(EventState.Completed, Assert.Single(inbox).State);
                    if (contract) AssertEnvelope(outbox[0], inbox[0]);
                }
            }
        }
    }

    private static void AssertEnvelope(OutboxEvent source, InboxEvent receipt)
    {
        var envelope = new EventEnvelope(source.Id, source.Kind, source.SchemaVersion, source.OrderingKey, source.Position, source.Payload);
        var encoded = JsonSerializer.Serialize(envelope);
        Assert.DoesNotContain("Propagated", encoded, StringComparison.Ordinal);
        Assert.Equal(envelope, JsonSerializer.Deserialize<EventEnvelope>(encoded));
        // A side whose row has run or was delivered keeps no payload (TM-G02-039); otherwise both carry the same one.
        var received = new EventEnvelope(receipt.Id, receipt.Kind, receipt.SchemaVersion, receipt.OrderingKey, receipt.Position, receipt.Payload);
        if (Audit.DurablePayloads.IsRedacted(source.Payload) || Audit.DurablePayloads.IsRedacted(receipt.Payload))
            Assert.Equal(envelope with { Payload = "" }, received with { Payload = "" });
        else Assert.Equal(envelope, received);
    }

    internal sealed class StopAt(EventCheckpoint checkpoint) : IEventObserver
    {
        public Task ObserveAsync(EventCheckpoint current, Guid eventId, CancellationToken cancellationToken) =>
            current == checkpoint ? throw new EventInterruptedException() : Task.CompletedTask;
    }
}
