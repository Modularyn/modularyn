using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// AC-003-P01: one grant applied equivalently at every declared entry point, with the inventory compared against the
/// declared permissions and the startup report showing no protected path that relies on interception alone. The
/// advertised .NET and TypeScript clients are CODE-06's; these tests cover the direct API, a declared integration
/// endpoint, an application service, a worker entry point and an asynchronous continuation.
/// </summary>
[Collection(AuthorizationPostgreSqlCollection.Name)]
public sealed partial class G02AC003Tests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC_003_P01_EveryEntryPointAppliesTheSameGrant_Unit()
    {
        await using var authorization = UnitAuthorization.Create();
        var tenant = authorization.Directory.Add("Tenant A");
        authorization.Seams.Membership.Add("user-1", tenant);
        var unit = await authorization.EnterAsync("user-1", tenant);
        var evaluator = authorization.Get<IPermissionEvaluator>();
        var endpoint = new ConventionCollector();
        endpoint.RequirePermission(Items.Update);
        var ran = 0;
        var update = endpoint.Build("/t/{tenant}/items/{code}", _ =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        });
        var policy = PermissionRequirement.Policy(Items.Update);
        var job = ExecutionPrincipal.ForService(new PrincipalReference(Items.Service, IdentityKind.Service), [Items.Update]);
        var propagated = PropagatedTenantContext.Capture(unit.Context).ToJson();

        async Task<string[]> EveryEntryPointAsync()
        {
            var codes = new List<string>();
            // 1. The HTTP endpoint's own declaration.
            var http = new DefaultHttpContext { RequestServices = unit.Scope.ServiceProvider };
            http.Response.Body = new MemoryStream();
            await update.RequestDelegate!(http);
            http.Response.Body.Position = 0;
            codes.Add(http.Response.StatusCode == StatusCodes.Status200OK ? "permitted"
                : JsonDocument.Parse(http.Response.Body).RootElement.GetProperty("code").GetString()!);
            // 2. The standard ASP.NET Core policy, decided by the same evaluator.
            var decided = await authorization.Get<IAuthorizationService>().AuthorizeAsync(new ClaimsPrincipal(), http, policy);
            codes.Add(decided.Succeeded ? "permitted" : decided.Failure!.FailureReasons.Single().Message);
            // 3. An application service.
            var direct = await evaluator.AuthorizeAsync(new AuthorizationRequest(unit.Context, unit.Principal, Items.Update), Cancellation);
            codes.Add(direct.Code ?? "permitted");
            // 4. A worker entry point, running the item job as its service identity for the request's producer.
            var outcome = await authorization.Get<AuthorizedWorkRunner>().RunAsync(job, propagated, Items.Update, (_, _, _) => Task.CompletedTask, Cancellation);
            codes.Add(outcome.Disposition == TenantWorkDisposition.Completed ? "permitted" : outcome.Code!);
            // 5. An asynchronous continuation of running work, which rechecks at the current profile.
            var resumed = await evaluator.AuthorizeAsync(new AuthorizationRequest(unit.Context, unit.Principal, Items.Update) { RequireCurrent = true }, Cancellation);
            codes.Add(resumed.Code ?? "permitted");
            return [.. codes];
        }

        var before = await EveryEntryPointAsync();
        Assert.Equal(Enumerable.Repeat(AuthorizationCodes.NotHeld, 5), before);
        Assert.Equal(0, ran);

        // The grant, as permission administration commits it: the store changes, then the tenant's cached authority is invalidated.
        authorization.Authority.Grant(tenant, "user-1", Items.Update);
        authorization.Get<TenantCache>().InvalidateTenant(tenant);
        Assert.Equal(Enumerable.Repeat("permitted", 5), await EveryEntryPointAsync());
        Assert.Equal(1, ran);
        output.WriteLine($"Before the grant every entry point refused with {before[0]}; after it every one permitted.");
    }

    [Fact]
    public async Task AC_003_P01_EveryEntryPointAppliesTheSameGrant_Contract()
    {
        await using var server = await ContractServer.StartAsync();
        var tenant = server.Directory.Add("Tenant A");
        server.Seams.Membership.Add("user-1", tenant);
        var direct = "/t/" + TenantIdentifier.Format(tenant) + "/items/item-1";
        var integration = "/t/" + TenantIdentifier.Format(tenant) + "/integration/items/item-1";

        // The inventory against the declared permissions: every endpoint declares a permission or explicit anonymous access,
        // except the fixture protected only by an authorization policy, which the startup report names.
        var inventory = AuthorizationEntryPoints.Describe(server.Get<EndpointDataSource>());
        foreach (var point in inventory) output.WriteLine($"{string.Join(",", point.Methods)} {point.Route}: {point.Kind} {point.Permission}");
        Assert.Equal(
        [
            ("/status", AuthorizationEntryPointKind.Anonymous, (string?)null),
            ("/t/{tenant}/integration/items/{code}", AuthorizationEntryPointKind.Permission, Items.Update),
            ("/t/{tenant}/items/{code}", AuthorizationEntryPointKind.Permission, Items.Update),
            (ContractServer.LegacyRoute, AuthorizationEntryPointKind.InterceptionOnly, null)
        ], inventory.Select(point => (point.Route, point.Kind, point.Permission)));
        Assert.All(inventory.Where(point => point.Kind == AuthorizationEntryPointKind.Permission),
            point => Assert.NotNull(server.Get<AuthorizationRegistry>().FindPermission(point.Permission)));
        var reported = server.Events.Events.Where(entry => entry.Id == 8304).Select(entry => entry.Message).ToArray();
        Assert.Contains(ContractServer.LegacyRoute, Assert.Single(reported), StringComparison.Ordinal);
        Assert.DoesNotContain(server.Events.Events, entry => entry.Id == 8305);

        // Before the grant both entry points refuse with the same status and code; the handler never runs.
        foreach (var path in new[] { direct, integration })
        {
            var method = path == direct ? HttpMethod.Put : HttpMethod.Post;
            Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.NotHeld), Pair(await server.CallAsync(method, path, "user-1")));
            Assert.Equal((HttpStatusCode.Unauthorized, (string?)null), Pair(await server.CallAsync(method, path, null)));
        }
        server.Authority.Grant(tenant, "user-1", Items.Update);
        server.Get<TenantCache>().InvalidateTenant(tenant);
        Assert.Equal(HttpStatusCode.OK, (await server.CallAsync(HttpMethod.Put, direct, "user-1")).Status);
        Assert.Equal(HttpStatusCode.OK, (await server.CallAsync(HttpMethod.Post, integration, "user-1")).Status);
        output.WriteLine("Both entry points refused with 403 MAZ005 before the grant and permitted after it.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_P01_EveryEntryPointAppliesTheSameGrant_Isolation()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var setup = AuthorizationHost.Create(database, clock);
        var tenantA = await setup.TenantAsync("Tenant A");
        var tenantB = await setup.TenantAsync("Tenant B");
        var admin = await setup.UserAsync("admin-1", tenantA, tenantB);
        var user = await setup.UserAsync("user-1", tenantA, tenantB);
        await setup.OwnerAsync(admin, tenantA);
        await setup.OwnerAsync(admin, tenantB);
        foreach (var tenant in new[] { tenantA, tenantB })
        {
            await using var owner = await setup.EnterAsync(admin, tenant);
            await ItemWork.AddAsync(database.ItemsModule, owner.Context, user.Id.ToString("D"), "item-1");
        }
        // user-1 holds item.read in tenant A, so a refusal of the update comes from the missing grant, not from visibility.
        await using (var owner = await setup.EnterAsync(admin, tenantA))
            await setup.Get<AuthorityAdministration>().GrantPermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Read, Cancellation);

        await using var server = await AuthorizationServer.StartAsync(database, clock);
        var userBrowser = server.Browser();
        await userBrowser.SignInAsync(user);
        var adminBrowser = server.Browser();
        await adminBrowser.SignInAsync(admin);
        string Path(Guid tenant, string rest) => "/t/" + TenantIdentifier.Format(tenant) + rest;
        var userSession = (await server.Get<Modules.Identity.Application.Contracts.ISessionService>().ValidateAsync(
            Modules.Identity.Application.Contracts.SessionToken.TryParse(userBrowser.Session, out var token) ? token : throw new InvalidOperationException(),
            Cancellation))!.ToPrincipal();

        async Task<string[]> EveryEntryPointAsync()
        {
            var codes = new List<string>
            {
                Code(await userBrowser.CallAsync(HttpMethod.Put, Path(tenantA, "/items/item-1"))),
                Code(await userBrowser.CallAsync(HttpMethod.Post, Path(tenantA, "/integration/items/item-1")))
            };
            // The application service and the continuation, in a unit of the server's own process.
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
                    TenantResolutionRequest.ForMember(SessionClaims(user), [Candidates.Header(tenantA)]), Cancellation);
                Assert.True(decision.Succeeded, decision.Code);
                var evaluator = server.Get<IPermissionEvaluator>();
                var item = await ItemWork.FindAsync(database.ItemsModule, decision.Context!, "item-1", Cancellation);
                codes.Add((await evaluator.AuthorizeAsync(new AuthorizationRequest(decision.Context!, userSession, Items.Update)
                    { Resource = Items.Reference(item!) }, Cancellation)).Code ?? "permitted");
                codes.Add((await evaluator.AuthorizeAsync(new AuthorizationRequest(decision.Context!, userSession, Items.Update) { RequireCurrent = true },
                    Cancellation)).Code ?? "permitted");
                // The worker entry point: the item job, as its service identity, for this producer.
                var propagated = PropagatedTenantContext.Capture(decision.Context!, clock).ToJson();
                await using var job = server.Get<IServiceIdentityScopes>().Begin(Items.Service);
                var outcome = await server.Get<AuthorizedWorkRunner>().RunAsync(job.Principal, propagated, Items.Update, (_, _, _) => Task.CompletedTask, Cancellation);
                codes.Add(outcome.Disposition == TenantWorkDisposition.Completed ? "permitted" : outcome.Code!);
            }
            return [.. codes];
        }

        var before = await EveryEntryPointAsync();
        Assert.Equal(Enumerable.Repeat(AuthorizationCodes.NotHeld, 5), before);

        // The tenant's owner grants item.update to user-1 in tenant A through permission administration over HTTP.
        Assert.Equal(HttpStatusCode.NoContent, (await adminBrowser.CallAsync(HttpMethod.Post, Path(tenantA, "/authority/grants"),
            new { member = user.Id.ToString("D"), permission = Items.Update })).Status);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table(database.AuthorizationSchema, "PermissionGrants")
            + " WHERE \"TenantId\" = @tenant AND \"Member\" = @member AND \"Permission\" = @permission", new NpgsqlParameter("tenant", tenantA),
            new NpgsqlParameter("member", user.Id.ToString("D")), new NpgsqlParameter("permission", Items.Update)));
        Assert.Equal(Enumerable.Repeat("permitted", 5), await EveryEntryPointAsync());

        // The grant is tenant A's: in tenant B the same member, the same item code and the same entry points are still refused.
        Assert.Equal(AuthorizationCodes.NotHeld, Code(await userBrowser.CallAsync(HttpMethod.Put, Path(tenantB, "/items/item-1"))));
        Assert.Equal(AuthorizationCodes.NotHeld, Code(await userBrowser.CallAsync(HttpMethod.Post, Path(tenantB, "/integration/items/item-1"))));
        output.WriteLine($"Before the grant all five entry points refused with {before[0]}; after it all permitted in tenant A, and tenant B still refused.");
    }

    private static (HttpStatusCode, string?) Pair(Answer answer) => (answer.Status, answer.Code);

    private static string Code(Answer answer) => answer.Status is HttpStatusCode.NoContent or HttpStatusCode.OK ? "permitted" : answer.Code ?? answer.Status.ToString();

    /// <summary>The claims identity's session handler issues for an account, which the identity actor seam resolves.</summary>
    private static ClaimsPrincipal SessionClaims(User user) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D"))],
        Modules.Identity.HttpApi.IdentityHttpServiceCollectionExtensions.AuthenticationScheme));
}
