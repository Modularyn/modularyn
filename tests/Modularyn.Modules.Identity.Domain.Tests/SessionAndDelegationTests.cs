using Modularyn.Framework.Security.Abstractions;
using Xunit;

namespace Modularyn.Modules.Identity.Domain.Tests;

public sealed class SessionRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly SessionPolicy Policy = new(TimeSpan.FromMinutes(20), TimeSpan.FromHours(8));

    private static SessionLookup Lookup(DateTimeOffset lastSeen, string stamp = "stamp-1", bool disabled = false, bool account = true,
        DateTimeOffset? ended = null, SessionEndReason? reason = null)
    {
        var accountId = Guid.NewGuid();
        var session = new SessionRecord(Guid.NewGuid(), new byte[32], accountId, Start, lastSeen, Start + Policy.AbsoluteLifetime, "stamp-1", ended, reason);
        return new SessionLookup(session, account ? new AccountSecurityState(accountId, "user-1", stamp, disabled) : null);
    }

    [Fact(DisplayName = "TM-G02-016 idle and absolute limits end a session at their exclusive edges")]
    public void TM_G02_016_Idle_and_absolute_limits_end_a_session_at_their_exclusive_edges()
    {
        var idle = Start.AddMinutes(10);
        Assert.Null(SessionRules.Evaluate(Lookup(idle), Policy, idle + Policy.IdleTimeout - TimeSpan.FromSeconds(1)));
        Assert.Equal(SessionEndReason.IdleTimeout, SessionRules.Evaluate(Lookup(idle), Policy, idle + Policy.IdleTimeout));
        Assert.Equal(SessionEndReason.IdleTimeout, SessionRules.Evaluate(Lookup(idle), Policy, idle + Policy.IdleTimeout + TimeSpan.FromSeconds(1)));

        var deadline = Start + Policy.AbsoluteLifetime;
        var active = deadline - TimeSpan.FromMinutes(1);
        Assert.Null(SessionRules.Evaluate(Lookup(active), Policy, deadline - TimeSpan.FromSeconds(1)));
        Assert.Equal(SessionEndReason.AbsoluteLifetime, SessionRules.Evaluate(Lookup(active), Policy, deadline));
        Assert.Equal(SessionEndReason.AbsoluteLifetime, SessionRules.Evaluate(Lookup(active), Policy, deadline + TimeSpan.FromSeconds(1)));
    }

    [Fact(DisplayName = "TM-G02-017 a changed security stamp, a disabled or missing account and an ended record end the session")]
    public void TM_G02_017_A_changed_security_stamp_disabled_or_missing_account_ends_the_session()
    {
        var now = Start.AddMinutes(1);
        Assert.Null(SessionRules.Evaluate(Lookup(Start), Policy, now));
        Assert.Equal(SessionEndReason.SecurityStateChanged, SessionRules.Evaluate(Lookup(Start, stamp: "stamp-2"), Policy, now));
        Assert.Equal(SessionEndReason.AccountDisabled, SessionRules.Evaluate(Lookup(Start, disabled: true), Policy, now));
        Assert.Equal(SessionEndReason.AccountUnavailable, SessionRules.Evaluate(Lookup(Start, account: false), Policy, now));
        Assert.Equal(SessionEndReason.SignedOut, SessionRules.Evaluate(Lookup(Start, ended: Start, reason: SessionEndReason.SignedOut), Policy, now));
        Assert.Equal(SessionEndReason.Revoked, SessionRules.Evaluate(Lookup(Start, ended: Start), Policy, now));

        var mismatched = Lookup(Start);
        var other = mismatched with { Account = mismatched.Account! with { AccountId = Guid.NewGuid() } };
        Assert.Equal(SessionEndReason.AccountUnavailable, SessionRules.Evaluate(other, Policy, now));
    }

    [Fact(DisplayName = "TM-G02-016 a tightened policy shortens a session issued under a longer one")]
    public void TM_G02_016_A_tightened_policy_shortens_an_issued_session()
    {
        var tighter = new SessionPolicy(TimeSpan.FromMinutes(20), TimeSpan.FromHours(1));
        var session = Lookup(Start.AddMinutes(59));
        Assert.Equal(Start.AddHours(1), SessionRules.AbsoluteDeadline(session.Session, tighter));
        Assert.Equal(SessionEndReason.AbsoluteLifetime, SessionRules.Evaluate(session, tighter, Start.AddHours(1)));
        Assert.Equal(Start + Policy.AbsoluteLifetime, SessionRules.AbsoluteDeadline(session.Session, new SessionPolicy(TimeSpan.FromMinutes(20), TimeSpan.FromHours(24))));
    }

    [Fact(DisplayName = "TM-G02-016 session policies refuse values outside their declared ranges")]
    public void TM_G02_016_Session_policies_refuse_values_outside_their_declared_ranges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionPolicy(TimeSpan.FromHours(13), TimeSpan.FromHours(14)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionPolicy(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionPolicy(TimeSpan.FromMinutes(20), TimeSpan.FromHours(25)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordPolicy(14, PasswordPolicy.MinimumHashIterations));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordPolicy(15, PasswordPolicy.MinimumHashIterations - 1));
        Assert.False(PasswordPolicy.IsPlausible(new SecretText("")));
        Assert.False(PasswordPolicy.IsPlausible(new SecretText(new string('x', PasswordPolicy.MaximumLength + 1))));
        Assert.True(PasswordPolicy.IsPlausible(new SecretText(new string('x', PasswordPolicy.MaximumLength))));
    }

    [Fact(DisplayName = "TM-G02-016 stored times are whole microseconds, and session limits finer than that are refused")]
    public void TM_G02_016_Stored_times_are_whole_microseconds_and_finer_session_limits_are_refused()
    {
        var at = new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_567);
        Assert.Equal(new DateTimeOffset(at.UtcTicks - 7, TimeSpan.Zero), StoredTime.Floor(at));
        Assert.Equal(new DateTimeOffset(at.UtcTicks + 3, TimeSpan.Zero), StoredTime.Ceiling(at));
        Assert.Equal(TimeSpan.Zero, StoredTime.Floor(at).Offset);
        Assert.Equal(StoredTime.Floor(at), StoredTime.Floor(StoredTime.Floor(at)));
        Assert.Equal(StoredTime.Floor(at), StoredTime.Ceiling(StoredTime.Floor(at)));
        Assert.True(StoredTime.IsNormalized(StoredTime.Floor(at)));
        Assert.False(StoredTime.IsNormalized(at));
        Assert.Equal(StoredTime.Floor(at), StoredTime.Require(StoredTime.Floor(at)));
        Assert.Throws<InvalidOperationException>(() => StoredTime.Require(at));
        // A comparison of a raw time with a whole-microsecond limit decides as the floored time does.
        var limit = StoredTime.Floor(at).AddMinutes(20);
        foreach (var ticks in new[] { -11, -10, -9, -1, 0, 1, 9, 10 })
            Assert.Equal(limit.AddTicks(ticks) < limit, StoredTime.Floor(limit.AddTicks(ticks)) < limit);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionPolicy(TimeSpan.FromMinutes(20).Add(TimeSpan.FromTicks(5)), TimeSpan.FromHours(8)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionPolicy(TimeSpan.FromMinutes(20), TimeSpan.FromHours(8).Add(TimeSpan.FromTicks(1))));
    }

    [Fact(DisplayName = "TM-G02-015 user names are the allowed ASCII characters, so their comparison form never depends on globalization")]
    public void TM_G02_015_User_names_are_ascii_so_their_comparison_form_never_depends_on_globalization()
    {
        Assert.Equal("ADMIN-1@EXAMPLE.TEST", UserNames.Normalize("Admin-1@example.test"));
        Assert.True(UserNames.IsPlausible("a.b_c-d+e@f"));
        Assert.True(UserNames.IsPlausible(new string('a', UserNames.MaximumLength)));
        foreach (var name in new[] { "", " ", "bad\u0000name", "admin 1", "e\u0301lan", "\u00C9LAN", "admin\u200B1", new string('a', UserNames.MaximumLength + 1) })
            Assert.False(UserNames.IsPlausible(name));
    }
}

public sealed class DelegationGrantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DelegationPolicy Policy = new(TimeSpan.FromHours(8));
    private static readonly PrincipalReference User1 = new(Guid.NewGuid().ToString("D"), IdentityKind.User);
    private static readonly PrincipalReference Support1 = new(Guid.NewGuid().ToString("D"), IdentityKind.User);
    private static readonly Guid TenantA = Guid.NewGuid();

    private static DelegationGrant Grant(DateTimeOffset notBefore, DateTimeOffset expiresAt) =>
        DelegationGrant.Issue(Guid.NewGuid(), Support1, User1, ["item.read"], "support case", TenantA, notBefore, expiresAt, User1, Now, Policy);

    [Fact(DisplayName = "TM-G02-018 a grant is active only inside its window and until it is revoked")]
    public void TM_G02_018_A_grant_is_active_only_inside_its_window_and_until_revoked()
    {
        var grant = Grant(Now.AddMinutes(1), Now.AddMinutes(30));
        Assert.Equal(DelegationState.Pending, grant.StateAt(Now));
        Assert.Equal(DelegationState.Active, grant.StateAt(Now.AddMinutes(1)));
        Assert.Equal(DelegationState.Active, grant.StateAt(Now.AddMinutes(30).AddSeconds(-1)));
        Assert.Equal(DelegationState.Expired, grant.StateAt(Now.AddMinutes(30)));
        Assert.Equal(Support1, grant.Actor);
        Assert.Equal(User1, grant.Subject);
        Assert.Equal(User1, grant.IssuedBy);
        Assert.Equal(TenantA, grant.Authority.Tenant);

        var revoked = DelegationGrant.Restore(grant.Id, grant.Actor, grant.Subject, grant.Authority.Scope, grant.Authority.Purpose, grant.Authority.Tenant,
            grant.Authority.NotBefore, grant.Authority.ExpiresAt, grant.IssuedBy, grant.IssuedAt, Now.AddMinutes(5), User1);
        Assert.Equal(DelegationState.Active, revoked.StateAt(Now.AddMinutes(5).AddSeconds(-1)));
        Assert.Equal(DelegationState.Revoked, revoked.StateAt(Now.AddMinutes(5)));
    }

    [Fact(DisplayName = "TM-G02-018 a requested window is narrowed to whole microseconds, never starting earlier or ending later than asked")]
    public void TM_G02_018_A_requested_window_is_narrowed_to_whole_microseconds()
    {
        var grant = DelegationGrant.Issue(Guid.NewGuid(), Support1, User1, ["item.read"], "support case", TenantA, Now.AddTicks(3),
            Now.AddMinutes(10).AddTicks(7), User1, Now.AddTicks(1), Policy);
        Assert.Equal((Now.AddTicks(10), Now.AddMinutes(10), Now), (grant.Authority.NotBefore, grant.Authority.ExpiresAt, grant.IssuedAt));
        Assert.Equal(DelegationState.Pending, grant.StateAt(Now.AddTicks(9)));
        Assert.Equal(DelegationState.Active, grant.StateAt(Now.AddTicks(10)));
        Assert.Equal(DelegationState.Active, grant.StateAt(Now.AddMinutes(10).AddTicks(-1)));
        Assert.NotEqual(DelegationState.Active, grant.StateAt(Now.AddMinutes(10)));
        // A window shorter than a microsecond is no window at all.
        Assert.ThrowsAny<ArgumentException>(() => DelegationGrant.Issue(Guid.NewGuid(), Support1, User1, ["item.read"], "support case", TenantA,
            Now.AddTicks(1), Now.AddTicks(9), User1, Now, Policy));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegationPolicy(TimeSpan.FromHours(8).Add(TimeSpan.FromTicks(1))));
    }

    [Fact(DisplayName = "TM-G02-018 issue refuses self delegation, service kinds, backdating and windows beyond the declared lifetime")]
    public void TM_G02_018_Issue_refuses_self_delegation_service_kinds_backdating_and_overlong_windows()
    {
        var service = new PrincipalReference("svc-items", IdentityKind.Service);
        Assert.Throws<ArgumentException>(() => DelegationGrant.Issue(Guid.NewGuid(), User1, User1, ["item.read"], "p", null, Now, Now.AddHours(1), User1, Now, Policy));
        Assert.Throws<ArgumentException>(() => DelegationGrant.Issue(Guid.NewGuid(), service, User1, ["item.read"], "p", null, Now, Now.AddHours(1), User1, Now, Policy));
        Assert.Throws<ArgumentException>(() => DelegationGrant.Issue(Guid.NewGuid(), Support1, service, ["item.read"], "p", null, Now, Now.AddHours(1), User1, Now, Policy));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grant(Now.AddSeconds(-1), Now.AddHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grant(Now, Now + Policy.MaximumLifetime + TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => Grant(Now, Now));
        Assert.Throws<ArgumentException>(() => DelegationGrant.Issue(Guid.NewGuid(), Support1, User1, ["item.*"], "p", null, Now, Now.AddHours(1), User1, Now, Policy));
        Assert.Equal(Now + Policy.MaximumLifetime, Grant(Now, Now + Policy.MaximumLifetime).Authority.ExpiresAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegationPolicy(TimeSpan.FromDays(8)));
        Assert.Throws<InvalidOperationException>(() => DelegationGrant.Restore(Guid.NewGuid(), User1, User1, ["item.read"], "p", null, Now, Now.AddHours(1),
            User1, Now, null, null));
        Assert.Throws<InvalidOperationException>(() => DelegationGrant.Restore(Guid.NewGuid(), Support1, User1, ["item.read"], "p", null, Now, Now.AddHours(1),
            User1, Now, Now, null));
    }

    [Fact(DisplayName = "TM-G02-018 service identities are declared once with exact scopes")]
    public void TM_G02_018_Service_identities_are_declared_once_with_exact_scopes()
    {
        var registry = new ServiceIdentityRegistry([new ServiceIdentityDeclaration("svc-items", ["item.reindex"]), new ServiceIdentityDeclaration("svc-mail", ["mail.send"])]);
        Assert.Equal(["item.reindex"], registry.Find("svc-items")!.Scope);
        Assert.Equal(IdentityKind.Service, registry.Find("svc-items")!.Principal.Kind);
        Assert.Null(registry.Find("svc-admin"));
        Assert.Null(registry.Find(null));
        Assert.Throws<ArgumentException>(() => new ServiceIdentityRegistry([new("svc-items", ["item.reindex"]), new("svc-items", ["item.read"])]));
        Assert.Throws<ArgumentException>(() => new ServiceIdentityDeclaration("svc-items", ["*"]));
        Assert.Throws<ArgumentException>(() => new ServiceIdentityDeclaration("Svc Items", ["item.reindex"]));
        Assert.Throws<ArgumentException>(() => new ServiceIdentityDeclaration("svc-items", []));
    }
}
