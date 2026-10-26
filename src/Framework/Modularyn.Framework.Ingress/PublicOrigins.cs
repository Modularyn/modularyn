using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Modularyn.Framework.Ingress;

/// <summary>
/// The configured public origins. Absolute links and redirects are built from these values, never from a Host or
/// forwarded header, and redirect targets are accepted only when they are local paths or one of these origins.
/// </summary>
public sealed class PublicOrigins
{
    private readonly Dictionary<string, string> byAuthority;

    public PublicOrigins(IOptions<IngressOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var origins = new List<string>();
        byAuthority = new(StringComparer.OrdinalIgnoreCase);
        foreach (var value in options.Value.PublicOrigins)
        {
            if (!Origins.TryNormalize(value, out var origin, out _))
                throw new InvalidOperationException("A configured public origin is invalid; ingress options were not validated.");
            origins.Add(origin);
            var uri = new Uri(origin);
            byAuthority[uri.Host + ":" + uri.Port] = origin;
        }
        Values = origins.AsReadOnly();
    }

    public IReadOnlyList<string> Values { get; }

    /// <summary>
    /// The configured origin that matches the request's validated host and port over HTTPS, or null. The returned
    /// value is the configuration's text, so a link built from it never carries a request-supplied host.
    /// </summary>
    public string? ForRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsHttps || !request.Host.HasValue) return null;
        return byAuthority.GetValueOrDefault(request.Host.Host + ":" + (request.Host.Port ?? 443));
    }

    /// <summary>
    /// True for a local path that starts with a single '/', or an absolute URL whose origin is configured.
    /// Protocol-relative, backslash, control-character, user-information and other-origin targets are refused.
    /// </summary>
    public bool IsAllowedRedirect(string? target)
    {
        if (string.IsNullOrEmpty(target) || target.Any(character => char.IsControl(character) || character == '\\')) return false;
        if (target[0] == '/') return target.Length == 1 || target[1] != '/';
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) return false;
        return Origins.TryNormalize(uri.GetLeftPart(UriPartial.Authority), out var origin, out _) && Values.Contains(origin, StringComparer.Ordinal);
    }
}
