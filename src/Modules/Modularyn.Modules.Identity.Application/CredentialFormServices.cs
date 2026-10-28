using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

internal enum CredentialKeyPurpose
{
    SeedEncryption = 1,
    PasskeyIntegrity = 2
}

/// <summary>
/// Versioned credential keys from the deployment's secret store, which holds them apart from the database. Each stored
/// key is exactly 32 bytes; HKDF-SHA256 derives a separate key for each purpose from it. Keys are resolved for each use
/// and cleared after it, so they are not held in memory between operations.
/// </summary>
internal sealed class CredentialKeyRing(IOptions<IdentitySecurityOptions> options, IServiceProvider services)
{
    private const int KeyLength = 32;
    private static readonly byte[] SeedInfo = Encoding.UTF8.GetBytes("Modularyn.Identity/authenticator-seed-encryption/v1");
    private static readonly byte[] PasskeyInfo = Encoding.UTF8.GetBytes("Modularyn.Identity/passkey-integrity/v1");

    internal int? CurrentVersion => options.Value.CredentialKeys.Keys.Count == 0 ? null : options.Value.CredentialKeys.CurrentVersion;

    /// <summary>The purpose key for a version, or null when that version is not configured or its secret cannot be resolved as 32 bytes.</summary>
    internal async ValueTask<byte[]?> DeriveAsync(int version, CredentialKeyPurpose purpose, CancellationToken cancellationToken)
    {
        var reference = options.Value.CredentialKeys.Keys.FirstOrDefault(key => key.Version == version)?.Secret;
        if (reference is null || services.GetService<ISecretStore>() is not { } store) return null;
        SecretValue secret;
        try
        {
            secret = await store.GetAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (SecretResolutionException)
        {
            return null;
        }
        using (secret)
        {
            var material = secret.RevealBytes();
            if (material.Length != KeyLength) return null;
            var derived = new byte[KeyLength];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, material, derived, ReadOnlySpan<byte>.Empty,
                purpose == CredentialKeyPurpose.SeedEncryption ? SeedInfo : PasskeyInfo);
            return derived;
        }
    }

    internal async ValueTask<(int Version, byte[] Key)> CurrentAsync(CredentialKeyPurpose purpose, CancellationToken cancellationToken)
    {
        var version = CurrentVersion ?? throw new InvalidOperationException("No credential key is configured, so the credential cannot be protected.");
        var key = await DeriveAsync(version, purpose, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Credential key version {version} is not available as 32 bytes from the secret store.");
        return (version, key);
    }
}

/// <summary>One-time-password seeds encrypted with AES-256-GCM under versioned keys (TM-G02-056).</summary>
internal sealed class AuthenticatorSeedService(IAuthenticatorSeedStore store, CredentialKeyRing keys, VerificationGate gate, IdentityClock clock,
    IdentityAuthorization authorization) : IAuthenticatorSeeds
{
    public async Task StoreAsync(IdentityAuthority authority, Guid accountId, ReadOnlyMemory<byte> seed, CancellationToken cancellationToken)
    {
        if (seed.Length is < ProtectedSeed.MinimumSeedLength or > ProtectedSeed.MaximumSeedLength)
            throw new ArgumentException("A one-time-password seed has from 16 to 64 bytes.", nameof(seed));
        await authorization.RequireOwnerAsync(authority, accountId, cancellationToken).ConfigureAwait(false);
        var (version, key) = await keys.CurrentAsync(CredentialKeyPurpose.SeedEncryption, cancellationToken).ConfigureAwait(false);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(ProtectedSeed.NonceLength);
            var ciphertext = new byte[seed.Length];
            var tag = new byte[ProtectedSeed.TagLength];
            using (var cipher = new AesGcm(key, ProtectedSeed.TagLength))
                cipher.Encrypt(nonce, seed.Span, ciphertext, tag, ProtectedSeed.AssociatedData(accountId, version));
            await store.SaveAsync(new ProtectedSeed(accountId, version, nonce, ciphertext, tag, clock.Now), cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<CredentialVerification> VerifyAsync(Guid accountId, Func<ReadOnlyMemory<byte>, bool> check, string source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        source = ClientSources.Require(source, nameof(source));
        await using var attempt = await gate.EnterAsync(source, ThrottleKey.ForAccount(ThrottleKind.OneTimeCode, accountId),
            clock.Now, cancellationToken).ConfigureAwait(false);
        if (attempt.RefusedUntil is { } retryAt) return new(CredentialVerificationStatus.Throttled, retryAt);
        var seed = await store.FindAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (seed is null) return CredentialVerification.Unavailable;
        var key = await keys.DeriveAsync(seed.KeyVersion, CredentialKeyPurpose.SeedEncryption, cancellationToken).ConfigureAwait(false);
        if (key is null) return CredentialVerification.Unavailable;
        var plaintext = new byte[seed.Ciphertext.Length];
        bool accepted;
        try
        {
            using (var cipher = new AesGcm(key, ProtectedSeed.TagLength))
                cipher.Decrypt(seed.Nonce, seed.Ciphertext, seed.Tag, plaintext, ProtectedSeed.AssociatedData(accountId, seed.KeyVersion));
            accepted = check(plaintext);
        }
        catch (CryptographicException)
        {
            // An altered, relabeled or moved ciphertext does not authenticate and is never used.
            return CredentialVerification.Unavailable;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
        if (!accepted)
        {
            await attempt.FailedAsync().ConfigureAwait(false);
            return CredentialVerification.Rejected;
        }
        await attempt.SucceededAsync().ConfigureAwait(false);
        return CredentialVerification.Accepted;
    }

    public async Task<bool> RemoveAsync(IdentityAuthority authority, Guid accountId, CancellationToken cancellationToken)
    {
        await authorization.RequireOwnerOrHostAsync(authority, accountId, IdentityHostOperations.CredentialsId, cancellationToken).ConfigureAwait(false);
        return await store.RemoveAsync(accountId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Recovery codes stored only as salted verifiers and consumed once (TM-G02-057).</summary>
internal sealed class RecoveryCodeService(IRecoveryCodeStore store, IAccountDirectory accounts, VerificationGate gate, IdentityClock clock,
    IdentityAuthorization authorization) : IRecoveryCodes
{
    public async Task<RecoveryCodeSet> GenerateAsync(IdentityAuthority authority, Guid accountId, CancellationToken cancellationToken)
    {
        // The codes are shown to whoever generates them, so only the owner may; an administrator who could would hold the account.
        await authorization.RequireOwnerAsync(authority, accountId, cancellationToken).ConfigureAwait(false);
        if (await accounts.FindAsync(accountId, cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException("Recovery codes belong to an existing account.");
        var now = clock.Now;
        var setId = Guid.NewGuid();
        var codes = Enumerable.Range(0, RecoveryCodes.CodesPerSet).Select(_ => RecoveryCodes.Generate()).ToArray();
        await store.ReplaceAsync(accountId, codes.Select(code => RecoveryCodes.CreateVerifier(accountId, setId, code, now)).ToArray(), now,
            cancellationToken).ConfigureAwait(false);
        return new RecoveryCodeSet(setId, Array.AsReadOnly(codes.Select(code => new SecretText(code)).ToArray()));
    }

    public async Task<CredentialVerification> ConsumeAsync(Guid accountId, SecretText code, string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        source = ClientSources.Require(source, nameof(source));
        var now = clock.Now;
        await using var attempt = await gate.EnterAsync(source, ThrottleKey.ForAccount(ThrottleKind.RecoveryCode, accountId),
            now, cancellationToken).ConfigureAwait(false);
        if (attempt.RefusedUntil is { } retryAt) return new(CredentialVerificationStatus.Throttled, retryAt);
        var normalized = RecoveryCodes.Normalize(code.Reveal());
        RecoveryCodeVerifier? match = null;
        if (normalized is not null)
        {
            foreach (var verifier in await store.FindUsableAsync(accountId, cancellationToken).ConfigureAwait(false))
                if (RecoveryCodes.Matches(verifier, normalized)) match ??= verifier;
        }
        if (match is null || !await store.ConsumeAsync(match.Id, now, cancellationToken).ConfigureAwait(false))
        {
            await attempt.FailedAsync().ConfigureAwait(false);
            return CredentialVerification.Rejected;
        }
        await attempt.SucceededAsync().ConfigureAwait(false);
        return CredentialVerification.Accepted;
    }

    public async Task<int> CountUsableAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await store.FindUsableAsync(accountId, cancellationToken).ConfigureAwait(false)).Count;
}

/// <summary>Passkey records bound to their account, credential and relying party by an integrity tag (TM-G02-058).</summary>
internal sealed class PasskeyCredentialService(IPasskeyCredentialStore store, CredentialKeyRing keys, IdentityClock clock, IdentityAuthorization authorization)
    : IPasskeyCredentials
{
    public async Task<bool> EnrollAsync(IdentityAuthority authority, PasskeyEnrollment enrollment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        // A passkey signs in as its account, so only the owner enrolls one: one enrolled by anyone else would hold the account.
        await authorization.RequireOwnerAsync(authority, enrollment.AccountId, cancellationToken).ConfigureAwait(false);
        var canonical = PasskeyCredentialRecord.CanonicalForm(enrollment.AccountId, enrollment.CredentialId, enrollment.RelyingPartyId,
            enrollment.PublicKey, enrollment.Algorithm);
        var (version, key) = await keys.CurrentAsync(CredentialKeyPurpose.PasskeyIntegrity, cancellationToken).ConfigureAwait(false);
        byte[] tag;
        try { tag = HMACSHA256.HashData(key, canonical); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return await store.AddAsync(new PasskeyCredentialRecord(Guid.NewGuid(), enrollment.AccountId, enrollment.CredentialId.ToArray(),
            enrollment.RelyingPartyId, enrollment.PublicKey.ToArray(), enrollment.Algorithm, clock.Now, version, tag), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PasskeyLookup> FindForSignInAsync(byte[] credentialId, string relyingPartyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        var record = await store.FindAsync(credentialId, cancellationToken).ConfigureAwait(false);
        if (record is null) return new(PasskeyLookupStatus.NotFound, null);
        // The tag is checked before anything else in the record is believed, including its relying party.
        var key = await keys.DeriveAsync(record.KeyVersion, CredentialKeyPurpose.PasskeyIntegrity, cancellationToken).ConfigureAwait(false);
        if (key is null) return new(PasskeyLookupStatus.IntegrityFailure, null);
        bool intact;
        try
        {
            var expected = HMACSHA256.HashData(key, PasskeyCredentialRecord.CanonicalForm(record.AccountId, record.CredentialId,
                record.RelyingPartyId, record.PublicKey, record.Algorithm));
            intact = record.IntegrityTag.Length == PasskeyCredentialRecord.TagLength && CryptographicOperations.FixedTimeEquals(expected, record.IntegrityTag);
        }
        catch (ArgumentException)
        {
            intact = false;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        if (!intact) return new(PasskeyLookupStatus.IntegrityFailure, null);
        if (!string.Equals(record.RelyingPartyId, relyingPartyId, StringComparison.Ordinal)) return new(PasskeyLookupStatus.NotFound, null);
        return new(PasskeyLookupStatus.Verified, new PasskeyCredentialView(record.AccountId, record.CredentialId.ToArray(), record.RelyingPartyId,
            record.PublicKey.ToArray(), record.Algorithm, record.CreatedAt));
    }

    public async Task<bool> RevokeAsync(IdentityAuthority authority, Guid accountId, byte[] credentialId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        await authorization.RequireOwnerOrHostAsync(authority, accountId, IdentityHostOperations.CredentialsId, cancellationToken).ConfigureAwait(false);
        return await store.RemoveAsync(accountId, credentialId, cancellationToken).ConfigureAwait(false);
    }
}
