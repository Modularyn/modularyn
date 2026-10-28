using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization;

/// <summary>
/// One subject's authority in one tenant as the store held it at one moment: whether it owns the tenant, the permissions
/// its direct grants and roles carry, the assignment ceiling its roles give it, and the tenant's authority version.
/// </summary>
internal sealed class AuthoritySnapshot
{
    internal AuthoritySnapshot(ActorReference subject, bool owner, IEnumerable<string> permissions, IEnumerable<string> ceiling, Guid? version,
        DateTimeOffset readAt)
    {
        Subject = subject;
        Owner = owner;
        Permissions = permissions.ToFrozenSet(StringComparer.Ordinal);
        Ceiling = ceiling.ToFrozenSet(StringComparer.Ordinal);
        Version = version;
        ReadAt = readAt;
    }

    public ActorReference Subject { get; }

    /// <summary>A tenant owner holds every declared permission, with an unbounded assignment ceiling.</summary>
    public bool Owner { get; }

    public IReadOnlySet<string> Permissions { get; }
    public IReadOnlySet<string> Ceiling { get; }
    public Guid? Version { get; }
    public DateTimeOffset ReadAt { get; }

    /// <summary>Whether the subject holds a declared permission. Callers pass only definitions the composition declares.</summary>
    public bool Holds(PermissionDefinition permission) => Owner || Permissions.Contains(permission.Name);

    /// <summary>
    /// Whether the subject may grant, or manage, a declared permission: within its assignment ceiling and held by itself, so
    /// that no one grants beyond what they hold.
    /// </summary>
    public bool MayAssign(PermissionDefinition permission) => Owner || (Ceiling.Contains(permission.Name) && Permissions.Contains(permission.Name));

    internal static IEnumerable<string> Split(string names) => names.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    internal static string Join(IEnumerable<string> names) => string.Join(' ', names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}

/// <summary>Where the evaluator reads authority. The PostgreSQL source is the only production one; unit tests supply their own.</summary>
internal interface IAuthoritySource
{
    Task<AuthoritySnapshot> ReadAsync(TenantContext context, ActorReference subject, CancellationToken cancellationToken);

    Task<bool> HasResourceGrantAsync(TenantContext context, ActorReference subject, ResourceReference resource, string permission, DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GrantedResourcesAsync(TenantContext context, ActorReference subject, string resourceType, string permission,
        DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Reads authority from the authorization module's tables, in units bound to the request's validated tenant context, so the
/// tenant filters confine every read to that tenant. A tenant's owners are host-scoped rows, read for the context's tenant.
/// </summary>
internal sealed class PostgreSqlAuthoritySource(PostgreSqlModule module, AuthorizationClock clock) : IAuthoritySource
{
    // Every tenant-level change rotates the tenant's version, so equal versions before and after mean the reads in between
    // saw one state. A tenant that keeps changing faster than it can be read is reported unavailable rather than half-read.
    private const int Attempts = 4;

    public async Task<AuthoritySnapshot> ReadAsync(TenantContext context, ActorReference subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subject);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        var db = (AuthorizationContext)work.Context;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var before = await VersionAsync(db, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadCoreAsync(db, context.TenantId!.Value, subject, before, clock.Now, cancellationToken).ConfigureAwait(false);
            if (await VersionAsync(db, cancellationToken).ConfigureAwait(false) == before) return snapshot;
        }
        throw new InvalidOperationException("The tenant's authority changed throughout the read.");
    }

    /// <summary>Reads within an open unit of the tenant; administration calls it after it has locked the tenant's authority.</summary>
    internal static async Task<AuthoritySnapshot> ReadCoreAsync(AuthorizationContext db, Guid tenant, ActorReference subject, Guid? version, DateTimeOffset readAt,
        CancellationToken cancellationToken)
    {
        var kind = (int)subject.Kind;
        var member = subject.Subject;
        var owner = await db.TenantOwners.AsNoTracking().AnyAsync(row => row.TenantId == tenant && row.OwnerKind == kind && row.Owner == member,
            cancellationToken).ConfigureAwait(false);
        var direct = await db.PermissionGrants.AsNoTracking().Where(row => row.MemberKind == kind && row.Member == member)
            .Select(row => row.Permission).ToListAsync(cancellationToken).ConfigureAwait(false);
        var roles = await (from membership in db.RoleMembers.AsNoTracking()
                           where membership.MemberKind == kind && membership.Member == member
                           join role in db.Roles.AsNoTracking() on new { membership.TenantId, Name = membership.Role } equals new { role.TenantId, role.Name }
                           select new { role.Permissions, role.Ceiling }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new AuthoritySnapshot(subject, owner, direct.Concat(roles.SelectMany(role => AuthoritySnapshot.Split(role.Permissions))),
            roles.SelectMany(role => AuthoritySnapshot.Split(role.Ceiling)), version, readAt);
    }

    internal static Task<Guid?> VersionAsync(AuthorizationContext db, CancellationToken cancellationToken) =>
        db.TenantAuthority.AsNoTracking().Select(row => (Guid?)row.Version).SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> HasResourceGrantAsync(TenantContext context, ActorReference subject, ResourceReference resource, string permission, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        var kind = (int)subject.Kind;
        return await ((AuthorizationContext)work.Context).ResourceGrants.AsNoTracking().AnyAsync(row => row.ResourceType == resource.Type
            && row.ResourceId == resource.Id && row.MemberKind == kind && row.Member == subject.Subject && row.Permission == permission
            && (row.ExpiresAt == null || row.ExpiresAt > now), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GrantedResourcesAsync(TenantContext context, ActorReference subject, string resourceType, string permission,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        var kind = (int)subject.Kind;
        return await ((AuthorizationContext)work.Context).ResourceGrants.AsNoTracking().Where(row => row.ResourceType == resourceType
                && row.MemberKind == kind && row.Member == subject.Subject && row.Permission == permission && (row.ExpiresAt == null || row.ExpiresAt > now))
            .Select(row => row.ResourceId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The cache of bounded authority. The production cache is SEC-04's <see cref="TenantCache"/>, under a principal-scoped
/// entry; tests replace it to show the declared behavior when the cache fails.
/// </summary>
internal interface IAuthorityCache
{
    Task<AuthoritySnapshot> GetOrCreateAsync(TenantContext context, ActorReference subject, TimeSpan bound,
        Func<CancellationToken, Task<AuthoritySnapshot>> fill, CancellationToken cancellationToken);

    void InvalidateTenant(Guid tenantId);

    void InvalidateHostPrincipal(string operationId, ActorReference actor);
}

/// <summary>
/// Whether the authority cache can be trusted to have seen every committed change. When an invalidation fails, as during
/// a cache outage, an entry built before the change could outlive it, so the evaluator decides from the store until the
/// declared bound has passed since the failure; by then every such entry has expired, since each expires at the bound
/// measured from the start of its fill.
/// </summary>
internal sealed class AuthorityCacheHealth(TimeProvider time, TimeSpan bound)
{
    private long failedAt;
    private int failed;

    internal void InvalidationFailed()
    {
        Interlocked.Exchange(ref failedAt, time.GetTimestamp());
        Volatile.Write(ref failed, 1);
    }

    internal bool Trusted => Volatile.Read(ref failed) == 0 || time.GetElapsedTime(Interlocked.Read(ref failedAt)) >= bound;
}

/// <summary>
/// Bounded authority in <see cref="TenantCache"/>: keyed by the tenant, the context's principal and the effective subject,
/// so no principal receives another's authority; expiring at the declared freshness bound; and made unreachable at once
/// by every change to the tenant's authority in this process.
/// </summary>
internal sealed class TenantAuthorityCache(TenantCache cache) : IAuthorityCache
{
    internal const string EntryName = "authority";

    internal static TenantCacheEntry Entry(ActorReference subject, TimeSpan bound) => new(AuthorizationModule.ModuleId, AuthorizationModule.CacheSchemaVersion,
        EntryName, bound, principalScoped: true, subject.Kind == ActorKind.User ? "user" : "service", subject.Subject);

    public Task<AuthoritySnapshot> GetOrCreateAsync(TenantContext context, ActorReference subject, TimeSpan bound,
        Func<CancellationToken, Task<AuthoritySnapshot>> fill, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(context, Entry(subject, bound), fill, cancellationToken);

    public void InvalidateTenant(Guid tenantId) => cache.InvalidateTenant(tenantId);

    public void InvalidateHostPrincipal(string operationId, ActorReference actor) => cache.InvalidateHostPrincipal(operationId, actor);
}
