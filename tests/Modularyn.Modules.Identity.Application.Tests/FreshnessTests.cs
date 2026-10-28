using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

public sealed class FreshnessTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "TM-G02-017 each security change ends the account's sessions at the next validation and records why")]
    public async Task TM_G02_017_Each_security_change_ends_sessions_at_the_next_validation()
    {
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits());
        var (accountId, password) = await identity.AccountAsync("editor-1");
        var (otherId, otherPassword) = await identity.AccountAsync("editor-2");
        var sessions = identity.Get<ISessionService>();
        var accounts = identity.Get<IAccountService>();
        var other = (await identity.SignInAsync("editor-2", otherPassword)).Session;

        async Task<(IssuedSession First, IssuedSession Second)> TwoSessions()
        {
            var first = (await identity.SignInAsync("editor-1", password)).Session;
            var second = (await identity.SignInAsync("editor-1", password)).Session;
            Assert.NotEqual(first.Token.Reveal(), second.Token.Reveal());
            return (first, second);
        }

        async Task AssertEnded(IssuedSession session, SessionEndCause cause)
        {
            Assert.Null(await sessions.ValidateAsync(session.Token, Cancellation));
            var record = await sessions.DescribeAsync(session.SessionId, Cancellation);
            Assert.Equal(cause, record!.EndCause);
            Assert.Equal(identity.Clock.Now, record.EndedAt);
        }

        var (signedOut, survivor) = await TwoSessions();
        Assert.True(await sessions.SignOutAsync(signedOut.Token, Cancellation));
        await AssertEnded(signedOut, SessionEndCause.SignedOut);
        Assert.NotNull(await sessions.ValidateAsync(survivor.Token, Cancellation));

        var newPassword = Generated.Password();
        Assert.Equal(PasswordChangeStatus.Changed,
            (await accounts.ChangePasswordAsync(accountId, new SecretText(password), new SecretText(newPassword), "src-1", Cancellation)).Status);
        password = newPassword;
        await AssertEnded(survivor, SessionEndCause.PasswordChanged);

        var (revokedA, revokedB) = await TwoSessions();
        Assert.Equal(2, await sessions.RevokeAllAsync(await identity.Administrator.SessionsAsync(), accountId, Cancellation));
        await AssertEnded(revokedA, SessionEndCause.Revoked);
        await AssertEnded(revokedB, SessionEndCause.Revoked);

        var (changedA, _) = await TwoSessions();
        Assert.True(await accounts.SecurityStateChangedAsync(await identity.Administrator.AccountsAsync(), accountId, Cancellation));
        await AssertEnded(changedA, SessionEndCause.SecurityStateChanged);

        var (disabledA, _) = await TwoSessions();
        Assert.True(await accounts.DisableAsync(await identity.Administrator.AccountsAsync(), accountId, Cancellation));
        await AssertEnded(disabledA, SessionEndCause.AccountDisabled);
        Assert.Equal(SignInStatus.Failed, (await identity.Get<ISignInService>().SignInWithPasswordAsync(
            new PasswordSignInRequest("editor-1", new SecretText(password), "src-1"), Cancellation)).Status);

        // None of this touched another account's session.
        Assert.NotNull(await sessions.ValidateAsync(other.Token, Cancellation));
        Assert.False(await accounts.DisableAsync(await identity.Administrator.AccountsAsync(), Guid.NewGuid(), Cancellation));
        Assert.NotEqual(accountId, otherId);
    }

    [Fact(DisplayName = "TM-G02-017 a security-sensitive recheck sees a session ended after the request began")]
    public async Task TM_G02_017_A_recheck_sees_a_session_ended_after_the_request_began()
    {
        await using var identity = UnitIdentity.Create();
        var (accountId, password) = await identity.AccountAsync("editor-1");
        var (session, principal) = await identity.SignInAsync("editor-1", password);
        var freshness = identity.Get<IPrincipalFreshness>();
        Assert.Equal(FreshnessStatus.Current, await freshness.RecheckAsync(principal, Cancellation));

        // The principal captured at the start of the request is unchanged, but the recheck reads the store.
        Assert.True(await identity.Get<IAccountService>().SecurityStateChangedAsync(await identity.Administrator.AccountsAsync(), accountId, Cancellation));
        Assert.Equal(FreshnessStatus.SessionEnded, await freshness.RecheckAsync(principal, Cancellation));
        Assert.Null(await identity.Get<ISessionService>().ValidateAsync(session.Token, Cancellation));

        var (_, idle) = await identity.SignInAsync("editor-1", password);
        identity.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(FreshnessStatus.SessionEnded, await freshness.RecheckAsync(idle, Cancellation));

        // A principal that names a session it does not own is never current.
        var (_, fresh) = await identity.SignInAsync("editor-1", password);
        var forged = ExecutionPrincipal.ForUser(new PrincipalReference(Guid.NewGuid().ToString("D"), IdentityKind.User), fresh.SessionId!.Value, identity.Clock.Now);
        Assert.Equal(FreshnessStatus.SessionEnded, await freshness.RecheckAsync(forged, Cancellation));
        Assert.Equal(FreshnessStatus.SubjectUnavailable,
            await freshness.RecheckAsync(ExecutionPrincipal.ForService(new PrincipalReference("svc-undeclared", IdentityKind.Service), ["item.read"]), Cancellation));
    }

    [Fact(DisplayName = "TM-G02-016 every sign-in issues a new session with a new token, and the store keeps only the token's hash")]
    public async Task TM_G02_016_Every_sign_in_issues_a_new_session_and_the_store_keeps_only_a_hash()
    {
        await using var identity = UnitIdentity.Create();
        var (_, password) = await identity.AccountAsync("editor-1");
        var first = (await identity.SignInAsync("editor-1", password)).Session;
        var second = (await identity.SignInAsync("editor-1", password)).Session;
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(first.Token.Reveal(), second.Token.Reveal());
        Assert.Equal(SessionToken.EncodedLength, first.Token.Reveal().Length);
        foreach (var record in identity.Stores.Sessions)
        {
            Assert.Equal(32, record.TokenHash.Length);
            Assert.DoesNotContain(record.TokenHash, new[] { first.Token, second.Token }.Select(token => System.Text.Encoding.ASCII.GetBytes(token.Reveal())));
        }
        Assert.Equal(first.Token.ComputeHash(), identity.Stores.Sessions.Single(record => record.Id == first.SessionId).TokenHash);
        Assert.Equal(SessionToken.Redacted, first.Token.ToString());
        Assert.DoesNotContain(first.Token.Reveal(), first.ToString() + System.Text.Json.JsonSerializer.Serialize(new { first.Token }), StringComparison.Ordinal);
        Assert.False(SessionToken.TryParse(first.Token.Reveal() + "A", out _));
        Assert.False(SessionToken.TryParse(first.Token.Reveal()[..^1] + "!", out _));
        Assert.False(SessionToken.TryParse(null, out _));
    }
}
