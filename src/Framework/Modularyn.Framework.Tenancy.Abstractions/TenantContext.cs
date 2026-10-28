using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Modularyn.Framework.Tenancy")]

namespace Modularyn.Framework.Tenancy.Abstractions;

public enum TenantScope { Tenant, Host }

/// <summary>How a context was established. Every source validates before the context exists.</summary>
public enum TenantContextSource
{
    /// <summary>Resolved from request candidates after directory, domain and membership checks.</summary>
    Request,
    /// <summary>Reconstructed from a propagated reference after the receiver revalidated it.</summary>
    Propagated,
    /// <summary>Granted under separately checked host authority and recorded in audit.</summary>
    HostAuthority
}

public enum ActorKind { User, Service }

/// <summary>
/// A reference to the authenticated actor of a unit of work, supplied by the identity seam. It names an identity; it is
/// never a credential and grants nothing by itself.
/// </summary>
public sealed record ActorReference
{
    public const int MaxSubjectLength = 256;

    public ActorReference(ActorKind kind, string subject)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (subject.Length > MaxSubjectLength || subject != subject.Trim() || subject.Any(char.IsControl))
            throw new ArgumentException("An actor subject is at most 256 characters, with no surrounding whitespace or control characters.", nameof(subject));
        Kind = kind;
        Subject = subject;
    }

    public ActorKind Kind { get; }
    public string Subject { get; }
}

/// <summary>
/// The separately granted, audited authority under which a host-scope context exists: one operation, with the entities
/// its owning module declared that it may reach and nothing more.
/// </summary>
public sealed record HostScopeGrant
{
    internal HostScopeGrant(HostOperation operation, string reason, DateTimeOffset grantedAt)
    {
        OperationId = operation.Id;
        Reach = operation.Reach;
        Reason = reason;
        GrantedAt = grantedAt;
    }

    public string OperationId { get; }
    public string Reason { get; }
    public DateTimeOffset GrantedAt { get; }

    /// <summary>The entities this grant may reach in host scope; the persistence adapter refuses every other entity.</summary>
    public IReadOnlySet<HostReach> Reach { get; }

    public bool Reaches(string moduleId, string table) =>
        moduleId is not null && table is not null && Reach.Any(entity => entity.ModuleId == moduleId && entity.Table == table);
}

/// <summary>
/// The validated tenant context of one unit of work. Only the tenancy runtime creates it, after validation; it is
/// immutable, and it stops being usable when its unit ends. A context is carried explicitly, never through ambient
/// state, so work that does not receive it has none.
/// </summary>
public sealed class TenantContext
{
    private int ended;

    internal TenantContext(TenantScope scope, Guid? tenantId, ActorReference actor, HostScopeGrant? hostGrant,
        TenantContextSource source, Guid unitId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(scope) || !Enum.IsDefined(source) || unitId == Guid.Empty)
            throw new ArgumentException("A tenant context needs a defined scope, source and unit identity.");
        if (scope == TenantScope.Tenant && (tenantId is null || tenantId == Guid.Empty || hostGrant is not null))
            throw new ArgumentException("A tenant-scope context names exactly one tenant and carries no host grant.");
        if (scope == TenantScope.Host && (tenantId is not null || hostGrant is null || source == TenantContextSource.Request))
            throw new ArgumentException("A host-scope context names no tenant and carries its host grant.");
        Scope = scope;
        TenantId = tenantId;
        Actor = actor;
        HostGrant = hostGrant;
        Source = source;
        UnitId = unitId;
    }

    public TenantScope Scope { get; }

    /// <summary>The effective tenant; null in host scope.</summary>
    public Guid? TenantId { get; }

    public ActorReference Actor { get; }

    /// <summary>The audited grant of a host-scope context; null in tenant scope.</summary>
    public HostScopeGrant? HostGrant { get; }

    public TenantContextSource Source { get; }

    /// <summary>Identifies the unit of work for diagnostics and audit. It carries no authority.</summary>
    public Guid UnitId { get; }

    /// <summary>False once the unit of work that owns this context has ended, on every path.</summary>
    public bool IsActive => Volatile.Read(ref ended) == 0;

    internal void End() => Interlocked.Exchange(ref ended, 1);
}
