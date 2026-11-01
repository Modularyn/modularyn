using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

public sealed class IngressOptionsTests
{
    [Theory(DisplayName = "TM-G02-048 invalid ingress options refuse startup before the server listens")]
    [InlineData("no-origin", "'Modularyn:Ingress:PublicOrigins' must list at least one public HTTPS origin.")]
    [InlineData("http-origin", "'Modularyn:Ingress:PublicOrigins:0' must be an exact HTTPS origin")]
    [InlineData("path-origin", "'Modularyn:Ingress:PublicOrigins:0' must be an exact HTTPS origin")]
    [InlineData("wildcard-origin", "'Modularyn:Ingress:PublicOrigins:0' must be an exact HTTPS origin")]
    [InlineData("userinfo-origin", "'Modularyn:Ingress:PublicOrigins:0' must be an exact HTTPS origin")]
    [InlineData("duplicate-origin", "'Modularyn:Ingress:PublicOrigins:1' repeats an origin.")]
    [InlineData("proxy-network", "'Modularyn:Ingress:Proxies:0' must be one exact, canonical IP address")]
    [InlineData("proxy-any", "'Modularyn:Ingress:Proxies:0' must be one exact, canonical IP address")]
    [InlineData("proxy-short-form", "'Modularyn:Ingress:Proxies:0' must be one exact, canonical IP address")]
    [InlineData("forward-limit", "'Modularyn:Ingress:ForwardLimit' must be from 1 to 8.")]
    [InlineData("https-port", "'Modularyn:Ingress:HttpsPort' must be a TCP port from 1 to 65535.")]
    [InlineData("short-hsts", "'Modularyn:Ingress:HstsMaxAge' must declare whole seconds from one day to two years.")]
    [InlineData("preload-without-subdomains", "'Modularyn:Ingress:HstsPreload' requires HstsIncludeSubDomains")]
    [InlineData("cors-wildcard", "'Modularyn:Ingress:CrossOrigin:Origins:0' must be an exact HTTPS origin")]
    [InlineData("cors-null", "'Modularyn:Ingress:CrossOrigin:Origins:0' must be an exact HTTPS origin")]
    [InlineData("cors-plain-http", "'Modularyn:Ingress:CrossOrigin:Origins:0' must be an exact HTTPS origin")]
    [InlineData("cors-without-methods", "'Modularyn:Ingress:CrossOrigin:Methods' must list the methods")]
    [InlineData("cors-wildcard-method", "'Modularyn:Ingress:CrossOrigin:Methods:0' must be one exact name")]
    [InlineData("cors-wildcard-header", "'Modularyn:Ingress:CrossOrigin:Headers:0' must be one exact name")]
    [InlineData("cors-header-list", "'Modularyn:Ingress:CrossOrigin:Headers:0' must be one exact name")]
    [InlineData("methods-without-origins", "'Modularyn:Ingress:CrossOrigin' declares methods or headers without any listed origin.")]
    [InlineData("body-limit", "'Modularyn:Ingress:Limits:MaxRequestBodyBytes' must be from 1 byte to 1 GiB.")]
    [InlineData("line-over-headers", "'Modularyn:Ingress:Limits:MaxRequestLineBytes' must be from 256 bytes to MaxRequestHeadersTotalBytes.")]
    [InlineData("json-depth", "'Modularyn:Ingress:Limits:MaxJsonDepth' must be from 1 to 256.")]
    [InlineData("unknown-key", "contains keys that its options do not declare: 'Proxy'.")]
    public async Task TM_G02_048_Invalid_ingress_options_refuse_startup_before_the_server_listens(string mutation, string expected)
    {
        var configuration = IngressServer.Settings();
        var key = "Modularyn:Ingress:";
        switch (mutation)
        {
            case "no-origin": configuration.Remove(key + "PublicOrigins:0"); configuration.Remove(key + "PublicOrigins:1"); break;
            case "http-origin": configuration[key + "PublicOrigins:0"] = "http://" + PublicHost; break;
            case "path-origin": configuration[key + "PublicOrigins:0"] = "https://" + PublicHost + "/app"; break;
            case "wildcard-origin": configuration[key + "PublicOrigins:0"] = "https://*.example.test"; break;
            case "userinfo-origin": configuration[key + "PublicOrigins:0"] = "https://operator@" + PublicHost; break;
            case "duplicate-origin": configuration[key + "PublicOrigins:1"] = "https://APP.example.test:443/"; break;
            case "proxy-network": configuration[key + "Proxies:0"] = "10.0.0.0/8"; break;
            case "proxy-any": configuration[key + "Proxies:0"] = "0.0.0.0"; break;
            case "proxy-short-form": configuration[key + "Proxies:0"] = "10.1"; break;
            case "forward-limit": configuration[key + "ForwardLimit"] = "0"; break;
            case "https-port": configuration[key + "HttpsPort"] = "0"; break;
            case "short-hsts": configuration[key + "HstsMaxAge"] = "00:05:00"; break;
            case "preload-without-subdomains": configuration[key + "HstsPreload"] = "true"; break;
            case "cors-wildcard": Cors(configuration, "*"); break;
            case "cors-null": Cors(configuration, "null"); break;
            case "cors-plain-http": Cors(configuration, "http://origin-1.example.test"); break;
            case "cors-without-methods": configuration[key + "CrossOrigin:Origins:0"] = ListedOrigin; break;
            case "cors-wildcard-method": Cors(configuration, ListedOrigin, "*"); break;
            case "cors-wildcard-header": Cors(configuration, ListedOrigin); configuration[key + "CrossOrigin:Headers:0"] = "*"; break;
            case "cors-header-list": Cors(configuration, ListedOrigin); configuration[key + "CrossOrigin:Headers:0"] = "Content-Type, X-Other"; break;
            case "methods-without-origins": configuration[key + "CrossOrigin:Methods:0"] = "GET"; break;
            case "body-limit": configuration[key + "Limits:MaxRequestBodyBytes"] = "0"; break;
            case "line-over-headers": configuration[key + "Limits:MaxRequestLineBytes"] = "65536"; break;
            case "json-depth": configuration[key + "Limits:MaxJsonDepth"] = "0"; break;
            default: configuration[key + "Proxy"] = "192.0.2.10"; break;
        }
        // Kestrel reads its limits while the application is built, so an invalid section can already refuse construction.
        WebApplication? application = null;
        try
        {
            var refused = await Record.ExceptionAsync(async () =>
            {
                application = Build(configuration);
                application.UseModularynIngress();
                await application.StartAsync(TestContext.Current.CancellationToken);
            });
            var exception = Assert.IsType<OptionsValidationException>(refused);
            Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
            if (application is not null)
            {
                Assert.False(application.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
                Assert.Empty(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            }
        }
        finally
        {
            if (application is not null) await application.DisposeAsync();
        }
    }

    [Theory(DisplayName = "TM-G02-006 ingress options widened elsewhere, or a missing key ring, refuse startup before module activation")]
    [InlineData("host-filtering", "Host filtering was changed after the ingress policy")]
    [InlineData("empty-hosts", "Host filtering was changed after the ingress policy")]
    [InlineData("forwarded-network", "Forwarded-header trust was changed after the ingress policy")]
    [InlineData("forwarded-prefix", "Forwarded-header trust was changed after the ingress policy")]
    [InlineData("https-port", "HTTPS redirection or HSTS was changed after the ingress policy")]
    [InlineData("hsts-excluded-host", "HTTPS redirection or HSTS was changed after the ingress policy")]
    [InlineData("antiforgery-cookie", "The antiforgery cookie was changed after the ingress policy")]
    [InlineData("body-limit", "Request limits were changed after the ingress policy")]
    [InlineData("json-depth", "Request limits were changed after the ingress policy")]
    [InlineData("no-key-ring", "the key ring must be declared with AddModularynDataProtection")]
    public async Task TM_G02_006_Widened_ingress_options_or_a_missing_key_ring_refuse_startup_before_module_activation(string mutation, string expected)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(IngressServer.Settings(("Proxies:0", "192.0.2.10")));
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, 0));
        if (mutation != "no-key-ring") builder.Services.AddModularynDataProtection(builder.Configuration);
        builder.Services.AddModularynIngress(builder.Configuration);
        switch (mutation)
        {
            case "host-filtering": builder.Services.PostConfigure<HostFilteringOptions>(options => options.AllowedHosts = ["*"]); break;
            case "empty-hosts": builder.Services.PostConfigure<HostFilteringOptions>(options => options.AllowEmptyHosts = true); break;
            case "forwarded-network": builder.Services.PostConfigure<ForwardedHeadersOptions>(options => options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("0.0.0.0/0"))); break;
            case "forwarded-prefix": builder.Services.PostConfigure<ForwardedHeadersOptions>(options => options.ForwardedHeaders |= ForwardedHeaders.XForwardedPrefix); break;
            case "https-port": builder.Services.PostConfigure<HttpsRedirectionOptions>(options => options.HttpsPort = 444); break;
            case "hsts-excluded-host": builder.Services.PostConfigure<HstsOptions>(options => options.ExcludedHosts.Add(PublicHost)); break;
            case "antiforgery-cookie": builder.Services.PostConfigure<AntiforgeryOptions>(options => options.Cookie.SameSite = SameSiteMode.Lax); break;
            case "body-limit": builder.Services.PostConfigure<KestrelServerOptions>(options => options.Limits.MaxRequestBodySize = null); break;
            case "json-depth": builder.Services.PostConfigure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options => options.SerializerOptions.MaxDepth = 1000); break;
        }
        var constructed = 0;
        builder.Services.AddModularynModules(new CompositionEnvironment(SemanticVersion.Parse("10.0.0"), SemanticVersion.Parse("1.0.0")),
            [new ModuleRegistration(new ModuleManifest("A", SemanticVersion.Parse("1.0.0")), _ => { constructed++; return new IdleModule(); })],
            new ModuleHostOptions { AllowUnverifiedComposition = true });
        await using var application = builder.Build();
        application.UseModularynIngress();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => application.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, constructed);
        Assert.Empty(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
    }

    [Fact]
    public async Task The_ingress_pipeline_requires_its_registration()
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var application = builder.Build();
        Assert.Throws<InvalidOperationException>(() => application.UseModularynIngress());
    }

    private sealed class IdleModule : IModule
    {
        public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void Cors(Dictionary<string, string?> configuration, string origin, string method = "GET")
    {
        configuration["Modularyn:Ingress:CrossOrigin:Origins:0"] = origin;
        configuration["Modularyn:Ingress:CrossOrigin:Methods:0"] = method;
    }
}
