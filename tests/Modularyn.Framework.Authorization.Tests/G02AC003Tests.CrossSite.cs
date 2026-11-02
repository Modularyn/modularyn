using System.Net;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.HttpApi;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// AC-003-N02: cross-site writes, unlisted origins, ended sessions and over-ceiling grants. user-1 has a cookie session in
/// tenant A and keeps a copy of a signed-out session cookie; the deployment lists exactly one cross-origin caller, origin-1;
/// admin-a's assignment ceiling excludes item.delete and includes item.export. The browser method runs the same rows in a
/// real browser.
/// </summary>
public sealed partial class G02AC003Tests
{
    internal const string ListedOrigin = "https://origin-1.example.net";
    internal const string UnlistedOrigin = "https://attacker.example.org";

    private sealed record CrossSite(Guid Tenant, User Owner, User Admin, User User1);

    /// <summary>The AC-003-N02 preconditions: a member with read and update, and admin-a under a ceiling of read and export.</summary>
    private static async Task<CrossSite> CrossSiteAsync(AuthorizationHost host)
    {
        var tenant = await host.TenantAsync("Tenant A");
        var owner = await host.UserAsync("owner-a", tenant);
        var admin = await host.UserAsync("admin-a", tenant);
        var user = await host.UserAsync("user-1", tenant);
        await host.OwnerAsync(owner, tenant);
        await using var unit = await host.EnterAsync(owner, tenant);
        var administration = host.Get<AuthorityAdministration>();
        Assert.True(await administration.DefineRoleAsync(unit.Context, unit.Principal, "item-admins",
            [AuthorizationModule.ManagePermission, Items.Read, Items.Export, Items.Delete], [Items.Read, Items.Export], Cancellation));
        Assert.True(await administration.AssignRoleAsync(unit.Context, unit.Principal, admin.Actor, "item-admins", Cancellation));
        foreach (var permission in new[] { Items.Read, Items.Update })
            Assert.True(await administration.GrantPermissionAsync(unit.Context, unit.Principal, user.Actor, permission, Cancellation));
        await ItemWork.AddAsync(host.Database.ItemsModule, unit.Context, user.Id.ToString("D"), "item-1");
        return new(tenant, owner, admin, user);
    }

    internal static Dictionary<string, string?> CrossOriginSettings(string listed) => new()
    {
        ["Modularyn:Ingress:CrossOrigin:Origins:0"] = listed,
        ["Modularyn:Ingress:CrossOrigin:Methods:0"] = "GET",
        ["Modularyn:Ingress:CrossOrigin:Methods:1"] = "POST",
        ["Modularyn:Ingress:CrossOrigin:Methods:2"] = "PUT",
        ["Modularyn:Ingress:CrossOrigin:Headers:0"] = "Content-Type",
        ["Modularyn:Ingress:CrossOrigin:Headers:1"] = "RequestVerificationToken"
    };

    private static Task<long> GrantsAsync(AuthorizationDatabase database, User member, string permission) => database.ScalarAsync<long>(
        "SELECT count(*) FROM " + database.Table(database.AuthorizationSchema, "PermissionGrants") + " WHERE \"Member\" = @member AND \"Permission\" = @permission",
        new NpgsqlParameter("member", member.Id.ToString("D")), new NpgsqlParameter("permission", permission));

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_N02_CrossSiteEndedSessionAndOverCeilingCallsAreRefused_Contract()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var setup = AuthorizationHost.Create(database, clock);
        var site = await CrossSiteAsync(setup);
        await using var server = await DeclaredServer.StartAsync(database, clock, CrossOriginSettings(ListedOrigin));
        string Path(string rest) => "/t/" + TenantIdentifier.Format(site.Tenant) + rest;
        var item = Path("/items/item-1");
        var browser = server.NewClient();
        await browser.SignInAsync(site.User1);
        Assert.Contains(SessionCookie.Name, browser.Cookies.Keys);
        var stored = await StoredAsync(database, site.Tenant, "item-1");

        // 1. A cookie-authenticated state change without an antiforgery token is refused before any handler; a safe method that
        //    names a state change, through the query, an override header or HEAD, only reads.
        var unprotected = await browser.SendAsync(HttpMethod.Put, item, new { title = "Forged" }, antiforgery: false);
        Assert.Equal((HttpStatusCode.BadRequest, ApiCodes.Refused), (unprotected.Status, unprotected.Code));
        foreach (var configure in new Action<HttpRequestMessage>[]
        {
            request => request.RequestUri = new Uri(request.RequestUri + "?method=DELETE&title=Forged"),
            request => request.Headers.Add("X-HTTP-Method-Override", "DELETE")
        })
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(HttpMethod.Get, item, configure: configure)).Status);
        // The declaration answers GET only, so HEAD is not even routed to it.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await browser.SendAsync(HttpMethod.Head, item)).Status);
        Assert.Equal(stored, await StoredAsync(database, site.Tenant, "item-1"));
        Assert.DoesNotContain(server.Traces, trace => trace.Method == "DELETE");
        output.WriteLine($"Unprotected PUT: {unprotected.Status} {unprotected.Code}; safe methods read and changed nothing.");

        // 2. The signed-out cookie, with an antiforgery token that is valid for the request it rides on, is refused for a read and
        //    an update, and the server deletes it again.
        var signedOut = browser.Cookies[SessionCookie.Name];
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(HttpMethod.Post, IdentityEndpoints.SignOutPath)).Status);
        Assert.DoesNotContain(SessionCookie.Name, browser.Cookies.Keys);
        browser.Cookies[SessionCookie.Name] = signedOut;
        await browser.RefreshTokenAsync();
        browser.Cookies[SessionCookie.Name] = signedOut;
        foreach (var (method, body) in new[] { (HttpMethod.Get, (object?)null), (HttpMethod.Put, new { title = "Replayed" }) })
        {
            browser.Cookies[SessionCookie.Name] = signedOut;
            var refused = await browser.SendAsync(method, item, body);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
            Assert.Contains(SessionCookie.Name + "=;", refused.Header("Set-Cookie") ?? "", StringComparison.Ordinal);
        }
        Assert.Equal(stored, await StoredAsync(database, site.Tenant, "item-1"));
        Assert.True(SessionToken.TryParse(signedOut, out var token));
        Assert.Null(await server.Get<ISessionService>().ValidateAsync(token!, Cancellation));

        // 3. Credentialed preflight and actual requests from origin-1, an unlisted origin and the null origin: only origin-1 is
        //    granted, with its exact origin and credentials, every answer varies by Origin, and the others' preflights are refused.
        var caller = server.NewClient();
        await caller.SignInAsync(site.User1);
        foreach (var origin in new[] { ListedOrigin, UnlistedOrigin, "null" })
        {
            var preflight = await caller.SendAsync(HttpMethod.Options, item, origin: origin, configure: request =>
            {
                request.Headers.Add("Access-Control-Request-Method", "PUT");
                request.Headers.Add("Access-Control-Request-Headers", "content-type,requestverificationtoken");
            });
            var actual = await caller.SendAsync(HttpMethod.Get, item, origin: origin);
            output.WriteLine($"{origin}: preflight {preflight.Status} {preflight.Header("Access-Control-Allow-Origin")}; GET {actual.Status} "
                + $"{actual.Header("Access-Control-Allow-Origin")} {actual.Header("Access-Control-Allow-Credentials")} vary {actual.Header("Vary")}");
            Assert.Contains("Origin", actual.Header("Vary") ?? "", StringComparison.Ordinal);
            Assert.Contains("Origin", preflight.Header("Vary") ?? "", StringComparison.Ordinal);
            if (origin == ListedOrigin)
            {
                Assert.True(preflight.Status is HttpStatusCode.NoContent or HttpStatusCode.OK);
                foreach (var reply in new[] { preflight, actual })
                    Assert.Equal((ListedOrigin, "true"), (reply.Header("Access-Control-Allow-Origin"), reply.Header("Access-Control-Allow-Credentials")));
                Assert.Contains("PUT", preflight.Header("Access-Control-Allow-Methods") ?? "", StringComparison.Ordinal);
                Assert.Equal(HttpStatusCode.OK, actual.Status);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Forbidden, preflight.Status);
                foreach (var reply in new[] { preflight, actual })
                {
                    Assert.Null(reply.Header("Access-Control-Allow-Origin"));
                    Assert.Null(reply.Header("Access-Control-Allow-Credentials"));
                }
            }
        }
        Assert.DoesNotContain(server.Traces, trace => trace.AllowOrigin is "*" or UnlistedOrigin or "null");

        // 4. admin-a may not grant what its ceiling excludes; the refusal names only its code.
        var admin = server.NewClient();
        await admin.SignInAsync(site.Admin);
        var beyond = await admin.SendAsync(HttpMethod.Post, Path("/authority/grants"),
            new { member = site.User1.Id.ToString("D"), permission = Items.Delete, revoke = false });
        Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.BeyondCeiling), (beyond.Status, beyond.Code));
        Assert.Equal(0L, await GrantsAsync(database, site.User1, Items.Delete));
        var within = await admin.SendAsync(HttpMethod.Post, Path("/authority/grants"),
            new { member = site.User1.Id.ToString("D"), permission = Items.Export, revoke = false });
        Assert.Equal(HttpStatusCode.OK, within.Status);
        Assert.Equal(1L, await GrantsAsync(database, site.User1, Items.Export));
        output.WriteLine($"admin-a granting item.delete: {beyond.Status} {beyond.Code}; item.export within its ceiling: {within.Status}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_N02_CrossSiteEndedSessionAndOverCeilingCallsAreRefused_Integration()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = AuthorizationHost.Create(database, clock);
        var site = await CrossSiteAsync(host);
        var administration = host.Get<AuthorityAdministration>();

        // The ended session's record: signing out ends it with its reason, a principal that it yielded is no longer current for
        // any current-profile decision, and the token never validates again.
        var (session, principal) = await host.SignInAsync(site.User1);
        Assert.True(await host.Get<ISessionService>().SignOutAsync(session.Token, Cancellation));
        var record = await database.ScalarAsync<int>("SELECT \"EndReason\" FROM " + database.Table(database.IdentitySchema, "Sessions") + " WHERE \"Id\" = @id",
            new NpgsqlParameter("id", principal.SessionId!.Value));
        Assert.NotEqual(0, record);
        Assert.Null(await host.Get<ISessionService>().ValidateAsync(session.Token, Cancellation));
        await using (var stale = await host.EnterAsync(site.User1, site.Tenant, principal))
        {
            Assert.Equal(AuthorizationCodes.PrincipalNotCurrent, (await host.AuthorizeAsync(stale, Items.Read, current: true)).Code);
            Assert.Equal(AuthorizationCodes.PrincipalNotCurrent, (await host.AuthorizeAsync(stale, Items.Update, current: true)).Code);
        }
        output.WriteLine($"The signed-out session ended with reason {record}; its principal is refused with {AuthorizationCodes.PrincipalNotCurrent}.");

        // admin-a's grant beyond its ceiling commits nothing and records nothing.
        await using var admin = await host.EnterAsync(site.Admin, site.Tenant);
        await using var owner = await host.EnterAsync(site.Owner, site.Tenant);
        var recorded = host.Audit.Records.Count;
        Assert.Equal(AuthorizationCodes.BeyondCeiling, (await Assert.ThrowsAsync<AuthorityChangeException>(() =>
            administration.GrantPermissionAsync(admin.Context, admin.Principal, site.User1.Actor, Items.Delete, Cancellation))).Code);
        Assert.Equal(0L, await GrantsAsync(database, site.User1, Items.Delete));
        Assert.Equal(recorded, host.Audit.Records.Count);

        Task<bool> Grant() => administration.GrantPermissionAsync(admin.Context, admin.Principal, site.User1.Actor, Items.Export, Cancellation);
        Task<bool> Reduce() => administration.DefineRoleAsync(owner.Context, owner.Principal, "item-admins",
            [AuthorizationModule.ManagePermission, Items.Read, Items.Export, Items.Delete], [Items.Read], Cancellation);
        Task<bool> Restore() => administration.DefineRoleAsync(owner.Context, owner.Principal, "item-admins",
            [AuthorizationModule.ManagePermission, Items.Read, Items.Export, Items.Delete], [Items.Read, Items.Export], Cancellation);

        // The concurrent pair, ordered both ways by holding the first change inside its transaction. Grant first: the reduction
        // waits on the tenant's authority row, the grant commits under the earlier ceiling, and the reduction cannot recall it.
        var grantHeld = HoldChange(host, AuthorityChangeKind.PermissionGranted, out var releaseGrant);
        var grant = Grant();
        await SignalledAsync(grantHeld, grant);
        var reduction = Reduce();
        Assert.NotSame(reduction, await Task.WhenAny(reduction, Task.Delay(TimeSpan.FromMilliseconds(500), Cancellation)));
        releaseGrant();
        Assert.True(await grant);
        Assert.True(await reduction);
        Assert.Equal(1L, await GrantsAsync(database, site.User1, Items.Export));
        var order = host.Audit.Records.Skip(recorded).Select(change => change.Kind).ToArray();
        Assert.Equal([AuthorityChangeKind.PermissionGranted, AuthorityChangeKind.RoleDefined], order);

        // Reduction first: the grant waits, then reads the reduced ceiling and is refused, so no grant commits under a ceiling
        // that excluded it.
        Assert.True(await administration.RevokePermissionAsync(owner.Context, owner.Principal, site.User1.Actor, Items.Export, Cancellation));
        Assert.True(await Restore());
        var reductionHeld = HoldChange(host, AuthorityChangeKind.RoleDefined, out var releaseReduction);
        reduction = Reduce();
        await SignalledAsync(reductionHeld, reduction);
        grant = Grant();
        Assert.NotSame(grant, await Task.WhenAny(grant, Task.Delay(TimeSpan.FromMilliseconds(500), Cancellation)));
        releaseReduction();
        Assert.True(await reduction);
        Assert.Equal(AuthorizationCodes.BeyondCeiling, (await Assert.ThrowsAsync<AuthorityChangeException>(() => grant)).Code);
        Assert.Equal(0L, await GrantsAsync(database, site.User1, Items.Export));
        await using (var member = await host.EnterAsync(site.User1, site.Tenant))
            Assert.Equal(AuthorizationCodes.NotHeld, (await host.AuthorizeAsync(member, Items.Export, current: true)).Code);
        output.WriteLine($"Grant first: committed under the earlier ceiling, then the reduction ({string.Join(", ", order)}). Reduction first: the grant "
            + $"was refused with {AuthorizationCodes.BeyondCeiling}.");
    }

    private static Task HoldChange(AuthorizationHost host, AuthorityChangeKind kind, out Action release)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = () =>
        {
            host.Audit.Holding = null;
            gate.SetResult();
        };
        host.Audit.Holding = async change =>
        {
            if (change.Kind != kind) return;
            reached.TrySetResult();
            await gate.Task.WaitAsync(SignalDeadline, TestContext.Current.CancellationToken);
        };
        return reached.Task;
    }
}
