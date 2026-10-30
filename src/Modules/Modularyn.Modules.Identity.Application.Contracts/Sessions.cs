using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Modules.Identity.Application.Contracts;

public static class IdentityModule
{
    /// <summary>The module identity under the names contract, used by its manifest and its PostgreSQL ownership record.</summary>
    public const string Id = "Modularyn.Modules.Identity";
}

/// <summary>
/// The bearer secret of a server-side session: 32 random bytes in unpadded base64url. The store keeps only its SHA-256.
/// Formatting, logging, debugging and JSON serialization see only a redacted form.
/// </summary>
[DebuggerDisplay("[session]")]
[JsonConverter(typeof(SessionTokenJsonConverter))]
public sealed class SessionToken
{
    public const int EncodedLength = 43;
    public const string Redacted = "[session]";
    private const int SecretLength = 32;
    private readonly string value;

    private SessionToken(string value) => this.value = value;

    public static SessionToken Create() => new(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretLength)));

    /// <summary>Accepts exactly the form <see cref="Create"/> produces, so a malformed value never reaches the store.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out SessionToken? token)
    {
        token = null;
        if (text is not { Length: EncodedLength } || !text.All(IsBase64UrlCharacter)) return false;
        Span<byte> decoded = stackalloc byte[SecretLength];
        try
        {
            // Only the canonical encoding is accepted, so each session has exactly one token text.
            if (!Base64Url.TryDecodeFromChars(text, decoded, out var written) || written != SecretLength
                || !string.Equals(Base64Url.EncodeToString(decoded), text, StringComparison.Ordinal)) return false;
        }
        catch (FormatException)
        {
            // The decoder throws, rather than returning false, when the final character's unused bits are set.
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
        token = new SessionToken(text);
        return true;
    }

    public string Reveal() => value;

    /// <summary>The SHA-256 of the token's secret bytes, as the session store records it.</summary>
    public byte[] ComputeHash()
    {
        var secret = Base64Url.DecodeFromChars(value);
        try { return SHA256.HashData(secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public override string ToString() => Redacted;

    private static bool IsBase64UrlCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
}

internal sealed class SessionTokenJsonConverter : JsonConverter<SessionToken>
{
    public override SessionToken Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Session tokens are read by the session adapter, never deserialized.");

    public override void Write(Utf8JsonWriter writer, SessionToken value, JsonSerializerOptions options) => writer.WriteStringValue(SessionToken.Redacted);
}

/// <summary>A newly issued session. The token is delivered to the client once, by the adapter that issued the session.</summary>
public sealed class IssuedSession
{
    public IssuedSession(Guid sessionId, Guid accountId, SessionToken token, DateTimeOffset authenticatedAt, DateTimeOffset expiresAt)
    {
        SessionId = sessionId;
        AccountId = accountId;
        Token = token ?? throw new ArgumentNullException(nameof(token));
        AuthenticatedAt = authenticatedAt;
        ExpiresAt = expiresAt;
    }

    public Guid SessionId { get; }
    public Guid AccountId { get; }
    public SessionToken Token { get; }
    public DateTimeOffset AuthenticatedAt { get; }

    /// <summary>The session's absolute deadline; it ends earlier when idle.</summary>
    public DateTimeOffset ExpiresAt { get; }

    public override string ToString() => $"Session {SessionId:D}";
}

/// <summary>An active session after validation. It identifies the session record and never carries the token.</summary>
public sealed record ActiveSession(Guid SessionId, Guid AccountId, DateTimeOffset AuthenticatedAt, DateTimeOffset ExpiresAt)
{
    public PrincipalReference Account => new(AccountId.ToString("D"), IdentityKind.User);

    public ExecutionPrincipal ToPrincipal() => ExecutionPrincipal.ForUser(Account, SessionId, AuthenticatedAt);
}

/// <summary>Why a session ended, as its server-side record states.</summary>
public enum SessionEndCause
{
    SignedOut = 1,
    IdleTimeout = 2,
    AbsoluteLifetime = 3,
    SecurityStateChanged = 4,
    Revoked = 5,
    PasswordChanged = 6,
    AccountDisabled = 7,
    Superseded = 8,
    AccountUnavailable = 9
}

/// <summary>A session record as an operator or the account owner may see it: times and end reason, never the token or its hash.</summary>
public sealed record SessionRecordView(Guid SessionId, Guid AccountId, DateTimeOffset AuthenticatedAt, DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? EndedAt, SessionEndCause? EndCause);

/// <summary>
/// Server-side sessions (TM-G02-016, TM-G02-017). Every validation reads the session record and its account's current
/// security state, so a revocation, sign-out or security change takes effect at the next request.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Validates a presented token and records the activity. A session past its idle or absolute limit, or whose account is
    /// disabled or changed its security stamp, is ended with that reason. Null when the token does not identify an active session.
    /// </summary>
    Task<ActiveSession?> ValidateAsync(SessionToken token, CancellationToken cancellationToken);

    /// <summary>Ends the session the token identifies. False when it was unknown or had already ended.</summary>
    Task<bool> SignOutAsync(SessionToken token, CancellationToken cancellationToken);

    /// <summary>Ends one session: the account owner's own, or any in a host scope granted for <see cref="IdentityHostOperations.Sessions"/>.</summary>
    Task<bool> RevokeAsync(IdentityAuthority authority, Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Ends every session of the account and returns how many were active: the account owner's own, or any account's in a
    /// host scope granted for <see cref="IdentityHostOperations.Sessions"/>.
    /// </summary>
    Task<int> RevokeAllAsync(IdentityAuthority authority, Guid accountId, CancellationToken cancellationToken);

    Task<SessionRecordView?> DescribeAsync(Guid sessionId, CancellationToken cancellationToken);
}
