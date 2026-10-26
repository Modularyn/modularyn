namespace Modularyn.Modules.Identity.Domain;

/// <summary>The declared idle and absolute session lifetimes (TM-G02-016).</summary>
public sealed record SessionPolicy
{
    public static readonly TimeSpan MinimumIdleTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumIdleTimeout = TimeSpan.FromHours(12);
    public static readonly TimeSpan MinimumAbsoluteLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaximumAbsoluteLifetime = TimeSpan.FromHours(24);

    public SessionPolicy(TimeSpan idleTimeout, TimeSpan absoluteLifetime)
    {
        if (idleTimeout < MinimumIdleTimeout || idleTimeout > MaximumIdleTimeout)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "A session idle timeout is from one minute to twelve hours.");
        if (absoluteLifetime < MinimumAbsoluteLifetime || absoluteLifetime > MaximumAbsoluteLifetime || absoluteLifetime < idleTimeout)
            throw new ArgumentOutOfRangeException(nameof(absoluteLifetime), "A session absolute lifetime is from fifteen minutes to a day, and at least the idle timeout.");
        if (!StoredTime.IsWhole(idleTimeout) || !StoredTime.IsWhole(absoluteLifetime))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "Session limits are whole microseconds, the precision the database keeps.");
        IdleTimeout = idleTimeout;
        AbsoluteLifetime = absoluteLifetime;
    }

    public TimeSpan IdleTimeout { get; }
    public TimeSpan AbsoluteLifetime { get; }
}

public enum SessionEndReason
{
    SignedOut = 1,
    IdleTimeout = 2,
    AbsoluteLifetime = 3,
    SecurityStateChanged = 4,
    Revoked = 5,
    PasswordChanged = 6,
    AccountDisabled = 7,
    Superseded = 8,
    AccountUnavailable = 9
}

/// <summary>
/// A server-side session record. The store keeps only the SHA-256 of the session token, so reading the store does not
/// yield a usable session.
/// </summary>
public sealed record SessionRecord(Guid Id, byte[] TokenHash, Guid AccountId, DateTimeOffset AuthenticatedAt, DateTimeOffset LastSeenAt,
    DateTimeOffset AbsoluteExpiresAt, string SecurityStamp, DateTimeOffset? EndedAt, SessionEndReason? EndReason);

/// <summary>A session record together with the current state of its account, read in one step.</summary>
public sealed record SessionLookup(SessionRecord Session, AccountSecurityState? Account);

public interface ISessionStore
{
    Task CreateAsync(SessionRecord session, CancellationToken cancellationToken);

    Task<SessionLookup?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    Task<SessionLookup?> FindAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Records activity only while the session has not ended, was last seen after <paramref name="idleCutoff"/> and expires
    /// after <paramref name="now"/>, in one conditional write. False when the session no longer qualifies.
    /// </summary>
    Task<bool> TouchAsync(Guid sessionId, DateTimeOffset now, DateTimeOffset idleCutoff, CancellationToken cancellationToken);

    /// <summary>Ends the session with its reason unless it has already ended. False when it had.</summary>
    Task<bool> EndAsync(Guid sessionId, SessionEndReason reason, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Ends every session of the account that has not already ended, and returns how many it ended.</summary>
    Task<int> EndAllAsync(Guid accountId, SessionEndReason reason, DateTimeOffset now, CancellationToken cancellationToken);
}

public static class SessionRules
{
    /// <summary>The instant the session ends regardless of activity: its recorded expiry, or sooner under a tightened policy.</summary>
    public static DateTimeOffset AbsoluteDeadline(SessionRecord session, SessionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(policy);
        var underPolicy = session.AuthenticatedAt + policy.AbsoluteLifetime;
        return underPolicy < session.AbsoluteExpiresAt ? underPolicy : session.AbsoluteExpiresAt;
    }

    /// <summary>
    /// Why the session must end at <paramref name="now"/>, or null while it may continue. A session ends when its account is
    /// gone or disabled, when the account's security stamp has changed since issue, at its absolute deadline and once it has
    /// been idle for the idle timeout. Each limit is exclusive: a request exactly at a limit is refused.
    /// </summary>
    public static SessionEndReason? Evaluate(SessionLookup lookup, SessionPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(policy);
        var session = lookup.Session;
        if (session.EndedAt is not null) return session.EndReason ?? SessionEndReason.Revoked;
        if (lookup.Account is not { } account || account.AccountId != session.AccountId) return SessionEndReason.AccountUnavailable;
        if (account.Disabled) return SessionEndReason.AccountDisabled;
        if (!string.Equals(account.SecurityStamp, session.SecurityStamp, StringComparison.Ordinal)) return SessionEndReason.SecurityStateChanged;
        if (now >= AbsoluteDeadline(session, policy)) return SessionEndReason.AbsoluteLifetime;
        if (now >= session.LastSeenAt + policy.IdleTimeout) return SessionEndReason.IdleTimeout;
        return null;
    }
}
