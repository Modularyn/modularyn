using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Events;

/// <summary>
/// Durable local transport between two independently owned module transactions. Delivery is at least once; receipt,
/// ordering position and handler effects commit together. Every handler receives the same unit and running principal.
/// </summary>
public sealed class EventPump(EventStore producer, EventStore receiver, EventConsumer consumer, AuthorizedWorkRunner runner,
    TimeProvider clock, EventRetryPolicy retry, IEventObserver? observer = null)
{
    /// <summary>A contiguous receipt waiting for its missing predecessor.</summary>
    public const string GapCode = "OrderingGap";
    /// <summary>A contiguous receipt quarantined after its gap-wait budget.</summary>
    public const string GapExhaustedCode = "OrderingGapExhausted";
    /// <summary>A row waiting after a concurrency check that no competing transaction decided.</summary>
    public const string ConflictCode = "ConcurrencyConflict";
    /// <summary>A row quarantined after its conflict budget.</summary>
    public const string ConflictExhaustedCode = "ConcurrencyConflictExhausted";

    public async Task<int> DispatchAsync(TenantContext host, ExecutionPrincipal principal, int limit = 100, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var now = UtcMicroseconds.Now(clock);
        await using var scan = await EfCoreUnitOfWork.BeginAsync(producer.Module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var rows = await (await scan.Context.AcrossTenantsAsync<OutboxEvent>(cancellationToken).ConfigureAwait(false)).Query.AsNoTracking()
            .Where(row => row.Consumer == consumer.Name && row.State == EventState.Pending && row.DueAt <= now)
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.Position).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var delivered = 0;
        foreach (var row in rows)
        {
            TenantWorkOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(principal, row.Propagated, consumer.Permission, async (unit, _, token) =>
                {
                    await using var work = await EfCoreUnitOfWork.BeginAsync(receiver.Module, unit.Context, cancellationToken: token).ConfigureAwait(false);
                    await receiver.AcceptAsync(work, row, token).ConfigureAwait(false);
                    if ((await work.CompleteAsync(token).ConfigureAwait(false)).State != UnitOfWorkState.Committed)
                        throw new DbUpdateConcurrencyException();
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (DbUpdateConcurrencyException)
            {
                // A competing delivery that acknowledged the outbox changed its version, so nothing is recorded for it. Any
                // other conflict counts against its own budget with a delay, and the same identity is retried after it.
                await ConflictOutboxAsync(host, row, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception)
            {
                await SetOutboxAsync(host, row, EventState.Pending, "DeliveryFailed", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (outcome.Disposition == TenantWorkDisposition.Completed)
            {
                if (observer is not null) await observer.ObserveAsync(EventCheckpoint.ReceivedBeforeDispatchAcknowledgement, row.Id, cancellationToken).ConfigureAwait(false);
                if (await SetOutboxAsync(host, row, EventState.Completed, "Delivered", cancellationToken).ConfigureAwait(false)) delivered++;
            }
            else await SetOutboxAsync(host, row, outcome.Disposition == TenantWorkDisposition.Denied ? EventState.Denied : EventState.Pending,
                outcome.Code ?? "Deferred", cancellationToken).ConfigureAwait(false);
        }
        return delivered;
    }

    public async Task<int> ConsumeAsync(TenantContext host, ExecutionPrincipal principal, int limit = 100, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var now = UtcMicroseconds.Now(clock);
        await using var scan = await EfCoreUnitOfWork.BeginAsync(receiver.Module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var rows = await (await scan.Context.AcrossTenantsAsync<InboxEvent>(cancellationToken).ConfigureAwait(false)).Query.AsNoTracking()
            .Where(row => row.Consumer == consumer.Name && row.State == EventState.Pending && row.DueAt <= now)
            .OrderBy(row => row.DueAt).ThenBy(row => row.Position).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var completed = 0;
        foreach (var candidate in rows)
        {
            var committed = false;
            var conflicted = false;
            TenantWorkOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(principal, candidate.Propagated, consumer.Permission, async (unit, _, token) =>
                {
                    // Before any read: the unit's lookup is filtered to the revalidated tenant, so a reference that names
                    // another tenant than its row would otherwise find nothing and leave the row pending, unbounded.
                    if (unit.Context.TenantId != candidate.TenantId) throw new InvalidOperationException("The event tenant changed.");
                    await using var work = await EfCoreUnitOfWork.BeginAsync(receiver.Module, unit.Context, cancellationToken: token).ConfigureAwait(false);
                    var row = await work.Context.Set<InboxEvent>().SingleOrDefaultAsync(row => row.Id == candidate.Id, token).ConfigureAwait(false);
                    if (row is null || row.State != EventState.Pending || row.DueAt > UtcMicroseconds.Now(clock)) return;
                    var handler = consumer.Find(row) ?? throw new UnsupportedEventException();
                    var position = await work.Context.Set<EventPosition>().SingleOrDefaultAsync(entry => entry.Consumer == row.Consumer
                        && entry.OrderingKey == row.OrderingKey, token).ConfigureAwait(false);
                    position ??= work.Context.Add(new EventPosition { Consumer = row.Consumer, OrderingKey = row.OrderingKey }).Entity;
                    if (row.Position > position.Position)
                    {
                        if (consumer.Ordering == EventOrdering.Contiguous && row.Position != position.Position + 1)
                        {
                            // The missing predecessor gets a bounded wait with the retry policy's delays, counted apart
                            // from handler attempts. At the budget the receipt is quarantined, visibly, so an operator
                            // can replay it once the predecessor is resolved.
                            row.GapWaits++;
                            if (row.GapWaits >= retry.GapWaits)
                            {
                                row.State = EventState.Poison;
                                row.Code = GapExhaustedCode;
                            }
                            else
                            {
                                row.Code = GapCode;
                                row.DueAt = UtcMicroseconds.Now(clock) + retry.Delay(row.GapWaits);
                            }
                            conflicted = (await work.CompleteAsync(token).ConfigureAwait(false)).State != UnitOfWorkState.Committed;
                            return;
                        }
                        await handler.Handle(new(work, principal, new(row.Id, row.Kind, row.SchemaVersion, row.OrderingKey, row.Position, row.Payload)), token)
                            .ConfigureAwait(false);
                        position.Position = row.Position;
                        row.Code = "Applied";
                    }
                    else row.Code = "PositionAlreadyApplied";
                    row.State = EventState.Completed;
                    // A receipt that has run keeps no payload: it is redacted with the effects it produced.
                    row.Payload = DurablePayloads.Redacted;
                    row.Attempts++;
                    row.CompletedAt = UtcMicroseconds.Now(clock);
                    // Flush within the transaction so interruption here also exercises rollback of provider writes.
                    await work.FlushAsync(token).ConfigureAwait(false);
                    if (observer is not null) await observer.ObserveAsync(EventCheckpoint.EffectBeforeCommit, row.Id, token).ConfigureAwait(false);
                    var current = await unit.Services.GetRequiredService<IPermissionEvaluator>().AuthorizeAsync(
                        new(unit.Context, principal, consumer.Permission) { RequireCurrent = true }, token).ConfigureAwait(false);
                    if (!current.Permitted) throw new EventAuthorizationException(current.Code!, current.IsTransient);
                    committed = (await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
                    conflicted = !committed;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (EventInterruptedException) { throw; }
            catch (DbUpdateConcurrencyException)
            {
                // A handler's own or the position's lost check is not a handler failure; it has its own budget.
                await ConflictInboxAsync(host, candidate, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (EventAuthorizationException exception)
            {
                await SetInboxAsync(host, candidate, exception.Transient ? EventState.Pending : EventState.Denied, exception.Code, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception exception)
            {
                await FailAsync(host, candidate, exception is UnsupportedEventException ? "UnsupportedVersion" : "HandlerFailed", cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (conflicted)
            {
                // A commit that lost its concurrency check leaves the row unchanged; without a recorded conflict it would
                // stay due and lead the next scan.
                await ConflictInboxAsync(host, candidate, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (committed)
            {
                completed++;
                if (observer is not null) await observer.ObserveAsync(EventCheckpoint.CommittedBeforeAcknowledgement, candidate.Id, cancellationToken).ConfigureAwait(false);
            }
            if (outcome.Disposition != TenantWorkDisposition.Completed)
                await SetInboxAsync(host, candidate, outcome.Disposition == TenantWorkDisposition.Denied ? EventState.Denied : EventState.Pending,
                    outcome.Code ?? "Deferred", cancellationToken).ConfigureAwait(false);
        }
        return completed;
    }

    private Task<bool> FailAsync(TenantContext host, InboxEvent row, string code, CancellationToken token) =>
        SetInboxAsync(host, row, row.Attempts + 1 >= retry.Attempts ? EventState.Poison : EventState.Pending, code, token);

    /// <summary>
    /// Counts a conflict that no competing transaction decided: the update is guarded by the scanned version, so a row a
    /// competitor already changed records nothing. The row waits with the retry policy's delay for its conflict count,
    /// apart from handler attempts, and the conflict that reaches the budget quarantines it.
    /// </summary>
    private async Task<bool> ConflictOutboxAsync(TenantContext host, OutboxEvent row, CancellationToken token)
    {
        var conflicts = row.Conflicts + 1;
        var exhausted = conflicts >= retry.Conflicts;
        await using var work = await EfCoreUnitOfWork.BeginAsync(producer.Module, host, cancellationToken: token).ConfigureAwait(false);
        var a = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, string>(row.TenantId, item => item.Id, row.Id, row.Version, item => item.Code,
            exhausted ? ConflictExhaustedCode : ConflictCode, token).ConfigureAwait(false);
        if (!a.Succeeded) return false;
        var b = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, EventState>(row.TenantId, item => item.Id, row.Id, a.Version!.Value, item => item.State,
            exhausted ? EventState.Poison : EventState.Pending, token).ConfigureAwait(false);
        if (!b.Succeeded) return false;
        var c = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, DateTimeOffset>(row.TenantId, item => item.Id, row.Id, b.Version!.Value,
            item => item.DueAt, UtcMicroseconds.Now(clock) + retry.Delay(conflicts), token).ConfigureAwait(false);
        if (!c.Succeeded) return false;
        var d = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, int>(row.TenantId, item => item.Id, row.Id, c.Version!.Value,
            item => item.Conflicts, conflicts, token).ConfigureAwait(false);
        if (!d.Succeeded) return false;
        return (await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    /// <summary>The receiver's form of <see cref="ConflictOutboxAsync"/>, guarded by the scanned receipt version.</summary>
    private async Task<bool> ConflictInboxAsync(TenantContext host, InboxEvent row, CancellationToken token)
    {
        var conflicts = row.Conflicts + 1;
        var exhausted = conflicts >= retry.Conflicts;
        await using var work = await EfCoreUnitOfWork.BeginAsync(receiver.Module, host, cancellationToken: token).ConfigureAwait(false);
        var a = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, string>(row.TenantId, item => item.Id, row.Id, row.Version, item => item.Code,
            exhausted ? ConflictExhaustedCode : ConflictCode, token).ConfigureAwait(false);
        if (!a.Succeeded) return false;
        var b = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, EventState>(row.TenantId, item => item.Id, row.Id, a.Version!.Value, item => item.State,
            exhausted ? EventState.Poison : EventState.Pending, token).ConfigureAwait(false);
        if (!b.Succeeded) return false;
        var c = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, DateTimeOffset>(row.TenantId, item => item.Id, row.Id, b.Version!.Value,
            item => item.DueAt, UtcMicroseconds.Now(clock) + retry.Delay(conflicts), token).ConfigureAwait(false);
        if (!c.Succeeded) return false;
        var d = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, int>(row.TenantId, item => item.Id, row.Id, c.Version!.Value,
            item => item.Conflicts, conflicts, token).ConfigureAwait(false);
        if (!d.Succeeded) return false;
        return (await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    private async Task<bool> SetOutboxAsync(TenantContext host, OutboxEvent row, EventState state, string code, CancellationToken token)
    {
        if (state == EventState.Pending && row.Attempts + 1 >= retry.Attempts) state = EventState.Poison;
        await using var work = await EfCoreUnitOfWork.BeginAsync(producer.Module, host, cancellationToken: token).ConfigureAwait(false);
        var version = row.Version;
        var a = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, string>(row.TenantId, item => item.Id, row.Id, version, item => item.Code, code, token).ConfigureAwait(false);
        if (!a.Succeeded) return false;
        var b = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, EventState>(row.TenantId, item => item.Id, row.Id, a.Version!.Value, item => item.State, state, token).ConfigureAwait(false);
        if (!b.Succeeded) return false;
        var c = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, DateTimeOffset>(row.TenantId, item => item.Id, row.Id, b.Version!.Value,
            item => item.DueAt, UtcMicroseconds.Now(clock) + retry.Delay(row.Attempts + 1), token).ConfigureAwait(false);
        if (!c.Succeeded) return false;
        var d = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, int>(row.TenantId, item => item.Id, row.Id, c.Version!.Value,
            item => item.Attempts, row.Attempts + 1, token).ConfigureAwait(false);
        if (!d.Succeeded) return false;
        // A delivered or denied event never runs again from the outbox, so its payload is redacted with that disposition.
        if (state is EventState.Completed or EventState.Denied)
        {
            var e = await work.Sql.UpdateForTenantAsync<OutboxEvent, Guid, string>(row.TenantId, item => item.Id, row.Id, d.Version!.Value,
                item => item.Payload, DurablePayloads.Redacted, token).ConfigureAwait(false);
            if (!e.Succeeded) return false;
        }
        return (await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    private async Task<bool> SetInboxAsync(TenantContext host, InboxEvent row, EventState state, string code, CancellationToken token)
    {
        if (state == EventState.Pending && row.Attempts + 1 >= retry.Attempts) state = EventState.Poison;
        await using var work = await EfCoreUnitOfWork.BeginAsync(receiver.Module, host, cancellationToken: token).ConfigureAwait(false);
        var a = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, string>(row.TenantId, item => item.Id, row.Id, row.Version, item => item.Code, code, token).ConfigureAwait(false);
        if (!a.Succeeded) return false;
        var b = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, EventState>(row.TenantId, item => item.Id, row.Id, a.Version!.Value, item => item.State, state, token).ConfigureAwait(false);
        if (!b.Succeeded) return false;
        var c = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, DateTimeOffset>(row.TenantId, item => item.Id, row.Id, b.Version!.Value,
            item => item.DueAt, UtcMicroseconds.Now(clock) + retry.Delay(row.Attempts + 1), token).ConfigureAwait(false);
        if (!c.Succeeded) return false;
        var d = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, int>(row.TenantId, item => item.Id, row.Id, c.Version!.Value,
            item => item.Attempts, row.Attempts + 1, token).ConfigureAwait(false);
        if (!d.Succeeded) return false;
        // A denied receipt is never replayed, so its payload is redacted with the denial.
        if (state == EventState.Denied)
        {
            var e = await work.Sql.UpdateForTenantAsync<InboxEvent, Guid, string>(row.TenantId, item => item.Id, row.Id, d.Version!.Value,
                item => item.Payload, DurablePayloads.Redacted, token).ConfigureAwait(false);
            if (!e.Succeeded) return false;
        }
        return (await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    private sealed class UnsupportedEventException : Exception { }
    private sealed class EventAuthorizationException(string code, bool transient) : Exception("Current authority refused the event.")
    {
        public string Code { get; } = code;
        public bool Transient { get; } = transient;
    }
}
