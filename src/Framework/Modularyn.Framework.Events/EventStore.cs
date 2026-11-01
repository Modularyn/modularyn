using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Events;

/// <summary>
/// The durable event store of one module. Operator replays are recorded through <paramref name="audit"/> in the replay's
/// own transaction; without it no replay is made.
/// </summary>
public sealed class EventStore(PostgreSqlModule module, TimeProvider clock, IAuditTrail? audit = null)
{
    public const string ReplayPermission = "events.replay";
    public const string ReplayedAction = "events.replayed";
    public const string DispatchReplayedAction = "events.dispatch-replayed";
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
        var trail = RequireAudit();
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await work.Context.Set<InboxEvent>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || row.State != EventState.Poison) return false;
        // The record keeps what the replay resets and what the next disposition overwrites: the quarantine code, the
        // budgets spent and the original producer, so the replay is told apart from new work.
        var entry = Replay(ReplayedAction, "event.inbox", context, principal, row.Id,
            $"code {row.Code} attempts {row.Attempts} gap-waits {row.GapWaits} conflicts {row.Conflicts}", row.Propagated);
        row.State = EventState.Pending;
        row.Attempts = 0;
        row.GapWaits = 0;
        row.Conflicts = 0;
        row.Code = "ReplayRequested";
        row.DueAt = UtcMicroseconds.Now(clock);
        await trail.RecordAsync(work, entry, cancellationToken).ConfigureAwait(false);
        // The original producer remains the authority for execution. Replay never calls the handler directly.
        return (await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    /// <summary>Retries quarantined dispatch through ordinary delivery and the receiver's inbox.</summary>
    public async Task<bool> ReplayDispatchAsync(TenantContext context, ExecutionPrincipal principal, IPermissionEvaluator evaluator,
        Guid id, CancellationToken cancellationToken = default)
    {
        var decision = await evaluator.AuthorizeAsync(new(context, principal, ReplayPermission) { RequireCurrent = true }, cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted) return false;
        var trail = RequireAudit();
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await work.Context.Set<OutboxEvent>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || row.State != EventState.Poison) return false;
        var entry = Replay(DispatchReplayedAction, "event.outbox", context, principal, row.Id,
            $"code {row.Code} attempts {row.Attempts} conflicts {row.Conflicts}", row.Propagated);
        row.State = EventState.Pending;
        row.Attempts = 0;
        row.Conflicts = 0;
        row.Code = "ReplayRequested";
        row.DueAt = UtcMicroseconds.Now(clock);
        await trail.RecordAsync(work, entry, cancellationToken).ConfigureAwait(false);
        return (await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    private IAuditTrail RequireAudit() => audit
        ?? throw new InvalidOperationException("An operator replay is recorded in audit, and this store has no audit trail, so nothing is replayed.");

    /// <summary>
    /// The operator's record of one replay: who replayed which row and when, with what it reset and its original
    /// provenance, the producer and producing unit its stored reference names, so the replay is told apart from new work.
    /// </summary>
    private static AuditEntry Replay(string action, string type, TenantContext context, ExecutionPrincipal principal, Guid id, string reset,
        string propagated) => AuditEntry.InContext(action, context, principal) with
    {
        ResourceType = type, ResourceId = id.ToString("D"),
        Detail = reset + (PropagatedTenantContext.TryParse(propagated, out var producer)
            ? " producer " + (producer!.Actor.Kind == ActorKind.User ? "user:" : "service:") + producer.Actor.Subject + " unit " + producer.ProducerUnitId.ToString("D")
            : " producer unreadable")
    };

    private void RequireProducer(EfCoreUnitOfWork work)
    {
        if (!ReferenceEquals(work.Context.Module, Module) || work.TransactionId is null || work.State != UnitOfWorkState.Active
            || work.Context.Tenant is not { IsActive: true, Scope: TenantScope.Tenant })
            throw new InvalidOperationException("Durable events require the producing module's active tenant transaction.");
    }
}
