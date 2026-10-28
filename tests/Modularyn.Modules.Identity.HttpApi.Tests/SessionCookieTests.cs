using System.Net;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.HttpApi.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class SessionCookieTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task<string> AccountAsync(IdentityTestServer server, string userName)
    {
        var password = Generated.Password();
        Assert.Equal(AccountCreationStatus.Created,
            (await server.Get<IAccountService>().CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), userName, new SecretText(password), Cancellation)).Status);
        return password;
    }

    [Fact(DisplayName = "TM-G02-016 sign-in issues a new __Host- session cookie that is Secure, HttpOnly, SameSite=Strict, host-only and without an expiry")]
    public async Task TM_G02_016_Sign_in_issues_a_new_host_only_secure_http_only_strict_session_cookie()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);
        var password = await AccountAsync(server, "admin-1");
        var browser = server.Browser();
        var response = await browser.SignInAsync("admin-1", password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var header = Assert.Single(browser.SetCookieHeaders, value => value.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        var attributes = header.Split(';').Skip(1).Select(part => part.Trim().ToLowerInvariant()).ToArray();
        Assert.Contains("secure", attributes);
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, attribute => attribute.StartsWith("domain=", StringComparison.Ordinal)
            || attribute.StartsWith("expires=", StringComparison.Ordinal) || attribute.StartsWith("max-age=", StringComparison.Ordinal));
        Assert.StartsWith("__Host-", SessionCookie.Name, StringComparison.Ordinal);
        Assert.Equal(SessionToken.EncodedLength, browser.Session!.Length);

        // The record is server-side, and the browser's session is the only active one for the account.
        var current = await browser.CurrentUserAsync();
        Assert.Equal("user", current!.Value.GetProperty("actor").GetProperty("kind").GetString());
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Sessions") + " WHERE \"EndedAt\" IS NULL"));
        output.WriteLine($"Session cookie attributes: {string.Join("; ", attributes)}.");
    }

    [Fact(DisplayName = "TM-G02-016 a value set before sign-in never becomes authenticated, and a session held before sign-in ends")]
    public async Task TM_G02_016_A_value_set_before_sign_in_never_becomes_authenticated()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);
        var password = await AccountAsync(server, "admin-1");

        // Browser C carries a well-formed identifier that an attacker set before sign-in.
        var browser = server.Browser();
        await browser.RefreshTokenAsync();
        var planted = SessionToken.Create().Reveal();
        browser.Cookies[SessionCookie.Name] = planted;
        Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("admin-1", password)).StatusCode);
        Assert.NotEqual(planted, browser.Session);
        var attacker = server.Browser();
        attacker.Cookies[SessionCookie.Name] = planted;
        Assert.Null(await attacker.CurrentUserAsync());

        // Signing in again from a browser that holds a session replaces it: the earlier identifier is ended, not kept beside the new one.
        var earlier = browser.Copy();
        Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("admin-1", password)).StatusCode);
        Assert.NotEqual(earlier.Session, browser.Session);
        Assert.Null(await earlier.CurrentUserAsync());
        Assert.NotNull(await browser.CurrentUserAsync());
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Sessions") + " WHERE \"EndedAt\" IS NULL"));

        // Malformed and duplicated session cookies are refused without reaching the store, which records no activity for them.
        server.Clock.Advance(TimeSpan.FromMinutes(1));
        var lastSeen = await database.ScalarAsync<DateTime>("SELECT \"LastSeenAt\" FROM " + database.Table("Sessions") + " WHERE \"EndedAt\" IS NULL");
        foreach (var cookie in new[] { "x", planted + "A", browser.Session + "; " + SessionCookie.Name + "=" + browser.Session })
        {
            var request = server.Request(HttpMethod.Get, IdentityEndpoints.CurrentUserPath);
            request.Headers.Add("Cookie", SessionCookie.Name + "=" + cookie);
            Assert.Equal(HttpStatusCode.Unauthorized, (await server.SendAsync(request)).StatusCode);
        }
        Assert.Equal(lastSeen, await database.ScalarAsync<DateTime>("SELECT \"LastSeenAt\" FROM " + database.Table("Sessions") + " WHERE \"EndedAt\" IS NULL"));
    }

    [Fact(DisplayName = "TM-G02-016 idle and absolute expiry end sessions at their edges, and the record states the ended state and reason")]
    public async Task TM_G02_016_Idle_and_absolute_expiry_end_sessions_at_their_edges()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database, new(IdentityTestServer.TestLimits())
        {
            ["Modularyn:Identity:Sessions:IdleTimeout"] = "00:05:00",
            ["Modularyn:Identity:Sessions:AbsoluteLifetime"] = "00:30:00"
        });
        var password = await AccountAsync(server, "admin-1");
        var signedInAt = server.Clock.Now;
        var idle = server.Browser("203.0.113.10");
        var shortIdle = server.Browser("203.0.113.11");
        var active = server.Browser("203.0.113.12");
        foreach (var browser in new[] { idle, shortIdle, active }) Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("admin-1", password)).StatusCode);

        server.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.NotNull(await shortIdle.CurrentUserAsync());
        Assert.NotNull(await active.CurrentUserAsync());
        server.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await idle.CurrentUserAsync());
        await AssertEnded(database, SessionEndCause.IdleTimeout, 1);

        // Requests below the idle timeout keep a session until A minus one second; at A plus one second it is refused.
        var deadline = signedInAt + TimeSpan.FromMinutes(30);
        while (server.Clock.Now + TimeSpan.FromMinutes(4) < deadline - TimeSpan.FromSeconds(1))
        {
            server.Clock.Advance(TimeSpan.FromMinutes(4));
            Assert.NotNull(await active.CurrentUserAsync());
        }
        server.Clock.Set(deadline - TimeSpan.FromSeconds(1));
        Assert.NotNull(await active.CurrentUserAsync());
        server.Clock.Set(deadline + TimeSpan.FromSeconds(1));
        Assert.Null(await active.CurrentUserAsync());
        await AssertEnded(database, SessionEndCause.AbsoluteLifetime, 1);
        Assert.Null(await shortIdle.CurrentUserAsync());
        output.WriteLine("Idle 5 minutes and absolute 30 minutes: every request inside a limit succeeded and the first past it was refused, with its reason recorded.");
    }

    private static async Task AssertEnded(IdentityDatabase database, SessionEndCause cause, long count) =>
        Assert.Equal(count, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Sessions") + " WHERE \"EndedAt\" IS NOT NULL AND \"EndReason\" = @reason",
            new NpgsqlParameter("reason", (int)cause)));

    [Fact(DisplayName = "TM-G02-016 sessions are never authenticated over plain HTTP or for an unconfigured host")]
    public async Task TM_G02_016_Sessions_are_never_authenticated_over_plain_http_or_for_an_unconfigured_host()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);
        var password = await AccountAsync(server, "admin-1");
        var browser = server.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("admin-1", password)).StatusCode);
        var lastSeen = await database.ScalarAsync<DateTime>("SELECT \"LastSeenAt\" FROM " + database.Table("Sessions"));
        server.Clock.Advance(TimeSpan.FromMinutes(1));

        var plain = server.Request(HttpMethod.Get, IdentityEndpoints.CurrentUserPath, https: false);
        plain.Headers.Add("Cookie", SessionCookie.Name + "=" + browser.Session);
        var redirect = await server.SendAsync(plain);
        Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
        Assert.False(redirect.Headers.Contains("Set-Cookie"));
        var unconfigured = server.Request(HttpMethod.Get, IdentityEndpoints.CurrentUserPath, host: "other.example.test");
        unconfigured.Headers.Add("Cookie", SessionCookie.Name + "=" + browser.Session);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.SendAsync(unconfigured)).StatusCode);
        // Neither request reached the session store: the ingress refused both before authentication.
        Assert.Equal(lastSeen, await database.ScalarAsync<DateTime>("SELECT \"LastSeenAt\" FROM " + database.Table("Sessions")));
        Assert.NotNull(await browser.CurrentUserAsync());
        Assert.NotEqual(lastSeen, await database.ScalarAsync<DateTime>("SELECT \"LastSeenAt\" FROM " + database.Table("Sessions")));
    }

    [Fact(DisplayName = "TM-G02-007 a state change with a session needs an antiforgery token issued to that session")]
    public async Task TM_G02_007_A_state_change_with_a_session_needs_an_antiforgery_token_issued_to_that_session()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);
        var password = await AccountAsync(server, "admin-1");
        var other = await AccountAsync(server, "admin-2");
        var browser = server.Browser();
        await browser.RefreshTokenAsync();
        var anonymousToken = browser.RequestToken;

        // Without a token the sign-in itself is refused; the anonymous token then signs in.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(HttpMethod.Post, IdentityEndpoints.SignInPath, sendToken: false,
            content: System.Net.Http.Json.JsonContent.Create(new { userName = "admin-1", password }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("admin-1", password)).StatusCode);
        Assert.NotEqual(anonymousToken, browser.RequestToken);

        // A token issued before sign-in, or to another account's session, does not authorize this session's state change.
        var otherBrowser = server.Browser();
        Assert.Equal(HttpStatusCode.OK, (await otherBrowser.SignInAsync("admin-2", other)).StatusCode);
        var signedIn = browser.RequestToken;
        foreach (var token in new[] { anonymousToken, otherBrowser.RequestToken, null })
        {
            browser.RequestToken = token;
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.SignOutAsync()).StatusCode);
            Assert.NotNull(await browser.CurrentUserAsync());
        }
        browser.RequestToken = signedIn;
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SignOutAsync()).StatusCode);
        Assert.Null(browser.Session);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Sessions")
            + " WHERE \"EndedAt\" IS NOT NULL AND \"EndReason\" = @reason", new NpgsqlParameter("reason", (int)SessionEndCause.SignedOut)));
    }
}
