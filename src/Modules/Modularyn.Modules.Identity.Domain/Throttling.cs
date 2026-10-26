using System.Security.Cryptography;
using System.Text;

namespace Modularyn.Modules.Identity.Domain;

/// <summary>What a throttle counter limits. Each credential kind has its own per-subject counter; all share the per-source counter.</summary>
public enum ThrottleKind
{
    Source = 1,
    Password = 2,
    OneTimeCode = 3,
    RecoveryCode = 4
}

/// <summary>
/// The key of one throttle counter: its kind and a SHA-256 of the normalized value it counts. The store never holds a
/// typed user name or address, which may contain a password typed into the wrong field.
/// </summary>
public sealed class ThrottleKey : IEquatable<ThrottleKey>
{
    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("Modularyn.Identity.Throttle.v1");
    private readonly byte[] hash;

    private ThrottleKey(ThrottleKind kind, string value)
    {
        Kind = kind;
        var text = Encoding.UTF8.GetBytes(value);
        var input = new byte[Domain.Length + 2 + text.Length];
        Domain.CopyTo(input, 0);
        input[Domain.Length] = (byte)kind;
        text.CopyTo(input, Domain.Length + 2);
        hash = SHA256.HashData(input);
    }

    public ThrottleKind Kind { get; }
    public ReadOnlySpan<byte> Hash => hash;

    /// <summary>The per-source counter for a client source, already reduced to its blocking unit by the adapter that observed it.</summary>
    public static ThrottleKey ForSource(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        return new(ThrottleKind.Source, source);
    }

    /// <summary>A per-account counter keyed by normalized user name, so unknown and existing names are throttled alike.</summary>
    public static ThrottleKey ForUserName(ThrottleKind kind, string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        if (kind == ThrottleKind.Source || !Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return new(kind, UserNames.Normalize(userName));
    }

    /// <summary>A per-account counter for a credential verified after the account is already known, such as a one-time code.</summary>
    public static ThrottleKey ForAccount(ThrottleKind kind, Guid accountId)
    {
        if (kind is ThrottleKind.Source or ThrottleKind.Password || !Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return new(kind, accountId.ToString("D"));
    }

    public bool Equals(ThrottleKey? other) => other is not null && Kind == other.Kind && hash.AsSpan().SequenceEqual(other.hash);
    public override bool Equals(object? obj) => obj is ThrottleKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, BitConverter.ToInt32(hash, 0));
    public override string ToString() => Kind + " counter";
}

/// <summary>One counter's state. Refused attempts are never recorded, so a refusal cannot extend a block or a delay.</summary>
public sealed record ThrottleCounter(int Failures, DateTimeOffset? WindowStartedAt, DateTimeOffset? LastFailureAt, DateTimeOffset? BlockedUntil)
{
    public static ThrottleCounter Empty { get; } = new(0, null, null, null);
}

public sealed record ThrottleDecision(bool Allowed, DateTimeOffset? RetryAt)
{
    public static ThrottleDecision Allow { get; } = new(true, null);
    public static ThrottleDecision Refuse(DateTimeOffset retryAt) => new(false, retryAt);
}

/// <summary>
/// The declared verification limits (TM-G02-015). A source is blocked for <see cref="SourceBlock"/> once it reaches
/// <see cref="SourceThreshold"/> failures within <see cref="SourceWindow"/>. A subject, such as an account's password,
/// allows <see cref="SubjectFreeAttempts"/> failures, then requires a delay after each further failure that starts at
/// <see cref="SubjectBaseDelay"/>, doubles and stops at <see cref="SubjectMaximumDelay"/>, so failed attempts by others
/// slow the owner but never lock them out. A subject's failures are forgotten after a success or after
/// <see cref="SubjectResetAfter"/> without a failure.
/// </summary>
public sealed record ThrottlePolicy
{
    public ThrottlePolicy(int sourceThreshold, TimeSpan sourceWindow, TimeSpan sourceBlock, int subjectFreeAttempts,
        TimeSpan subjectBaseDelay, TimeSpan subjectMaximumDelay, TimeSpan subjectResetAfter)
    {
        if (sourceThreshold is < 3 or > 1000) throw new ArgumentOutOfRangeException(nameof(sourceThreshold), "A source threshold is from 3 to 1000 failures.");
        if (sourceWindow < TimeSpan.FromMinutes(1) || sourceWindow > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(sourceWindow), "A source window is from one minute to one day.");
        if (sourceBlock < TimeSpan.FromMinutes(1) || sourceBlock > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(sourceBlock), "A source block is from one minute to one day.");
        if (subjectFreeAttempts is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(subjectFreeAttempts), "From 1 to 50 failures are allowed before delays begin.");
        if (subjectBaseDelay < TimeSpan.FromSeconds(1) || subjectBaseDelay > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(subjectBaseDelay), "The first delay is from one second to five minutes.");
        if (subjectMaximumDelay < subjectBaseDelay || subjectMaximumDelay > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(subjectMaximumDelay), "The delay cap is from the first delay to one hour.");
        if (subjectResetAfter < subjectMaximumDelay || subjectResetAfter > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException(nameof(subjectResetAfter), "Failures are forgotten after from the delay cap to thirty days.");
        SourceThreshold = sourceThreshold;
        SourceWindow = sourceWindow;
        SourceBlock = sourceBlock;
        SubjectFreeAttempts = subjectFreeAttempts;
        SubjectBaseDelay = subjectBaseDelay;
        SubjectMaximumDelay = subjectMaximumDelay;
        SubjectResetAfter = subjectResetAfter;
    }

    public int SourceThreshold { get; }
    public TimeSpan SourceWindow { get; }
    public TimeSpan SourceBlock { get; }
    public int SubjectFreeAttempts { get; }
    public TimeSpan SubjectBaseDelay { get; }
    public TimeSpan SubjectMaximumDelay { get; }
    public TimeSpan SubjectResetAfter { get; }
}

public static class VerificationThrottle
{
    public static ThrottleDecision EvaluateSource(ThrottlePolicy policy, ThrottleCounter counter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(counter);
        return counter.BlockedUntil is { } until && now < until ? ThrottleDecision.Refuse(until) : ThrottleDecision.Allow;
    }

    public static ThrottleCounter SourceFailed(ThrottlePolicy policy, ThrottleCounter counter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(counter);
        var counting = counter.BlockedUntil is null && counter.WindowStartedAt is { } started && now < started + policy.SourceWindow;
        var failures = counting ? counter.Failures + 1 : 1;
        if (failures >= policy.SourceThreshold) return new(0, null, now, now + policy.SourceBlock);
        return new(failures, counting ? counter.WindowStartedAt : now, now, null);
    }

    public static ThrottleDecision EvaluateSubject(ThrottlePolicy policy, ThrottleCounter counter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(counter);
        var failures = EffectiveFailures(policy, counter, now);
        if (failures < policy.SubjectFreeAttempts) return ThrottleDecision.Allow;
        var retryAt = counter.LastFailureAt!.Value + SubjectDelay(policy, failures);
        return now >= retryAt ? ThrottleDecision.Allow : ThrottleDecision.Refuse(retryAt);
    }

    public static ThrottleCounter SubjectFailed(ThrottlePolicy policy, ThrottleCounter counter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(counter);
        return new(EffectiveFailures(policy, counter, now) + 1, null, now, null);
    }

    /// <summary>The delay required after the given number of failures: none within the free attempts, then doubling to the cap.</summary>
    public static TimeSpan SubjectDelay(ThrottlePolicy policy, int failures)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (failures < policy.SubjectFreeAttempts) return TimeSpan.Zero;
        var exponent = failures - policy.SubjectFreeAttempts;
        if (exponent >= 30) return policy.SubjectMaximumDelay;
        var ticks = policy.SubjectBaseDelay.Ticks * (1L << exponent);
        return ticks <= 0 || ticks >= policy.SubjectMaximumDelay.Ticks ? policy.SubjectMaximumDelay : TimeSpan.FromTicks(ticks);
    }

    private static int EffectiveFailures(ThrottlePolicy policy, ThrottleCounter counter, DateTimeOffset now) =>
        counter.LastFailureAt is { } last && now - last < policy.SubjectResetAfter ? counter.Failures : 0;
}

/// <summary>
/// Durable throttle counters. Acquiring an entry locks its counters, in a fixed order, until the entry is committed or
/// disposed, so concurrent attempts against the same counter are decided one at a time and none can exceed a limit.
/// </summary>
public interface IVerificationLedger
{
    Task<IVerificationLedgerEntry> AcquireAsync(IReadOnlyCollection<ThrottleKey> keys, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IVerificationLedgerEntry : IAsyncDisposable
{
    ThrottleCounter Get(ThrottleKey key);

    void Set(ThrottleKey key, ThrottleCounter counter);

    /// <summary>Stores every changed counter and releases the locks. Disposing without committing discards the changes.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}
