using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Jobs;

/// <summary>A daily civil-time schedule: skip gaps, choose the first occurrence in folds, and never consult machine local time.</summary>
public sealed class DailySchedule
{
    private readonly TimeZoneInfo zone;
    public DailySchedule(string timeZone, TimeOnly localTime, MisfirePolicy misfire = MisfirePolicy.CatchUpOnce)
    {
        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _))
            throw new ArgumentException("Declare an IANA time-zone identifier.", nameof(timeZone));
        if (localTime.Ticks % TimeSpan.TicksPerMicrosecond != 0) throw new ArgumentOutOfRangeException(nameof(localTime));
        if (!Enum.IsDefined(misfire)) throw new ArgumentOutOfRangeException(nameof(misfire));
        zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
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
