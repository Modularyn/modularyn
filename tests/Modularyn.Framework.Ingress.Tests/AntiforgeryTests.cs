using System.Net;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

// AC-003-N02 row 1 exercised at the framework layer. Session cookies and signed-out sessions remain with the identity work.
public sealed class AntiforgeryTests
{
    [Fact(DisplayName = "AC-003-N02 state changes without a valid antiforgery token are refused")]
    public async Task AC_003_N02_State_changes_without_a_valid_antiforgery_token_are_refused()
    {
        await using var server = await StartAsync(IngressServer.Settings());
        var (cookie, token) = await IssueAsync(server);
        var (otherCookie, otherToken) = await IssueAsync(server);

        Assert.Equal(HttpStatusCode.BadRequest, (await Change(server, "/change", cookie, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(server, "/change", null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(server, "/change", null, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(server, "/change", cookie, otherToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(server, "/change", otherCookie + "x", otherToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(server, "/change", cookie, null, HttpMethod.Delete)).StatusCode);
        Assert.Equal(0, server.Changes);

        // A safe method never reaches the state-changing handler.
        var safe = await server.SendAsync(true, HttpMethod.Get, "/change", request => request.Headers.Add("Cookie", cookie));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, safe.StatusCode);
        Assert.Equal(0, server.Changes);

        Assert.Equal(HttpStatusCode.NoContent, (await Change(server, "/change", cookie, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Change(server, "/change", otherCookie, otherToken)).StatusCode);
        Assert.Equal(2, server.Changes);
    }

    [Fact(DisplayName = "TM-G02-007 only an explicit exemption without cookies skips antiforgery")]
    public async Task TM_G02_007_Only_an_explicit_exemption_without_cookies_skips_antiforgery()
    {
        await using var server = await StartAsync(IngressServer.Settings());
        Assert.Equal(HttpStatusCode.NoContent, (await Change(server, "/bearer", null, null)).StatusCode);
        Assert.Equal(1, server.Changes);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(server, "/bearer", "session=present", null)).StatusCode);
        var (cookie, token) = await IssueAsync(server);
        Assert.Equal(HttpStatusCode.NoContent, (await Change(server, "/bearer", cookie, token)).StatusCode);
        Assert.Equal(2, server.Changes);
    }

    [Fact(DisplayName = "TM-G02-007 the antiforgery cookie is host-only, Secure, HttpOnly and SameSite=Strict")]
    public async Task TM_G02_007_The_antiforgery_cookie_is_host_only_secure_http_only_and_same_site_strict()
    {
        await using var server = await StartAsync(IngressServer.Settings());
        var response = await server.SendAsync(true, HttpMethod.Get, "/token");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(IngressServiceCollectionExtensions.AntiforgeryCookieName + "=", StringComparison.Ordinal));
        var attributes = cookie.Split(';').Skip(1).Select(part => part.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("secure", attributes);
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain=", StringComparison.Ordinal));
        Assert.StartsWith("__Host-", IngressServiceCollectionExtensions.AntiforgeryCookieName, StringComparison.Ordinal);
    }

    private static async Task<(string Cookie, string Token)> IssueAsync(IngressServer server)
    {
        var response = await server.SendAsync(true, HttpMethod.Get, "/token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(IngressServiceCollectionExtensions.AntiforgeryCookieName + "=", StringComparison.Ordinal));
        return (cookie.Split(';')[0], await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static Task<HttpResponseMessage> Change(IngressServer server, string path, string? cookie, string? token, HttpMethod? method = null) =>
        server.SendAsync(true, method ?? HttpMethod.Post, path, request =>
        {
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            if (token is not null) request.Headers.Add("RequestVerificationToken", token);
            request.Content = new StringContent("");
        });
}
