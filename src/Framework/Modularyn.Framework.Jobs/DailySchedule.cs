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
        if (!IsZoneName(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var found)
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

    /// <summary>
    /// The form of an IANA zone or link name: components of ASCII letters, digits, '_', '-' and '+', each beginning with a
    /// letter, joined by single '/'. The lookup resolves a name as a path in the operating system's database, so it also
    /// finds what is no zone name: an empty, dot or doubled component, the posixrules file, the localtime link to the
    /// machine's own zone, and the posix and right copies of the database. Those are refused here.
    /// </summary>
    private static bool IsZoneName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var components = value.Split('/');
        if (components[0] is "posixrules" or "localtime" or "posix" or "right") return false;
        return components.All(component => component.Length > 0 && char.IsAsciiLetter(component[0])
            && component.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '+'));
    }
}
