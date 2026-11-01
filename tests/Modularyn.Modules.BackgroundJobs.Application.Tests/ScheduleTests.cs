using Modularyn.Framework.Jobs;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Modules.BackgroundJobs.Application.Tests;

public sealed class ScheduleTests
{
    [Fact]
    public void Civil_time_skips_the_gap_and_chooses_the_first_fold_occurrence()
    {
        Assert.Null(new DailySchedule("America/Chicago", new(2, 30)).Resolve(new(2026, 3, 8)));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), new DailySchedule("America/Chicago", new(1, 30)).Resolve(new(2026, 11, 1)));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 7, 30, 0, TimeSpan.Zero), new DailySchedule("America/Chicago", new(2, 30)).Resolve(new(2026, 3, 9)));
    }

    [Fact]
    public void Time_is_normalized_before_storage_and_policy_durations_preserve_microseconds()
    {
        var at = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);
        for (var tick = 0; tick < 10; tick++) Assert.Equal(at, UtcMicroseconds.Normalize(at.AddTicks(tick)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobPolicy(lease: TimeSpan.FromTicks(11)));
        Assert.Throws<ArgumentException>(() => new DailySchedule("unrecognized-zone", new(1, 30)));
    }

    [Fact]
    public void Transitions_need_not_be_a_whole_hour()
    {
        Assert.Null(new DailySchedule("Australia/Lord_Howe", new(2, 15)).Resolve(new(2026, 10, 4)));
        Assert.Equal(new DateTimeOffset(2026, 4, 4, 14, 45, 0, TimeSpan.Zero),
            new DailySchedule("Australia/Lord_Howe", new(1, 45)).Resolve(new(2026, 4, 5)));
    }

    [Fact]
    public void Only_an_IANA_identifier_declares_a_schedule_zone()
    {
        foreach (var refused in new[] { "Eastern Standard Time", "unrecognized-zone", "", "   ", "../etc/passwd" })
            Assert.Throws<ArgumentException>(() => new DailySchedule(refused, new(1, 30)));
        Assert.Throws<ArgumentException>(() => new DailySchedule(null!, new(1, 30)));
        Assert.Equal("America/New_York", new DailySchedule("America/New_York", new(1, 30)).TimeZone);
    }
}
