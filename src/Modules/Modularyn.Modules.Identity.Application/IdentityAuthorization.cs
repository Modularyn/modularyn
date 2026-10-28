using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;

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

    private static bool IsHost(IdentityAuthority authority, string operationId) =>
        authority.HostContext is { Scope: TenantScope.Host, IsActive: true, HostGrant: { } grant } && grant.OperationId == operationId;

    private async Task<bool> IsOwnerAsync(IdentityAuthority authority, Guid accountId, CancellationToken cancellationToken) =>
        authority.OwnerPrincipal is { IsDelegated: false, ServiceScope: null, SessionId: not null, Actor.Kind: IdentityKind.User } owner
        && string.Equals(owner.Actor.Id, accountId.ToString("D"), StringComparison.Ordinal)
        && await freshness.RecheckAsync(owner, cancellationToken).ConfigureAwait(false) == FreshnessStatus.Current;
}
