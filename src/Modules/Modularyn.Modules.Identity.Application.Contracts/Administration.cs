using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Identity.Application.Contracts;

/// <summary>
/// The host operations under which identity's administrative writes run (SEC-05). Each is granted only through the
/// elevated policy and recorded by host-scope audit before its scope exists. Accounts, sessions and credentials are
/// identity's own records, written in its owner-bound units once the operation is authorized, so those operations reach
/// no entity in host scope. Tenant memberships decide who may establish a tenant context, so they are authority data:
/// they are written in the audited host scope itself, which reaches only the membership table.
/// </summary>
public static class IdentityHostOperations
{
    public const string AccountsId = "modularyn.identity.accounts";
    public const string MembershipsId = "modularyn.identity.memberships";
    public const string SessionsId = "modularyn.identity.sessions";
    public const string CredentialsId = "modularyn.identity.credentials";

    /// <summary>Account creation, disabling and enabling, and security-state changes.</summary>
    public static HostOperation Accounts { get; } = new(AccountsId, []);

    /// <summary>Adding and removing tenant memberships, written in this host scope.</summary>
    public static HostOperation Memberships { get; } = new(MembershipsId, [new HostReach(IdentityModule.Id, "TenantMemberships")]);

    /// <summary>Revoking another account's sessions.</summary>
    public static HostOperation Sessions { get; } = new(SessionsId, []);

    /// <summary>Revoking another account's passkeys and one-time-password seed.</summary>
    public static HostOperation Credentials { get; } = new(CredentialsId, []);

    /// <summary>The operations as authorization's registry binds them to the identity module's manifest.</summary>
    public static IReadOnlyList<HostOperationDefinition> Definitions(SemanticVersion version) =>
        [new(IdentityModule.Id, version, Accounts), new(IdentityModule.Id, version, Memberships), new(IdentityModule.Id, version, Sessions),
            new(IdentityModule.Id, version, Credentials)];
}

/// <summary>
/// Who performs an administrative identity write. An identity administrator acts in an active host scope granted for
/// the write's operation, which tenancy establishes only under host authority and after audit; the account's owner acts
/// as itself through its own current, undelegated session, for the writes that are the owner's to make. Nothing else
/// authorizes these writes: a tenant context or a tenant administrator's membership confers nothing in identity.
/// </summary>
public sealed class IdentityAuthority
{
    private IdentityAuthority(TenantContext? host, ExecutionPrincipal? owner)
    {
        HostContext = host;
        OwnerPrincipal = owner;
    }

    public TenantContext? HostContext { get; }
    public ExecutionPrincipal? OwnerPrincipal { get; }

    public static IdentityAuthority Host(TenantContext host) => new(host ?? throw new ArgumentNullException(nameof(host)), null);

    public static IdentityAuthority Owner(ExecutionPrincipal owner) => new(null, owner ?? throw new ArgumentNullException(nameof(owner)));

    public override string ToString() => HostContext is not null ? "Identity administration in host scope" : "The account owner";
}

/// <summary>Stable refusal codes for identity's administrative writes. A refusal changes nothing.</summary>
public static class IdentityAuthorityCodes
{
    /// <summary>The write needs an active, audited host scope granted for its identity operation.</summary>
    public const string HostOperationRequired = "MID001";

    /// <summary>The write needs the account owner's own current session, or an active host scope granted for its operation.</summary>
    public const string OwnerOrHostRequired = "MID002";

    /// <summary>Only the account's owner, through its own current session, may make the write.</summary>
    public const string OwnerRequired = "MID003";
}

public sealed class IdentityAuthorityException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
