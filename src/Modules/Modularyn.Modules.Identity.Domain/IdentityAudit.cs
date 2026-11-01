using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Identity.Domain;

/// <summary>
/// Who makes an audited identity change and where: the actor, and the unit and host operation it acts in when an
/// identity administrator acts in a host scope. The store records the change, with the account it changes, in the
/// change's own transaction. References only: never a credential, a token, a session identifier or a verifier.
/// </summary>
public sealed record IdentityChangeActor(ActorReference Actor)
{
    public Guid? UnitId { get; init; }
    public string? OperationId { get; init; }

    /// <summary>The account's owner acting on its own account.</summary>
    public static IdentityChangeActor Owner(Guid accountId) => new(new ActorReference(ActorKind.User, accountId.ToString("D")));
}

/// <summary>Stable action identifiers of identity's audited changes.</summary>
public static class IdentityAuditActions
{
    public const string AccountCreated = "identity.account-created";
    public const string AccountDisabled = "identity.account-disabled";
    public const string AccountEnabled = "identity.account-enabled";
    public const string SecurityStateChanged = "identity.security-state-changed";
    public const string PasswordChanged = "identity.password-changed";
    public const string SessionRevoked = "identity.session-revoked";
    public const string SessionsRevoked = "identity.sessions-revoked";
    public const string DelegationIssued = "identity.delegation-issued";
    public const string DelegationRevoked = "identity.delegation-revoked";
    public const string DelegationUsed = "identity.delegation-used";
    public const string AuthenticatorSeedStored = "identity.authenticator-seed-stored";
    public const string AuthenticatorSeedRemoved = "identity.authenticator-seed-removed";
    public const string RecoveryCodesReplaced = "identity.recovery-codes-replaced";
    public const string PasskeyAdded = "identity.passkey-added";
    public const string PasskeyRemoved = "identity.passkey-removed";
}

/// <summary>An identity change could not be recorded in audit, so it was not made. The message holds no value.</summary>
public sealed class IdentityAuditUnavailableException() : InvalidOperationException("The identity change could not be recorded in audit, so it was not made.");
