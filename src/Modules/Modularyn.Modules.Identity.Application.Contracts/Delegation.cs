using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Modules.Identity.Application.Contracts;

/// <summary>A request for <see cref="ActorAccountId"/> to act for <see cref="SubjectAccountId"/> within a scope, for a purpose, until an expiry.</summary>
public sealed class DelegationRequest
{
    public DelegationRequest(Guid actorAccountId, Guid subjectAccountId, IEnumerable<string> scope, string purpose, Guid? tenant,
        DateTimeOffset expiresAt, DateTimeOffset? notBefore = null)
    {
        ActorAccountId = actorAccountId;
        SubjectAccountId = subjectAccountId;
        Scope = PermissionNames.ToScope(scope, nameof(scope));
        if (!DelegatedAuthority.IsValidPurpose(purpose))
            throw new ArgumentException("A delegation records its purpose in 1 to 500 characters without control characters.", nameof(purpose));
        if (tenant == Guid.Empty) throw new ArgumentException("A delegation's tenant is a tenant identifier, never the empty one.", nameof(tenant));
        Purpose = purpose;
        Tenant = tenant;
        ExpiresAt = expiresAt;
        NotBefore = notBefore;
    }

    public Guid ActorAccountId { get; }
    public Guid SubjectAccountId { get; }
    public IReadOnlySet<string> Scope { get; }
    public string Purpose { get; }

    /// <summary>The only tenant the delegation applies in, by its tenant identifier, or null for work outside any tenant.</summary>
    public Guid? Tenant { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>When the delegation begins; null begins it at issue.</summary>
    public DateTimeOffset? NotBefore { get; }
}

public enum DelegationIssueStatus
{
    Issued = 1,

    /// <summary>Only the subject, acting as themselves through their own session, may delegate their authority in G-02.</summary>
    IssuerNotPermitted = 2,

    ActorUnavailable = 3,
    SubjectUnavailable = 4,

    /// <summary>The window is invalid or longer than the declared maximum lifetime.</summary>
    InvalidWindow = 5
}

public sealed record DelegationIssueResult(DelegationIssueStatus Status, Guid? GrantId);

/// <summary>A delegation record as stored: who acts for whom, its scope, purpose, tenant, window, issuer and revocation.</summary>
public sealed record DelegationRecordView(Guid GrantId, PrincipalReference Actor, PrincipalReference Subject, IReadOnlySet<string> Scope,
    string Purpose, Guid? Tenant, DateTimeOffset IssuedAt, DateTimeOffset NotBefore, DateTimeOffset ExpiresAt, PrincipalReference IssuedBy,
    DateTimeOffset? RevokedAt, PrincipalReference? RevokedBy);

/// <summary>
/// Explicit, scoped, expiring delegation (ARC-006, TM-G02-018). A resolved delegation yields a principal whose actor is the
/// delegate and whose effective subject is the delegator, bounded by the delegation's scope, tenant and window.
/// </summary>
public interface IDelegationService
{
    /// <summary>
    /// Issues a delegation. The issuer must be the subject, acting as themselves; a delegated or service execution can never
    /// issue one, so a delegation cannot be chained or widened.
    /// </summary>
    Task<DelegationIssueResult> IssueAsync(DelegationRequest request, ExecutionPrincipal issuedBy, CancellationToken cancellationToken);

    /// <summary>Revokes the delegation; it stops resolving at once. Its actor or subject may revoke it. False when nothing was revoked.</summary>
    Task<bool> RevokeAsync(Guid grantId, ExecutionPrincipal revokedBy, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the delegation for its actor's own, undelegated principal. Null when the grant is unknown, belongs to another
    /// actor, is pending, expired or revoked, or its subject is unavailable.
    /// </summary>
    Task<ExecutionPrincipal?> ResolveAsync(Guid grantId, ExecutionPrincipal actor, CancellationToken cancellationToken);

    Task<DelegationRecordView?> DescribeAsync(Guid grantId, CancellationToken cancellationToken);
}

/// <summary>What a current-user API reports (FND-017): the actor, the effective subject, their kinds and any delegation.</summary>
public sealed record CurrentPrincipalView(PrincipalReference Actor, PrincipalReference EffectiveSubject, bool Delegated, Guid? DelegationId,
    IReadOnlyList<string>? DelegatedScope, string? DelegationPurpose, Guid? DelegationTenant, DateTimeOffset? DelegationExpiresAt,
    IReadOnlyList<string>? ServiceScope, DateTimeOffset? AuthenticatedAt)
{
    public static CurrentPrincipalView Describe(ExecutionPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var delegation = principal.Delegation;
        return new(principal.Actor, principal.EffectiveSubject, principal.IsDelegated, delegation?.GrantId,
            delegation?.Scope.Order(StringComparer.Ordinal).ToArray(), delegation?.Purpose, delegation?.Tenant, delegation?.ExpiresAt,
            principal.ServiceScope?.Order(StringComparer.Ordinal).ToArray(), principal.AuthenticatedAt);
    }
}
