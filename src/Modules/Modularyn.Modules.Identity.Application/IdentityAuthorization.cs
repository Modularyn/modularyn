using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

/// <summary>
/// Decides whether an <see cref="IdentityAuthority"/> authorizes an administrative identity write, before the write reads or
/// changes anything. A host scope counts only while active and only for the write's own operation. The owner counts only
/// as itself, through an undelegated session of the account, rechecked against the store now.
/// </summary>
internal sealed class IdentityAuthorization(IPrincipalFreshness freshness)
{
    /// <summary>The active host scope granted for <paramref name="operationId"/>; anything else is refused.</summary>
    internal static TenantContext RequireHost(IdentityAuthority authority, string operationId)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return IsHost(authority, operationId) ? authority.HostContext!
            : throw new IdentityAuthorityException(IdentityAuthorityCodes.HostOperationRequired,
                "This identity write needs an active, audited host scope granted for its identity operation.");
    }

    internal async Task RequireOwnerOrHostAsync(IdentityAuthority authority, Guid accountId, string operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (IsHost(authority, operationId) || await IsOwnerAsync(authority, accountId, cancellationToken).ConfigureAwait(false)) return;
        throw new IdentityAuthorityException(IdentityAuthorityCodes.OwnerOrHostRequired,
            "This identity write needs the account owner's own current session, or an active host scope granted for its identity operation.");
    }

    internal async Task RequireOwnerAsync(IdentityAuthority authority, Guid accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (await IsOwnerAsync(authority, accountId, cancellationToken).ConfigureAwait(false)) return;
        throw new IdentityAuthorityException(IdentityAuthorityCodes.OwnerRequired,
            "Only the account's owner, through its own current session, may make this identity write.");
    }

    /// <summary>
    /// The actor an audited identity write records: an identity administrator in its host scope, with the scope's unit and
    /// operation, or the account's owner acting as itself.
    /// </summary>
    internal static IdentityChangeActor ActorOf(IdentityAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (authority.HostContext is { } host) return new(host.Actor) { UnitId = host.UnitId, OperationId = host.HostGrant?.OperationId };
        return ActorOf(authority.OwnerPrincipal ?? throw new ArgumentException("The authority names no actor.", nameof(authority)));
    }

    /// <summary>The actor an audited identity write by <paramref name="principal"/> records.</summary>
    internal static IdentityChangeActor ActorOf(ExecutionPrincipal principal) =>
        new(new ActorReference(principal.Actor.Kind == IdentityKind.User ? ActorKind.User : ActorKind.Service, principal.Actor.Id));

    private static bool IsHost(IdentityAuthority authority, string operationId) =>
        authority.HostContext is { Scope: TenantScope.Host, IsActive: true, HostGrant: { } grant } && grant.OperationId == operationId;

    private async Task<bool> IsOwnerAsync(IdentityAuthority authority, Guid accountId, CancellationToken cancellationToken) =>
        authority.OwnerPrincipal is { IsDelegated: false, ServiceScope: null, SessionId: not null, Actor.Kind: IdentityKind.User } owner
        && string.Equals(owner.Actor.Id, accountId.ToString("D"), StringComparison.Ordinal)
        && await freshness.RecheckAsync(owner, cancellationToken).ConfigureAwait(false) == FreshnessStatus.Current;
}
