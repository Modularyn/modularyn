using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Jobs;

/// <summary>A daily civil-time schedule: skip gaps, choose the first occurrence in folds, and never consult machine local time.</summary>
public sealed class DailySchedule
{
    private readonly TimeZoneInfo zone;
    public DailySchedule(string timeZone, TimeOnly localTime, MisfirePolicy misfire = MisfirePolicy.CatchUpOnce)
    {
        // The system time-zone database must know the identifier as an IANA zone, exactly as written. This needs no ICU
        // data, so it holds in globalization's invariant mode wherever the operating system supplies that database.
        if (string.IsNullOrWhiteSpace(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var found)
            || !found.HasIanaId || !string.Equals(found.Id, timeZone, StringComparison.Ordinal))
            throw new ArgumentException("Declare an IANA time-zone identifier.", nameof(timeZone));
        if (localTime.Ticks % TimeSpan.TicksPerMicrosecond != 0) throw new ArgumentOutOfRangeException(nameof(localTime));
        if (!Enum.IsDefined(misfire)) throw new ArgumentOutOfRangeException(nameof(misfire));
        zone = found;
        TimeZone = timeZone;
        LocalTime = localTime;
        Misfire = misfire;
    }
    public string TimeZone { get; }
    public TimeOnly LocalTime { get; }
    public MisfirePolicy Misfire { get; }

    public DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcMicroseconds.Normalize(instant), zone).DateTime);

    public DateTimeOffset? Resolve(DateOnly date)
    {
        var local = date.ToDateTime(LocalTime, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) return null;
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return UtcMicroseconds.Normalize(new DateTimeOffset(local, offset));
    }
}
