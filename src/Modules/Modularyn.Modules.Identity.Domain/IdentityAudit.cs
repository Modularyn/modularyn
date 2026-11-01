using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Identity.Domain;

/// <summary>
/// One administrative or security change to identity's records as audit records it, in the change's own transaction: the
/// action's stable identifier, who made it, the account it changes, and the unit and tenant it was made in. References
/// only: never a credential, a token, a session identifier or a verifier.
/// </summary>
public sealed record IdentityChangeRecord(string Action, ActorReference Actor, Guid Account)
{
    public Guid? TenantId { get; init; }
    public Guid? UnitId { get; init; }
    public string? OperationId { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>Stable action identifiers of identity's audited changes.</summary>
public static class IdentityAuditActions
{
    public const string AccountCreated = "identity.account-created";
    public const string AccountDisabled = "identity.account-disabled";
    public const string AccountEnabled = "identity.account-enabled";
    public const string SecurityStateChanged = "identity.security-state-changed";
    public const string SessionRevoked = "identity.session-revoked";
    public const string SessionsRevoked = "identity.sessions-revoked";
    public const string DelegationIssued = "identity.delegation-issued";
    public const string DelegationRevoked = "identity.delegation-revoked";
    public const string AuthenticatorSeedStored = "identity.authenticator-seed-stored";
    public const string AuthenticatorSeedRemoved = "identity.authenticator-seed-removed";
    public const string RecoveryCodesReplaced = "identity.recovery-codes-replaced";
    public const string PasskeyAdded = "identity.passkey-added";
    public const string PasskeyRemoved = "identity.passkey-removed";
}

/// <summary>An identity change could not be recorded in audit, so it was not made. The message holds no value.</summary>
public sealed class IdentityAuditUnavailableException() : InvalidOperationException("The identity change could not be recorded in audit, so it was not made.");
