namespace Modularyn.Framework.Tenancy.Abstractions;

/// <summary>The common time boundary for durable work and its propagated context.</summary>
public static class UtcMicroseconds
{
    public static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    public static DateTimeOffset Now(TimeProvider clock) => Normalize(clock.GetUtcNow());

    public static TimeSpan RequirePositive(TimeSpan duration) => duration > TimeSpan.Zero
        && duration.Ticks % TimeSpan.TicksPerMicrosecond == 0 ? duration
        : throw new ArgumentOutOfRangeException(nameof(duration), "Use a positive duration in whole microseconds.");
}
