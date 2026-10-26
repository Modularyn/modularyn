using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

// Unit tests over in-memory stores; the PostgreSQL and HTTP tests repeat the limits on the real providers.
public sealed class SignInTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static Task<SignInResult> Attempt(UnitIdentity identity, string userName, string password, string source) =>
        identity.Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(userName, new SecretText(password), source), Cancellation);

    [Fact(DisplayName = "TM-G02-015 a source is blocked at its threshold, its correct attempt refused unverified, while another source signs in")]
    public async Task TM_G02_015_A_source_is_blocked_at_its_threshold_while_another_source_signs_in()
    {
        // Source threshold 6; the account allows 3 free failures, so spread the guesses over several accounts.
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits());
        var (_, password) = await identity.AccountAsync("admin-1");
        foreach (var name in new[] { "user-a", "user-b", "user-c" }) await identity.AccountAsync(name);
        var targets = new[] { "user-a", "user-a", "user-b", "user-b", "user-c" };
        foreach (var target in targets) Assert.Equal(SignInStatus.Failed, (await Attempt(identity, target, Generated.Password(), "src-1")).Status);

        // After N minus one failures the source still signs in, and that success does not reset the source's count.
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, "src-1")).Status);
        Assert.Equal(SignInStatus.Failed, (await Attempt(identity, "user-c", Generated.Password(), "src-1")).Status);
        var verified = identity.Stores.Verifications;
        var blocked = await Attempt(identity, "admin-1", password, "src-1");
        Assert.Equal(SignInStatus.Throttled, blocked.Status);
        Assert.Equal(identity.Clock.Now.AddMinutes(15), blocked.RetryAt);
        Assert.Equal(verified, identity.Stores.Verifications);
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, "src-2")).Status);

        identity.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, "src-1")).Status);
    }

    [Fact(DisplayName = "TM-G02-015 account delays follow the declared schedule to their cap and the owner signs in once a delay has passed")]
    public async Task TM_G02_015_Account_delays_follow_the_schedule_and_the_owner_signs_in_after_the_delay()
    {
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits(new() { ["Modularyn:Identity:Throttling:SourceThreshold"] = "1000" }));
        var (_, password) = await identity.AccountAsync("admin-1");
        var source = 0;
        string NextSource() => "src-" + ++source;
        for (var failure = 0; failure < 3; failure++) Assert.Equal(SignInStatus.Failed, (await Attempt(identity, "admin-1", Generated.Password(), NextSource())).Status);

        foreach (var delay in new[] { 2, 4, 8, 16, 16 })
        {
            // Inside the delay even the owner's correct password is refused without verification, from any source.
            var verified = identity.Stores.Verifications;
            identity.Clock.Advance(TimeSpan.FromSeconds(delay - 1));
            var early = await Attempt(identity, "admin-1", password, NextSource());
            Assert.Equal(SignInStatus.Throttled, early.Status);
            Assert.Equal(identity.Clock.Now.AddSeconds(1), early.RetryAt);
            Assert.Equal(verified, identity.Stores.Verifications);
            identity.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(SignInStatus.Failed, (await Attempt(identity, "admin-1", Generated.Password(), NextSource())).Status);
        }
        // The cap holds however many failures follow; once it passes, the owner signs in and the delay is cleared.
        identity.Clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, NextSource())).Status);
        Assert.Equal(SignInStatus.Failed, (await Attempt(identity, "admin-1", Generated.Password(), NextSource())).Status);
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, NextSource())).Status);
    }

    [Fact(DisplayName = "TM-G02-015 unknown accounts, disabled accounts and wrong passwords fail identically after one verification each")]
    public async Task TM_G02_015_Unknown_disabled_and_wrong_fail_identically()
    {
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits());
        var (_, password) = await identity.AccountAsync("admin-1");
        var (disabledId, disabledPassword) = await identity.AccountAsync("editor-1");
        Assert.True(await identity.Get<IAccountService>().DisableAsync(disabledId, Cancellation));
        var results = new List<SignInResult>();
        foreach (var (name, attempt) in new[] { ("admin-1", Generated.Password()), ("unknown-1", Generated.Password()), ("editor-1", disabledPassword) })
        {
            var before = identity.Stores.Verifications;
            results.Add(await Attempt(identity, name, attempt, "src-" + name));
            Assert.Equal(before + 1, identity.Stores.Verifications);
        }
        Assert.All(results, result => Assert.Same(SignInResult.Failed, result));

        // Unknown names are throttled exactly like existing ones, so a refusal reveals nothing either.
        for (var failure = 0; failure < 2; failure++)
        {
            await Attempt(identity, "admin-1", Generated.Password(), "src-x" + failure);
            await Attempt(identity, "unknown-1", Generated.Password(), "src-y" + failure);
        }
        var known = await Attempt(identity, "admin-1", password, "src-z");
        var unknown = await Attempt(identity, "unknown-1", Generated.Password(), "src-z");
        Assert.Equal((SignInStatus.Throttled, SignInStatus.Throttled), (known.Status, unknown.Status));
        Assert.Equal(known.RetryAt, unknown.RetryAt);
        Assert.Equal(SignInResult.Failed, await Attempt(identity, "admin-1", "", "src-z"));
        Assert.Equal(SignInResult.Failed, await Attempt(identity, "", password, "src-z"));
        Assert.Equal(SignInResult.Failed, await Attempt(identity, "admin-1", new string('x', PasswordPolicy.MaximumLength + 1), "src-z"));
    }

    [Fact(DisplayName = "TM-G02-015 password changes share sign-in's counters, and a success clears the account delay but not the source count")]
    public async Task TM_G02_015_Password_changes_share_the_sign_in_counters()
    {
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits());
        var (accountId, password) = await identity.AccountAsync("admin-1");
        var accounts = identity.Get<IAccountService>();
        Assert.Equal(SignInStatus.Failed, (await Attempt(identity, "admin-1", Generated.Password(), "src-1")).Status);
        Assert.Equal(PasswordChangeStatus.CurrentPasswordRejected,
            (await accounts.ChangePasswordAsync(accountId, new SecretText(Generated.Password()), new SecretText(Generated.Password()), "src-2", Cancellation)).Status);
        Assert.Equal(SignInStatus.Failed, (await Attempt(identity, "admin-1", Generated.Password(), "src-3")).Status);
        var verified = identity.Stores.Verifications;
        var refused = await accounts.ChangePasswordAsync(accountId, new SecretText(password), new SecretText(Generated.Password()), "src-4", Cancellation);
        Assert.Equal(PasswordChangeStatus.Throttled, refused.Status);
        Assert.Equal(verified, identity.Stores.Verifications);
        Assert.Equal(SignInStatus.Throttled, (await Attempt(identity, "admin-1", password, "src-4")).Status);

        identity.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, "src-1")).Status);
        var subject = identity.Stores.Counter(ThrottleKey.ForUserName(ThrottleKind.Password, "admin-1"));
        Assert.Equal(ThrottleCounter.Empty, subject);
        Assert.Equal(1, identity.Stores.Counter(ThrottleKey.ForSource("src-1")).Failures);
        Assert.Equal(PasswordChangeStatus.PasswordPolicy,
            (await accounts.ChangePasswordAsync(accountId, new SecretText(password), new SecretText("short"), "src-1", Cancellation)).Status);
    }

    [Fact(DisplayName = "TM-G02-015 concurrent attempts against one account never verify more than its free attempts")]
    public async Task TM_G02_015_Concurrent_attempts_never_verify_more_than_the_free_attempts()
    {
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits(new() { ["Modularyn:Identity:Throttling:SourceThreshold"] = "1000" }));
        await identity.AccountAsync("admin-1");
        var before = identity.Stores.Verifications;
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Attempt(identity, "admin-1", Generated.Password(), "src-" + index)));
        Assert.Equal(3, identity.Stores.Verifications - before);
        Assert.Equal(3, results.Count(result => result.Status == SignInStatus.Failed));
        Assert.Equal(21, results.Count(result => result.Status == SignInStatus.Throttled));
    }
}
