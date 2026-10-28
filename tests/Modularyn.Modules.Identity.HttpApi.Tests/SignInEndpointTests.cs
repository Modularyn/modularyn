using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.HttpApi.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class SignInEndpointTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private sealed record Observed(HttpStatusCode Status, string Body, string Headers);

    private static async Task<Observed> ObserveAsync(HttpResponseMessage response) =>
        new(response.StatusCode, await response.Content.ReadAsStringAsync(Cancellation),
            string.Join("|", response.Headers.Concat(response.Content.Headers).Where(header => header.Key is not ("Date" or "Set-Cookie"))
                .OrderBy(header => header.Key, StringComparer.Ordinal).Select(header => header.Key + "=" + string.Join(",", header.Value))));

    [Fact(DisplayName = "TM-G02-015 unknown accounts, disabled accounts and wrong passwords receive the same status, body and headers")]
    public async Task TM_G02_015_Unknown_disabled_and_wrong_passwords_receive_identical_responses()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        // Enough free attempts that every sample is a verification rather than a throttled refusal.
        await using var server = await IdentityTestServer.StartAsync(database, new(IdentityTestServer.TestLimits())
        {
            ["Modularyn:Identity:Throttling:AccountFreeAttempts"] = "50"
        });
        var accounts = server.Get<IAccountService>();
        await accounts.CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), "admin-1", new SecretText(Generated.Password()), Cancellation);
        var disabledPassword = Generated.Password();
        var disabled = (await accounts.CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), "editor-1", new SecretText(disabledPassword), Cancellation)).AccountId!.Value;
        Assert.True(await accounts.DisableAsync(await server.Administrator.AccountsAsync(), disabled, Cancellation));

        var observed = new List<Observed>();
        var timings = new Dictionary<string, List<TimeSpan>>();
        foreach (var (name, password) in new[] { ("admin-1", Generated.Password()), ("unknown-1", Generated.Password()), ("editor-1", disabledPassword) })
        {
            timings[name] = [];
            for (var attempt = 0; attempt < 7; attempt++)
            {
                var browser = server.Browser("192.0.2." + (observed.Count + 1));
                await browser.RefreshTokenAsync();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                observed.Add(await ObserveAsync(await browser.SignInAsync(name, password)));
                timings[name].Add(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
        }
        Assert.All(observed, item => Assert.Equal(observed[0], item));
        Assert.Equal(HttpStatusCode.Unauthorized, observed[0].Status);
        Assert.Equal("{\"code\":\"sign_in_failed\"}", observed[0].Body);
        output.WriteLine($"Every failure: {(int)observed[0].Status} {observed[0].Body}; headers {observed[0].Headers}.");
        var medians = timings.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ElementAt(pair.Value.Count / 2));
        output.WriteLine("Median elapsed per attempt, one PBKDF2 verification each: " + string.Join("; ", medians.Select(pair =>
            pair.Key + " " + pair.Value.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms")));
        // A deliberately wide bound: each path performs one verification at the policy cost, so their medians stay close.
        var tolerance = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(25).Ticks, medians["admin-1"].Ticks / 2));
        Assert.All(medians.Values, median => Assert.InRange(median, medians["admin-1"] - tolerance, medians["admin-1"] + tolerance));
    }

    [Fact(DisplayName = "TM-G02-015 a blocked source is refused with a retry time while another source signs in, through the declared proxy")]
    public async Task TM_G02_015_A_blocked_source_is_refused_while_another_source_signs_in()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);
        var accounts = server.Get<IAccountService>();
        var password = Generated.Password();
        await accounts.CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), "admin-1", new SecretText(password), Cancellation);
        foreach (var name in new[] { "user-a", "user-b", "user-c" }) await accounts.CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), name, new SecretText(Generated.Password()), Cancellation);
        var attacker = server.Browser("203.0.113.66");
        foreach (var name in new[] { "user-a", "user-a", "user-b", "user-b", "user-c", "user-c" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.SignInAsync(name, Generated.Password())).StatusCode);

        var blocked = await attacker.SignInAsync("admin-1", password);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("{\"code\":\"sign_in_throttled\"}", await blocked.Content.ReadAsStringAsync(Cancellation));
        Assert.Equal(TimeSpan.FromMinutes(15), blocked.Headers.RetryAfter!.Delta);
        Assert.Null(attacker.Session);

        var owner = server.Browser("198.51.100.8");
        Assert.Equal(HttpStatusCode.OK, (await owner.SignInAsync("admin-1", password)).StatusCode);
        server.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await attacker.SignInAsync("admin-1", password)).StatusCode);
        output.WriteLine("After six failures from one source it was refused for 15 minutes while another source signed in.");
    }

    [Fact(DisplayName = "TM-G02-006 sign-in returns only a local path or a configured origin as its redirect")]
    public async Task TM_G02_006_Sign_in_returns_only_a_local_path_or_a_configured_origin_as_its_redirect()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);
        var password = Generated.Password();
        await server.Get<IAccountService>().CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), "admin-1", new SecretText(password), Cancellation);
        var cases = new Dictionary<string, string>
        {
            ["/admin/items?page=2"] = "/admin/items?page=2",
            [IdentityTestServer.PublicOrigin + "/admin"] = IdentityTestServer.PublicOrigin + "/admin",
            ["https://evil.example.test/admin"] = "/",
            ["//evil.example.test/admin"] = "/",
            ["/\\evil.example.test"] = "/",
            ["http://" + IdentityTestServer.PublicHost + "/admin"] = "/",
            ["https://user@" + IdentityTestServer.PublicHost + "/admin"] = "/",
            ["javascript:alert(1)"] = "/"
        };
        var index = 0;
        foreach (var (requested, expected) in cases)
        {
            var browser = server.Browser("198.51.100." + ++index);
            var response = await browser.SignInAsync("admin-1", password, requested);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
            Assert.Equal(expected, body.RootElement.GetProperty("redirect").GetString());
        }
    }

    [Fact(DisplayName = "TM-G02-015 malformed sign-in requests are refused before any verification")]
    public async Task TM_G02_015_Malformed_sign_in_requests_are_refused_before_any_verification()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);
        var browser = server.Browser();
        await browser.RefreshTokenAsync();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await browser.SendAsync(HttpMethod.Post, IdentityEndpoints.SignInPath,
            content: new FormUrlEncodedContent([new("userName", "admin-1"), new("password", "x")]))).StatusCode);
        foreach (var body in new[] { "{", "[]", "{\"userName\":\"admin-1\"}", "{\"userName\":\"admin-1\",\"password\":\"x\",\"role\":\"admin\"}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.SendAsync(HttpMethod.Post, IdentityEndpoints.SignInPath,
                content: new StringContent(body, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("VerificationThrottle")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.SendAsync(HttpMethod.Post, IdentityEndpoints.SignInPath,
            content: JsonContent.Create(new { userName = "admin-1", password = new string('x', 300) }))).StatusCode);
    }
}
