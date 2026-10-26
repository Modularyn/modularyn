using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Modularyn.Framework.Ingress;

/// <summary>Deployment options for HTTP ingress. Collections start empty; the binder appends configured entries.</summary>
public sealed class IngressOptions
{
    /// <summary>The public HTTPS origins, such as <c>https://app.example.com</c>. Their hosts are the only accepted host names.</summary>
    public List<string> PublicOrigins { get; set; } = [];

    /// <summary>Exact IP addresses of the declared reverse proxies. Forwarded headers are honored only from these.</summary>
    public List<string> Proxies { get; set; } = [];

    /// <summary>How many forwarded entries, counted from the nearest, are processed from declared proxies.</summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>The public HTTPS port that plain-HTTP requests are redirected to.</summary>
    public int HttpsPort { get; set; } = 443;

    /// <summary>The declared Strict-Transport-Security lifetime, from one day to two years.</summary>
    public TimeSpan HstsMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>A deployment choice recorded with the release.</summary>
    public bool HstsIncludeSubDomains { get; set; }

    /// <summary>A deployment choice recorded with the release; it requires subdomains and a lifetime of at least a year.</summary>
    public bool HstsPreload { get; set; }

    public CrossOriginOptions CrossOrigin { get; set; } = new();

    public RequestLimitOptions Limits { get; set; } = new();
}

/// <summary>Credentialed cross-origin callers. Empty means same-origin only.</summary>
public sealed class CrossOriginOptions
{
    /// <summary>Exactly listed HTTPS origins. Wildcards, <c>null</c> and plain-HTTP origins are refused.</summary>
    public List<string> Origins { get; set; } = [];

    public List<string> Methods { get; set; } = [];

    public List<string> Headers { get; set; } = [];

    public List<string> ExposedHeaders { get; set; } = [];

    public TimeSpan PreflightMaxAge { get; set; } = TimeSpan.FromMinutes(10);
}

public sealed class RequestLimitOptions
{
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;
    public int MaxRequestHeadersTotalBytes { get; set; } = 32 * 1024;
    public int MaxRequestHeaderCount { get; set; } = 100;
    public int MaxRequestLineBytes { get; set; } = 8 * 1024;
    public TimeSpan RequestHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxFormValueCount { get; set; } = 1024;
    public int MaxFormValueBytes { get; set; } = 1024 * 1024;
    public int MaxFormKeyBytes { get; set; } = 2048;
    public int MaxJsonDepth { get; set; } = 32;
}

internal static class Origins
{
    /// <summary>Normalizes an absolute HTTPS origin with an ASCII host and no user information, path, query or fragment.</summary>
    internal static bool TryNormalize(string? value, out string origin, out string host)
    {
        origin = host = "";
        if (string.IsNullOrEmpty(value) || value != value.Trim() || value.Any(character => character > 0x7E || char.IsControl(character))
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || uri.Host.EndsWith('.')
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return false;
        var authority = value[(Uri.UriSchemeHttps.Length + 3)..].TrimEnd('/');
        if (authority.Contains('/') || authority.Contains('\\')) return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        host = uri.Host;
        return true;
    }
}

internal sealed partial class IngressOptionsValidation(string sectionPath) : IValidateOptions<IngressOptions>
{
    public ValidateOptionsResult Validate(string? name, IngressOptions options)
    {
        var failures = new List<string>();
        var key = sectionPath + ":";
        if (options.PublicOrigins.Count == 0) failures.Add($"'{key}PublicOrigins' must list at least one public HTTPS origin.");
        Distinct(options.PublicOrigins, key + "PublicOrigins", failures);
        for (var index = 0; index < options.Proxies.Count; index++)
        {
            var value = options.Proxies[index];
            if (!IPAddress.TryParse(value, out var address) || address.ToString() != value || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.None))
                failures.Add($"'{key}Proxies:{index}' must be one exact, canonical IP address of a declared proxy, not a network or wildcard.");
        }
        if (options.Proxies.Distinct(StringComparer.Ordinal).Count() != options.Proxies.Count) failures.Add($"'{key}Proxies' lists a proxy more than once.");
        if (options.ForwardLimit is < 1 or > 8) failures.Add($"'{key}ForwardLimit' must be from 1 to 8.");
        if (options.HttpsPort is < 1 or > 65535) failures.Add($"'{key}HttpsPort' must be a TCP port from 1 to 65535.");
        if (options.HstsMaxAge < TimeSpan.FromDays(1) || options.HstsMaxAge > TimeSpan.FromDays(730) || options.HstsMaxAge.Ticks % TimeSpan.TicksPerSecond != 0)
            failures.Add($"'{key}HstsMaxAge' must declare whole seconds from one day to two years.");
        if (options.HstsPreload && (!options.HstsIncludeSubDomains || options.HstsMaxAge < TimeSpan.FromDays(365)))
            failures.Add($"'{key}HstsPreload' requires HstsIncludeSubDomains and an HstsMaxAge of at least 365 days.");

        var cors = options.CrossOrigin;
        Distinct(cors.Origins, key + "CrossOrigin:Origins", failures);
        if (cors.Origins.Count == 0 && (cors.Methods.Count + cors.Headers.Count + cors.ExposedHeaders.Count) != 0)
            failures.Add($"'{key}CrossOrigin' declares methods or headers without any listed origin.");
        if (cors.Origins.Count != 0 && cors.Methods.Count == 0) failures.Add($"'{key}CrossOrigin:Methods' must list the methods cross-origin callers may use.");
        Tokens(cors.Methods, key + "CrossOrigin:Methods", Method(), failures);
        Tokens(cors.Headers, key + "CrossOrigin:Headers", Token(), failures);
        Tokens(cors.ExposedHeaders, key + "CrossOrigin:ExposedHeaders", Token(), failures);
        if (cors.PreflightMaxAge < TimeSpan.Zero || cors.PreflightMaxAge > TimeSpan.FromDays(1))
            failures.Add($"'{key}CrossOrigin:PreflightMaxAge' must be from zero to one day.");

        var limits = options.Limits;
        var at = key + "Limits:";
        if (limits.MaxRequestBodyBytes is < 1 or > 1024L * 1024 * 1024) failures.Add($"'{at}MaxRequestBodyBytes' must be from 1 byte to 1 GiB.");
        if (limits.MaxRequestHeadersTotalBytes is < 1024 or > 1024 * 1024) failures.Add($"'{at}MaxRequestHeadersTotalBytes' must be from 1 KiB to 1 MiB.");
        if (limits.MaxRequestHeaderCount is < 1 or > 1000) failures.Add($"'{at}MaxRequestHeaderCount' must be from 1 to 1000.");
        if (limits.MaxRequestLineBytes < 256 || limits.MaxRequestLineBytes > limits.MaxRequestHeadersTotalBytes)
            failures.Add($"'{at}MaxRequestLineBytes' must be from 256 bytes to MaxRequestHeadersTotalBytes.");
        if (limits.RequestHeadersTimeout < TimeSpan.FromSeconds(1) || limits.RequestHeadersTimeout > TimeSpan.FromMinutes(2))
            failures.Add($"'{at}RequestHeadersTimeout' must be from 1 second to 2 minutes.");
        if (limits.MaxFormValueCount is < 1 or > 100_000) failures.Add($"'{at}MaxFormValueCount' must be from 1 to 100000.");
        if (limits.MaxFormValueBytes < 1 || limits.MaxFormValueBytes > limits.MaxRequestBodyBytes)
            failures.Add($"'{at}MaxFormValueBytes' must be from 1 byte to MaxRequestBodyBytes.");
        if (limits.MaxFormKeyBytes is < 1 or > 64 * 1024) failures.Add($"'{at}MaxFormKeyBytes' must be from 1 byte to 64 KiB.");
        if (limits.MaxJsonDepth is < 1 or > 256) failures.Add($"'{at}MaxJsonDepth' must be from 1 to 256.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Distinct(List<string> values, string key, List<string> failures)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            if (!Origins.TryNormalize(values[index], out var origin, out _))
                failures.Add($"'{key}:{index}' must be an exact HTTPS origin with an ASCII host and no wildcard, path, query or user information.");
            else if (!seen.Add(origin)) failures.Add($"'{key}:{index}' repeats an origin.");
        }
    }

    private static void Tokens(List<string> values, string key, Regex pattern, List<string> failures)
    {
        for (var index = 0; index < values.Count; index++)
            if (values[index] is not { } value || value == "*" || !pattern.IsMatch(value))
                failures.Add($"'{key}:{index}' must be one exact name, not a wildcard or a list.");
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count) failures.Add($"'{key}' lists a name more than once.");
    }

    [GeneratedRegex(@"\A[A-Z]{1,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Method();

    [GeneratedRegex(@"\A[A-Za-z0-9!#$%&'+.^_`|~-]{1,100}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
