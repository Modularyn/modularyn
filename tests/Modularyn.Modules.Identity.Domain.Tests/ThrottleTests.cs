using System.Text;
using Xunit;

namespace Modularyn.Modules.Identity.Domain.Tests;

public sealed class ThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static ThrottlePolicy Policy(int sourceThreshold = 5, int freeAttempts = 3) => new(sourceThreshold, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10),
        freeAttempts, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(16), TimeSpan.FromHours(1));

    [Fact(DisplayName = "TM-G02-015 a source is blocked at its threshold and released when its block ends")]
    public void TM_G02_015_A_source_is_blocked_at_its_threshold_and_released_when_its_block_ends()
    {
        var policy = Policy(sourceThreshold: 5);
        var counter = ThrottleCounter.Empty;
        var now = Start;
        for (var failure = 1; failure < 5; failure++)
        {
            Assert.True(VerificationThrottle.EvaluateSource(policy, counter, now).Allowed);
            counter = VerificationThrottle.SourceFailed(policy, counter, now);
            now = now.AddSeconds(10);
        }
        // After N minus one failures the next attempt is still verified; the Nth failure blocks the source.
        Assert.True(VerificationThrottle.EvaluateSource(policy, counter, now).Allowed);
        counter = VerificationThrottle.SourceFailed(policy, counter, now);
        var blockedUntil = now + policy.SourceBlock;
        Assert.Equal(blockedUntil, counter.BlockedUntil);
        Assert.Equal(ThrottleDecision.Refuse(blockedUntil), VerificationThrottle.EvaluateSource(policy, counter, now));
        Assert.False(VerificationThrottle.EvaluateSource(policy, counter, blockedUntil.AddSeconds(-1)).Allowed);
        Assert.True(VerificationThrottle.EvaluateSource(policy, counter, blockedUntil).Allowed);

        // Counting starts again after the block.
        counter = VerificationThrottle.SourceFailed(policy, counter, blockedUntil);
        Assert.Equal(1, counter.Failures);
        Assert.Null(counter.BlockedUntil);
    }

    [Fact(DisplayName = "TM-G02-015 source failures outside the window start a new count")]
    public void TM_G02_015_Source_failures_outside_the_window_start_a_new_count()
    {
        var policy = Policy(sourceThreshold: 3);
        var counter = VerificationThrottle.SourceFailed(policy, ThrottleCounter.Empty, Start);
        counter = VerificationThrottle.SourceFailed(policy, counter, Start.AddMinutes(14));
        Assert.Equal(2, counter.Failures);
        counter = VerificationThrottle.SourceFailed(policy, counter, Start.AddMinutes(15));
        Assert.Equal(1, counter.Failures);
        Assert.Null(counter.BlockedUntil);
        Assert.Equal(Start.AddMinutes(15), counter.WindowStartedAt);
    }

    [Fact(DisplayName = "TM-G02-015 subject delays double from the first delay and stop at the cap, so no number of failures locks the owner out")]
    public void TM_G02_015_Subject_delays_double_and_stop_at_the_cap()
    {
        var policy = Policy(freeAttempts: 3);
        Assert.Equal(TimeSpan.Zero, VerificationThrottle.SubjectDelay(policy, 2));
        Assert.Equal(TimeSpan.FromSeconds(2), VerificationThrottle.SubjectDelay(policy, 3));
        Assert.Equal(TimeSpan.FromSeconds(4), VerificationThrottle.SubjectDelay(policy, 4));
        Assert.Equal(TimeSpan.FromSeconds(8), VerificationThrottle.SubjectDelay(policy, 5));
        Assert.Equal(TimeSpan.FromSeconds(16), VerificationThrottle.SubjectDelay(policy, 6));
        Assert.Equal(TimeSpan.FromSeconds(16), VerificationThrottle.SubjectDelay(policy, 7));
        Assert.Equal(TimeSpan.FromSeconds(16), VerificationThrottle.SubjectDelay(policy, 40));
        Assert.Equal(TimeSpan.FromSeconds(16), VerificationThrottle.SubjectDelay(policy, int.MaxValue));

        var counter = ThrottleCounter.Empty;
        var now = Start;
        for (var failure = 0; failure < 3; failure++)
        {
            Assert.True(VerificationThrottle.EvaluateSubject(policy, counter, now).Allowed);
            counter = VerificationThrottle.SubjectFailed(policy, counter, now);
        }
        // From here each attempt waits for its delay, measured from the latest failure, and is allowed once it has passed.
        foreach (var delay in new[] { 2, 4, 8, 16, 16, 16 })
        {
            Assert.Equal(ThrottleDecision.Refuse(now.AddSeconds(delay)), VerificationThrottle.EvaluateSubject(policy, counter, now.AddSeconds(delay - 1)));
            now = now.AddSeconds(delay);
            Assert.True(VerificationThrottle.EvaluateSubject(policy, counter, now).Allowed);
            counter = VerificationThrottle.SubjectFailed(policy, counter, now);
        }
        Assert.Equal(9, counter.Failures);
    }

    [Fact(DisplayName = "TM-G02-015 subject failures are forgotten after the reset period")]
    public void TM_G02_015_Subject_failures_are_forgotten_after_the_reset_period()
    {
        var policy = Policy(freeAttempts: 1);
        var counter = VerificationThrottle.SubjectFailed(policy, ThrottleCounter.Empty, Start);
        counter = VerificationThrottle.SubjectFailed(policy, counter, Start.AddSeconds(5));
        Assert.False(VerificationThrottle.EvaluateSubject(policy, counter, Start.AddSeconds(6)).Allowed);
        Assert.True(VerificationThrottle.EvaluateSubject(policy, counter, Start.AddSeconds(5) + policy.SubjectResetAfter).Allowed);
        Assert.Equal(1, VerificationThrottle.SubjectFailed(policy, counter, Start.AddSeconds(5) + policy.SubjectResetAfter).Failures);
    }

    [Fact(DisplayName = "TM-G02-015 policies refuse values outside their declared ranges")]
    public void TM_G02_015_Policies_refuse_values_outside_their_declared_ranges()
    {
        var tick = TimeSpan.FromTicks(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(20, TimeSpan.FromMinutes(15) + tick, TimeSpan.FromMinutes(15), 5,
            TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15), TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(20, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15), 5,
            TimeSpan.FromSeconds(1) + tick, TimeSpan.FromMinutes(15), TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(20, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15), 5,
            TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15), TimeSpan.FromDays(1) + tick));
        var minute = TimeSpan.FromMinutes(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(2, minute, minute, 3, TimeSpan.FromSeconds(1), minute, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(5, TimeSpan.FromSeconds(30), minute, 3, TimeSpan.FromSeconds(1), minute, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(5, minute, TimeSpan.FromDays(2), 3, TimeSpan.FromSeconds(1), minute, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(5, minute, minute, 0, TimeSpan.FromSeconds(1), minute, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(5, minute, minute, 3, TimeSpan.Zero, minute, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(5, minute, minute, 3, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(5, minute, minute, 3, TimeSpan.FromSeconds(1), TimeSpan.FromHours(2), TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottlePolicy(5, minute, minute, 3, TimeSpan.FromSeconds(1), minute, TimeSpan.FromSeconds(30)));
    }

    [Fact(DisplayName = "TM-G02-015 throttle keys hash their values, share one counter for equal names and separate credential kinds")]
    public void TM_G02_015_Throttle_keys_hash_their_values()
    {
        Assert.Equal(ThrottleKey.ForUserName(ThrottleKind.Password, "Alice"), ThrottleKey.ForUserName(ThrottleKind.Password, "ALICE"));
        Assert.NotEqual(ThrottleKey.ForUserName(ThrottleKind.Password, "alice"), ThrottleKey.ForUserName(ThrottleKind.Password, "alicia"));
        var account = Guid.NewGuid();
        Assert.NotEqual(ThrottleKey.ForAccount(ThrottleKind.OneTimeCode, account), ThrottleKey.ForAccount(ThrottleKind.RecoveryCode, account));
        Assert.NotEqual(ThrottleKey.ForSource("203.0.113.7"), ThrottleKey.ForUserName(ThrottleKind.Password, "203.0.113.7"));
        var typed = "typed-" + Guid.NewGuid().ToString("N");
        var key = ThrottleKey.ForUserName(ThrottleKind.Password, typed);
        Assert.Equal(32, key.Hash.Length);
        Assert.Equal(-1, key.Hash.IndexOf(Encoding.UTF8.GetBytes(typed.ToUpperInvariant())));
        Assert.DoesNotContain(typed, key.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrottleKey.ForUserName(ThrottleKind.Source, "alice"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrottleKey.ForAccount(ThrottleKind.Password, account));
    }
}
