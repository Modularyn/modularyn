using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
internal sealed class PostgreSqlAuthoritySource(PostgreSqlModule module, AuthorizationClock clock, IServiceProvider services) : IAuthoritySource
{
    // Every tenant-level change rotates the tenant's version, so equal versions before and after mean the reads in between
    // saw one state. A tenant that keeps changing faster than it can be read is reported unavailable rather than half-read.
    private const int Attempts = 4;

    public async Task<AuthoritySnapshot> ReadAsync(TenantContext context, ActorReference subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subject);
        var since = await MemberSinceAsync(services, subject, context.TenantId!.Value, cancellationToken).ConfigureAwait(false);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        var db = (AuthorizationContext)work.Context;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var before = await VersionAsync(db, cancellationToken).ConfigureAwait(false);
            var snapshot = await ReadCoreAsync(db, context.TenantId!.Value, subject, before, clock.Now, since, cancellationToken).ConfigureAwait(false);
            if (await VersionAsync(db, cancellationToken).ConfigureAwait(false) == before) return snapshot;
        }
        throw new InvalidOperationException("The tenant's authority changed throughout the read.");
    }

    /// <summary>
    /// The instant from which a subject's tenant-level grants count: the start of its current membership of the tenant, so
    /// removing a membership ends the grants made during it, and adding the member again revives none of them. Null when
    /// the subject is not a member now, so nothing counts. A service identity holds no grants and is never a member.
    /// </summary>
    internal static async Task<DateTimeOffset?> MemberSinceAsync(IServiceProvider services, ActorReference subject, Guid tenant, CancellationToken cancellationToken)
    {
        if (subject.Kind != ActorKind.User) return DateTimeOffset.MinValue;
        var membership = services.GetService<ITenantMembership>() ?? throw new InvalidOperationException("Membership cannot be read, so no authority is.");
        return await membership.MemberSinceAsync(subject, tenant, cancellationToken).ConfigureAwait(false) is { } since ? AuthorizationClock.Floor(since) : null;
    }

    /// <summary>
    /// Reads within an open unit of the tenant; administration calls it after it has locked the tenant's authority. Only the
    /// grants and role assignments made since <paramref name="since"/>, the subject's current membership's start, count.
    /// </summary>
    internal static async Task<AuthoritySnapshot> ReadCoreAsync(AuthorizationContext db, Guid tenant, ActorReference subject, Guid? version, DateTimeOffset readAt,
        DateTimeOffset? since, CancellationToken cancellationToken)
    {
        if (since is not { } start) return new AuthoritySnapshot(subject, false, [], [], version, readAt);
        var kind = (int)subject.Kind;
        var member = subject.Subject;
        var owner = await db.TenantOwners.AsNoTracking().AnyAsync(row => row.TenantId == tenant && row.OwnerKind == kind && row.Owner == member,
            cancellationToken).ConfigureAwait(false);
        var grants = db.PermissionGrants.AsNoTracking().Where(row => row.MemberKind == kind && row.Member == member);
        var assignments = db.RoleMembers.AsNoTracking().Where(row => row.MemberKind == kind && row.Member == member);
        if (start != DateTimeOffset.MinValue)
        {
            grants = grants.Where(row => row.GrantedAt >= start);
            assignments = assignments.Where(row => row.AddedAt >= start);
        }
        var direct = await grants.Select(row => row.Permission).ToListAsync(cancellationToken).ConfigureAwait(false);
        var roles = await (from membership in assignments
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
        if (await MemberSinceAsync(services, subject, context.TenantId!.Value, cancellationToken).ConfigureAwait(false) is not { } since) return false;
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        var kind = (int)subject.Kind;
        return await Current(((AuthorizationContext)work.Context).ResourceGrants.AsNoTracking(), since).AnyAsync(row => row.ResourceType == resource.Type
            && row.ResourceId == resource.Id && row.MemberKind == kind && row.Member == subject.Subject && row.Permission == permission
            && (row.ExpiresAt == null || row.ExpiresAt > now), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GrantedResourcesAsync(TenantContext context, ActorReference subject, string resourceType, string permission,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await MemberSinceAsync(services, subject, context.TenantId!.Value, cancellationToken).ConfigureAwait(false) is not { } since) return [];
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        var kind = (int)subject.Kind;
        return await Current(((AuthorizationContext)work.Context).ResourceGrants.AsNoTracking(), since).Where(row => row.ResourceType == resourceType
                && row.MemberKind == kind && row.Member == subject.Subject && row.Permission == permission && (row.ExpiresAt == null || row.ExpiresAt > now))
            .Select(row => row.ResourceId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The resource grants made since the subject's current membership began; those of an earlier membership no longer count.</summary>
    private static IQueryable<ResourceGrantRow> Current(IQueryable<ResourceGrantRow> grants, DateTimeOffset since) =>
        since == DateTimeOffset.MinValue ? grants : grants.Where(row => row.GrantedAt >= since);
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
