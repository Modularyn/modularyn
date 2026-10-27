using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Configuration;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Modularyn.Framework.Ingress;

public static class IngressServiceCollectionExtensions
{
    /// <summary>The antiforgery cookie: host-only, Secure, HttpOnly and SameSite=Strict.</summary>
    public const string AntiforgeryCookieName = "__Host-Modularyn-Antiforgery";

    /// <summary>
    /// Registers ingress policy from the deployment section, validated before startup: accepted hosts and public origins,
    /// declared proxies, HTTPS redirection and HSTS, cross-origin callers, request limits and antiforgery. The policy is
    /// applied after other configuration of the same ASP.NET Core options, and startup is refused if the effective
    /// options still differ from it or if the data-protection key ring was not declared with AddModularynDataProtection.
    /// </summary>
    public static IServiceCollection AddModularynIngress(this IServiceCollection services, IConfiguration configuration, string sectionPath = "Modularyn:Ingress")
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddModularynDeploymentOptions<IngressOptions>(configuration, sectionPath);
        services.AddSingleton<IValidateOptions<IngressOptions>>(new IngressOptionsValidation(sectionPath));
        services.AddSingleton<PublicOrigins>();
        services.AddSingleton<IngressMarker>();
        services.AddSingleton<IngressPipeline>();
        services.AddTransient<IStartupFilter, AuthorizationOrderCheck>();
        services.AddSingleton<IValidateOptions<IngressPolicyCheck>>(provider => new IngressPolicyValidation(provider));
        services.AddOptions<IngressPolicyCheck>().ValidateOnStart();
        services.AddAntiforgery();

        services.AddOptions<ForwardedHeadersOptions>().PostConfigure<IOptions<IngressOptions>>((options, ingress) =>
        {
            var policy = ingress.Value;
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = policy.ForwardLimit;
            options.RequireHeaderSymmetry = false;
            options.KnownIPNetworks.Clear();
#pragma warning disable ASPDEPR005
            options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
            options.KnownProxies.Clear();
            foreach (var proxy in policy.Proxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
            options.AllowedHosts = Hosts(policy);
        });
        services.AddOptions<HostFilteringOptions>().PostConfigure<IOptions<IngressOptions>>((options, ingress) =>
        {
            options.AllowedHosts = Hosts(ingress.Value);
            options.AllowEmptyHosts = false;
            options.IncludeFailureMessage = false;
        });
        services.AddOptions<HttpsRedirectionOptions>().PostConfigure<IOptions<IngressOptions>>((options, ingress) =>
        {
            options.HttpsPort = ingress.Value.HttpsPort;
            options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
        });
        services.AddOptions<HstsOptions>().PostConfigure<IOptions<IngressOptions>>((options, ingress) =>
        {
            options.MaxAge = ingress.Value.HstsMaxAge;
            options.IncludeSubDomains = ingress.Value.HstsIncludeSubDomains;
            options.Preload = ingress.Value.HstsPreload;
            options.ExcludedHosts.Clear();
        });
        services.AddOptions<AntiforgeryOptions>().PostConfigure(options =>
        {
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.Domain = null;
        });
        services.AddOptions<KestrelServerOptions>().PostConfigure<IOptions<IngressOptions>>((options, ingress) =>
        {
            var limits = ingress.Value.Limits;
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
            options.Limits.MaxRequestHeadersTotalSize = limits.MaxRequestHeadersTotalBytes;
            options.Limits.MaxRequestHeaderCount = limits.MaxRequestHeaderCount;
            options.Limits.MaxRequestLineSize = limits.MaxRequestLineBytes;
            options.Limits.RequestHeadersTimeout = limits.RequestHeadersTimeout;
        });
        services.AddOptions<FormOptions>().PostConfigure<IOptions<IngressOptions>>((options, ingress) =>
        {
            var limits = ingress.Value.Limits;
            options.ValueCountLimit = limits.MaxFormValueCount;
            options.ValueLengthLimit = limits.MaxFormValueBytes;
            options.KeyLengthLimit = limits.MaxFormKeyBytes;
            options.MultipartBodyLengthLimit = limits.MaxRequestBodyBytes;
            options.BufferBodyLengthLimit = limits.MaxRequestBodyBytes;
        });
        services.AddOptions<HttpJsonOptions>().PostConfigure<IOptions<IngressOptions>>((options, ingress) =>
            options.SerializerOptions.MaxDepth = ingress.Value.Limits.MaxJsonDepth);
        return services;
    }

    private static string[] Hosts(IngressOptions policy) => policy.PublicOrigins
        .Select(value => Origins.TryNormalize(value, out _, out var host) ? host
            : throw new InvalidOperationException("A configured public origin is invalid; ingress options were not validated."))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public static class IngressApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the ingress policy at the start of the pipeline, in order: declared-proxy forwarded headers, host filtering,
    /// HTTPS redirection, HSTS, the request body limit, cross-origin policy, authentication when authentication services
    /// are registered, and antiforgery enforcement. Authentication therefore never runs for a plain-HTTP or
    /// unconfigured-host request, and antiforgery tokens are validated against the authenticated user they were issued
    /// to. Call it before any other middleware and do not call UseAuthentication yourself; call UseAuthorization after it,
    /// and after tenancy when the application uses it. Endpoint metadata is available when routing runs first, as
    /// WebApplication arranges by default.
    /// </summary>
    public static IApplicationBuilder UseModularynIngress(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService<IngressMarker>() is null)
            throw new InvalidOperationException("Call AddModularynIngress before UseModularynIngress.");
        if (app.Properties.ContainsKey(AuthenticationSet) || app.Properties.ContainsKey(AuthorizationSet))
            throw new InvalidOperationException("UseAuthentication or UseAuthorization was called before UseModularynIngress. Remove UseAuthentication, "
                + "which the ingress places after its transport checks, and call UseAuthorization after the ingress.");
        // Kept so that the startup check can see whether the application placed authorization after the ingress.
        app.ApplicationServices.GetRequiredService<IngressPipeline>().Application = app;
        app.UseMiddleware<ProxyTrustMiddleware>();
        app.UseHostFiltering();
        app.UseHttpsRedirection();
        app.UseHsts();
        app.UseMiddleware<RequestBodyLimitMiddleware>();
        app.UseMiddleware<CrossOriginMiddleware>();
        // Setting this key, as UseAuthentication does, also stops WebApplication from adding authentication ahead of this pipeline.
        if (app.ApplicationServices.GetService<IServiceProviderIsService>()?.IsService(typeof(IAuthenticationSchemeProvider)) is true) app.UseAuthentication();
        app.UseMiddleware<AntiforgeryEnforcementMiddleware>();
        return app;
    }

    // The property keys that ASP.NET Core's UseAuthentication and UseAuthorization set, and that WebApplication reads before
    // adding either middleware ahead of the application's own pipeline. Authorization placed before the ingress would run
    // before authentication.
    private const string AuthenticationSet = "__AuthenticationMiddlewareSet";
    internal const string AuthorizationSet = "__AuthorizationMiddlewareSet";
}

internal sealed class IngressMarker;

/// <summary>The application pipeline that called UseModularynIngress, if any.</summary>
internal sealed class IngressPipeline
{
    internal IApplicationBuilder? Application { get; set; }
}

/// <summary>
/// Refuses to start a host that registered authorization services but never called UseAuthorization after the ingress.
/// WebApplication would otherwise add authorization ahead of the whole pipeline when it starts, so it would run before
/// the ingress and before authentication. The check runs before WebApplication adds it, after the application's own
/// pipeline is complete.
/// </summary>
internal sealed class AuthorizationOrderCheck(IngressPipeline pipeline, IServiceProvider services) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
    {
        if (pipeline.Application is { } application && !application.Properties.ContainsKey(IngressApplicationBuilderExtensions.AuthorizationSet)
            && services.GetService<IServiceProviderIsService>()?.IsService(typeof(IAuthorizationHandlerProvider)) is true)
            throw new InvalidOperationException("Authorization services are registered, but UseAuthorization is not called after UseModularynIngress, "
                + "so authorization would run ahead of the ingress and authentication. Call UseAuthorization after UseModularynIngress, and after "
                + "UseModularynTenancy when the application uses tenancy.");
        next(builder);
    };
}
