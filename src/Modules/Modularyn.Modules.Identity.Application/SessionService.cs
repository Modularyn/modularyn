using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

/// <summary>
/// Server-side sessions. Every validation reads the record and its account's state from the store, so the declared
/// freshness bound for sessions and their claims is the next request: nothing about a session is cached.
/// </summary>
internal sealed class SessionService(ISessionStore store, IdentityPolicies policies, TimeProvider clock) : ISessionService
{
    private SessionPolicy Policy => policies.Sessions;

    /// <summary>Issues a new session with a new token for a verified account. The token leaves this method only inside the result.</summary>
    internal async Task<IssuedSession> IssueAsync(AccountSecurityState account, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = SessionToken.Create();
        var record = new SessionRecord(Guid.NewGuid(), token.ComputeHash(), account.AccountId, now, now, now + Policy.AbsoluteLifetime,
            account.SecurityStamp, null, null);
        await store.CreateAsync(record, cancellationToken).ConfigureAwait(false);
        return new IssuedSession(record.Id, account.AccountId, token, now, record.AbsoluteExpiresAt);
    }

    public async Task<ActiveSession?> ValidateAsync(SessionToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var now = clock.GetUtcNow();
        var lookup = await store.FindByTokenHashAsync(token.ComputeHash(), cancellationToken).ConfigureAwait(false);
        if (lookup is null) return null;
        var session = lookup.Session;
        if (SessionRules.Evaluate(lookup, Policy, now) is { } reason)
        {
            if (session.EndedAt is null) await store.EndAsync(session.Id, reason, now, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (!await store.TouchAsync(session.Id, now, now - Policy.IdleTimeout, cancellationToken).ConfigureAwait(false))
        {
            // Another request ended it, or a limit passed, between the read and the write; record why if nothing has.
            var current = await store.FindAsync(session.Id, cancellationToken).ConfigureAwait(false);
            if (current is { Session.EndedAt: null } && SessionRules.Evaluate(current, Policy, now) is { } late)
                await store.EndAsync(session.Id, late, now, cancellationToken).ConfigureAwait(false);
            return null;
        }
        return new ActiveSession(session.Id, session.AccountId, session.AuthenticatedAt, SessionRules.AbsoluteDeadline(session, Policy));
    }

    public async Task<bool> SignOutAsync(SessionToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var lookup = await store.FindByTokenHashAsync(token.ComputeHash(), cancellationToken).ConfigureAwait(false);
        return lookup is not null
            && await store.EndAsync(lookup.Session.Id, SessionEndReason.SignedOut, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RevokeAsync(Guid sessionId, CancellationToken cancellationToken) =>
        store.EndAsync(sessionId, SessionEndReason.Revoked, clock.GetUtcNow(), cancellationToken);

    public Task<int> RevokeAllAsync(Guid accountId, CancellationToken cancellationToken) =>
        store.EndAllAsync(accountId, SessionEndReason.Revoked, clock.GetUtcNow(), cancellationToken);

    public async Task<SessionRecordView?> DescribeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var lookup = await store.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (lookup is null) return null;
        var session = lookup.Session;
        return new SessionRecordView(session.Id, session.AccountId, session.AuthenticatedAt, session.LastSeenAt,
            SessionRules.AbsoluteDeadline(session, Policy), session.EndedAt, session.EndReason is { } reason ? (SessionEndCause)(int)reason : null);
    }

    internal Task<int> EndAllAsync(Guid accountId, SessionEndReason reason, CancellationToken cancellationToken) =>
        store.EndAllAsync(accountId, reason, clock.GetUtcNow(), cancellationToken);
}
