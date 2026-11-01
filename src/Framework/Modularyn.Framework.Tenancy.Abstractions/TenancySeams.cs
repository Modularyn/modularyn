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

    /// <summary>
    /// When the actor's current membership of the tenant began, at whole microseconds; null when it is not a member now.
    /// Authority granted in the tenant before that instant belongs to an earlier membership and no longer counts.
    /// </summary>
    ValueTask<DateTimeOffset?> MemberSinceAsync(ActorReference actor, Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Answers whether an actor currently holds the separately granted host authority, under the elevated policy, for one
/// host operation, and what that operation may reach. Tenant membership or tenant administration never implies it.
/// Supplied by authorization (SEC-05).
/// </summary>
public interface IHostAuthority
{
    /// <summary>
    /// The operation as its owning module declared it, with the entities it may reach, when the actor currently holds host
    /// authority for it; null when the actor does not, or the operation is not declared.
    /// </summary>
    ValueTask<HostOperation?> AuthorizeAsync(ActorReference actor, string operationId, CancellationToken cancellationToken);
}

/// <summary>
/// Records a host-scope grant before the scope exists. A failure refuses the grant. Supplied by audit (SEC-07).
/// </summary>
public interface IHostScopeAudit
{
    ValueTask RecordAsync(HostScopeAuditRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// A host-scope grant as audit receives it. Its time is whole microseconds from the host's clock, as the grant itself
/// records it. The correlation identifier is diagnostic linkage only.
/// </summary>
public sealed record HostScopeAuditRecord(Guid UnitId, ActorReference Actor, string OperationId, string Reason,
    TenantContextSource Source, DateTimeOffset At, string? CorrelationId);

/// <summary>The privileged filter bypasses the persistence adapter offers, each recorded in audit before its query exists.</summary>
public enum FilterBypass
{
    /// <summary>A host-scope read of every tenant's rows of one entity, lifting only the tenant predicate.</summary>
    CrossTenant = 1,

    /// <summary>A read that includes soft-deleted rows of one entity, lifting only the soft-delete predicate.</summary>
    SoftDeleted = 2
}

/// <summary>
/// Records a privileged filter bypass, before its query exists, for the unit whose context tenancy attached it to. A
/// failure refuses the bypass. Supplied by audit (SEC-07).
/// </summary>
public interface IFilterBypassAudit
{
    ValueTask RecordAsync(FilterBypassRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// A filter bypass as audit receives it: the unit, its actor, scope and tenant, the entity as its module and table, and
/// the operation and reason the bypass declares, which for a cross-tenant read are its host grant's. It holds references
/// only. The correlation identifier is diagnostic linkage only.
/// </summary>
public sealed record FilterBypassRecord(Guid UnitId, ActorReference Actor, TenantScope Scope, Guid? TenantId, FilterBypass Bypass,
    string ModuleId, string Table, string OperationId, string Reason, string? CorrelationId);
