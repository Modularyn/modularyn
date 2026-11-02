using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Features;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// AC-003-N03: settings, features and composition supply no authority. The deployment's session idle timeout is a deployment
/// option and no setting, so a tenant cannot override it; feature items.export-enabled is on for tenant A while user-1 lacks
/// item.export; module M, Test.Excluded, is not in the composition; and a release that registers an endpoint declaring
/// neither a policy nor explicit anonymous access fails startup naming it.
/// </summary>
public sealed partial class G02AC003Tests
{
    private const string ExcludedPermission = "excluded.read";
    private const string IdentityModule = "Modularyn.Modules.Identity";
    private const string IdleTimeout = "sessions.idle-timeout";

    private sealed class OnGate : IFeatureGate
    {
        public ValueTask<bool> AllowsAsync(TenantContext context, FeatureDefinition feature, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    [Fact]
    public async Task AC_003_N03_SettingsAndFeaturesCannotSupplyAuthority_Unit()
    {
        // The deployment's idle timeout is an identity option, bound once at startup and validated there; no value definition of
        // any composed module names it, so the settings contract has nothing a tenant could change.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModularynIdentity(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Modularyn:Identity:Sessions:IdleTimeout"] = "00:20:00" }).Build());
        await using (var options = services.BuildServiceProvider())
            Assert.Equal(TimeSpan.FromMinutes(20), options.GetRequiredService<IOptions<IdentitySecurityOptions>>().Value.Sessions.IdleTimeout);
        var manifests = new[] { new Modularity.Abstractions.ModuleManifest(Items.ModuleId, Items.Version,
            definitions: [Items.ExportEnabled.Declaration, Items.PageSize.Declaration]) };
        var values = new ValueDefinitionRegistry(manifests, [Items.ExportEnabled, Items.PageSize]);
        Assert.DoesNotContain(values.Definitions, definition => definition.ModuleId == IdentityModule || definition.Name.Contains("idle", StringComparison.Ordinal));

        // An entitlement is no permission: the evaluator never consults a feature, so with the feature on and no grant the export
        // is refused, and the bootstrap description holds no export. Module M's permission is declared by no composed module, so
        // it confers nothing even where a stale grant of it survives in the store.
        await using var authorization = UnitAuthorization.Create(configure => configure.AddSingleton<IFeatureGate>(new OnGate()));
        var tenant = authorization.Directory.Add("Tenant A");
        authorization.Seams.Membership.Add("user-1", tenant);
        authorization.Authority.Grant(tenant, "user-1", Items.Read, ExcludedPermission);
        var unit = await authorization.EnterAsync("user-1", tenant);
        var evaluator = authorization.Get<IPermissionEvaluator>();
        Assert.Equal(AuthorizationCodes.NotHeld, (await evaluator.AuthorizeAsync(new AuthorizationRequest(unit.Context, unit.Principal, Items.Export), Cancellation)).Code);
        Assert.Equal(AuthorizationCodes.UndefinedPermission,
            (await evaluator.AuthorizeAsync(new AuthorizationRequest(unit.Context, unit.Principal, ExcludedPermission) { RequireCurrent = true }, Cancellation)).Code);
        Assert.Null(authorization.Get<AuthorizationRegistry>().FindPermission(ExcludedPermission));
        Assert.Equal([Items.Read], (await evaluator.DescribeAsync(unit.Context, unit.Principal, Cancellation))!.Permissions);

        // A release that maps one endpoint with neither a policy nor an explicit anonymous declaration, and one whose declaration
        // says anonymous while the endpoint is not, never starts: the inventory check refuses before the server listens, naming it.
        foreach (var (route, declare) in new (string, Action<RouteHandlerBuilder>)[]
        {
            ("/t/{tenant}/reports", _ => { }),
            ("/status", builder => builder.WithApiContract(new ApiEndpoint("Status", "GET", "/status", "Anonymous", null, false, null, null,
                Versioned: false, Authentication: "none")))
        })
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, 0));
            builder.Services.AddModularynApi();
            await using var application = builder.Build();
            var answered = 0;
            declare(application.MapGet(route, () =>
            {
                Interlocked.Increment(ref answered);
                return Results.Ok();
            }));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => application.StartAsync(Cancellation));
            Assert.Contains("GET " + route, refused.Message, StringComparison.Ordinal);
            Assert.Empty(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            Assert.Equal(0, answered);
            output.WriteLine($"Startup refused: {refused.Message}");
        }
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_N03_SettingsAndFeaturesCannotSupplyAuthority_Contract()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var setup = AuthorizationHost.Create(database, clock);
        var tenant = await setup.TenantAsync("Tenant A");
        var owner = await setup.UserAsync("owner-a", tenant);
        var user = await setup.UserAsync("user-1", tenant);
        await setup.OwnerAsync(owner, tenant);
        await using var server = await DeclaredServer.StartAsync(database, clock, services: services => services.AddSingleton<IFeatureGate>(new OnGate()));
        string Path(string rest) => "/t/" + TenantIdentifier.Format(tenant) + rest;
        var administrator = server.NewClient();
        await administrator.SignInAsync(owner);

        // 1. Tenant A's administrator, who may manage tenant overrides, sets the session idle timeout at tenant scope: refused as
        //    no setting, while a declared setting at the same scope is accepted, and the effective option is the deployment's.
        var idle = await administrator.SendAsync(HttpMethod.Put, Path("/settings/" + IdentityModule + "/" + IdleTimeout), new { value = "00:00:01" });
        Assert.Equal((HttpStatusCode.NotFound, ApiCodes.NotFound), (idle.Status, idle.Code));
        var declared = await administrator.SendAsync(HttpMethod.Put, Path("/settings/" + Items.ModuleId + "/" + Items.PageSize.Name), new { value = "50" });
        Assert.Equal(HttpStatusCode.OK, declared.Status);
        Assert.Equal(TimeSpan.FromMinutes(20), server.Get<IOptions<IdentitySecurityOptions>>().Value.Sessions.IdleTimeout);
        // The session the administrator holds is still current after two idle minutes, as the deployment's 20 minutes allow.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await administrator.SendAsync(HttpMethod.Get, IdentityEndpointsCurrentUser)).Status);
        output.WriteLine($"Tenant override of the idle timeout: {idle.Status} {idle.Code}; of the declared page size: {declared.Status}.");

        // 2. The export feature is on for tenant A and its gate allows it; user-1 holds no item.export, and the export is refused.
        await using (var unit = await setup.EnterAsync(owner, tenant))
        {
            await using var scope = setup.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<FeatureService>().SetAsync(unit.Context, unit.Principal, Items.ExportEnabled, ValueSource.Tenant, true,
                cancellationToken: Cancellation);
        }
        await using (var unit = await setup.EnterAsync(user, tenant))
        {
            await using var scope = server.Services.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<FeatureService>().IsEnabledAsync(unit.Context, Items.ExportEnabled, Cancellation));
        }
        var member = server.NewClient();
        await member.SignInAsync(user);
        var export = await member.SendAsync(HttpMethod.Post, Path("/exports"));
        Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.NotHeld), (export.Status, export.Code));

        // Module M's permission cannot be granted, even by the tenant's owner, who holds every declared permission.
        var excluded = await administrator.SendAsync(HttpMethod.Post, Path("/authority/grants"),
            new { member = user.Id.ToString("D"), permission = ExcludedPermission, revoke = false });
        Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.ChangeUndefined), (excluded.Status, excluded.Code));

        // 3. Module M's routes are absent rather than hidden: they answer exactly as a route that never existed, not 403.
        var absent = await member.SendAsync(HttpMethod.Get, Path("/excluded/items"));
        var never = await member.SendAsync(HttpMethod.Get, Path("/no-such-route"));
        Assert.Equal((HttpStatusCode.NotFound, ApiCodes.NotFound), (absent.Status, absent.Code));
        Assert.Equal(Uncorrelated(never), Uncorrelated(absent));
        output.WriteLine($"Export with the feature on: {export.Status} {export.Code}; grant of M's permission: {excluded.Code}; M's route: {absent.Status}.");

        // 4. The same release with one undeclared endpoint added fails startup before the host is ready, naming it.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DeclaredServer.StartAsync(database, clock,
            endpoints: application => application.MapGet("/t/{tenant}/excluded/items", () => Results.Ok()).RequireTenantMember()));
        Assert.Contains("GET /t/{tenant}/excluded/items", refused.Message, StringComparison.Ordinal);
        output.WriteLine($"The fixture build failed startup: {refused.Message}");
    }

    private const string IdentityEndpointsCurrentUser = Modules.Identity.HttpApi.IdentityEndpoints.CurrentUserPath;
}
