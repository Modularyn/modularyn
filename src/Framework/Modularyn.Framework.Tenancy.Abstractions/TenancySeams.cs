using System.Security.Claims;

namespace Modularyn.Framework.Tenancy.Abstractions;

// The narrow seams through which tenancy consumes identity, authorization and audit. Tenancy supplies no
// implementation of any of them: when one is absent or fails, the operation that needs it is refused.

/// <summary>Maps an authenticated principal to its actor reference. Supplied by identity (SEC-03).</summary>
public interface ITenantActorResolver
{
    /// <summary>The principal's actor, or null when it is not authenticated or not known.</summary>
    ActorReference? Resolve(ClaimsPrincipal principal);
}

/// <summary>
/// Answers whether an actor is currently a member of a tenant, from the current record rather than from cached claims.
/// Supplied by identity or authorization (SEC-03, SEC-05).
/// </summary>
public interface ITenantMembership
{
    ValueTask<bool> IsMemberAsync(ActorReference actor, Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Answers whether an actor currently holds the separately granted host authority, under the elevated policy, for one
/// host operation. Tenant membership or tenant administration never implies it. Supplied by authorization (SEC-05).
/// </summary>
public interface IHostAuthority
{
    ValueTask<bool> IsGrantedAsync(ActorReference actor, string operationId, CancellationToken cancellationToken);
}

/// <summary>
/// Records a host-scope grant before the scope exists. A failure refuses the grant. Supplied by audit (SEC-07).
/// </summary>
public interface IHostScopeAudit
{
    ValueTask RecordAsync(HostScopeAuditRecord record, CancellationToken cancellationToken);
}

/// <summary>A host-scope grant as audit receives it. The correlation identifier is diagnostic linkage only.</summary>
public sealed record HostScopeAuditRecord(Guid UnitId, ActorReference Actor, string OperationId, string Reason,
    TenantContextSource Source, DateTimeOffset At, string? CorrelationId);
