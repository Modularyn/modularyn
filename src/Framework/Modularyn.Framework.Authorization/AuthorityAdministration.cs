using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization;

/// <summary>A refused authority change, with a stable code from <see cref="AuthorizationCodes"/> and no grant detail.</summary>
public sealed class AuthorityChangeException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Permission administration within one tenant (MOD-026, TM-G02-022). Every change runs in a tenant unit of the actor's
/// own validated context, from the actor's own current interactive principal, and in one transaction it locks the
/// tenant's authority, reads the actor's authority as it stands under that lock, checks the change against it, records
/// the change in audit and commits. No change grants, or manages, a permission outside the actor's assignment ceiling or
/// one the actor does not hold, and concurrent changes in a tenant are serialized, so none can exceed a ceiling that
/// another change reduced. Authority records are never written in an owner-bound unit.
/// </summary>
public sealed partial class AuthorityAdministration
{
    // Changes in one tenant serialize on its authority row; one that loses the race reads again and retries a few times.
    private const int Attempts = 4;
    private readonly PostgreSqlModule module;
    private readonly AuthorizationRegistry registry;
    private readonly IAuthorityCache cache;
    private readonly AuthorityCacheHealth health;
    private readonly AuthorizationClock clock;
    private readonly IServiceProvider services;
    private readonly ILogger logger;

    internal AuthorityAdministration(PostgreSqlModule module, AuthorizationRegistry registry, IAuthorityCache cache, AuthorityCacheHealth health,
        AuthorizationClock clock, IServiceProvider services, ILogger<AuthorityAdministration> logger)
    {
        AuthorizationModule.Require(module);
        this.module = module;
        this.registry = registry;
        this.cache = cache;
        this.health = health;
        this.clock = clock;
        this.services = services;
        this.logger = logger;
    }

    /// <summary>
    /// Creates or replaces a role: the permissions its members hold and the ceiling within which they may assign. Every
    /// permission the role carries, before and after, must be within the actor's ceiling. Each change returns false when it
    /// would change nothing, and is then not made.
    /// </summary>
    public Task<bool> DefineRoleAsync(TenantContext context, ExecutionPrincipal principal, string role, IEnumerable<string> permissions,
        IEnumerable<string> ceiling, CancellationToken cancellationToken = default)
    {
        RequireRoleName(role);
        var held = Definitions(permissions, nameof(permissions));
        var assignable = Definitions(ceiling, nameof(ceiling));
        return ChangeAsync(context, principal, AuthorityChangeKind.RoleDefined, null, "role:" + role, Names(held.Concat(assignable)), async change =>
        {
            change.RequireManage();
            var existing = await change.Db.Roles.SingleOrDefaultAsync(row => row.Name == role, cancellationToken).ConfigureAwait(false);
            change.RequireAssignable(held.Concat(assignable).Concat(existing is null ? [] : RoleDefinitions(existing)));
            var permissionText = AuthoritySnapshot.Join(Names(held));
            var ceilingText = AuthoritySnapshot.Join(Names(assignable));
            if (existing is null) change.Db.Add(new RoleRow { Name = role, Permissions = permissionText, Ceiling = ceilingText, UpdatedAt = change.Now });
            else if (existing.Permissions == permissionText && existing.Ceiling == ceilingText) return false;
            else (existing.Permissions, existing.Ceiling, existing.UpdatedAt) = (permissionText, ceilingText, change.Now);
            return true;
        }, cancellationToken);
    }

    /// <summary>Removes a role and every assignment of it. Everything the role carries must be within the actor's ceiling.</summary>
    public Task<bool> RemoveRoleAsync(TenantContext context, ExecutionPrincipal principal, string role, CancellationToken cancellationToken = default)
    {
        RequireRoleName(role);
        return ChangeAsync(context, principal, AuthorityChangeKind.RoleRemoved, null, "role:" + role, [], async change =>
        {
            change.RequireManage();
            var existing = await change.Db.Roles.SingleOrDefaultAsync(row => row.Name == role, cancellationToken).ConfigureAwait(false)
                ?? throw Refuse(AuthorizationCodes.ChangeInvalid, "The role does not exist in the tenant.");
            change.RequireAssignable(RoleDefinitions(existing));
            change.Db.RemoveRange(await change.Db.RoleMembers.Where(row => row.Role == role).ToListAsync(cancellationToken).ConfigureAwait(false));
            change.Db.Remove(existing);
            return true;
        }, cancellationToken);
    }

    /// <summary>Assigns a role to a current member of the tenant. Everything the role carries must be within the actor's ceiling.</summary>
    public Task<bool> AssignRoleAsync(TenantContext context, ExecutionPrincipal principal, ActorReference member, string role,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        RequireRoleName(role);
        return ChangeAsync(context, principal, AuthorityChangeKind.RoleAssigned, member, "role:" + role, [], async change =>
        {
            change.RequireManage();
            var existing = await change.Db.Roles.AsNoTracking().SingleOrDefaultAsync(row => row.Name == role, cancellationToken).ConfigureAwait(false)
                ?? throw Refuse(AuthorizationCodes.ChangeInvalid, "The role does not exist in the tenant.");
            change.RequireAssignable(RoleDefinitions(existing));
            await change.RequireMemberAsync(member, cancellationToken).ConfigureAwait(false);
            var kind = (int)member.Kind;
            if (await change.Db.RoleMembers.AnyAsync(row => row.Role == role && row.MemberKind == kind && row.Member == member.Subject, cancellationToken)
                    .ConfigureAwait(false)) return false;
            change.Db.Add(new RoleMemberRow
            {
                Role = role, MemberKind = kind, Member = member.Subject, AddedAt = change.Now, AddedByKind = (int)change.Actor.Kind, AddedBy = change.Actor.Subject
            });
            return true;
        }, cancellationToken);
    }

    /// <summary>Removes a role from a member. Everything the role carries must be within the actor's ceiling.</summary>
    public Task<bool> UnassignRoleAsync(TenantContext context, ExecutionPrincipal principal, ActorReference member, string role,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        RequireRoleName(role);
        return ChangeAsync(context, principal, AuthorityChangeKind.RoleUnassigned, member, "role:" + role, [], async change =>
        {
            change.RequireManage();
            var existing = await change.Db.Roles.AsNoTracking().SingleOrDefaultAsync(row => row.Name == role, cancellationToken).ConfigureAwait(false)
                ?? throw Refuse(AuthorizationCodes.ChangeInvalid, "The role does not exist in the tenant.");
            change.RequireAssignable(RoleDefinitions(existing));
            var kind = (int)member.Kind;
            var row = await change.Db.RoleMembers.SingleOrDefaultAsync(row => row.Role == role && row.MemberKind == kind && row.Member == member.Subject,
                cancellationToken).ConfigureAwait(false);
            if (row is null) return false;
            change.Db.Remove(row);
            return true;
        }, cancellationToken);
    }

    /// <summary>Grants a declared permission directly to a current member of the tenant, within the actor's ceiling.</summary>
    public Task<bool> GrantPermissionAsync(TenantContext context, ExecutionPrincipal principal, ActorReference member, string permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        var definition = Definition(permission);
        return ChangeAsync(context, principal, AuthorityChangeKind.PermissionGranted, member, "permission", [definition.Name], async change =>
        {
            change.RequireManage();
            change.RequireAssignable([definition]);
            await change.RequireMemberAsync(member, cancellationToken).ConfigureAwait(false);
            var kind = (int)member.Kind;
            if (await change.Db.PermissionGrants.AnyAsync(row => row.MemberKind == kind && row.Member == member.Subject && row.Permission == definition.Name,
                    cancellationToken).ConfigureAwait(false)) return false;
            change.Db.Add(new PermissionGrantRow
            {
                MemberKind = kind, Member = member.Subject, Permission = definition.Name, GrantedAt = change.Now, GrantedByKind = (int)change.Actor.Kind,
                GrantedBy = change.Actor.Subject
            });
            return true;
        }, cancellationToken);
    }

    /// <summary>Revokes a member's direct grant of a declared permission, which must be within the actor's ceiling.</summary>
    public Task<bool> RevokePermissionAsync(TenantContext context, ExecutionPrincipal principal, ActorReference member, string permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        var definition = Definition(permission);
        return ChangeAsync(context, principal, AuthorityChangeKind.PermissionRevoked, member, "permission", [definition.Name], async change =>
        {
            change.RequireManage();
            change.RequireAssignable([definition]);
            var kind = (int)member.Kind;
            var row = await change.Db.PermissionGrants.SingleOrDefaultAsync(row => row.MemberKind == kind && row.Member == member.Subject
                && row.Permission == definition.Name, cancellationToken).ConfigureAwait(false);
            if (row is null) return false;
            change.Db.Remove(row);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Grants a current member a permission on one resource of the tenant, optionally until an expiry. The resource's owner
    /// may share it when it holds the permission and the share permission; an administrator may grant it within its ceiling.
    /// </summary>
    public Task<bool> GrantResourceAsync(TenantContext context, ExecutionPrincipal principal, ActorReference member, ResourceReference resource, string permission,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(resource);
        var definition = Definition(permission);
        // A grant ends at a whole microsecond at or before the requested instant, as the database keeps it.
        var until = expiresAt is { } requested ? AuthorizationClock.Floor(requested) : (DateTimeOffset?)null;
        return ChangeAsync(context, principal, AuthorityChangeKind.ResourceGranted, member, ResourceTarget(resource), [definition.Name], async change =>
        {
            change.RequireSharing(resource, definition);
            if (until <= change.Now) throw Refuse(AuthorizationCodes.ChangeInvalid, "A resource grant expires after it is made.");
            await change.RequireMemberAsync(member, cancellationToken).ConfigureAwait(false);
            var kind = (int)member.Kind;
            var row = await change.Db.ResourceGrants.SingleOrDefaultAsync(row => row.ResourceType == resource.Type && row.ResourceId == resource.Id
                && row.MemberKind == kind && row.Member == member.Subject && row.Permission == definition.Name, cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                if (row.ExpiresAt == until) return false;
                row.ExpiresAt = until;
                return true;
            }
            change.Db.Add(new ResourceGrantRow
            {
                ResourceType = resource.Type, ResourceId = resource.Id, MemberKind = kind, Member = member.Subject, Permission = definition.Name, ExpiresAt = until,
                GrantedAt = change.Now, GrantedByKind = (int)change.Actor.Kind, GrantedBy = change.Actor.Subject
            });
            return true;
        }, cancellationToken);
    }

    /// <summary>Revokes a member's grant on one resource; whoever could have made the grant may revoke it. It stops at once.</summary>
    public Task<bool> RevokeResourceAsync(TenantContext context, ExecutionPrincipal principal, ActorReference member, ResourceReference resource, string permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(resource);
        var definition = Definition(permission);
        return ChangeAsync(context, principal, AuthorityChangeKind.ResourceRevoked, member, ResourceTarget(resource), [definition.Name], async change =>
        {
            change.RequireSharing(resource, definition);
            var kind = (int)member.Kind;
            var row = await change.Db.ResourceGrants.SingleOrDefaultAsync(row => row.ResourceType == resource.Type && row.ResourceId == resource.Id
                && row.MemberKind == kind && row.Member == member.Subject && row.Permission == definition.Name, cancellationToken).ConfigureAwait(false);
            if (row is null) return false;
            change.Db.Remove(row);
            return true;
        }, cancellationToken);
    }

    private async Task<bool> ChangeAsync(TenantContext context, ExecutionPrincipal principal, AuthorityChangeKind kind, ActorReference? member, string target,
        IReadOnlyList<string> permissions, Func<Change, Task<bool>> apply, CancellationToken cancellationToken)
    {
        var actor = RequireAdministrator(context, principal);
        var tenant = context.TenantId!.Value;
        for (var attempt = 1; ; attempt++)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
            var db = (AuthorizationContext)work.Context;
            var now = clock.Now;
            try
            {
                await LockAsync(db, work, now, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException) when (attempt < Attempts)
            {
                // Another change locked and rotated the version first, or created the tenant's row; read again.
                continue;
            }
            catch (DbUpdateException)
            {
                throw Refuse(AuthorizationCodes.ChangeConflict, "Concurrent authority changes kept committing first; read again and retry.");
            }
            // From here the tenant's authority is locked until this transaction ends, so what the actor holds cannot change under the decision.
            await RequireCurrentAsync(context, principal, cancellationToken).ConfigureAwait(false);
            var grantor = await PostgreSqlAuthoritySource.ReadCoreAsync(db, tenant, actor, null, now, cancellationToken).ConfigureAwait(false);
            // A change that changes nothing is not made: its transaction rolls back, version and all, and nothing is recorded.
            if (!await apply(new Change(this, db, context, actor, grantor, now)).ConfigureAwait(false)) return false;
            await AuditAsync(new AuthorityChangeRecord(Guid.NewGuid(), kind, tenant, actor, member, target, permissions, now,
                Activity.Current?.TraceId.ToHexString()), cancellationToken).ConfigureAwait(false);
            work.OnCompleted(result =>
            {
                if (result.State == UnitOfWorkState.Committed) Invalidate(() => cache.InvalidateTenant(tenant));
            });
            UnitOfWorkResult outcome;
            try { outcome = await work.CompleteAsync(cancellationToken).ConfigureAwait(false); }
            catch (DbUpdateException) when (attempt < Attempts) { continue; }
            if (outcome.State == UnitOfWorkState.Committed) return true;
            if (outcome.Conflicts.Count > 0 && attempt < Attempts) continue;
            throw Refuse(AuthorizationCodes.ChangeConflict, "Concurrent authority changes kept committing first; read again and retry.");
        }
    }

    /// <summary>Locks the tenant's authority row by writing it first, creating it on the tenant's first change, and rotates its version.</summary>
    private static async Task LockAsync(AuthorizationContext db, EfCoreUnitOfWork work, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = await db.TenantAuthority.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = new TenantAuthorityRow { ChangedAt = now };
            db.Add(state);
        }
        else
        {
            state.ChangedAt = now;
            db.Entry(state).Property(row => row.ChangedAt).IsModified = true;
        }
        await work.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ActorReference RequireAdministrator(TenantContext context, ExecutionPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);
        if (!context.IsActive || context.Scope != TenantScope.Tenant)
            throw Refuse(AuthorizationCodes.ChangeNotPermitted, "Authority changes run in an active tenant context.");
        // Authority is changed only by its holder acting as itself: never under a delegation and never by a service identity.
        if (principal.IsDelegated || principal.ServiceScope is not null || principal.SessionId is null || principal.Actor.Kind != IdentityKind.User
            || context.Actor.Kind != ActorKind.User || context.Actor.Subject != principal.Actor.Id)
            throw Refuse(AuthorizationCodes.ChangeNotPermitted, "Authority changes need the actor's own interactive principal in its own tenant context.");
        return context.Actor;
    }

    // An authority change is security-sensitive: the actor's session and membership are rechecked now, not trusted from the request's start.
    private async Task RequireCurrentAsync(TenantContext context, ExecutionPrincipal principal, CancellationToken cancellationToken)
    {
        if (services.GetService<IPrincipalFreshness>() is not { } freshness || services.GetService<ITenantMembership>() is not { } membership)
            throw Refuse(AuthorizationCodes.Unavailable, "The principal cannot be rechecked, so no authority change is made.");
        if (await freshness.RecheckAsync(principal, cancellationToken).ConfigureAwait(false) != FreshnessStatus.Current
            || !await membership.IsMemberAsync(context.Actor, context.TenantId!.Value, cancellationToken).ConfigureAwait(false))
            throw Refuse(AuthorizationCodes.ChangeNotPermitted, "The actor's principal is no longer current.");
    }

    internal async Task AuditAsync(AuthorityChangeRecord record, CancellationToken cancellationToken)
    {
        if (services.GetService<IAuthorityAudit>() is not { } audit)
            throw Refuse(AuthorizationCodes.AuditUnavailable, "No audit is registered, so no authority change is made.");
        try { await audit.RecordAsync(record, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Refuse(AuthorizationCodes.AuditUnavailable, "The change could not be recorded in audit, so it was not made.");
        }
    }

    /// <summary>
    /// A committed change is already authoritative. A cache that cannot be told is distrusted instead, so decisions come
    /// from the store until every entry it might hold from before the change has expired.
    /// </summary>
    internal void Invalidate(Action invalidate)
    {
        try { invalidate(); }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            health.InvalidationFailed();
            InvalidationFailed(logger);
        }
    }

    private PermissionDefinition Definition(string permission) => registry.FindPermission(permission)
        ?? throw Refuse(AuthorizationCodes.ChangeUndefined, "No composed module declares the permission, so it cannot be granted.");

    private PermissionDefinition[] Definitions(IEnumerable<string> permissions, string parameter)
    {
        ArgumentNullException.ThrowIfNull(permissions, parameter);
        return permissions.Distinct(StringComparer.Ordinal).Select(Definition).ToArray();
    }

    // A stored permission that the composition no longer declares confers nothing, and managing it needs no ceiling.
    private IEnumerable<PermissionDefinition> RoleDefinitions(RoleRow role) => AuthoritySnapshot.Split(role.Permissions).Concat(AuthoritySnapshot.Split(role.Ceiling))
        .Select(registry.FindPermission).OfType<PermissionDefinition>();

    private static string[] Names(IEnumerable<PermissionDefinition> definitions) =>
        definitions.Select(definition => definition.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string ResourceTarget(ResourceReference resource) => "resource:" + resource.Type + "/" + resource.Id;

    private static void RequireRoleName(string role)
    {
        if (!PermissionNames.IsValid(role))
            throw new ArgumentException("A role name is a stable identifier of lowercase ASCII segments, such as item-editors.", nameof(role));
    }

    internal static AuthorityChangeException Refuse(string code, string message) => new(code, message);

    [LoggerMessage(EventId = 8302, Level = LogLevel.Warning,
        Message = "An authority change committed but the authority cache could not be invalidated; decisions read the store until its entries expire.")]
    private static partial void InvalidationFailed(ILogger logger);

    /// <summary>One change in progress: its unit, the actor and the actor's authority as it stands under the tenant's lock.</summary>
    private sealed class Change(AuthorityAdministration owner, AuthorizationContext db, TenantContext context, ActorReference actor, AuthoritySnapshot grantor,
        DateTimeOffset now)
    {
        internal AuthorizationContext Db { get; } = db;
        internal ActorReference Actor { get; } = actor;
        internal DateTimeOffset Now { get; } = now;

        internal void RequireManage()
        {
            if (owner.registry.FindPermission(AuthorizationModule.ManagePermission) is not { } manage || !grantor.Holds(manage))
                throw Refuse(AuthorizationCodes.ChangeNotPermitted, "Authority changes need the permission-administration permission in the tenant.");
        }

        internal void RequireAssignable(IEnumerable<PermissionDefinition> definitions)
        {
            if (definitions.Any(definition => !grantor.MayAssign(definition)))
                throw Refuse(AuthorizationCodes.BeyondCeiling, "The change reaches beyond the actor's assignment ceiling or beyond what the actor holds.");
        }

        /// <summary>
        /// The resource's owner may share it with the share permission, holding the permission it shares; an administrator may
        /// grant on any resource of the tenant within its ceiling.
        /// </summary>
        internal void RequireSharing(ResourceReference resource, PermissionDefinition definition)
        {
            if (resource.TenantId != context.TenantId) throw Refuse(AuthorizationCodes.ChangeInvalid, "The resource is not in the tenant.");
            var share = owner.registry.FindPermission(AuthorizationModule.SharePermission);
            var manage = owner.registry.FindPermission(AuthorizationModule.ManagePermission);
            var asOwner = resource.Owner == Actor && share is not null && grantor.Holds(share) && grantor.Holds(definition);
            var asAdministrator = manage is not null && grantor.Holds(manage) && grantor.MayAssign(definition);
            if (!asOwner && !asAdministrator)
                throw Refuse(AuthorizationCodes.BeyondCeiling, "Only the resource's owner with the share permission, or an administrator within its ceiling, grants on it.");
        }

        /// <summary>Grants go only to current members of the tenant; a service identity is never granted anything.</summary>
        internal async Task RequireMemberAsync(ActorReference member, CancellationToken cancellationToken)
        {
            if (member.Kind != ActorKind.User) throw Refuse(AuthorizationCodes.ChangeInvalid, "Only users are granted authority; a service identity has its declared scope.");
            if (owner.services.GetService<ITenantMembership>() is not { } membership)
                throw Refuse(AuthorizationCodes.Unavailable, "Membership cannot be checked, so no authority change is made.");
            if (!await membership.IsMemberAsync(member, context.TenantId!.Value, cancellationToken).ConfigureAwait(false))
                throw Refuse(AuthorizationCodes.ChangeInvalid, "Authority is granted only to a current member of the tenant.");
        }
    }
}
