using System.Net;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

// AC-031-P01 rows 3 and 4 exercised at the framework layer on the reference ingress.
public sealed class HttpsTests
{
    [Fact(DisplayName = "AC-031-P01 plain HTTP is redirected before any processing and serves no content or cookies")]
    public async Task AC_031_P01_Plain_HTTP_is_redirected_before_any_processing_and_serves_no_content_or_cookies()
    {
        await using var server = await StartAsync(IngressServer.Settings());
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            var response = await server.SendAsync(false, method, method == HttpMethod.Get ? "/?q=1" : "/change", request =>
                request.Headers.Add("Cookie", "session=present"));
            Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
            Assert.Equal(method == HttpMethod.Get ? "https://" + PublicHost + "/?q=1" : "https://" + PublicHost + "/change",
                response.Headers.Location!.OriginalString);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, server.Handled);
        Assert.Equal(0, server.Changes);

        await using var custom = await StartAsync(IngressServer.Settings(("HttpsPort", "8443")));
        var redirected = await custom.SendAsync(false, HttpMethod.Get, "/echo");
        Assert.Equal("https://" + PublicHost + ":8443/echo", redirected.Headers.Location!.OriginalString);
    }

    [Fact(DisplayName = "AC-031-P01 every HTTPS response for a configured host carries the declared HSTS lifetime")]
    public async Task AC_031_P01_Every_HTTPS_response_for_a_configured_host_carries_the_declared_HSTS_lifetime()
    {
        await using var server = await StartAsync(IngressServer.Settings(("Limits:MaxRequestBodyBytes", "16"), ("Limits:MaxFormValueBytes", "16")));
        var responses = new[]
        {
            await server.SendAsync(true, HttpMethod.Get, "/"),
            await server.SendAsync(true, HttpMethod.Get, "/missing"),
            await server.SendAsync(true, HttpMethod.Post, "/change"),
            await server.SendAsync(true, HttpMethod.Options, "/", request =>
            {
                request.Headers.Add("Origin", "https://attacker.example.net");
                request.Headers.Add("Access-Control-Request-Method", "POST");
            }),
        };
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.Forbidden },
            responses.Select(response => response.StatusCode));
        Assert.All(responses, response => Assert.Equal("max-age=31536000", Assert.Single(response.Headers.GetValues("Strict-Transport-Security"))));
        // The refused upload is sent whole before its response is read; see RequestLimitTests.
        var oversized = await server.RawHttpsAsync(RequestLimitTests.Head("POST /upload", "Content-Length: 64"), new byte[64]);
        Assert.Equal(413, oversized.Status);
        Assert.Equal("max-age=31536000", oversized.Headers["Strict-Transport-Security"]);

        await using var preload = await StartAsync(IngressServer.Settings(("HstsMaxAge", "730.00:00:00"), ("HstsIncludeSubDomains", "true"), ("HstsPreload", "true")));
        Assert.Equal("max-age=63072000; includeSubDomains; preload",
            Assert.Single((await preload.SendAsync(true, HttpMethod.Get, "/")).Headers.GetValues("Strict-Transport-Security")));
    }

    [Theory(DisplayName = "AC-031-P01 a forwarded HTTPS scheme counts only from the declared proxy")]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.0.2.10", false)]
    [InlineData(null, false)]
    public async Task AC_031_P01_A_forwarded_HTTPS_scheme_counts_only_from_the_declared_proxy(string? proxy, bool honored)
    {
        await using var server = await StartAsync(proxy is null ? IngressServer.Settings() : IngressServer.Settings(("Proxies:0", proxy)));
        var response = await server.SendAsync(false, HttpMethod.Get, "/", request => request.Headers.Add("X-Forwarded-Proto", "https"));
        if (honored)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.Contains("Strict-Transport-Security"));
            Assert.True(response.Headers.Contains("Set-Cookie"));
        }
        else
        {
            Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Equal(0, server.Handled);
        }
    }
}
