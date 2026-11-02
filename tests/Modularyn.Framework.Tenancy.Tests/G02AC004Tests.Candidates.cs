using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// AC-004-N01, SEC-08's: user-a is a member of tenant-A only, the deployment declares one proxy and the domains of tenant-A
/// and tenant-B, and an unknown well-formed tenant identifier is prepared. Tenant-B and the unknown tenant, named by route,
/// header, claim or body, never become the effective tenant and are refused alike before any data access; forwarded values
/// count only from the declared proxy, where tenant-B's domain still needs membership; and links and redirects come from the
/// configured public origins alone.
/// </summary>
public sealed partial class G02AC004Tests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_N01_ForgedTenantAndOriginCandidatesAreRefused_Isolation()
    {
        await using var database = await AdversarialDatabase.CreateAsync();
        await using var host = AdversarialHost.Create(database);
        var seed = await AdversarialTenants.SeedAsync(host);
        var unknown = Guid.NewGuid();

        // 1. As user-a on tenant-A's domain, tenant-B or the unknown tenant in the route, a header or the principal's own claims:
        //    the candidates conflict and the request is refused, alike for both, with no context and so no data access.
        async Task<string> DecideAsync(Func<Guid, TenantCandidate[]> candidates, Guid named, Claim? claim = null)
        {
            var (decision, unit) = await host.TryEnterAsync(seed.UserA, claims =>
            {
                if (claim is not null) ((ClaimsIdentity)claims.Identity!).AddClaim(claim);
                return TenantResolutionRequest.ForMember(claims, candidates(named));
            });
            if (unit is not null) await unit.DisposeAsync();
            Assert.Null(decision.Context);
            return decision.Code!;
        }
        var onA = new TenantCandidate(TenantCandidateSource.Host, AdversarialHost.HostA);
        var rows = new (string Name, Func<Guid, TenantCandidate[]> Candidates, Func<Guid, Claim?> Claim)[]
        {
            ("route", tenant => [onA, new(TenantCandidateSource.Route, tenant.ToString("D"))], _ => null),
            ("header", tenant => [onA, new(TenantCandidateSource.Header, tenant.ToString("D"))], _ => null),
            ("claim", _ => [onA], tenant => new Claim(TenantClaimTypes.Tenant, tenant.ToString("D")))
        };
        foreach (var (name, candidates, claim) in rows)
        {
            var foreign = await DecideAsync(candidates, seed.TenantB, claim(seed.TenantB));
            Assert.Equal((TenancyCodes.Conflict, TenancyCodes.Conflict), (foreign, await DecideAsync(candidates, unknown, claim(unknown))));
            Log($"Candidate {name} naming tenant-B or an unknown tenant on tenant-A's domain: refused with {foreign}, no context.");
        }
        // Named alone, from a host no tenant owns, tenant-B is refused for want of membership and the unknown tenant as unknown;
        // the HTTP contract answers both as the same missing route (the contract case shows it).
        Assert.Equal(TenancyCodes.NotMember, await DecideAsync(tenant => [new(TenantCandidateSource.Route, tenant.ToString("D"))], seed.TenantB));
        Assert.Equal(TenancyCodes.UnknownTenant, await DecideAsync(tenant => [new(TenantCandidateSource.Route, tenant.ToString("D"))], unknown));
        // A body field is no candidate source at all: resolution takes the configured host, the route, the header and claims only.
        Assert.Equal([TenantCandidateSource.Host, TenantCandidateSource.Route, TenantCandidateSource.Header, TenantCandidateSource.Claim],
            Enum.GetValues<TenantCandidateSource>());

        // 2. Tenant-B's domain, as a forwarded host would make it, requires membership, which user-a lacks; tenant-A's resolves.
        Assert.Equal(TenancyCodes.NotMember, await DecideAsync(_ => [new(TenantCandidateSource.Host, AdversarialHost.HostB)], seed.TenantB));
        var (resolved, own) = await host.TryEnterAsync(seed.UserA, claims => TenantResolutionRequest.ForMember(claims, [onA]));
        await using (own) Assert.Equal((true, (Guid?)seed.TenantA), (resolved.Succeeded, resolved.Context?.TenantId));

        // 3 and 4. An unconfigured host has no public origin, so tenancy refuses it; the origin a link or redirect is built from
        //    is the configured one that matched the validated host, never a header; and only local paths and configured origins
        //    are accepted as return addresses.
        var origins = host.Get<PublicOrigins>();
        HttpRequest Request(string hostName, params (string Name, string Value)[] headers)
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = "https";
            context.Request.Host = new HostString(hostName);
            foreach (var (header, value) in headers) context.Request.Headers[header] = value;
            return context.Request;
        }
        Assert.Null(origins.ForRequest(Request("unconfigured.example.test")));
        Assert.Equal("https://" + AdversarialHost.HostA, origins.ForRequest(Request(AdversarialHost.HostA, ("X-Forwarded-Host", AdversarialHost.HostB),
            ("X-Forwarded-Proto", "https"), ("Forwarded", "host=" + AdversarialHost.HostB))));
        foreach (var refused in new[] { "https://attacker.example.org/after", "//attacker.example.org/after", "/\\attacker.example.org", "https://unconfigured.example.test/",
            "http://" + AdversarialHost.HostA + "/", "https://user@" + AdversarialHost.HostA + "/" })
            Assert.False(origins.IsAllowedRedirect(refused), refused);
        foreach (var accepted in new[] { "/t/" + seed.TenantA.ToString("D") + "/items", "https://" + AdversarialHost.HostA + "/after" })
            Assert.True(origins.IsAllowedRedirect(accepted), accepted);
        Log("Unconfigured host: no public origin. Forwarded tenant-B values beside tenant-A's host: the link origin stays tenant-A's.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_N01_ForgedTenantAndOriginCandidatesAreRefused_Contract()
    {
        await using var database = await AdversarialDatabase.CreateAsync();
        AdversarialTenants seed;
        await using (var setup = AdversarialHost.Create(database)) seed = await AdversarialTenants.SeedAsync(setup);
        // Two hosts over the same deployment: one that declares no proxy, so the test client is no proxy, and one that declares
        // the loopback address the test client sends from, so the client stands in for the declared proxy.
        await using var direct = await AdversarialServer.StartAsync(database, ["192.0.2.10"]);
        await using var proxied = await AdversarialServer.StartAsync(database, ["127.0.0.1"]);
        var client = direct.NewClient();
        await client.SignInAsync(seed.UserA);
        var unknown = Guid.NewGuid();
        var a = seed.TenantA;

        // 1. Tenant-B, then the unknown tenant, in a route value, a request header and a body field.
        async Task<(Reply Route, Reply Header, Reply Body)> NamedAsync(Guid tenant) =>
        (
            await client.SendAsync(HttpMethod.Get, Path(tenant, "/items")),
            await client.SendAsync(HttpMethod.Get, Path(a, "/items"), configure: request => request.Headers.Add(TenancyHttp.HeaderName, tenant.ToString("D"))),
            await client.SendAsync(HttpMethod.Post, Path(a, "/items"), new { code = "forged", name = "Forged", tenant = tenant.ToString("D") })
        );
        var foreign = await NamedAsync(seed.TenantB);
        var missing = await NamedAsync(unknown);
        Assert.Equal((missing.Route.Contract, missing.Header.Contract, missing.Body.Contract), (foreign.Route.Contract, foreign.Header.Contract, foreign.Body.Contract));
        Assert.Equal((HttpStatusCode.NotFound, ApiCodes.NotFound), (foreign.Route.Status, foreign.Route.Code));
        Assert.Equal((HttpStatusCode.NotFound, ApiCodes.NotFound), (foreign.Header.Status, foreign.Header.Code));
        Assert.Equal((HttpStatusCode.BadRequest, ApiCodes.Validation), (foreign.Body.Status, foreign.Body.Code));
        Log($"Route: {foreign.Route.Contract}; header: {foreign.Header.Contract}; body field: {foreign.Body.Contract}; the same for an unknown tenant.");
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("items", "Items") + " WHERE \"Code\" = 'forged'"));

        // 2. Forwarded host and scheme naming tenant-B's domain: from a client that is no declared proxy they are ignored, and
        //    the request stays tenant-A's; through the declared proxy, tenant-B's domain needs membership user-a lacks.
        void Forwarded(HttpRequestMessage request)
        {
            request.Headers.Add("X-Forwarded-Host", AdversarialHost.HostB);
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-For", "198.51.100.7");
        }
        var ignored = await client.SendAsync(HttpMethod.Get, Path(a, "/items"), configure: Forwarded);
        Assert.Equal(HttpStatusCode.OK, ignored.Status);
        Assert.All(ignored.Body!.Value.GetProperty("items").EnumerateArray(), item => Assert.Equal(a, item.GetProperty("tenant").GetGuid()));
        var throughProxy = proxied.NewClient();
        throughProxy.Cookies.Add(client.Cookies.Single(cookie => cookie.Key.Contains("Session", StringComparison.Ordinal)).Key,
            client.Cookies.Single(cookie => cookie.Key.Contains("Session", StringComparison.Ordinal)).Value);
        var toB = await throughProxy.SendAsync(HttpMethod.Get, Path(seed.TenantB, "/items"), configure: Forwarded);
        Assert.Equal((HttpStatusCode.NotFound, ApiCodes.NotFound), (toB.Status, toB.Code));
        Assert.Equal(missing.Route.Contract, toB.Contract);
        // The proxy itself works: through it, tenant-A's own domain and user-b's on tenant-B's resolve.
        Assert.Equal(HttpStatusCode.OK, (await throughProxy.SendAsync(HttpMethod.Get, Path(a, "/items"), configure: request =>
        {
            request.Headers.Add("X-Forwarded-Host", AdversarialHost.HostA);
            request.Headers.Add("X-Forwarded-Proto", "https");
        })).Status);
        var userB = proxied.NewClient(AdversarialHost.AppHost);
        await userB.SignInAsync(seed.UserB);
        Assert.Equal(HttpStatusCode.OK, (await userB.SendAsync(HttpMethod.Get, Path(seed.TenantB, "/items"), configure: Forwarded)).Status);
        Log($"Forwarded tenant-B from a non-proxy: {ignored.Status} with tenant-A's rows; through the proxy: {toB.Contract}.");

        // 3. An unconfigured host name is refused before authentication; a sign-in return address naming another origin is
        //    replaced by the local root, and a local path is kept.
        var stranger = direct.NewClient("unconfigured.example.test");
        var unconfigured = await stranger.SendAsync(HttpMethod.Get, Path(a, "/items"));
        Assert.Equal(HttpStatusCode.BadRequest, unconfigured.Status);
        Assert.DoesNotContain("unconfigured", unconfigured.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(unconfigured.Header("Set-Cookie"));
        Log($"Unconfigured host: {unconfigured.Contract}.");
        foreach (var (requested, expected) in new[]
        {
            ("https://attacker.example.org/after", "/"), ("//attacker.example.org/after", "/"), ("https://unconfigured.example.test/after", "/"),
            ("/t/" + a.ToString("D") + "/items", "/t/" + a.ToString("D") + "/items")
        })
        {
            var signIn = await direct.NewClient().SignInAsync(seed.UserA, requested);
            Assert.Equal(expected, signIn.Body!.Value.GetProperty("redirect").GetString());
            Assert.Null(signIn.Header("Location"));
        }

        // 4. On tenant-A's domain, the absolute link and the sign-in redirect stay on tenant-A's configured origin when Host and
        //    forwarded values name tenant-B's domain or an unconfigured host from a source that is not the declared proxy.
        var link = await client.SendAsync(HttpMethod.Get, Path(a, "/items/" + seed.ItemA1.Id + "/link"), configure: Forwarded);
        Assert.Equal("https://" + AdversarialHost.HostA + "/t/" + a.ToString("D") + "/items/" + seed.ItemA1.Id.ToString("D"),
            link.Body!.Value.GetProperty("link").GetString());
        var linkUnconfigured = await client.SendAsync(HttpMethod.Get, Path(a, "/items/" + seed.ItemA1.Id + "/link"), configure: request =>
            request.Headers.Add("X-Forwarded-Host", "unconfigured.example.test"));
        Assert.Equal(link.Body!.Value.GetProperty("link").GetString(), linkUnconfigured.Body!.Value.GetProperty("link").GetString());
        client.Host = AdversarialHost.HostB;
        var onB = await client.SendAsync(HttpMethod.Get, Path(a, "/items/" + seed.ItemA1.Id + "/link"));
        Assert.Equal(HttpStatusCode.NotFound, onB.Status);
        client.Host = "unconfigured.example.test";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(HttpMethod.Get, Path(a, "/items/" + seed.ItemA1.Id + "/link"))).Status);
        client.Host = AdversarialHost.HostA;
        foreach (var (requested, expected) in new[] { ("/t/" + a.ToString("D") + "/items", "/t/" + a.ToString("D") + "/items"),
            ("https://unconfigured.example.test/after", "/"), ("https://attacker.example.org/after", "/") })
        {
            var redirect = await direct.NewClient().SignInAsync(seed.UserA, requested, Forwarded);
            Assert.Equal(expected, redirect.Body!.Value.GetProperty("redirect").GetString());
            Assert.Null(redirect.Header("Location"));
        }
        Log($"Link with forwarded tenant-B or an unconfigured host from a non-proxy: {link.Body!.Value.GetProperty("link").GetString()}; "
            + $"with Host tenant-B's domain: {onB.Status}.");
    }
}
