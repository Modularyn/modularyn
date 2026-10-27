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
        Assert.InRange(identity.Stores.Verifications - before, 1, 3);
        Assert.All(results, result => Assert.NotEqual(SignInStatus.Succeeded, result.Status));
        // Whatever the interleaving, the account's free attempts are verified exactly once each, and no more.
        for (var attempt = 0; attempt < 6; attempt++) await Attempt(identity, "admin-1", Generated.Password(), "src-x" + attempt);
        Assert.Equal(3, identity.Stores.Verifications - before);
    }

    [Fact(DisplayName = "TM-G02-015 an attempt on an account already in flight is refused at once, while attempts from one source wait their turn, a few at most")]
    public async Task TM_G02_015_Attempts_on_an_account_in_flight_are_refused_and_from_one_source_wait_their_turn()
    {
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits());
        var (_, password) = await identity.AccountAsync("admin-1");
        var others = new List<(string Name, string Password)>();
        for (var index = 0; index <= VerificationGate.SourceWaiters; index++) others.Add(("user-" + index, (await identity.AccountAsync("user-" + index)).Password));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        identity.Stores.BeforeVerification = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var first = Attempt(identity, "admin-1", password, "src-1");
        await entered.Task.WaitAsync(Cancellation);
        identity.Stores.BeforeVerification = null;
        var acquisitions = identity.Stores.Acquisitions;

        // Another attempt on the same account is refused at once, without a store read, whatever its source.
        var sameAccount = await Attempt(identity, "admin-1", password, "src-2");
        Assert.Equal(SignInStatus.Throttled, sameAccount.Status);
        Assert.Equal(identity.Clock.Now.AddSeconds(1), sameAccount.RetryAt);

        // Sign-ins to other accounts from the same address wait their turn instead of failing; one more than the declared
        // number of waiters is refused at once.
        var waiting = others.Take(VerificationGate.SourceWaiters).Select(other => Attempt(identity, other.Name, other.Password, "src-1")).ToArray();
        var flooded = await Attempt(identity, others[^1].Name, others[^1].Password, "src-1");
        Assert.Equal((SignInStatus.Throttled, identity.Clock.Now.AddSeconds(1)), (flooded.Status, flooded.RetryAt));
        Assert.All(waiting, attempt => Assert.False(attempt.IsCompleted));
        Assert.Equal(acquisitions, identity.Stores.Acquisitions);
        release.TrySetResult();
        Assert.Equal(SignInStatus.Succeeded, (await first).Status);
        Assert.All(await Task.WhenAll(waiting), result => Assert.Equal(SignInStatus.Succeeded, result.Status));
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, "src-2")).Status);
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, others[^1].Name, others[^1].Password, "src-1")).Status);
    }

    [Fact(DisplayName = "TM-G02-015 a source that keeps trying a delayed account counts each refusal against itself and is blocked, while the owner signs in from another")]
    public async Task TM_G02_015_A_source_that_keeps_trying_a_delayed_account_blocks_itself()
    {
        // Source threshold 6; the account allows 3 free failures, then a delay of 2 s that doubles.
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits());
        var (_, password) = await identity.AccountAsync("admin-1");
        var accountKey = ThrottleKey.ForUserName(ThrottleKind.Password, "admin-1");
        for (var failure = 0; failure < 3; failure++) Assert.Equal(SignInStatus.Failed, (await Attempt(identity, "admin-1", Generated.Password(), "bot")).Status);
        var verified = identity.Stores.Verifications;
        var account = identity.Stores.Counter(accountKey);

        // Polls inside the account's delay are refused unverified and counted against the source, never the account; the
        // third brings the source to its threshold.
        SignInResult refused = null!;
        for (var poll = 0; poll < 3; poll++)
        {
            refused = await Attempt(identity, "admin-1", Generated.Password(), "bot");
            Assert.Equal(SignInStatus.Throttled, refused.Status);
        }
        Assert.Equal(verified, identity.Stores.Verifications);
        Assert.Equal(account, identity.Stores.Counter(accountKey));
        Assert.Equal(identity.Clock.Now.AddMinutes(15), identity.Stores.Counter(ThrottleKey.ForSource("bot")).BlockedUntil);
        Assert.Equal(identity.Clock.Now.AddMinutes(15), refused.RetryAt);

        // The block outlasts the account's delay, even for the right password, while the owner signs in from elsewhere.
        identity.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SignInStatus.Throttled, (await Attempt(identity, "admin-1", password, "bot")).Status);
        Assert.Equal(verified, identity.Stores.Verifications);
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, "owner-net")).Status);
    }

    [Fact(DisplayName = "TM-G02-015 an attacker that keeps renewing an account's delay from one source cannot keep the owner out, and gains no verification")]
    public async Task TM_G02_015_An_attacker_renewing_the_delay_from_one_source_cannot_keep_the_owner_out()
    {
        // A source threshold no source reaches here, so only the attacker's own counter on the account stands between it
        // and the owner. Free failures 3, then account delays of 2, 4, 8 and 16 s; the attacker's own delays are twice that.
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits(new() { ["Modularyn:Identity:Throttling:SourceThreshold"] = "1000" }));
        var (_, password) = await identity.AccountAsync("admin-1");
        var start = identity.Clock.Now;

        // The attacker tries a wrong password every second for five minutes.
        var admitted = new List<double>();
        for (var second = 0; second < 300; second++)
        {
            var before = identity.Stores.Verifications;
            await Attempt(identity, "admin-1", Generated.Password(), "bot");
            if (identity.Stores.Verifications > before) admitted.Add((identity.Clock.Now - start).TotalSeconds);
            identity.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        // It is admitted only when the account's delay and its own have both passed: after the free failures, every gap is
        // twice the delay its last failure set, so the account is open to everyone else for half of each gap.
        Assert.Equal([0, 1, 2, 6, 14, 30, 62, 94, 126, 158, 190, 222, 254, 286], admitted);

        // The owner, refused inside the delay, signs in at the instant the refusal named, although the attacker tries
        // first at that instant.
        var owner = await Attempt(identity, "admin-1", password, "owner-net");
        Assert.Equal((SignInStatus.Throttled, start.AddSeconds(302)), (owner.Status, owner.RetryAt));
        identity.Clock.Set(owner.RetryAt!.Value);
        Assert.Equal(SignInStatus.Throttled, (await Attempt(identity, "admin-1", Generated.Password(), "bot")).Status);
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, "owner-net")).Status);

        // The owner's success clears the account's delay but not the attacker's own, which still runs.
        identity.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SignInStatus.Throttled, (await Attempt(identity, "admin-1", Generated.Password(), "bot")).Status);
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, "owner-net")).Status);
    }

    [Fact(DisplayName = "TM-G02-015 attempts from an unknown source neither block nor delay one another, and still meet the account's limits")]
    public async Task TM_G02_015_Attempts_from_an_unknown_source_never_block_or_delay_one_another()
    {
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits());
        var (_, password) = await identity.AccountAsync("admin-1");
        var names = Enumerable.Range(0, 5).Select(index => "user-" + index).ToArray();
        foreach (var name in names) await identity.AccountAsync(name);

        // Ten failures across accounts, beyond a source's threshold of six, block nobody arriving without an address.
        foreach (var name in names)
            for (var failure = 0; failure < 2; failure++)
                Assert.Equal(SignInStatus.Failed, (await Attempt(identity, name, Generated.Password(), ClientSources.Unknown)).Status);
        Assert.Equal(SignInStatus.Succeeded, (await Attempt(identity, "admin-1", password, ClientSources.Unknown)).Status);
        Assert.Equal(ThrottleCounter.Empty, identity.Stores.Counter(ThrottleKey.ForSource(ClientSources.Unknown)));

        // The account's own delay still applies to them.
        Assert.Equal(SignInStatus.Failed, (await Attempt(identity, "user-0", Generated.Password(), ClientSources.Unknown)).Status);
        Assert.Equal(SignInStatus.Throttled, (await Attempt(identity, "user-0", Generated.Password(), ClientSources.Unknown)).Status);
    }
}
