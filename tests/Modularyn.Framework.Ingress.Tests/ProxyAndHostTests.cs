using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

// AC-004-N01 rows exercised at the framework layer: forwarded values from a non-proxy source, forwarded values through the
// declared proxy, an unconfigured host, and return addresses. Tenant membership remains with the tenancy work.
public sealed class ProxyAndHostTests
{
    [Fact(DisplayName = "AC-004-N01 forwarded values from a source that is not the declared proxy are ignored")]
    public async Task AC_004_N01_Forwarded_values_from_a_source_that_is_not_the_declared_proxy_are_ignored()
    {
        await using var server = await StartAsync(Settings(("Proxies:0", "192.0.2.10")));
        var echo = await EchoAsync(await server.SendAsync(true, HttpMethod.Get, "/echo", Forge));
        Assert.Equal("https", echo.Scheme);
        Assert.Equal(PublicHost, echo.Host);
        Assert.Equal("127.0.0.1", echo.Remote);
        Assert.Empty(echo.Forwarded);
        Assert.Equal("https://" + PublicHost, echo.Origin);

        var plain = await server.SendAsync(false, HttpMethod.Get, "/echo?x=1", request =>
        {
            Forge(request);
            request.Headers.Add("X-Forwarded-Proto", "https");
        });
        Assert.Equal(HttpStatusCode.PermanentRedirect, plain.StatusCode);
        Assert.Equal("https://" + PublicHost + "/echo?x=1", plain.Headers.Location!.OriginalString);
        Assert.Equal(1, server.Handled);
    }

    [Fact(DisplayName = "AC-004-N01 the declared proxy's scheme, host and client are honored only within configured hosts")]
    public async Task AC_004_N01_The_declared_proxy_s_scheme_host_and_client_are_honored_only_within_configured_hosts()
    {
        await using var server = await StartAsync(Settings(("Proxies:0", "127.0.0.1")));
        var forwarded = await server.SendAsync(false, HttpMethod.Get, "/echo", request =>
        {
            request.Headers.Add("X-Forwarded-For", "198.51.100.7");
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", OtherTenantHost);
        });
        Assert.True(forwarded.Headers.Contains("Strict-Transport-Security"));
        var echo = await EchoAsync(forwarded);
        Assert.Equal("https", echo.Scheme);
        Assert.Equal(OtherTenantHost, echo.Host);
        Assert.Equal("198.51.100.7", echo.Remote);
        Assert.Equal("https://" + OtherTenantHost, echo.Origin);
        Assert.DoesNotContain(echo.Forwarded, name => name.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase));

        // Only the nearest entry counts, so a client cannot prepend its own address.
        var chained = await EchoAsync(await server.SendAsync(false, HttpMethod.Get, "/echo", request =>
        {
            request.Headers.Add("X-Forwarded-For", "203.0.113.9, 198.51.100.7");
            request.Headers.Add("X-Forwarded-Proto", "https");
        }));
        Assert.Equal("198.51.100.7", chained.Remote);

        // An unconfigured forwarded host is never applied, even from the declared proxy.
        var unconfigured = await server.SendAsync(false, HttpMethod.Get, "/echo", request =>
        {
            request.Headers.Add("X-Forwarded-For", "198.51.100.7");
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", "attacker.example.net");
        });
        var kept = await EchoAsync(unconfigured);
        Assert.Equal(PublicHost, kept.Host);
        Assert.Equal("https://" + PublicHost, kept.Origin);
        Assert.DoesNotContain(kept.Forwarded, name => name.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "AC-004-N01 unconfigured and missing hosts are refused before any handler")]
    public async Task AC_004_N01_Unconfigured_and_missing_hosts_are_refused_before_any_handler()
    {
        await using var server = await StartAsync(Settings(("Proxies:0", "127.0.0.1")));
        foreach (var https in new[] { true, false })
        {
            var response = await server.SendAsync(https, HttpMethod.Get, "/", host: "attacker.example.net");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
        Assert.StartsWith("HTTP/1.1 400", await server.RawAsync("GET / HTTP/1.0\r\n\r\n"), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 400", await server.RawAsync("GET / HTTP/1.1\r\nHost: \r\nConnection: close\r\n\r\n"), StringComparison.Ordinal);
        Assert.Equal(0, server.Handled);
    }

    [Theory(DisplayName = "AC-004-N01 return addresses outside the configured origins are refused")]
    [InlineData("/", true)]
    [InlineData("/projects/7?tab=notes#top", true)]
    [InlineData("https://app.example.test/projects", true)]
    [InlineData("https://APP.example.test:443/projects", true)]
    [InlineData("https://tenant-b.example.test", true)]
    [InlineData("", false)]
    [InlineData("//attacker.example.net/", false)]
    [InlineData("/\\attacker.example.net", false)]
    [InlineData("\\\\attacker.example.net", false)]
    [InlineData("https://attacker.example.net/", false)]
    [InlineData("http://app.example.test/projects", false)]
    [InlineData("https://app.example.test:8443/projects", false)]
    [InlineData("https://app.example.test@attacker.example.net/", false)]
    [InlineData("https://app.example.test.attacker.example.net/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" /projects", false)]
    [InlineData("/projects\r\nLocation: https://attacker.example.net", false)]
    [InlineData("projects", false)]
    public async Task AC_004_N01_Return_addresses_outside_the_configured_origins_are_refused(string target, bool allowed)
    {
        await using var server = await StartAsync(Settings());
        var origins = server.Services.GetRequiredService<PublicOrigins>();
        Assert.Equal(new[] { "https://" + PublicHost, "https://" + OtherTenantHost }, origins.Values);
        Assert.Equal(allowed, origins.IsAllowedRedirect(target));
        Assert.False(origins.IsAllowedRedirect(null));
    }

    private static void Forge(HttpRequestMessage request)
    {
        request.Headers.Add("X-Forwarded-For", "198.51.100.7");
        request.Headers.Add("X-Forwarded-Host", OtherTenantHost);
        request.Headers.Add("X-Forwarded-Prefix", "/admin");
        request.Headers.Add("X-Original-For", "198.51.100.8");
        request.Headers.Add("Forwarded", "for=198.51.100.7;proto=https;host=" + OtherTenantHost);
    }
}
