using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Modules.Identity.Domain;

public sealed record DelegationPolicy
{
    public static readonly TimeSpan LongestAllowedLifetime = TimeSpan.FromDays(7);

    public DelegationPolicy(TimeSpan maximumLifetime)
    {
        if (maximumLifetime < TimeSpan.FromMinutes(1) || maximumLifetime > LongestAllowedLifetime)
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime), "A delegation lasts from one minute to seven days.");
        if (!StoredTime.IsWhole(maximumLifetime))
            throw new ArgumentOutOfRangeException(nameof(maximumLifetime), "A delegation lifetime is whole microseconds, the precision the database keeps.");
        MaximumLifetime = maximumLifetime;
    }

    public TimeSpan MaximumLifetime { get; }
}

public enum DelegationState
{
    Pending = 1,
    Active = 2,
    Expired = 3,
    Revoked = 4
}

/// <summary>
/// A grant for one user, the actor, to act for another, the subject (ARC-006). It records who issued it, its scope,
/// purpose, tenant and validity window, and its revocation. It never covers more than its scope, and the subject's own
/// grants still apply within it.
/// </summary>
public sealed class DelegationGrant
{
    private DelegationGrant(Guid id, PrincipalReference actor, PrincipalReference subject, DelegatedAuthority authority,
        PrincipalReference issuedBy, DateTimeOffset issuedAt, DateTimeOffset? revokedAt, PrincipalReference? revokedBy)
    {
        Id = id;
        Actor = actor;
        Subject = subject;
        Authority = authority;
        IssuedBy = issuedBy;
        IssuedAt = issuedAt;
        RevokedAt = revokedAt;
        RevokedBy = revokedBy;
    }

    public Guid Id { get; }
    public PrincipalReference Actor { get; }
    public PrincipalReference Subject { get; }
    public DelegatedAuthority Authority { get; }
    public PrincipalReference IssuedBy { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset? RevokedAt { get; }
    public PrincipalReference? RevokedBy { get; }

    /// <summary>
    /// Validates and creates a new grant. Its lifetime may not exceed the policy, and it may start no earlier than its issue.
    /// The requested window is narrowed to whole microseconds (<see cref="StoredTime"/>), its start rounded up and its end
    /// down, so the stored grant never begins earlier or ends later than requested.
    /// </summary>
    public static DelegationGrant Issue(Guid id, PrincipalReference actor, PrincipalReference subject, IEnumerable<string> scope, string purpose,
        Guid? tenant, DateTimeOffset notBefore, DateTimeOffset expiresAt, PrincipalReference issuedBy, DateTimeOffset now, DelegationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(issuedBy);
        ArgumentNullException.ThrowIfNull(policy);
        if (actor.Kind != IdentityKind.User || subject.Kind != IdentityKind.User)
            throw new ArgumentException("Delegation is between users; a service identity runs under its own declared scope.", nameof(actor));
        if (actor == subject) throw new ArgumentException("A delegation's actor and subject are different users.", nameof(subject));
        now = StoredTime.Floor(now);
        notBefore = StoredTime.Ceiling(notBefore);
        expiresAt = StoredTime.Floor(expiresAt);
        if (notBefore < now) throw new ArgumentOutOfRangeException(nameof(notBefore), "A delegation cannot begin before it is issued.");
        if (expiresAt - notBefore > policy.MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "The delegation would outlast the declared maximum lifetime.");
        return new(id, actor, subject, new DelegatedAuthority(id, scope, purpose, tenant, notBefore, expiresAt), issuedBy, now, null, null);
    }

    /// <summary>Rebuilds a stored grant. The store's values are validated exactly as at issue.</summary>
    public static DelegationGrant Restore(Guid id, PrincipalReference actor, PrincipalReference subject, IEnumerable<string> scope, string purpose,
        Guid? tenant, DateTimeOffset notBefore, DateTimeOffset expiresAt, PrincipalReference issuedBy, DateTimeOffset issuedAt,
        DateTimeOffset? revokedAt, PrincipalReference? revokedBy)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(issuedBy);
        if (actor.Kind != IdentityKind.User || subject.Kind != IdentityKind.User || actor == subject)
            throw new InvalidOperationException("A stored delegation names two different users.");
        if ((revokedAt is null) != (revokedBy is null)) throw new InvalidOperationException("A stored revocation records its time and its actor.");
        return new(id, actor, subject, new DelegatedAuthority(id, scope, purpose, tenant, notBefore, expiresAt), issuedBy, issuedAt, revokedAt, revokedBy);
    }

    public DelegationState StateAt(DateTimeOffset now)
    {
        if (RevokedAt is { } revoked && revoked <= now) return DelegationState.Revoked;
        if (now < Authority.NotBefore) return DelegationState.Pending;
        return now < Authority.ExpiresAt ? DelegationState.Active : DelegationState.Expired;
    }
}

public interface IDelegationStore
{
    Task CreateAsync(DelegationGrant grant, CancellationToken cancellationToken);

    Task<DelegationGrant?> FindAsync(Guid grantId, CancellationToken cancellationToken);

    /// <summary>Revokes the grant unless it is already revoked. False when it does not exist or was already revoked.</summary>
    Task<bool> RevokeAsync(Guid grantId, PrincipalReference revokedBy, DateTimeOffset now, CancellationToken cancellationToken);
}
