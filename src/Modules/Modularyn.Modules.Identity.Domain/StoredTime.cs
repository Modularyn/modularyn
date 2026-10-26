namespace Modularyn.Modules.Identity.Domain;

/// <summary>
/// The precision of every time the identity module stores and later compares: whole microseconds, which PostgreSQL's
/// <c>timestamptz</c> keeps exactly. .NET times tick in 100 ns, and Linux clocks report that resolution, so a time held
/// in memory at a finer precision would come back from the database up to 0.9 microseconds earlier, and a limit decided
/// before the round trip could be decided differently after it.
/// </summary>
/// <remarks>
/// The module's clock floors every time it reads, policy durations are whole microseconds, and the one time a caller
/// supplies, a delegation's window, is narrowed to whole microseconds when the grant is issued. Every time derived from
/// these is therefore whole microseconds too, and the persistence adapter refuses any other. A comparison of a raw time
/// with a whole-microsecond limit decides exactly as the floored time would.
/// </remarks>
public static class StoredTime
{
    /// <summary>The time at or before <paramref name="value"/> that is whole microseconds, in UTC.</summary>
    public static DateTimeOffset Floor(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    /// <summary>The time at or after <paramref name="value"/> that is whole microseconds, in UTC.</summary>
    public static DateTimeOffset Ceiling(DateTimeOffset value)
    {
        var floor = Floor(value);
        return floor.UtcTicks == value.UtcTicks ? floor : floor.AddTicks(TimeSpan.TicksPerMicrosecond);
    }

    public static bool IsNormalized(DateTimeOffset value) => value.UtcTicks % TimeSpan.TicksPerMicrosecond == 0;

    public static bool IsWhole(TimeSpan duration) => duration.Ticks % TimeSpan.TicksPerMicrosecond == 0;

    /// <summary>Returns <paramref name="value"/>, or refuses a time that is not whole microseconds.</summary>
    public static DateTimeOffset Require(DateTimeOffset value) => IsNormalized(value) ? value
        : throw new InvalidOperationException("An identity time must be normalized to whole microseconds before it is stored or compared.");
}
