using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

// The ingress places authentication after its transport checks and before antiforgery; authorization follows the ingress.
public sealed class AuthenticationOrderTests
{
    private const string ProbeScheme = "probe";
    private const string Cookie = "probe-user";

    /// <summary>Authenticates the user named by a cookie, and counts how often it is asked.</summary>
    private sealed class ProbeHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ProbeCounter counter)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Interlocked.Increment(ref counter.Calls);
            if (Request.Cookies[Cookie] is not { Length: > 0 } user) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], ProbeScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ProbeScheme)));
        }
    }

    private sealed class ProbeCounter
    {
        public int Calls;
    }

    private static Task<IngressServer> StartAsync(ProbeCounter counter) => IngressServer.StartAsync(Settings(), services =>
    {
        services.AddSingleton(counter);
        services.AddAuthentication(ProbeScheme).AddScheme<AuthenticationSchemeOptions, ProbeHandler>(ProbeScheme, _ => { });
    }, application =>
    {
        application.MapGet("/whoami", (HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } user
            ? Results.Text(user) : Results.StatusCode(StatusCodes.Status401Unauthorized));
        application.MapGet("/user-token", (HttpContext context, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(context).RequestToken);
    });

    [Fact(DisplayName = "TM-G02-060 authentication never runs for a plain-HTTP or unconfigured-host request")]
    public async Task TM_G02_060_Authentication_never_runs_for_a_plain_http_or_unconfigured_host_request()
    {
        var counter = new ProbeCounter();
        await using var server = await StartAsync(counter);
        var plain = await server.SendAsync(false, HttpMethod.Get, "/whoami", request => request.Headers.Add("Cookie", Cookie + "=alice"));
        Assert.Equal(HttpStatusCode.PermanentRedirect, plain.StatusCode);
        var unconfigured = await server.SendAsync(true, HttpMethod.Get, "/whoami", request => request.Headers.Add("Cookie", Cookie + "=alice"), host: "other.example.test");
        Assert.Equal(HttpStatusCode.BadRequest, unconfigured.StatusCode);
        Assert.Equal(0, Volatile.Read(ref counter.Calls));

        var served = await server.SendAsync(true, HttpMethod.Get, "/whoami", request => request.Headers.Add("Cookie", Cookie + "=alice"));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("alice", await served.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref counter.Calls));

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.SendAsync(true, HttpMethod.Get, "/whoami")).StatusCode);
        Assert.Equal(2, Volatile.Read(ref counter.Calls));
    }

    [Fact(DisplayName = "TM-G02-007 antiforgery tokens are validated against the authenticated user they were issued to")]
    public async Task TM_G02_007_Antiforgery_tokens_are_validated_against_the_authenticated_user()
    {
        await using var server = await StartAsync(new ProbeCounter());
        async Task<(string Cookie, string Token)> TokenAsync(string? user)
        {
            var response = await server.SendAsync(true, HttpMethod.Get, "/user-token", request =>
            {
                if (user is not null) request.Headers.Add("Cookie", Cookie + "=" + user);
            });
            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(IngressServiceCollectionExtensions.AntiforgeryCookieName + "=", StringComparison.Ordinal));
            return (cookie.Split(';')[0], await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        Task<HttpResponseMessage> Change(string antiforgeryCookie, string token, string? user) => server.SendAsync(true, HttpMethod.Post, "/change", request =>
        {
            request.Headers.Add("Cookie", antiforgeryCookie + (user is null ? "" : "; " + Cookie + "=" + user));
            request.Headers.Add("RequestVerificationToken", token);
            request.Content = new StringContent("");
        });

        var (aliceCookie, aliceToken) = await TokenAsync("alice");
        var (anonymousCookie, anonymousToken) = await TokenAsync(null);
        Assert.Equal(HttpStatusCode.NoContent, (await Change(aliceCookie, aliceToken, "alice")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(aliceCookie, aliceToken, "bob")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(aliceCookie, aliceToken, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(anonymousCookie, anonymousToken, "alice")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Change(anonymousCookie, anonymousToken, null)).StatusCode);
        Assert.Equal(2, server.Changes);
    }

    [Fact(DisplayName = "TM-G02-060 authentication or authorization added before the ingress is refused")]
    public async Task TM_G02_060_Authentication_or_authorization_added_before_the_ingress_is_refused()
    {
        foreach (var early in new Action<WebApplication>[] { application => application.UseAuthentication(), application => application.UseAuthorization() })
        {
            await using var application = Build(Settings(), services =>
            {
                services.AddAuthentication(ProbeScheme).AddScheme<AuthenticationSchemeOptions, ProbeHandler>(ProbeScheme, _ => { });
                services.AddSingleton(new ProbeCounter());
                services.AddAuthorization();
            });
            early(application);
            var refused = Assert.Throws<InvalidOperationException>(() => application.UseModularynIngress());
            Assert.Contains("before UseModularynIngress", refused.Message, StringComparison.Ordinal);
        }
    }
}
