namespace Modularyn.Framework.Tenancy.Abstractions;

/// <summary>
/// What a stored propagated reference is bound to: the purpose of the store that holds it and the key of the row or item
/// it travels with, such as an outbox event's identity. The reference's seal covers its binding, so a reference copied to
/// another row, another store or another purpose, even in the same tenant, is refused.
/// </summary>
public sealed record PropagationBinding
{
    public const int MaxPurposeLength = 200;
    public const int MaxKeyLength = 200;

    public PropagationBinding(string purpose, string key)
    {
        if (string.IsNullOrEmpty(purpose) || purpose.Length > MaxPurposeLength
            || !purpose.All(character => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_' or '/'))
            throw new ArgumentException("A binding purpose is 1 to 200 letters, digits, dots, hyphens, underscores or slashes.", nameof(purpose));
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key != key.Trim() || key.Any(char.IsControl))
            throw new ArgumentException("A binding key is 1 to 200 characters with no surrounding whitespace or control characters.", nameof(key));
        Purpose = purpose;
        Key = key;
    }

    public string Purpose { get; }
    public string Key { get; }

    /// <summary>A binding to one row, by its identity in the canonical hyphenated form.</summary>
    public static PropagationBinding ForRow(string purpose, Guid id) => new(purpose, id.ToString("D"));
}

/// <summary>
/// The server-keyed seal over stored propagated references (TM-G02-027): a reference that its store did not capture in
/// its producing unit, or that was altered or moved since, fails verification and never runs. Tenancy supplies the
/// implementation over the host's declared data-protection key ring, so keys rotate and revoke with that ring.
/// </summary>
public interface IPropagationSeal
{
    /// <summary>False while the host has no declared key ring; sealing then refuses and opening defers rather than denies.</summary>
    bool IsAvailable { get; }

    /// <summary>The seal over the canonical content of one reference and its binding.</summary>
    string Seal(ReadOnlySpan<byte> content);

    /// <summary>True only when <paramref name="seal"/> is this host's seal over exactly <paramref name="content"/>.</summary>
    bool Verify(ReadOnlySpan<byte> content, string seal);
}
