using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Jobs;

public enum JobState { Queued, Running, Retry, Completed, Failed, CancelledBeforeRun, CancelledDuringRun, Denied }
public enum JobAttemptState { Running, Completed, Failed, Cancelled, LeaseLost, Denied }
public enum MisfirePolicy { Skip, CatchUpOnce, CatchUpAll }
public enum OccurrenceState { Queued, CatchUp, Missed, NonexistentLocalTime }

public sealed class JobRecord : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; internal set; }
    public string Kind { get; internal set; } = "";
    public string Payload { get; internal set; } = "";
    public string Propagated { get; internal set; } = "";
    public DateTimeOffset CreatedAt { get; internal set; }
    public DateTimeOffset DueAt { get; internal set; }
    public DateTimeOffset AvailableAt { get; internal set; }
    public JobState State { get; internal set; }
    public int Attempts { get; internal set; }
    public int Deferrals { get; internal set; }
    public Guid? Lease { get; internal set; }
    public DateTimeOffset? LeaseUntil { get; internal set; }
    public bool CancellationRequested { get; internal set; }
    public string Code { get; internal set; } = "";
    public Guid? ScheduleId { get; internal set; }
    public Guid Version { get; set; }
}

public sealed class JobAttempt : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid JobId { get; internal set; }
    public Guid Lease { get; internal set; }
    public int Number { get; internal set; }
    public DateTimeOffset StartedAt { get; internal set; }
    public DateTimeOffset? EndedAt { get; internal set; }
    public JobAttemptState State { get; internal set; }
    public string Code { get; internal set; } = "";
    public Guid Version { get; set; }
}

public sealed class JobSchedule : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; internal set; }
    public string Kind { get; internal set; } = "";
    public string Payload { get; internal set; } = "";
    public string Propagated { get; internal set; } = "";
    public string TimeZone { get; internal set; } = "";
    public TimeOnly LocalTime { get; internal set; }
    public DateOnly NextDate { get; internal set; }
    public DateTimeOffset NextCheckAt { get; internal set; }
    public int Deferrals { get; internal set; }
    public MisfirePolicy Misfire { get; internal set; }
    public bool Enabled { get; internal set; } = true;
    public string Code { get; internal set; } = "";
    public Guid Version { get; set; }
}

public sealed class ScheduleOccurrence : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid ScheduleId { get; internal set; }
    public DateOnly LocalDate { get; internal set; }
    public DateTimeOffset? DueAt { get; internal set; }
    public OccurrenceState State { get; internal set; }
    public Guid? JobId { get; internal set; }
}

public sealed record JobRequest(string Kind, string Payload, DateTimeOffset DueAt);
public sealed record JobClaim(Guid Id, Guid Lease, int Attempt, DateTimeOffset LeaseUntil);

public sealed class JobPolicy
{
    public JobPolicy(int maxAttempts = 3, TimeSpan? lease = null, TimeSpan? retryDelay = null)
    {
        if (maxAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        MaxAttempts = maxAttempts;
        Lease = UtcMicroseconds.RequirePositive(lease ?? TimeSpan.FromMinutes(1));
        RetryDelay = UtcMicroseconds.RequirePositive(retryDelay ?? TimeSpan.FromSeconds(1));
        if (Lease > TimeSpan.FromDays(1) || RetryDelay > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(lease));
    }
    public int MaxAttempts { get; }
    public TimeSpan Lease { get; }
    public TimeSpan RetryDelay { get; }
    public TimeSpan Backoff(int attempt) => TimeSpan.FromTicks(RetryDelay.Ticks * (1L << Math.Clamp(attempt - 1, 0, 19)));
}
