using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Files.Application.Contracts;

/// <summary>What a download grant may be used for. A single download is consumed by its first delivery.</summary>
public enum FileGrantPurpose { Download = 1, Preview = 2, SingleDownload = 3 }

public enum FileGrantState { Active = 1, Revoked = 2, Consumed = 3, Expired = 4 }

public sealed record FileGrantInfo(Guid GrantId, Guid FileId, Guid VersionId, FileGrantPurpose Purpose, ActorReference CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, FileGrantState State);

/// <summary>A newly issued grant. The token is delivered to its issuer once; the store keeps only its verifier.</summary>
public sealed record IssuedFileGrant(FileGrantInfo Grant, FileGrantToken Token);

/// <summary>
/// The bearer secret of a download grant: a fixed prefix, the grant's identity in 32 lowercase hexadecimal digits and 32
/// random bytes in unpadded base64url. The identity only selects the grant; the store keeps a verifier of the secret, bound
/// to the tenant and the grant, and compares it in constant time. Formatting, logging, debugging and JSON serialization see
/// only a redacted form.
/// </summary>
[DebuggerDisplay("[file-grant]")]
[JsonConverter(typeof(FileGrantTokenJsonConverter))]
public sealed class FileGrantToken
{
    public const string Prefix = "mfg1_";
    public const int EncodedLength = 80;
    public const string Redacted = "[file-grant]";
    private const int SelectorLength = 32;
    private const int SecretLength = 32;
    private static readonly byte[] Domain = "modularyn.files.grant/1"u8.ToArray();
    private readonly string value;

    private FileGrantToken(Guid grantId, string value)
    {
        GrantId = grantId;
        this.value = value;
    }

    /// <summary>The grant this token selects. It identifies; only the secret part authenticates.</summary>
    public Guid GrantId { get; }

    public static FileGrantToken Create(Guid grantId)
    {
        if (grantId == Guid.Empty) throw new ArgumentException("A grant has an identity.", nameof(grantId));
        return new(grantId, Prefix + grantId.ToString("N") + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretLength)));
    }

    /// <summary>Accepts exactly the form <see cref="Create"/> produces, so a malformed value never reaches the store.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out FileGrantToken? token)
    {
        token = null;
        if (text is not { Length: EncodedLength } || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var selector = text.AsSpan(Prefix.Length, SelectorLength);
        foreach (var character in selector) if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        if (!Guid.TryParseExact(selector, "N", out var grantId) || grantId == Guid.Empty) return false;
        var secret = text.AsSpan(Prefix.Length + SelectorLength);
        foreach (var character in secret) if (!IsBase64UrlCharacter(character)) return false;
        Span<byte> decoded = stackalloc byte[SecretLength];
        try
        {
            // Only the canonical encoding is accepted, so each grant has exactly one token text.
            if (!Base64Url.TryDecodeFromChars(secret, decoded, out var written) || written != SecretLength
                || !secret.SequenceEqual(Base64Url.EncodeToString(decoded))) return false;
        }
        catch (FormatException)
        {
            // The decoder throws, rather than returning false, when the final character's unused bits are set.
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
        token = new(grantId, text);
        return true;
    }

    public string Reveal() => value;

    /// <summary>
    /// The verifier the store keeps: SHA-256 over a domain label, the tenant, the grant and the secret, so a verifier copied
    /// into another tenant's or another grant's row never matches.
    /// </summary>
    public byte[] ComputeVerifier(Guid tenantId)
    {
        var secret = Base64Url.DecodeFromChars(value.AsSpan(Prefix.Length + SelectorLength));
        Span<byte> identity = stackalloc byte[16];
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Domain);
            tenantId.TryWriteBytes(identity);
            hash.AppendData(identity);
            GrantId.TryWriteBytes(identity);
            hash.AppendData(identity);
            hash.AppendData(secret);
            return hash.GetHashAndReset();
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public override string ToString() => Redacted;

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
}

internal sealed class FileGrantTokenJsonConverter : JsonConverter<FileGrantToken>
{
    public override FileGrantToken Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Grant tokens are parsed by the delivery adapter, never deserialized.");

    public override void Write(Utf8JsonWriter writer, FileGrantToken value, JsonSerializerOptions options) => writer.WriteStringValue(FileGrantToken.Redacted);
}
