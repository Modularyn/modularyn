using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization;

/// <summary>
/// Tenancy's host-authority seam (SEC-04) under the elevated policy: an actor holds host authority for an operation only
/// when a composed module declares the operation, and the deployment's standing operators or a stored host-authority
/// grant names the actor for it. The answer carries the operation's declared reach, so the resulting host scope reaches
/// only those entities. Stored grants are read afresh for every host scope, so a revocation applies to the next one.
/// </summary>
internal sealed class HostAuthority : IHostAuthority
{
    private readonly AuthorizationRegistry registry;
    private readonly PostgreSqlModule module;
    private readonly FrozenSet<(ActorKind Kind, string Subject, string Operation)> standing;

    public HostAuthority(AuthorizationRegistry registry, IOptions<AuthorizationSecurityOptions> options, PostgreSqlModule module)
    {
        this.registry = registry;
        this.module = module;
        standing = options.Value.HostOperators.SelectMany(entry => entry.Operations.Select(operation => (entry.Kind, entry.Subject, operation))).ToFrozenSet();
    }

    public async ValueTask<HostOperation?> AuthorizeAsync(ActorReference actor, string operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (registry.FindHostOperation(operationId) is not { } definition) return null;
        if (standing.Contains((actor.Kind, actor.Subject, operationId))) return definition.Operation;
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        var kind = (int)actor.Kind;
        return await ((AuthorizationContext)work.Context).HostAuthorityGrants.AsNoTracking()
            .AnyAsync(row => row.ActorKind == kind && row.Actor == actor.Subject && row.OperationId == operationId, cancellationToken).ConfigureAwait(false)
            ? definition.Operation : null;
    }
}

/// <summary>
/// Host-level authority records, each changed only in an audited host scope granted for its own host operation, never
/// in an owner-bound unit: host-authority grants, under <see cref="AuthorizationModule.HostAuthorityOperation"/>, and a
/// tenant's owners, the root of authority within the tenant, under <see cref="AuthorizationModule.TenantOwnersOperation"/>.
/// No actor grants host authority it does not hold itself, and every change is recorded in audit before it commits.
/// </summary>
public sealed class HostAuthorityAdministration
{
    private readonly PostgreSqlModule module;
    private readonly AuthorizationRegistry registry;
    private readonly IHostAuthority authority;
    private readonly ITenantDirectory directory;
    private readonly IAuthorityCache cache;
    private readonly AuthorizationClock clock;
    private readonly AuthorityAdministration changes;

    internal HostAuthorityAdministration(PostgreSqlModule module, AuthorizationRegistry registry, IHostAuthority authority, ITenantDirectory directory,
        IAuthorityCache cache, AuthorizationClock clock, AuthorityAdministration changes)
    {
        AuthorizationModule.Require(module);
        this.module = module;
        this.registry = registry;
        this.authority = authority;
        this.directory = directory;
        this.cache = cache;
        this.clock = clock;
        this.changes = changes;
    }

    /// <summary>Grants an actor host authority for a declared operation that the granting actor holds itself.</summary>
    public Task<bool> GrantAsync(TenantContext host, ActorReference actor, string operationId, CancellationToken cancellationToken = default) =>
        ChangeGrantAsync(host, actor, operationId, grant: true, cancellationToken);

    /// <summary>Revokes an actor's stored host authority for an operation that the revoking actor holds itself. It applies to the next host scope.</summary>
    public Task<bool> RevokeAsync(TenantContext host, ActorReference actor, string operationId, CancellationToken cancellationToken = default) =>
        ChangeGrantAsync(host, actor, operationId, grant: false, cancellationToken);

    /// <summary>Names a user as an owner of an active tenant: it holds every declared permission there, with an unbounded ceiling.</summary>
    public Task<bool> AddTenantOwnerAsync(TenantContext host, Guid tenantId, ActorReference owner, CancellationToken cancellationToken = default) =>
        ChangeOwnerAsync(host, tenantId, owner, add: true, cancellationToken);

    public Task<bool> RemoveTenantOwnerAsync(TenantContext host, Guid tenantId, ActorReference owner, CancellationToken cancellationToken = default) =>
        ChangeOwnerAsync(host, tenantId, owner, add: false, cancellationToken);

    private async Task<bool> ChangeGrantAsync(TenantContext host, ActorReference actor, string operationId, bool grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireHost(host, AuthorizationModule.HostAuthorityOperation.Id);
        if (registry.FindHostOperation(operationId) is null)
            throw AuthorityAdministration.Refuse(AuthorizationCodes.ChangeUndefined, "No composed module declares the host operation.");
        // No one grants or revokes host authority beyond the operations they hold themselves.
        if (await authority.AuthorizeAsync(host.Actor, operationId, cancellationToken).ConfigureAwait(false) is null)
            throw AuthorityAdministration.Refuse(AuthorizationCodes.BeyondCeiling, "The acting operator does not hold the host operation itself.");
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var db = (AuthorizationContext)work.Context;
        var kind = (int)actor.Kind;
        var now = clock.Now;
        var row = await db.HostAuthorityGrants.SingleOrDefaultAsync(row => row.ActorKind == kind && row.Actor == actor.Subject && row.OperationId == operationId,
            cancellationToken).ConfigureAwait(false);
        if (grant == row is not null) return false;
        if (grant)
            db.Add(new HostAuthorityGrantRow
            {
                ActorKind = kind, Actor = actor.Subject, OperationId = operationId, GrantedAt = now, GrantedByKind = (int)host.Actor.Kind, GrantedBy = host.Actor.Subject
            });
        else db.Remove(row!);
        await changes.AuditAsync(new AuthorityChangeRecord(Guid.NewGuid(), grant ? AuthorityChangeKind.HostAuthorityGranted : AuthorityChangeKind.HostAuthorityRevoked,
            null, host.Actor, actor, "host-operation:" + operationId, [], now, Activity.Current?.TraceId.ToHexString()), cancellationToken).ConfigureAwait(false);
        work.OnCompleted(result =>
        {
            if (result.State == UnitOfWorkState.Committed) changes.Invalidate(() => cache.InvalidateHostPrincipal(operationId, actor));
        });
        return await CommitAsync(work, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ChangeOwnerAsync(TenantContext host, Guid tenantId, ActorReference owner, bool add, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RequireHost(host, AuthorizationModule.TenantOwnersOperation.Id);
        if (owner.Kind != ActorKind.User) throw AuthorityAdministration.Refuse(AuthorizationCodes.ChangeInvalid, "A tenant's owner is a user.");
        if (add && await directory.FindTenantAsync(tenantId, cancellationToken).ConfigureAwait(false) is not { State: TenantState.Active })
            throw AuthorityAdministration.Refuse(AuthorizationCodes.ChangeInvalid, "Owners are named only for an active tenant.");
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var db = (AuthorizationContext)work.Context;
        var kind = (int)owner.Kind;
        var now = clock.Now;
        var row = await db.TenantOwners.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.OwnerKind == kind && row.Owner == owner.Subject,
            cancellationToken).ConfigureAwait(false);
        if (add == row is not null) return false;
        if (add)
            db.Add(new TenantOwnerRow
            {
                TenantId = tenantId, OwnerKind = kind, Owner = owner.Subject, AddedAt = now, AddedByKind = (int)host.Actor.Kind, AddedBy = host.Actor.Subject
            });
        else db.Remove(row!);
        await changes.AuditAsync(new AuthorityChangeRecord(Guid.NewGuid(), add ? AuthorityChangeKind.TenantOwnerAdded : AuthorityChangeKind.TenantOwnerRemoved,
            tenantId, host.Actor, owner, "tenant-owner", [], now, Activity.Current?.TraceId.ToHexString()), cancellationToken).ConfigureAwait(false);
        work.OnCompleted(result =>
        {
            if (result.State == UnitOfWorkState.Committed) changes.Invalidate(() => cache.InvalidateTenant(tenantId));
        });
        return await CommitAsync(work, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireHost(TenantContext host, string operationId)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host is not { Scope: TenantScope.Host, IsActive: true, HostGrant: { } grant } || grant.OperationId != operationId)
            throw AuthorityAdministration.Refuse(AuthorizationCodes.ChangeNotPermitted, "This change needs an active, audited host scope granted for its own host operation.");
    }

    private static async Task<bool> CommitAsync(EfCoreUnitOfWork work, CancellationToken cancellationToken)
    {
        UnitOfWorkResult outcome;
        try { outcome = await work.CompleteAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateException)
        {
            throw AuthorityAdministration.Refuse(AuthorizationCodes.ChangeConflict, "A concurrent change committed first; read again and retry.");
        }
        return outcome.State == UnitOfWorkState.Committed ? true
            : throw AuthorityAdministration.Refuse(AuthorizationCodes.ChangeConflict, "A concurrent change committed first; read again and retry.");
    }
}
