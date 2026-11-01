using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Events;

public sealed class EventStore(PostgreSqlModule module, TimeProvider clock)
{
    public const string ReplayPermission = "events.replay";
    public static readonly TimeSpan ReceiptRetention = TimeSpan.FromHours(24);
    public PostgreSqlModule Module { get; } = module;

    /// <summary>Stages intent in the business transaction. A suppressed or completed unit is refused.</summary>
    public Guid Append(EfCoreUnitOfWork work, EventIntent intent)
    {
        RequireProducer(work);
        if (string.IsNullOrWhiteSpace(intent.Consumer) || intent.Consumer.Length > 128
            || string.IsNullOrWhiteSpace(intent.Kind) || intent.Kind.Length > 128
            || string.IsNullOrWhiteSpace(intent.OrderingKey) || intent.OrderingKey.Length > 200
            || intent.Position < 1 || intent.SchemaVersion < 1 || intent.Payload is null || intent.Payload.Length > 1_048_576)
            throw new ArgumentException("An event needs a bounded consumer, kind, ordering key, positive version and position, and payload.", nameof(intent));
        var now = UtcMicroseconds.Now(clock);
        var row = new OutboxEvent
        {
            Id = Guid.NewGuid(), Consumer = intent.Consumer, Kind = intent.Kind, SchemaVersion = intent.SchemaVersion,
            OrderingKey = intent.OrderingKey, Position = intent.Position, Payload = intent.Payload,
            Propagated = PropagatedTenantContext.Capture(work.Context.Tenant!, clock).ToJson(), CreatedAt = now, DueAt = now
        };
        work.Context.Add(row);
        return row.Id;
    }

    internal async Task AcceptAsync(EfCoreUnitOfWork work, OutboxEvent source, CancellationToken token)
    {
        RequireProducer(work);
        if (work.Context.Tenant!.TenantId != source.TenantId) throw new InvalidOperationException("The received event is outside this unit.");
        var existing = await work.Context.Set<InboxEvent>().SingleOrDefaultAsync(row => row.Id == source.Id, token).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Consumer != source.Consumer || existing.Kind != source.Kind || existing.SchemaVersion != source.SchemaVersion
                || existing.OrderingKey != source.OrderingKey || existing.Position != source.Position || existing.Payload != source.Payload)
                throw new InvalidOperationException("An event identity cannot name different content.");
            return;
        }
        var now = UtcMicroseconds.Now(clock);
        work.Context.Add(new InboxEvent
        {
            Id = source.Id, Consumer = source.Consumer, Kind = source.Kind, SchemaVersion = source.SchemaVersion,
            OrderingKey = source.OrderingKey, Position = source.Position, Payload = source.Payload,
            // Capture anew from the revalidated receiving unit; never copy a propagated reference from the outbox.
            Propagated = PropagatedTenantContext.Capture(work.Context.Tenant!, clock).ToJson(), ReceivedAt = now, DueAt = now
        });
    }

    public async Task<int> PruneReceiptsAsync(EfCoreUnitOfWork work, int limit = 100, CancellationToken cancellationToken = default)
    {
        RequireProducer(work);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var cutoff = UtcMicroseconds.Now(clock) - ReceiptRetention;
        var rows = await work.Context.Set<InboxEvent>().Where(row => row.State == EventState.Completed && row.CompletedAt <= cutoff)
            .OrderBy(row => row.CompletedAt).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        work.Context.RemoveRange(rows);
        return rows.Count;
    }

    public async Task<bool> ReplayAsync(TenantContext context, ExecutionPrincipal principal, IPermissionEvaluator evaluator,
        Guid id, CancellationToken cancellationToken = default)
    {
        var decision = await evaluator.AuthorizeAsync(new(context, principal, ReplayPermission) { RequireCurrent = true }, cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted) return false;
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await work.Context.Set<InboxEvent>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || row.State != EventState.Poison) return false;
        row.State = EventState.Pending;
        row.Attempts = 0;
        row.GapWaits = 0;
        row.Conflicts = 0;
        row.Code = "ReplayRequested";
        row.DueAt = UtcMicroseconds.Now(clock);
        // The original producer remains the authority for execution. Replay never calls the handler directly.
        return (await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    /// <summary>Retries quarantined dispatch through ordinary delivery and the receiver's inbox.</summary>
    public async Task<bool> ReplayDispatchAsync(TenantContext context, ExecutionPrincipal principal, IPermissionEvaluator evaluator,
        Guid id, CancellationToken cancellationToken = default)
    {
        var decision = await evaluator.AuthorizeAsync(new(context, principal, ReplayPermission) { RequireCurrent = true }, cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted) return false;
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await work.Context.Set<OutboxEvent>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || row.State != EventState.Poison) return false;
        row.State = EventState.Pending;
        row.Attempts = 0;
        row.Conflicts = 0;
        row.Code = "ReplayRequested";
        row.DueAt = UtcMicroseconds.Now(clock);
        return (await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    private void RequireProducer(EfCoreUnitOfWork work)
    {
        if (!ReferenceEquals(work.Context.Module, Module) || work.TransactionId is null || work.State != UnitOfWorkState.Active
            || work.Context.Tenant is not { IsActive: true, Scope: TenantScope.Tenant })
            throw new InvalidOperationException("Durable events require the producing module's active tenant transaction.");
    }
}
