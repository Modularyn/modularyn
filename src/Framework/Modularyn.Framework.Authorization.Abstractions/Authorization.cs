using System.Collections.Frozen;
using System.Linq.Expressions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization.Abstractions;

/// <summary>
/// Stable authorization codes. Every entry point reports the same code for the same decision, and a refusal carries no
/// grant, role or resource detail.
/// </summary>
public static class AuthorizationCodes
{
    /// <summary>No authenticated principal: nothing is permitted without one.</summary>
    public const string Unauthenticated = "MAZ001";

    /// <summary>The unit has no active tenant-scope context; permissions apply within one tenant.</summary>
    public const string NoTenantContext = "MAZ002";

    /// <summary>The permission is not declared by any composed module, so it confers nothing and cannot be granted.</summary>
    public const string UndefinedPermission = "MAZ003";

    /// <summary>The execution's ceiling (a delegation's scope, tenant and window, or a service identity's scope) excludes the permission.</summary>
    public const string OutsideCeiling = "MAZ004";

    /// <summary>The effective subject does not hold the permission in the tenant.</summary>
    public const string NotHeld = "MAZ005";

    /// <summary>The principal's session, delegation or subject is no longer current.</summary>
    public const string PrincipalNotCurrent = "MAZ006";

    /// <summary>The resource is not in the tenant, or the subject neither owns it nor holds a current grant on it; answered as for a missing resource.</summary>
    public const string ResourceNotFound = "MAZ007";

    /// <summary>Authorization data or a seam it needs is unavailable; refused, and transient.</summary>
    public const string Unavailable = "MAZ008";

    /// <summary>The principal is not the actor the tenant context was established for.</summary>
    public const string PrincipalMismatch = "MAZ009";

    /// <summary>The effective subject is not a current member of the tenant.</summary>
    public const string SubjectNotMember = "MAZ010";

    /// <summary>An authority change needs the permission-administration permission, from the actor's own interactive principal.</summary>
    public const string ChangeNotPermitted = "MAZ020";

    /// <summary>An authority change would grant, or manage, authority beyond the actor's assignment ceiling or beyond what the actor holds.</summary>
    public const string BeyondCeiling = "MAZ021";

    /// <summary>An authority change named a permission that no composed module declares.</summary>
    public const string ChangeUndefined = "MAZ022";

    /// <summary>A concurrent authority change committed first, repeatedly; read again and retry.</summary>
    public const string ChangeConflict = "MAZ023";

    /// <summary>The authority change could not be recorded in audit, so it was not made.</summary>
    public const string AuditUnavailable = "MAZ024";

    /// <summary>The change names something that does not exist or is not eligible, such as an unknown role or a non-member.</summary>
    public const string ChangeInvalid = "MAZ025";
}

/// <summary>Where the authority behind a decision came from.</summary>
public enum AuthorizationSource
{
    /// <summary>No authority was read: the decision refused before reaching it.</summary>
    None = 0,

    /// <summary>The subject's authority was read from the store for this decision.</summary>
    Store = 1,

    /// <summary>The subject's authority came from a principal-scoped cache entry within the declared freshness bound.</summary>
    Cache = 2,

    /// <summary>A service identity acting as itself, whose authority is exactly its declared scope.</summary>
    ServiceScope = 3
}

/// <summary>
/// A resource as its owning module resolved it, within the effective tenant: its tenant, type, identifier and owner. The
/// module resolves it first, through its tenant-filtered unit, and authorization then decides on it.
/// </summary>
public sealed record ResourceReference
{
    public const int MaxIdLength = 200;

    public ResourceReference(Guid tenantId, string type, string id, ActorReference? owner)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A resource belongs to one tenant.", nameof(tenantId));
        if (!PermissionNames.IsValid(type))
            throw new ArgumentException("A resource type is a stable identifier of lowercase ASCII segments, such as item.", nameof(type));
        if (!IsValidId(id)) throw new ArgumentException("A resource identifier is 1 to 200 printable ASCII characters without spaces.", nameof(id));
        TenantId = tenantId;
        Type = type;
        Id = id;
        Owner = owner;
    }

    public Guid TenantId { get; }
    public string Type { get; }
    public string Id { get; }
    public ActorReference? Owner { get; }

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= MaxIdLength } && id.All(character => character is >= '!' and <= '~');
}

/// <summary>What an operation asks authorization: the unit's context and principal, the permission, and any resource.</summary>
public sealed record AuthorizationRequest
{
    public AuthorizationRequest(TenantContext context, ExecutionPrincipal? principal, string permission)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Principal = principal;
        Permission = permission ?? throw new ArgumentNullException(nameof(permission));
    }

    public TenantContext Context { get; }
    public ExecutionPrincipal? Principal { get; }
    public string Permission { get; }

    /// <summary>The resolved resource the operation acts on, if it acts on one.</summary>
    public ResourceReference? Resource { get; init; }

    /// <summary>
    /// Requires the current profile whatever the permission declares, as running work does at each authority check after it
    /// started, so a revocation stops it at that check.
    /// </summary>
    public bool RequireCurrent { get; init; }
}

/// <summary>One server-side decision. Only a permitted decision allows the operation; every other outcome refuses it.</summary>
public sealed record AuthorizationDecision
{
    private AuthorizationDecision(bool permitted, string? code, string permission, DateTimeOffset decidedAt, PermissionFreshness freshness,
        AuthorizationSource source, Guid? authorityVersion, DateTimeOffset? authorityReadAt)
    {
        Permitted = permitted;
        Code = code;
        Permission = permission;
        DecidedAt = decidedAt;
        Freshness = freshness;
        Source = source;
        AuthorityVersion = authorityVersion;
        AuthorityReadAt = authorityReadAt;
    }

    public bool Permitted { get; }

    /// <summary>The stable refusal code, one of <see cref="AuthorizationCodes"/>; null when permitted.</summary>
    public string? Code { get; }

    public string Permission { get; }
    public DateTimeOffset DecidedAt { get; }

    /// <summary>The profile the decision applied: the permission's own, or current when the request required it.</summary>
    public PermissionFreshness Freshness { get; }

    public AuthorizationSource Source { get; }

    /// <summary>The tenant's authority version the decision read, or null when no tenant-level change has yet been made.</summary>
    public Guid? AuthorityVersion { get; }

    /// <summary>When the subject's authority behind the decision was read from the store.</summary>
    public DateTimeOffset? AuthorityReadAt { get; }

    /// <summary>True for a refusal caused by unavailable data or seams, which a retry may overcome.</summary>
    public bool IsTransient => Code == AuthorizationCodes.Unavailable;

    public static AuthorizationDecision Permit(string permission, DateTimeOffset decidedAt, PermissionFreshness freshness, AuthorizationSource source,
        Guid? authorityVersion, DateTimeOffset? authorityReadAt) =>
        new(true, null, permission, decidedAt, freshness, source, authorityVersion, authorityReadAt);

    public static AuthorizationDecision Deny(string code, string permission, DateTimeOffset decidedAt, PermissionFreshness freshness,
        AuthorizationSource source = AuthorizationSource.None, Guid? authorityVersion = null, DateTimeOffset? authorityReadAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new(false, code, permission, decidedAt, freshness, source, authorityVersion, authorityReadAt);
    }

    public override string ToString() => Permitted ? $"Permitted {Permission}" : $"Refused {Permission} with {Code}";
}

/// <summary>
/// Which resources of one type a subject may see for a permission, for building lists, counts, previews and exports
/// (TM-G02-021): the resources it owns and those it holds a current grant on, and none at all when the tenant-level
/// decision refused. <see cref="Apply"/> composes the filter into the query itself, so hidden rows affect neither content
/// nor totals.
/// </summary>
public sealed class ResourceVisibility
{
    public ResourceVisibility(AuthorizationDecision decision, string? ownerSubject, IEnumerable<string> grantedIds)
    {
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
        ArgumentNullException.ThrowIfNull(grantedIds);
        OwnerSubject = decision.Permitted ? ownerSubject : null;
        GrantedIds = decision.Permitted ? grantedIds.ToFrozenSet(StringComparer.Ordinal) : FrozenSet<string>.Empty;
    }

    public AuthorizationDecision Decision { get; }

    /// <summary>The user subject whose own resources are visible, or null when the subject owns none.</summary>
    public string? OwnerSubject { get; }

    /// <summary>The identifiers of resources the subject holds a current grant on.</summary>
    public IReadOnlySet<string> GrantedIds { get; }

    public bool Includes(string? owner, string id) => Decision.Permitted
        && ((OwnerSubject is not null && string.Equals(owner, OwnerSubject, StringComparison.Ordinal)) || (id is not null && GrantedIds.Contains(id)));

    /// <summary>
    /// Restricts the query to the visible resources, given the members that hold each row's owner subject and identifier.
    /// The values become query parameters; nothing is concatenated into query text.
    /// </summary>
    public IQueryable<T> Apply<T>(IQueryable<T> query, Expression<Func<T, string>> owner, Expression<Func<T, string>> id)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(id);
        var row = Expression.Parameter(typeof(T), "row");
        if (!Decision.Permitted) return query.Where(Expression.Lambda<Func<T, bool>>(Expression.Constant(false), row));
        var values = Expression.Constant(new Values(OwnerSubject, [.. GrantedIds]));
        Expression filter = Expression.Call(Expression.Property(values, nameof(Values.Ids)), typeof(List<string>).GetMethod(nameof(List<string>.Contains))!,
            Rebind.Body(id, row));
        if (OwnerSubject is not null)
            filter = Expression.OrElse(Expression.Equal(Rebind.Body(owner, row), Expression.Property(values, nameof(Values.Owner))), filter);
        return query.Where(Expression.Lambda<Func<T, bool>>(filter, row));
    }

    // Read through members of a constant, as a closure would be, so a query provider binds them as parameters.
    private sealed class Values(string? owner, List<string> ids)
    {
        public string? Owner { get; } = owner;
        public List<string> Ids { get; } = ids;
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        internal static Expression Body(LambdaExpression lambda, ParameterExpression row) => new Rebind(lambda.Parameters[0], row).Visit(lambda.Body);

        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

/// <summary>
/// The permissions a principal may exercise in a tenant: the effective subject's, within the execution's ceiling, as a
/// bootstrap or current-user response reports them. It describes; each operation still asks for its own decision.
/// </summary>
public sealed record EffectiveAuthority(ActorReference Subject, IReadOnlySet<string> Permissions, Guid? AuthorityVersion, DateTimeOffset ReadAt,
    AuthorizationSource Source);

/// <summary>
/// The one server-side authorization evaluator (FND-015, FND-016, ARC-016). Every HTTP endpoint, worker entry point and
/// asynchronous continuation asks it for the same decision; it reads no authority from the client and refuses whenever the
/// data it needs is unavailable.
/// </summary>
public interface IPermissionEvaluator
{
    ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken);

    /// <summary>The resources of one type the request's subject may see for the request's permission; the request names no resource.</summary>
    ValueTask<ResourceVisibility> VisibleAsync(AuthorizationRequest request, string resourceType, CancellationToken cancellationToken);

    /// <summary>The principal's effective permissions in the context's tenant, or null when it has none to describe.</summary>
    ValueTask<EffectiveAuthority?> DescribeAsync(TenantContext context, ExecutionPrincipal? principal, CancellationToken cancellationToken);
}

/// <summary>The kind of an authority change, as audit receives it.</summary>
public enum AuthorityChangeKind
{
    RoleDefined = 1,
    RoleRemoved = 2,
    RoleAssigned = 3,
    RoleUnassigned = 4,
    PermissionGranted = 5,
    PermissionRevoked = 6,
    ResourceGranted = 7,
    ResourceRevoked = 8,
    HostAuthorityGranted = 9,
    HostAuthorityRevoked = 10,
    TenantOwnerAdded = 11,
    TenantOwnerRemoved = 12
}

/// <summary>
/// One authority change as audit receives it before the change commits: who made it, in which tenant, to whom, what it
/// names and when. The correlation identifier is diagnostic linkage only.
/// </summary>
public sealed record AuthorityChangeRecord(Guid ChangeId, AuthorityChangeKind Kind, Guid? TenantId, ActorReference Actor, ActorReference? Member,
    string Target, IReadOnlyList<string> Permissions, DateTimeOffset At, string? CorrelationId);

/// <summary>
/// Records an authority change before it commits. A failure, or no audit at all, refuses the change, so no authority
/// changes without its record (TM-G02-051). Supplied by audit (SEC-07).
/// </summary>
public interface IAuthorityAudit
{
    ValueTask RecordAsync(AuthorityChangeRecord record, CancellationToken cancellationToken);
}
