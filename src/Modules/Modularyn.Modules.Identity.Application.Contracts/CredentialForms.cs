using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Modules.Identity.Application.Contracts;

public enum CredentialVerificationStatus
{
    Accepted = 1,
    Rejected = 2,

    /// <summary>Refused before verification by the per-source or per-account limit.</summary>
    Throttled = 3,

    /// <summary>The account has no such credential, or its keys are not available from the secret store.</summary>
    Unavailable = 4
}

public sealed record CredentialVerification(CredentialVerificationStatus Status, DateTimeOffset? RetryAt)
{
    public static CredentialVerification Accepted { get; } = new(CredentialVerificationStatus.Accepted, null);
    public static CredentialVerification Rejected { get; } = new(CredentialVerificationStatus.Rejected, null);
    public static CredentialVerification Unavailable { get; } = new(CredentialVerificationStatus.Unavailable, null);
}

/// <summary>
/// The stored form of one-time-password seeds (TM-G02-056), for the enrollment and sign-in journeys of G-03. Seeds are
/// encrypted under versioned keys from the secret store and are decrypted only to verify a code, never returned.
/// </summary>
public interface IAuthenticatorSeeds
{
    /// <summary>Encrypts and stores the account's seed under the current key version, replacing any earlier seed.</summary>
    Task StoreAsync(Guid accountId, ReadOnlyMemory<byte> seed, CancellationToken cancellationToken);

    /// <summary>
    /// Decrypts the seed for the duration of <paramref name="check"/> and clears it afterward. The attempt counts against the
    /// per-source and per-account one-time-code limits, and is refused without decryption when either is reached.
    /// </summary>
    Task<CredentialVerification> VerifyAsync(Guid accountId, Func<ReadOnlyMemory<byte>, bool> check, string source, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>A newly generated recovery-code set, to be shown to its owner once. Its codes are never stored or shown again.</summary>
public sealed class RecoveryCodeSet
{
    public RecoveryCodeSet(Guid setId, IReadOnlyList<SecretText> codes)
    {
        SetId = setId;
        Codes = codes ?? throw new ArgumentNullException(nameof(codes));
    }

    public Guid SetId { get; }
    public IReadOnlyList<SecretText> Codes { get; }

    public override string ToString() => $"Recovery-code set {SetId:D} with {Codes.Count} codes";
}

/// <summary>The stored form of recovery codes (TM-G02-057): salted verifiers only, each consumed at most once.</summary>
public interface IRecoveryCodes
{
    /// <summary>Generates a new set and invalidates every unconsumed code of the earlier set in the same transaction.</summary>
    Task<RecoveryCodeSet> GenerateAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Accepts an unconsumed code of the current set and marks it consumed in the write that accepts it, so it is accepted
    /// at most once. The attempt counts against the per-source and per-account recovery-code limits.
    /// </summary>
    Task<CredentialVerification> ConsumeAsync(Guid accountId, SecretText code, string source, CancellationToken cancellationToken);

    Task<int> CountUsableAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed record PasskeyEnrollment(Guid AccountId, byte[] CredentialId, string RelyingPartyId, byte[] PublicKey, int Algorithm);

public enum PasskeyLookupStatus
{
    Verified = 1,
    NotFound = 2,

    /// <summary>The record's integrity tag does not verify: it was changed outside enrollment and must not be trusted.</summary>
    IntegrityFailure = 3
}

public sealed record PasskeyCredentialView(Guid AccountId, byte[] CredentialId, string RelyingPartyId, byte[] PublicKey, int Algorithm, DateTimeOffset CreatedAt);

public sealed record PasskeyLookup(PasskeyLookupStatus Status, PasskeyCredentialView? Credential);

/// <summary>
/// The stored form of passkey credential records (TM-G02-058), for the passkey journeys of G-03. Records change only
/// through enrollment and revocation, and each lookup verifies the record's integrity tag before returning it.
/// </summary>
public interface IPasskeyCredentials
{
    /// <summary>Stores the credential with an integrity tag under the current key version. False when its identifier is already enrolled.</summary>
    Task<bool> EnrollAsync(PasskeyEnrollment enrollment, CancellationToken cancellationToken);

    /// <summary>Finds the credential for a sign-in at the relying party, verifying its integrity tag first.</summary>
    Task<PasskeyLookup> FindForSignInAsync(byte[] credentialId, string relyingPartyId, CancellationToken cancellationToken);

    Task<bool> RevokeAsync(Guid accountId, byte[] credentialId, CancellationToken cancellationToken);
}
