using System.Text.Json;
using Microsoft.Playwright;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.HttpApi;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// AC-003-N02 in a real browser: the hash-verified Chrome Headless Shell, driven by Playwright, visits the application and
/// three other sites through host mapping on loopback. origin-1 is the one listed cross-origin caller and, like an unlisted
/// origin, shares the application's registrable domain, so the browser sends them its SameSite=Strict session cookie; the
/// attacker's site does not, so it never receives the cookie at all. The browser, not the test, decides what each page may
/// send and read.
/// </summary>
public sealed partial class G02AC003Tests
{
    // The script every page runs to call the application: its outcome as the page sees it, or the error the browser raised.
    private const string FetchScript = """
        async ({ url, method, body, token, credentials }) => {
          try {
            const headers = {};
            if (body !== null) headers['Content-Type'] = 'application/json';
            if (token !== null) headers['RequestVerificationToken'] = token;
            const response = await fetch(url, { method, headers, credentials, body: body === null ? undefined : JSON.stringify(body) });
            return JSON.stringify({ status: response.status, text: await response.text() });
          } catch (error) {
            return JSON.stringify({ status: 0, text: error.name });
          }
        }
        """;

    private const string TokenScript = "async (url) => (await (await fetch(url, { credentials: 'include' })).json()).requestToken";

    private const string SignInScript = """
        async ({ userName, password }) => {
          const token = (await (await fetch('/identity/antiforgery')).json()).requestToken;
          const response = await fetch('/identity/sign-in', { method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify({ userName, password }) });
          return response.status;
        }
        """;

    // A sandboxed frame without allow-same-origin has an opaque origin, so its requests carry Origin: null.
    private const string NullOriginScript = """
        async (url) => await new Promise(resolve => {
          window.addEventListener('message', event => resolve(String(event.data)), { once: true });
          const frame = document.createElement('iframe');
          frame.setAttribute('sandbox', 'allow-scripts');
          frame.srcdoc = '<script>fetch(' + JSON.stringify(url) + ', { credentials: "include" }).then(r => parent.postMessage("status:" + r.status, "*"))'
            + '.catch(e => parent.postMessage("blocked:" + e.name, "*"));</' + 'script>';
          document.body.appendChild(frame);
        })
        """;

    private sealed record Seen(int Status, string Text);

    private static async Task<Seen> FetchAsync(IPage page, string url, string method, object? body = null, string? token = null, string credentials = "include")
    {
        var json = await page.EvaluateAsync<string>(FetchScript, new { url, method, body, token, credentials });
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.GetProperty("status").GetInt32(), document.RootElement.GetProperty("text").GetString()!);
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_N02_CrossSiteEndedSessionAndOverCeilingCallsAreRefused_Browser()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var work = Path.Combine(Path.GetTempPath(), "modularyn-browser-" + Guid.NewGuid().ToString("N"));
        try
        {
            var prepared = await VerifiedBrowser.PrepareAsync(work, Cancellation);
            output.WriteLine(prepared.Detail);
            if (prepared.Executable is null) Assert.Skip(prepared.Detail);

            var clock = new TestClock();
            await using var setup = AuthorizationHost.Create(database, clock);
            var site = await CrossSiteAsync(setup);
            var appPort = BrowserSites.FreePort();
            await using var sites = await BrowserSites.StartAsync(BrowserSites.FreePort());
            var app = "https://" + BrowserSites.AppHost + ":" + appPort;
            var settings = CrossOriginSettings(sites.Origin(BrowserSites.ListedHost));
            settings["Modularyn:Ingress:PublicOrigins:0"] = app;
            await using var server = await DeclaredServer.StartAsync(database, clock, settings, appPort);
            var tenantPath = app + "/t/" + TenantIdentifier.Format(site.Tenant);
            var item = tenantPath + "/items/item-1";

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                ExecutablePath = prepared.Executable,
                Headless = true,
                // Every host the test names resolves to loopback, and nothing else resolves at all.
                Args = ["--host-resolver-rules=MAP " + BrowserSites.AppHost + " 127.0.0.1, MAP " + BrowserSites.ListedHost + " 127.0.0.1, MAP "
                    + BrowserSites.UnlistedHost + " 127.0.0.1, MAP " + BrowserSites.AttackerHost + " 127.0.0.1, MAP * ~NOTFOUND"]
            });
            output.WriteLine($"Browser {browser.Version}; application {app}; other sites on port {sites.Port}.");
            await using var user = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            var page = await user.NewPageAsync();
            Assert.Equal(200, (await page.GotoAsync(app + "/app"))!.Status);
            Assert.Equal(200, await page.EvaluateAsync<int>(SignInScript, new { userName = site.User1.Name, password = site.User1.Password }));
            var session = Assert.Single(await user.CookiesAsync(), cookie => cookie.Name == SessionCookie.Name);
            Assert.True(session.HttpOnly && session.Secure && session.SameSite == SameSiteAttribute.Strict && session.Expires < 0);
            Assert.Equal(BrowserSites.AppHost, session.Domain);

            // 1. Same-origin: a state change without the antiforgery token is refused; with it, the member's update applies.
            Assert.Equal(400, (await FetchAsync(page, item, "PUT", new { title = "Unprotected" }, credentials: "same-origin")).Status);
            var token = await page.EvaluateAsync<string>(TokenScript, app + IdentityEndpoints.AntiforgeryPath);
            Assert.Equal(200, (await FetchAsync(page, item, "PUT", new { title = "Signed" }, token, "same-origin")).Status);
            var stored = await StoredAsync(database, site.Tenant, "item-1");
            Assert.Equal("Signed", stored!.Value.Title);

            // 2. The attacker's site, on another registrable domain: its form post arrives without the session cookie, which
            //    SameSite=Strict withholds, and without a token, and is refused; its script cannot even send the JSON write, whose
            //    preflight is refused; an image that names a state change only reads.
            var attacker = await user.NewPageAsync();
            await attacker.GotoAsync(sites.Origin(BrowserSites.AttackerHost) + "/");
            var exports = tenantPath + "/exports";
            var posted = await attacker.RunAndWaitForResponseAsync(() => attacker.EvaluateAsync(
                "url => { const form = document.createElement('form'); form.method = 'POST'; form.action = url; document.body.appendChild(form); form.submit(); }",
                exports), response => response.Url == exports && response.Request.Method == "POST");
            Assert.Equal(400, posted.Status);
            var formTrace = server.Traces.Last(trace => trace.Method == "POST" && trace.Path.EndsWith("/exports", StringComparison.Ordinal));
            Assert.DoesNotContain(SessionCookie.Name, formTrace.Cookies);
            await attacker.GotoAsync(sites.Origin(BrowserSites.AttackerHost) + "/");
            Assert.Equal(new Seen(0, "TypeError"), await FetchAsync(attacker, item, "PUT", new { title = "Attacker" }));
            Assert.DoesNotContain(server.Traces, trace => trace.Method == "PUT" && trace.Origin == sites.Origin(BrowserSites.AttackerHost));
            await attacker.EvaluateAsync("url => new Promise(resolve => { const image = new Image(); image.onload = image.onerror = resolve; image.src = url; })",
                item + "?method=DELETE&title=Attacker");
            Assert.Equal(stored, await StoredAsync(database, site.Tenant, "item-1"));
            Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table(database.ItemsSchema, "ItemExports")));
            output.WriteLine($"Attacker's form post: {posted.Status}, sent with cookies [{string.Join(",", formTrace.Cookies)}]; its scripted write was blocked.");

            // 3. origin-1, listed: the browser lets its page read a credentialed response and send a credentialed write with a token.
            var listed = await user.NewPageAsync();
            await listed.GotoAsync(sites.Origin(BrowserSites.ListedHost) + "/");
            var read = await FetchAsync(listed, item, "GET");
            Assert.Equal(200, read.Status);
            Assert.Contains("\"code\":\"item-1\"", read.Text, StringComparison.Ordinal);
            var listedToken = await listed.EvaluateAsync<string>(TokenScript, app + IdentityEndpoints.AntiforgeryPath);
            Assert.Equal(200, (await FetchAsync(listed, item, "PUT", new { title = "From origin-1" }, listedToken)).Status);
            var granted = server.Traces.Last(trace => trace.Method == "PUT" && trace.Origin == sites.Origin(BrowserSites.ListedHost));
            Assert.Equal((sites.Origin(BrowserSites.ListedHost), "true"), (granted.AllowOrigin, granted.AllowCredentials));
            Assert.Contains("Origin", granted.Vary ?? "", StringComparison.Ordinal);
            stored = await StoredAsync(database, site.Tenant, "item-1");
            Assert.Equal("From origin-1", stored!.Value.Title);

            // An unlisted origin of the same registrable domain: the browser sends the cookie but hides the answer, and refuses to
            // send the write once its preflight is refused. The null origin of a sandboxed frame is granted nothing either.
            var unlisted = await user.NewPageAsync();
            await unlisted.GotoAsync(sites.Origin(BrowserSites.UnlistedHost) + "/");
            Assert.Equal(new Seen(0, "TypeError"), await FetchAsync(unlisted, item, "GET"));
            var hidden = server.Traces.Last(trace => trace.Method == "GET" && trace.Origin == sites.Origin(BrowserSites.UnlistedHost));
            Assert.Equal((200, (string?)null), (hidden.Status, hidden.AllowOrigin));
            Assert.Equal(new Seen(0, "TypeError"), await FetchAsync(unlisted, item, "PUT", new { title = "Unlisted" }));
            Assert.DoesNotContain(server.Traces, trace => trace.Method == "PUT" && trace.Origin == sites.Origin(BrowserSites.UnlistedHost));
            Assert.Contains(server.Traces, trace => trace.Method == "OPTIONS" && trace.Origin == sites.Origin(BrowserSites.UnlistedHost) && trace.Status == 403);
            Assert.Equal("blocked:TypeError", await unlisted.EvaluateAsync<string>(NullOriginScript, item));
            Assert.All(server.Traces.Where(trace => trace.Origin == "null"), trace => Assert.Null(trace.AllowOrigin));
            Assert.Equal(stored, await StoredAsync(database, site.Tenant, "item-1"));
            Assert.DoesNotContain(server.Traces, trace => trace.AllowOrigin is not null && trace.AllowOrigin != sites.Origin(BrowserSites.ListedHost));
            output.WriteLine("origin-1 read and wrote with credentials; the unlisted origin and the null origin could read and write nothing.");

            // 4. The signed-out session: the browser keeps a copy of its cookie, signs out, and presents the copy again with a
            //    token valid for that request; the read and the update are refused.
            Assert.Equal(204, (await FetchAsync(page, app + IdentityEndpoints.SignOutPath, "POST", null, token, "same-origin")).Status);
            Assert.DoesNotContain(await user.CookiesAsync(), cookie => cookie.Name == SessionCookie.Name);
            await user.AddCookiesAsync([new Cookie { Name = session.Name, Value = session.Value, Url = app, HttpOnly = true, Secure = true, SameSite = SameSiteAttribute.Strict }]);
            Assert.Equal(401, (await FetchAsync(page, item, "GET", credentials: "same-origin")).Status);
            await user.AddCookiesAsync([new Cookie { Name = session.Name, Value = session.Value, Url = app, HttpOnly = true, Secure = true, SameSite = SameSiteAttribute.Strict }]);
            var fresh = await page.EvaluateAsync<string>(TokenScript, app + IdentityEndpoints.AntiforgeryPath);
            await user.AddCookiesAsync([new Cookie { Name = session.Name, Value = session.Value, Url = app, HttpOnly = true, Secure = true, SameSite = SameSiteAttribute.Strict }]);
            Assert.Equal(401, (await FetchAsync(page, item, "PUT", new { title = "Replayed" }, fresh, "same-origin")).Status);
            Assert.Equal(stored, await StoredAsync(database, site.Tenant, "item-1"));

            // 5. admin-a, in its own browser profile, grants what its ceiling excludes: refused, and nothing is granted.
            await using var administrator = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            var adminPage = await administrator.NewPageAsync();
            await adminPage.GotoAsync(app + "/app");
            Assert.Equal(200, await adminPage.EvaluateAsync<int>(SignInScript, new { userName = site.Admin.Name, password = site.Admin.Password }));
            var adminToken = await adminPage.EvaluateAsync<string>(TokenScript, app + IdentityEndpoints.AntiforgeryPath);
            var beyond = await FetchAsync(adminPage, tenantPath + "/authority/grants", "POST",
                new { member = site.User1.Id.ToString("D"), permission = Items.Delete, revoke = false }, adminToken, "same-origin");
            Assert.Equal(403, beyond.Status);
            Assert.Contains("\"code\":\"" + AuthorizationCodes.BeyondCeiling + "\"", beyond.Text, StringComparison.Ordinal);
            Assert.Equal(0L, await GrantsAsync(database, site.User1, Items.Delete));
            output.WriteLine($"Signed-out cookie: 401 for the read and the update; admin-a's grant beyond its ceiling: {beyond.Status}.");
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }
}
