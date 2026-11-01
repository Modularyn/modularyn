using System.Net;
using Xunit;
using static Modularyn.Framework.Ingress.Tests.IngressServer;

namespace Modularyn.Framework.Ingress.Tests;

// AC-003-N02 row 3 exercised at the framework layer: one configured caller, an unlisted origin and the null origin.
public sealed class CrossOriginTests
{
    private static readonly string[] Granting =
    [
        "Access-Control-Allow-Origin", "Access-Control-Allow-Credentials", "Access-Control-Allow-Methods",
        "Access-Control-Allow-Headers", "Access-Control-Expose-Headers", "Access-Control-Max-Age"
    ];

    [Fact(DisplayName = "AC-003-N02 only the listed origin receives credentialed access, and other preflights are refused")]
    public async Task AC_003_N02_Only_the_listed_origin_receives_credentialed_access_and_other_preflights_are_refused()
    {
        await using var server = await StartAsync(IngressServer.Settings(("CrossOrigin:Origins:0", ListedOrigin), ("CrossOrigin:Methods:0", "GET"),
            ("CrossOrigin:Methods:1", "POST"), ("CrossOrigin:Headers:0", "Content-Type"), ("CrossOrigin:Headers:1", "RequestVerificationToken"),
            ("CrossOrigin:ExposedHeaders:0", "Location")));

        var granted = await Preflight(server, ListedOrigin, "POST", "content-type, requestverificationtoken");
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        Assert.Equal(ListedOrigin, Single(granted, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(granted, "Access-Control-Allow-Credentials"));
        Assert.Equal("GET, POST", Single(granted, "Access-Control-Allow-Methods"));
        Assert.Equal("Content-Type, RequestVerificationToken", Single(granted, "Access-Control-Allow-Headers"));
        Assert.Equal("600", Single(granted, "Access-Control-Max-Age"));
        Assert.Contains("Origin", granted.Headers.Vary);

        foreach (var (origin, method, headers) in new[]
                 {
                     ("https://origin-2.example.test", "POST", ""), ("null", "POST", ""), ("http://origin-1.example.test", "POST", ""),
                     ("https://origin-1.example.test.attacker.example.net", "POST", ""), (ListedOrigin, "DELETE", ""), (ListedOrigin, "POST", "x-other")
                 })
        {
            var refused = await Preflight(server, origin, method, headers);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            AssertNoGrant(refused);
            Assert.Contains("Origin", refused.Headers.Vary);
        }

        var actual = await server.SendAsync(true, HttpMethod.Get, "/leaky", request => request.Headers.Add("Origin", ListedOrigin));
        Assert.Equal(ListedOrigin, Single(actual, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(actual, "Access-Control-Allow-Credentials"));
        Assert.Equal("Location", Single(actual, "Access-Control-Expose-Headers"));
        Assert.Contains("Origin", actual.Headers.Vary);

        foreach (var origin in new[] { "https://origin-2.example.test", "null" })
        {
            var other = await server.SendAsync(true, HttpMethod.Get, "/leaky", request => request.Headers.Add("Origin", origin));
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            AssertNoGrant(other);
            Assert.Contains("Origin", other.Headers.Vary);
        }
        var sameOrigin = await server.SendAsync(true, HttpMethod.Get, "/leaky");
        AssertNoGrant(sameOrigin);
        Assert.Contains("Origin", sameOrigin.Headers.Vary);
    }

    [Fact(DisplayName = "TM-G02-059 without configured callers no response grants cross-origin access")]
    public async Task TM_G02_059_Without_configured_callers_no_response_grants_cross_origin_access()
    {
        await using var server = await StartAsync(IngressServer.Settings());
        var preflight = await Preflight(server, ListedOrigin, "GET", "");
        Assert.Equal(HttpStatusCode.Forbidden, preflight.StatusCode);
        AssertNoGrant(preflight);
        var leaky = await server.SendAsync(true, HttpMethod.Get, "/leaky", request => request.Headers.Add("Origin", ListedOrigin));
        Assert.Equal(HttpStatusCode.OK, leaky.StatusCode);
        AssertNoGrant(leaky);
    }

    private static Task<HttpResponseMessage> Preflight(IngressServer server, string origin, string method, string headers) =>
        server.SendAsync(true, HttpMethod.Options, "/change", request =>
        {
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", method);
            if (headers.Length != 0) request.Headers.Add("Access-Control-Request-Headers", headers);
        });

    private static string Single(HttpResponseMessage response, string name) => Assert.Single(response.Headers.GetValues(name));

    private static void AssertNoGrant(HttpResponseMessage response) =>
        Assert.All(Granting, name => Assert.False(response.Headers.Contains(name), name));
}
