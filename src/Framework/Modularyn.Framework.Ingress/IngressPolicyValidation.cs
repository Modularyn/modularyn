using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Configuration;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Modularyn.Framework.Ingress;

/// <summary>Carries no values; its startup validation checks the effective ASP.NET Core options against the ingress policy.</summary>
internal sealed class IngressPolicyCheck;

/// <summary>
/// Runs with the other startup validations, before any hosted service and so before module activation. It refuses
/// startup when other configuration has widened the options the ingress policy sets, or when antiforgery would protect
/// tokens with keys outside the declared key ring.
/// </summary>
internal sealed class IngressPolicyValidation(IServiceProvider services) : IValidateOptions<IngressPolicyCheck>
{
    public ValidateOptionsResult Validate(string? name, IngressPolicyCheck options)
    {
        IngressOptions policy;
        try
        {
            policy = services.GetRequiredService<IOptions<IngressOptions>>().Value;
        }
        catch (OptionsValidationException)
        {
            // The ingress section reports its own failures.
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();
        var hosts = policy.PublicOrigins.Select(value => Origins.TryNormalize(value, out _, out var host) ? host : "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hostFiltering = services.GetRequiredService<IOptions<HostFilteringOptions>>().Value;
        if (hostFiltering.AllowEmptyHosts || hostFiltering.IncludeFailureMessage || !hosts.SetEquals(hostFiltering.AllowedHosts))
            failures.Add("Host filtering was changed after the ingress policy; it must accept exactly the public origins' hosts.");

        var forwarded = services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
#pragma warning disable ASPDEPR005
        var networks = forwarded.KnownIPNetworks.Count + forwarded.KnownNetworks.Count;
#pragma warning restore ASPDEPR005
        if (networks != 0 || !forwarded.KnownProxies.Select(proxy => proxy.ToString()).ToHashSet(StringComparer.Ordinal).SetEquals(policy.Proxies)
            || forwarded.ForwardLimit != policy.ForwardLimit || forwarded.ForwardedHeaders.HasFlag(Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedPrefix)
            || forwarded.AllowedHosts.Count == 0 || !hosts.SetEquals(forwarded.AllowedHosts))
            failures.Add("Forwarded-header trust was changed after the ingress policy; it must trust exactly the declared proxies.");

        var redirection = services.GetRequiredService<IOptions<HttpsRedirectionOptions>>().Value;
        var hsts = services.GetRequiredService<IOptions<HstsOptions>>().Value;
        if (redirection.HttpsPort != policy.HttpsPort || hsts.MaxAge != policy.HstsMaxAge || hsts.IncludeSubDomains != policy.HstsIncludeSubDomains
            || hsts.Preload != policy.HstsPreload || hsts.ExcludedHosts.Count != 0)
            failures.Add("HTTPS redirection or HSTS was changed after the ingress policy; they must use the declared port and lifetime for every host.");

        var antiforgery = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        if (antiforgery.Cookie.Name != IngressServiceCollectionExtensions.AntiforgeryCookieName || antiforgery.Cookie.SecurePolicy != CookieSecurePolicy.Always
            || antiforgery.Cookie.SameSite != SameSiteMode.Strict || !antiforgery.Cookie.HttpOnly || antiforgery.Cookie.Path != "/" || antiforgery.Cookie.Domain is not null)
            failures.Add("The antiforgery cookie was changed after the ingress policy; it must stay host-only, Secure, HttpOnly and SameSite=Strict.");

        var limits = policy.Limits;
        var kestrel = services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        var form = services.GetRequiredService<IOptions<FormOptions>>().Value;
        var json = services.GetRequiredService<IOptions<HttpJsonOptions>>().Value;
        if (kestrel.MaxRequestBodySize != limits.MaxRequestBodyBytes || kestrel.MaxRequestHeadersTotalSize != limits.MaxRequestHeadersTotalBytes
            || kestrel.MaxRequestHeaderCount != limits.MaxRequestHeaderCount || kestrel.MaxRequestLineSize != limits.MaxRequestLineBytes
            || kestrel.RequestHeadersTimeout != limits.RequestHeadersTimeout || form.ValueCountLimit != limits.MaxFormValueCount
            || form.ValueLengthLimit != limits.MaxFormValueBytes || form.KeyLengthLimit != limits.MaxFormKeyBytes
            || form.MultipartBodyLengthLimit != limits.MaxRequestBodyBytes || json.SerializerOptions.MaxDepth != limits.MaxJsonDepth)
            failures.Add("Request limits were changed after the ingress policy; the server, form and JSON limits must match the declared limits.");

        if (services.GetService<DeclaredKeyRing>() is null)
            failures.Add("Antiforgery tokens are protected by data protection, so the key ring must be declared with AddModularynDataProtection.");
        else
        {
            try
            {
                if (services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository is null)
                    failures.Add("The data-protection key store was removed after the key ring was declared.");
            }
            catch (OptionsValidationException)
            {
                // The key-ring section reports its own failures.
            }
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
