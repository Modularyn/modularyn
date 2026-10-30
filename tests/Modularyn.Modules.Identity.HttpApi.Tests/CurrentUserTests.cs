using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.HttpApi.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class CurrentUserTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "TM-G02-019 the current-user view requires an authenticated principal and is inventoried as requiring one")]
    public async Task TM_G02_019_The_current_user_view_requires_an_authenticated_principal_and_is_inventoried_as_requiring_one()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);

        // The endpoint declares the requirement itself, so the authorization report and the API inventory both name it.
        var endpoints = server.Get<EndpointDataSource>();
        var point = Assert.Single(AuthorizationEntryPoints.Describe(endpoints), point => point.Route == IdentityEndpoints.CurrentUserPath);
        Assert.Equal(AuthorizationEntryPointKind.Authenticated, point.Kind);
        Assert.Null(point.Permission);
        var declared = Assert.Single(ApiEndpoints.Describe(endpoints).Endpoints, endpoint => endpoint.Route == IdentityEndpoints.CurrentUserPath);
        Assert.Equal("Authenticated", declared.Authorization);
        Assert.Equal(FrameworkApi.Endpoint("GetCurrentUser"), declared);

        // Without a session, and with a value that is not one, the request is refused before the view is built.
        var anonymous = server.Browser();
        foreach (var cookie in new string?[] { null, "not-a-session" })
        {
            if (cookie is not null) anonymous.Cookies[SessionCookie.Name] = cookie;
            var refused = await anonymous.SendAsync(HttpMethod.Get, IdentityEndpoints.CurrentUserPath);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", refused.Headers.CacheControl?.ToString());
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Cancellation));
            Assert.Equal(AuthorizationCodes.Unauthenticated, problem.RootElement.GetProperty("code").GetString());
            Assert.False(problem.RootElement.TryGetProperty("actor", out _));
        }

        // A signed-in user still receives its own view, as before.
        var password = Generated.Password();
        var account = (await server.Get<IAccountService>().CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), "admin-1",
            new SecretText(password), Cancellation)).AccountId!.Value;
        var browser = server.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.SignInAsync("admin-1", password)).StatusCode);
        var view = await browser.CurrentUserAsync();
        Assert.NotNull(view);
        Assert.Equal(account.ToString("D"), view.Value.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("user", view.Value.GetProperty("actor").GetProperty("kind").GetString());
        Assert.False(view.Value.GetProperty("delegated").GetBoolean());
        output.WriteLine($"Anonymous: 401 {AuthorizationCodes.Unauthenticated}; signed in: the caller's own view; classified {point.Kind}.");
    }
}
