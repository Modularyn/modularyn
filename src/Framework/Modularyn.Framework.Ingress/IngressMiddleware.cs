using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Modularyn.Framework.Ingress;

/// <summary>
/// Applies forwarded headers only when the immediate peer is a declared proxy, and removes every forwarded header the
/// application did not apply, so later code cannot read an unverified scheme, host or client address.
/// </summary>
internal sealed class ProxyTrustMiddleware
{
    private static readonly string[] Supplied = ["X-Original-For", "X-Original-Proto", "X-Original-Host", "X-Original-Prefix"];
    private static readonly string[] Unapplied = ["X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix", "Forwarded"];
    private readonly RequestDelegate pipeline;

    public ProxyTrustMiddleware(RequestDelegate next, IOptions<IngressOptions> ingress, IOptions<ForwardedHeadersOptions> forwarded, ILoggerFactory loggers)
    {
        RequestDelegate sanitized = context =>
        {
            foreach (var name in Unapplied) context.Request.Headers.Remove(name);
            return next(context);
        };
        var proxies = ingress.Value.Proxies;
        if (proxies.Count == 0)
        {
            pipeline = sanitized;
            return;
        }
        // ASP.NET Core trusts every source when both trust lists are empty, so the lists must hold exactly the declared proxies.
        var options = forwarded.Value;
#pragma warning disable ASPDEPR005
        var networks = options.KnownIPNetworks.Count + options.KnownNetworks.Count;
#pragma warning restore ASPDEPR005
        if (networks != 0 || options.KnownProxies.Count != proxies.Count
            || !proxies.All(proxy => options.KnownProxies.Any(known => known.ToString() == proxy)))
            throw new InvalidOperationException("Forwarded headers must trust exactly the declared proxies.");
        pipeline = new ForwardedHeadersMiddleware(sanitized, loggers, forwarded).Invoke;
    }

    public Task Invoke(HttpContext context)
    {
        foreach (var name in Supplied) context.Request.Headers.Remove(name);
        return pipeline(context);
    }
}

/// <summary>Refuses a declared body larger than the limit before any handler, and caps undeclared bodies at the same limit.</summary>
internal sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<IngressOptions> options)
{
    private readonly long limit = options.Value.Limits.MaxRequestBodyBytes;

    public Task Invoke(HttpContext context)
    {
        if (context.Request.ContentLength > limit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return Task.CompletedTask;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature
            && (feature.MaxRequestBodySize is null || feature.MaxRequestBodySize > limit))
            feature.MaxRequestBodySize = limit;
        return next(context);
    }
}

/// <summary>
/// The only source of cross-origin response headers. A listed origin receives credentialed access with its exact
/// configured value; every other origin, including <c>null</c>, receives none, and its preflight is refused with 403.
/// Cross-origin headers set by later code are replaced or removed, and every response varies by origin.
/// </summary>
internal sealed class CrossOriginMiddleware
{
    private static readonly string[] Granting =
    [
        HeaderNames.AccessControlAllowOrigin, HeaderNames.AccessControlAllowCredentials, HeaderNames.AccessControlAllowMethods,
        HeaderNames.AccessControlAllowHeaders, HeaderNames.AccessControlExposeHeaders, HeaderNames.AccessControlMaxAge
    ];
    private readonly RequestDelegate next;
    private readonly HashSet<string> origins = new(StringComparer.Ordinal);
    private readonly HashSet<string> methods;
    private readonly HashSet<string> headers;
    private readonly string allowMethods;
    private readonly string allowHeaders;
    private readonly string exposeHeaders;
    private readonly string maxAge;

    public CrossOriginMiddleware(RequestDelegate next, IOptions<IngressOptions> options)
    {
        this.next = next;
        var cors = options.Value.CrossOrigin;
        foreach (var value in cors.Origins)
            origins.Add(Origins.TryNormalize(value, out var origin, out _) ? origin
                : throw new InvalidOperationException("A configured cross-origin caller is invalid; ingress options were not validated."));
        methods = new(cors.Methods, StringComparer.Ordinal);
        headers = new(cors.Headers, StringComparer.OrdinalIgnoreCase);
        allowMethods = string.Join(", ", cors.Methods);
        allowHeaders = string.Join(", ", cors.Headers);
        exposeHeaders = string.Join(", ", cors.ExposedHeaders);
        maxAge = ((long)cors.PreflightMaxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }

    public Task Invoke(HttpContext context)
    {
        var request = context.Request;
        var origin = request.Headers.Origin;
        var listed = origin.Count == 1 && origin[0] is { } value && origins.Contains(value) ? value : null;
        if (HttpMethods.IsOptions(request.Method) && !StringValues.IsNullOrEmpty(origin)
            && request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod))
            return Preflight(context, listed);

        context.Response.OnStarting(() =>
        {
            var response = context.Response.Headers;
            foreach (var name in Granting) response.Remove(name);
            AddVary(response, "Origin");
            if (listed is not null)
            {
                response.AccessControlAllowOrigin = listed;
                response.AccessControlAllowCredentials = "true";
                if (exposeHeaders.Length != 0) response.AccessControlExposeHeaders = exposeHeaders;
            }
            return Task.CompletedTask;
        });
        return next(context);
    }

    private Task Preflight(HttpContext context, string? listed)
    {
        var request = context.Request.Headers;
        var response = context.Response;
        AddVary(response.Headers, "Origin");
        AddVary(response.Headers, HeaderNames.AccessControlRequestMethod);
        AddVary(response.Headers, HeaderNames.AccessControlRequestHeaders);
        var requested = request.AccessControlRequestHeaders.SelectMany(item => (item ?? "").Split(',')).Select(item => item.Trim())
            .Where(item => item.Length != 0).ToArray();
        if (listed is null || request.AccessControlRequestMethod.Count != 1 || !methods.Contains(request.AccessControlRequestMethod[0]!)
            || !requested.All(headers.Contains))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        response.StatusCode = StatusCodes.Status204NoContent;
        response.Headers.AccessControlAllowOrigin = listed;
        response.Headers.AccessControlAllowCredentials = "true";
        response.Headers.AccessControlAllowMethods = allowMethods;
        if (allowHeaders.Length != 0) response.Headers.AccessControlAllowHeaders = allowHeaders;
        response.Headers.AccessControlMaxAge = maxAge;
        return Task.CompletedTask;
    }

    private static void AddVary(IHeaderDictionary headers, string name)
    {
        var existing = headers.Vary.SelectMany(item => (item ?? "").Split(',')).Select(item => item.Trim());
        if (!existing.Contains(name, StringComparer.OrdinalIgnoreCase)) headers.Append(HeaderNames.Vary, name);
    }
}

/// <summary>
/// Requires a valid antiforgery token on every state-changing request that carries cookies. A request without cookies
/// is exempt only when its endpoint explicitly disables antiforgery, as an endpoint for bearer-token clients does.
/// GET, HEAD, OPTIONS and TRACE are not checked; endpoints must not change state on them.
/// </summary>
internal sealed class AntiforgeryEnforcementMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    public async Task Invoke(HttpContext context)
    {
        var method = context.Request.Method;
        if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)))
        {
            var exempt = context.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: false };
            if (context.Request.Headers.Cookie.Count != 0 || !exempt)
            {
                bool valid;
                try
                {
                    valid = await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false);
                }
                catch (BadHttpRequestException exception)
                {
                    context.Response.StatusCode = exception.StatusCode;
                    return;
                }
                catch (Exception exception) when (exception is AntiforgeryValidationException or InvalidDataException)
                {
                    // Raised, for example, when the form that would carry the token exceeds the form limits.
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                if (!valid)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
        }
        await next(context).ConfigureAwait(false);
    }
}
