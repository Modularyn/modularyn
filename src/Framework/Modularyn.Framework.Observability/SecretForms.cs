using System.Text.RegularExpressions;

namespace Modularyn.Framework.Observability;

/// <summary>
/// The secret forms the framework recognizes in text: credentials, tokens and key material that never belong in a log,
/// a trace, an exception message, a diagnostic, an audit record or a durable payload. It is a deny list over the forms
/// this framework and its transports use, and it backs up, never replaces, the rule that telemetry carries only
/// allowlisted fields: the framework's own events never put a value in text to begin with.
/// </summary>
/// <remarks>
/// The forms are an authorization header's credential, a connection string's password, the session cookie and a bare
/// session token (43 base64url characters mixing letters and digits), a file grant token, a data-protection payload such
/// as an antiforgery token, a JSON web token, a PEM private key and a secret reference (<c>secret:&lt;name&gt;</c>). A
/// reference is not the secret, but it names where one is kept, and Settings masks it wherever it displays one, so it is
/// redacted from telemetry and refused in records and payloads alike. Over-matching only redacts or refuses more; a value
/// in none of these forms is not recognized, so text from outside the framework must still be kept out of telemetry.
/// </remarks>
public static partial class SecretForms
{
    /// <summary>What replaces a recognized secret form in scrubbed text.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>True when <paramref name="text"/> holds any recognized secret form.</summary>
    public static bool Contains(string? text) => !string.IsNullOrEmpty(text) && Pattern().IsMatch(text);

    /// <summary>The text with every recognized secret form replaced by <see cref="Redacted"/>.</summary>
    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Pattern().Replace(text, Redacted);
    }

    // One alternation, so a text is scanned once. Every quantifier is bounded or stops at a delimiter.
    [GeneratedRegex(
        @"\b(?i:bearer|basic)[ \t]+[A-Za-z0-9._~+/=-]{8,}"
        + @"|\b(?i:password|pwd)[ \t]*=[ \t]*(?:""[^""]*""|'[^']*'|[^;\r\n]*)"
        + @"|__Host-Modularyn-Session=[^;\s]*"
        + @"|mfg1_[0-9a-f]{32}[A-Za-z0-9_-]{43}"
        + @"|CfDJ8[A-Za-z0-9_-]{20,}"
        + @"|eyJ[A-Za-z0-9_-]{5,}\.eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*"
        + @"|-----BEGIN [A-Z ]{0,40}PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]{0,40}PRIVATE KEY-----|\z)"
        + @"|\bsecret:[A-Za-z0-9][A-Za-z0-9._-]{0,127}"
        + @"|(?<![A-Za-z0-9_-])(?=[A-Za-z0-9_-]{0,42}[0-9])(?=[A-Za-z0-9_-]{0,42}[A-Za-z])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}
