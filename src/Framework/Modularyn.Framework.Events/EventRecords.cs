using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Events;

public enum EventState { Pending, Completed, Poison, Denied }
public enum EventOrdering { MonotonicSnapshot, Contiguous }
public enum EventCheckpoint { ReceivedBeforeDispatchAcknowledgement, EffectBeforeCommit, CommittedBeforeAcknowledgement }

/// <summary>Payload data only. The store supplies identity, time, tenant and propagated context.</summary>
public sealed record EventIntent(string Consumer, string Kind, int SchemaVersion, string OrderingKey, long Position, string Payload);
public sealed record EventEnvelope(Guid Id, string Kind, int SchemaVersion, string OrderingKey, long Position, string Payload);
public sealed record EventStatus(Guid Id, EventState State, int Attempts, string Code, DateTimeOffset DueAt);

public sealed class OutboxEvent : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; internal set; }
    public string Consumer { get; internal set; } = "";
    public string Kind { get; internal set; } = "";
    public int SchemaVersion { get; internal set; }
    public string OrderingKey { get; internal set; } = "";
    public long Position { get; internal set; }
    public string Payload { get; internal set; } = "";
    public string Propagated { get; internal set; } = "";
    public DateTimeOffset CreatedAt { get; internal set; }
    public DateTimeOffset DueAt { get; internal set; }
    public EventState State { get; internal set; }
    public int Attempts { get; internal set; }
    public string Code { get; internal set; } = "";
    public Guid Version { get; set; }
}

public sealed class InboxEvent : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; internal set; }
    public string Consumer { get; internal set; } = "";
    public string Kind { get; internal set; } = "";
    public int SchemaVersion { get; internal set; }
    public string OrderingKey { get; internal set; } = "";
    public long Position { get; internal set; }
    public string Payload { get; internal set; } = "";
    public string Propagated { get; internal set; } = "";
    public DateTimeOffset ReceivedAt { get; internal set; }
    public DateTimeOffset DueAt { get; internal set; }
    public DateTimeOffset? CompletedAt { get; internal set; }
    public EventState State { get; internal set; }
    public int Attempts { get; internal set; }
    public string Code { get; internal set; } = "";
    public Guid Version { get; set; }
}

/// <summary>Never expires with receipts. Monotonic positions fence late delivery after receipt retention.</summary>
public sealed class EventPosition : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public string Consumer { get; internal set; } = "";
    public string OrderingKey { get; internal set; } = "";
    public long Position { get; internal set; }
    public Guid Version { get; set; }
}

public sealed class EventRetryPolicy
{
    public EventRetryPolicy(int attempts = 3, TimeSpan? initialDelay = null)
    {
        if (attempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(attempts));
        Attempts = attempts;
        InitialDelay = UtcMicroseconds.RequirePositive(initialDelay ?? TimeSpan.FromSeconds(1));
        if (InitialDelay > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(initialDelay));
    }
    public int Attempts { get; }
    public TimeSpan InitialDelay { get; }
    public TimeSpan Delay(int attempt) => TimeSpan.FromTicks(InitialDelay.Ticks * (1L << Math.Clamp(attempt - 1, 0, 19)));
}
