using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Modularyn.Framework.Security.Abstractions;

/// <summary>
/// An exact HTTPS origin, <c>https://host[:port]</c>, with an ASCII host and the default port omitted. Credentials are
/// bound to origins, never to host names, paths or patterns.
/// </summary>
public sealed class EndpointOrigin : IEquatable<EndpointOrigin>
{
    private EndpointOrigin(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// The origin of an absolute HTTPS request target. Plain HTTP, user information, non-ASCII or trailing-dot hosts and
    /// relative targets have no origin a credential can be bound to.
    /// </summary>
    public static bool TryFromRequestUri(Uri? target, [NotNullWhen(true)] out EndpointOrigin? origin)
    {
        origin = null;
        if (target is null || !target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || target.UserInfo.Length != 0
            || target.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || target.Host.Length == 0 || target.Host.EndsWith('.') || target.Host.Any(character => character > 0x7E)) return false;
        origin = new EndpointOrigin(target.GetLeftPart(UriPartial.Authority));
        return true;
    }

    /// <summary>Parses a configured origin, which must be exactly an HTTPS origin with no path, query or fragment.</summary>
    public static EndpointOrigin Parse(string configuredOrigin)
    {
        const string prefix = "https://";
        if (string.IsNullOrEmpty(configuredOrigin) || !configuredOrigin.StartsWith(prefix, StringComparison.Ordinal)
            || configuredOrigin.Any(character => character is <= ' ' or > '~' or '\\' or '?' or '#')
            || configuredOrigin[prefix.Length..].TrimEnd('/').Contains('/')
            || !Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var uri) || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || !TryFromRequestUri(uri, out var origin))
            throw new FormatException("A credential is bound to an exact HTTPS origin with an ASCII host and no path, query or user information.");
        return origin;
    }

    public bool Equals(EndpointOrigin? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is EndpointOrigin other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;
}

/// <summary>An explicit credential for one request, such as a bearer token. Its value is redacted from formatting and logs.</summary>
public sealed partial class ClientCredential
{
    public ClientCredential(string scheme, SecretText value)
    {
        if (scheme is null || !Token().IsMatch(scheme)) throw new ArgumentException("An authentication scheme is a single HTTP token, such as Bearer.", nameof(scheme));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0) throw new ArgumentException("A credential has a value.", nameof(value));
        Scheme = scheme;
        Value = value;
    }

    public string Scheme { get; }
    public SecretText Value { get; }

    public override string ToString() => Scheme + " " + SecretText.Redacted;

    [GeneratedRegex(@"\A[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}

/// <summary>
/// The seam through which generated and dynamic clients obtain credentials (TM-G02-013, for CODE-06). A client asks for
/// every request it sends and again for every redirect target, passing the origin it is about to contact, and sends no
/// credential when the provider returns null. Clients never forward ambient credentials, such as cookies or the
/// process's default credentials, and never reuse a credential obtained for one origin on another.
/// </summary>
public interface IClientCredentialProvider
{
    ValueTask<ClientCredential?> GetCredentialAsync(EndpointOrigin target, CancellationToken cancellationToken);
}

/// <summary>
/// Supplies a credential only for the configured endpoint origins. For any other origin, including one learned from
/// discovery data or a redirect, it returns null without consulting its credential source.
/// </summary>
public sealed class OriginBoundCredentialProvider : IClientCredentialProvider
{
    private readonly FrozenSet<EndpointOrigin> origins;
    private readonly Func<EndpointOrigin, CancellationToken, ValueTask<ClientCredential>> source;

    public OriginBoundCredentialProvider(IEnumerable<EndpointOrigin> origins, Func<EndpointOrigin, CancellationToken, ValueTask<ClientCredential>> source)
    {
        ArgumentNullException.ThrowIfNull(origins);
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        var copy = origins.ToArray();
        if (copy.Length == 0 || copy.Any(origin => origin is null))
            throw new ArgumentException("A credential provider is bound to at least one configured origin.", nameof(origins));
        this.origins = copy.ToFrozenSet();
    }

    public IReadOnlyCollection<EndpointOrigin> Origins => origins;

    public async ValueTask<ClientCredential?> GetCredentialAsync(EndpointOrigin target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!origins.Contains(target)) return null;
        return await source(target, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The credential source returned no credential for a configured origin.");
    }
}
