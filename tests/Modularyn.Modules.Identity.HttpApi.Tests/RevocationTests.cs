using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.HttpApi.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class RevocationTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static void MapPublish(WebApplication application) =>
        application.MapPost("/publish", (IExecutionPrincipalAccessor principals) =>
            principals.Current is null ? Results.StatusCode(StatusCodes.Status401Unauthorized) : Results.NoContent());

    private static async Task<(HttpStatusCode Read, HttpStatusCode Publish)> UseAsync(Browser browser) =>
        ((await browser.SendAsync(HttpMethod.Get, IdentityEndpoints.CurrentUserPath)).StatusCode,
            (await browser.SendAsync(HttpMethod.Post, "/publish", content: new StringContent(""))).StatusCode);

    [Fact(DisplayName = "TM-G02-017 sign-out, revocation and security changes end exactly the sessions they should at the next request")]
    public async Task TM_G02_017_Sign_out_revocation_and_security_changes_end_sessions_at_the_next_request()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database, endpoints: MapPublish);
        var accounts = server.Get<IAccountService>();
        var password = Generated.Password();
        var account = (await accounts.CreateLocalAccountAsync("admin-1", new SecretText(password), Cancellation)).AccountId!.Value;
        var bystanderPassword = Generated.Password();
        await accounts.CreateLocalAccountAsync("editor-1", new SecretText(bystanderPassword), Cancellation);
        var bystander = server.Browser("198.51.100.7");
        Assert.Equal(HttpStatusCode.OK, (await bystander.SignInAsync("editor-1", bystanderPassword)).StatusCode);

        async Task<(Browser Live, Browser Copy)> SignedIn()
        {
            var browser = server.Browser();
            Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("admin-1", password)).StatusCode);
            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NoContent), await UseAsync(browser));
            return (browser, browser.Copy());
        }

        async Task Refused(Browser copy, SessionEndCause cause, string description)
        {
            // The copied cookie, with a valid antiforgery token, is refused for a read and for a state change.
            Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.BadRequest), await UseAsync(copy));
            Assert.True(await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Sessions") + " WHERE \"EndReason\" = @reason",
                new NpgsqlParameter("reason", (int)cause)) >= 1);
            output.WriteLine($"{description}: the copied session cookie was refused at its next request; the record states {cause}.");
        }

        var (s1, s1Copy) = await SignedIn();
        var (s2, _) = await SignedIn();
        Assert.Equal(HttpStatusCode.NoContent, (await s1.SignOutAsync()).StatusCode);
        await Refused(s1Copy, SessionEndCause.SignedOut, "Sign-out of S1");
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NoContent), await UseAsync(s2));

        var (_, s3Copy) = await SignedIn();
        var newPassword = Generated.Password();
        Assert.Equal(PasswordChangeStatus.Changed,
            (await accounts.ChangePasswordAsync(account, new SecretText(password), new SecretText(newPassword), "203.0.113.5", Cancellation)).Status);
        password = newPassword;
        await Refused(s3Copy, SessionEndCause.PasswordChanged, "A password change");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s2.SendAsync(HttpMethod.Get, IdentityEndpoints.CurrentUserPath)).StatusCode);

        var (_, s4Copy) = await SignedIn();
        var (_, s5Copy) = await SignedIn();
        Assert.Equal(2, await server.Get<ISessionService>().RevokeAllAsync(account, Cancellation));
        await Refused(s4Copy, SessionEndCause.Revoked, "Revocation of every admin-1 session");
        await Refused(s5Copy, SessionEndCause.Revoked, "Revocation of every admin-1 session");

        var (_, s6Copy) = await SignedIn();
        Assert.True(await accounts.SecurityStateChangedAsync(account, Cancellation));
        await Refused(s6Copy, SessionEndCause.SecurityStateChanged, "A security-state change made by another module");

        var (_, s7Copy) = await SignedIn();
        Assert.True(await accounts.DisableAsync(account, Cancellation));
        await Refused(s7Copy, SessionEndCause.AccountDisabled, "Disabling the account");
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Browser().SignInAsync("admin-1", password)).StatusCode);

        // None of these events ended the other account's session.
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NoContent), await UseAsync(bystander));
    }

    [Fact(DisplayName = "TM-G02-017 a security-sensitive recheck inside a running request sees a revocation made after the request began")]
    public async Task TM_G02_017_A_recheck_inside_a_running_request_sees_a_later_revocation()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await IdentityTestServer.StartAsync(database, endpoints: application =>
            application.MapPost("/export", async (IExecutionPrincipalAccessor principals, IPrincipalFreshness freshness, CancellationToken token) =>
            {
                var principal = principals.Current;
                if (principal is null) return Results.StatusCode(StatusCodes.Status401Unauthorized);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                var status = await freshness.RecheckAsync(principal, token);
                return status == FreshnessStatus.Current ? Results.Ok("exported") : Results.Json(new { status = status.ToString() }, statusCode: StatusCodes.Status403Forbidden);
            }));
        var password = Generated.Password();
        var account = (await server.Get<IAccountService>().CreateLocalAccountAsync("editor-5", new SecretText(password), Cancellation)).AccountId!.Value;
        var browser = server.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("editor-5", password)).StatusCode);

        var export = browser.SendAsync(HttpMethod.Post, "/export", content: new StringContent(""));
        await entered.Task.WaitAsync(Cancellation);
        Assert.Equal(1, await server.Get<ISessionService>().RevokeAllAsync(account, Cancellation));
        release.TrySetResult();
        var response = await export;
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(nameof(FreshnessStatus.SessionEnded), await response.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        output.WriteLine("An export that began before the revocation rechecked the session and stopped.");
    }
}
