using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Modularyn.Modules.Identity.Domain;

/// <summary>
/// The stored form of a one-time-password seed (TM-G02-056): AES-256-GCM ciphertext and tag under a versioned key that
/// the deployment's secret store holds apart from the database. The associated data binds the ciphertext to its account
/// and key version, so a seed copied to another account or relabeled with another key version fails to decrypt.
/// </summary>
public sealed record ProtectedSeed(Guid AccountId, int KeyVersion, byte[] Nonce, byte[] Ciphertext, byte[] Tag, DateTimeOffset CreatedAt)
{
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int MinimumSeedLength = 16;
    public const int MaximumSeedLength = 64;
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("Modularyn.Identity.AuthenticatorSeed.v1");

    public static byte[] AssociatedData(Guid accountId, int keyVersion)
    {
        var data = new byte[Purpose.Length + 16 + 4];
        Purpose.CopyTo(data, 0);
        accountId.TryWriteBytes(data.AsSpan(Purpose.Length, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(Purpose.Length + 16), keyVersion);
        return data;
    }
}

public interface IAuthenticatorSeedStore
{
    /// <summary>Stores the account's seed, replacing any earlier one.</summary>
    Task SaveAsync(ProtectedSeed seed, CancellationToken cancellationToken);

    Task<ProtectedSeed?> FindAsync(Guid accountId, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// The stored form of one recovery code (TM-G02-057): a salted PBKDF2-HMAC-SHA256 verifier with its iteration count. The
/// code itself is never stored; it is shown to the account owner once, when its set is generated.
/// </summary>
public sealed record RecoveryCodeVerifier(Guid Id, Guid AccountId, Guid SetId, byte[] Salt, byte[] Verifier, int Iterations, DateTimeOffset CreatedAt);

public static class RecoveryCodes
{
    public const int CodesPerSet = 10;
    public const int SymbolsPerCode = 16;
    public const int Iterations = 10_000;
    public const int SaltLength = 16;
    public const int VerifierLength = 32;

    /// <summary>32 symbols without the easily confused I, O, 0 and 1, so each symbol carries five bits and each code eighty.</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A new code in four groups of four symbols, such as <c>ABCD-EFGH-JKLM-NPQR</c>.</summary>
    public static string Generate()
    {
        var symbols = RandomNumberGenerator.GetItems<char>(Alphabet, SymbolsPerCode);
        return string.Join('-', Enumerable.Range(0, 4).Select(group => new string(symbols, group * 4, 4)));
    }

    /// <summary>The comparison form of a presented code, or null when it cannot be a code: separators and case are ignored.</summary>
    public static string? Normalize(string? presented)
    {
        if (presented is null || presented.Length > 64) return null;
        var normalized = new string(presented.Where(character => character is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length == SymbolsPerCode && normalized.All(character => Alphabet.Contains(character, StringComparison.Ordinal)) ? normalized : null;
    }

    public static byte[] Derive(string normalizedCode, ReadOnlySpan<byte> salt, int iterations)
    {
        ArgumentNullException.ThrowIfNull(normalizedCode);
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(normalizedCode), salt, iterations, HashAlgorithmName.SHA256, VerifierLength);
    }

    public static RecoveryCodeVerifier CreateVerifier(Guid accountId, Guid setId, string code, DateTimeOffset now)
    {
        var normalized = Normalize(code) ?? throw new ArgumentException("The value is not a recovery code.", nameof(code));
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        return new(Guid.NewGuid(), accountId, setId, salt, Derive(normalized, salt, Iterations), Iterations, now);
    }

    public static bool Matches(RecoveryCodeVerifier verifier, string normalizedCode)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (verifier.Iterations < Iterations || verifier.Salt.Length < SaltLength || verifier.Verifier.Length != VerifierLength) return false;
        return CryptographicOperations.FixedTimeEquals(Derive(normalizedCode, verifier.Salt, verifier.Iterations), verifier.Verifier);
    }
}

public interface IRecoveryCodeStore
{
    /// <summary>Invalidates every unconsumed code of the account and stores the new set, in one transaction.</summary>
    Task ReplaceAsync(Guid accountId, IReadOnlyList<RecoveryCodeVerifier> verifiers, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The account's codes that are neither consumed nor invalidated.</summary>
    Task<IReadOnlyList<RecoveryCodeVerifier>> FindUsableAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the code consumed in one conditional write that succeeds only while it is still usable, so of any number of
    /// concurrent attempts with the same code exactly one is accepted. False when it was consumed or invalidated first.
    /// </summary>
    Task<bool> ConsumeAsync(Guid verifierId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// The stored form of a passkey credential (TM-G02-058). The integrity tag is an HMAC-SHA256, under a versioned key the
/// secret store holds apart from the database, over the record's account, credential identifier, relying-party
/// identifier, public key and algorithm, so substituting any of them invalidates the record.
/// </summary>
public sealed record PasskeyCredentialRecord(Guid Id, Guid AccountId, byte[] CredentialId, string RelyingPartyId, byte[] PublicKey,
    int Algorithm, DateTimeOffset CreatedAt, int KeyVersion, byte[] IntegrityTag)
{
    public const int MaximumCredentialIdLength = 1023;
    public const int MaximumPublicKeyLength = 4096;
    public const int MaximumRelyingPartyIdLength = 253;
    public const int TagLength = 32;
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("Modularyn.Identity.Passkey.v1");

    /// <summary>The canonical, length-prefixed bytes the integrity tag covers.</summary>
    public static byte[] CanonicalForm(Guid accountId, ReadOnlySpan<byte> credentialId, string relyingPartyId, ReadOnlySpan<byte> publicKey, int algorithm)
    {
        ArgumentNullException.ThrowIfNull(relyingPartyId);
        if (credentialId.IsEmpty || credentialId.Length > MaximumCredentialIdLength)
            throw new ArgumentException("A credential identifier has from 1 to 1023 bytes.", nameof(credentialId));
        if (publicKey.IsEmpty || publicKey.Length > MaximumPublicKeyLength)
            throw new ArgumentException("A public key has from 1 to 4096 bytes.", nameof(publicKey));
        if (!IsValidRelyingPartyId(relyingPartyId)) throw new ArgumentException("A relying-party identifier is a lowercase ASCII host name.", nameof(relyingPartyId));
        var rp = Encoding.ASCII.GetBytes(relyingPartyId);
        var buffer = new byte[Purpose.Length + 16 + 2 + credentialId.Length + 2 + rp.Length + 2 + publicKey.Length + 4];
        var span = buffer.AsSpan();
        Purpose.CopyTo(span);
        var offset = Purpose.Length;
        accountId.TryWriteBytes(span.Slice(offset, 16), bigEndian: true, out _);
        offset += 16;
        offset = WriteBlock(span, offset, credentialId);
        offset = WriteBlock(span, offset, rp);
        offset = WriteBlock(span, offset, publicKey);
        BinaryPrimitives.WriteInt32BigEndian(span[offset..], algorithm);
        return buffer;
    }

    public static bool IsValidRelyingPartyId(string? value) =>
        value is { Length: > 0 and <= MaximumRelyingPartyIdLength } && !value.StartsWith('.') && !value.EndsWith('.')
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');

    private static int WriteBlock(Span<byte> span, int offset, ReadOnlySpan<byte> block)
    {
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)block.Length);
        block.CopyTo(span[(offset + 2)..]);
        return offset + 2 + block.Length;
    }
}

public interface IPasskeyCredentialStore
{
    /// <summary>Stores a newly enrolled credential. False when its credential identifier is already enrolled, to any account.</summary>
    Task<bool> AddAsync(PasskeyCredentialRecord record, CancellationToken cancellationToken);

    Task<PasskeyCredentialRecord?> FindAsync(byte[] credentialId, CancellationToken cancellationToken);

    /// <summary>Removes the account's credential. False when the account has no such credential.</summary>
    Task<bool> RemoveAsync(Guid accountId, byte[] credentialId, CancellationToken cancellationToken);
}
