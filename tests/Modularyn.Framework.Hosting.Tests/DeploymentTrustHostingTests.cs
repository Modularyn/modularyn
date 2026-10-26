using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

// The corpus has no reference-profile trust case, so these tests carry the control identifier they serve.
public sealed class DeploymentTrustHostingTests : IDisposable
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private readonly string root = Path.Combine(Path.GetTempPath(), "modularyn-trust-host-" + Guid.NewGuid().ToString("N"));
    private readonly string content;
    private readonly string lockPath;

    public DeploymentTrustHostingTests()
    {
        content = Path.Combine(root, "app");
        Directory.CreateDirectory(content);
        lockPath = Path.Combine(root, "deployment.lock.json");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact(DisplayName = "TM-G02-001 a host without deployment trust refuses activation before construction")]
    public async Task TM_G02_001_A_host_without_deployment_trust_refuses_activation_before_construction()
    {
        using var fixture = new LifecycleFixture();
        var host = fixture.Host(options: new ModuleHostOptions());
        var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => host.StartAsync(Cancellation));
        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(ModuleHostDiagnosticCodes.TrustNotConfigured, diagnostic.Code);
        Assert.Equal("MDC108", diagnostic.Code);
        Assert.Equal(new[] { "A", "B", "C" }, diagnostic.ModuleIds);
        Assert.Contains("AddModularynDeploymentTrust", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("MDC108", Assert.Single(host.Report.Diagnostics).Code);
        AssertRefusedBeforeActivation(host, fixture);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()));
        using var application = builder.Build();
        await Assert.ThrowsAsync<ModuleCompositionException>(() => application.StartAsync(Cancellation));
        Assert.False(application.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, fixture.Constructed);
        var health = application.Services.GetRequiredService<HealthCheckService>();
        Assert.False(application.Services.GetRequiredService<ModuleHost>().Work.IsAccepting);
        AssertStatusOnly(await Check(health, ModuleHealthChecks.ReadinessTag), HealthStatus.Unhealthy);
        AssertStatusOnly(await Check(health, ModuleHealthChecks.LivenessTag), HealthStatus.Healthy);
    }

    [Fact(DisplayName = "TM-G02-001 readiness follows a verified activation, and a refused composition stays unready with status-only health")]
    public async Task TM_G02_001_Readiness_follows_a_verified_activation_and_a_refused_composition_stays_unready_with_status_only_health()
    {
        using var fixture = new LifecycleFixture();
        var graph = LifecycleFixture.Graph();
        File.WriteAllText(lockPath, new DeploymentLock(graph.Select(Package)).ToJson(), new UTF8Encoding(false));
        var file = Path.Combine(content, "B", "Modularyn.Modules.B.dll");
        var original = File.ReadAllBytes(file);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddModularynDeploymentTrust(lockPath, content);
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(graph));
        await using var application = builder.Build();
        application.UseRouting();
        application.UseModularynRequests();
        application.MapModularynHealthChecks();
        application.MapGet("/work", () => "done");
        var host = application.Services.GetRequiredService<ModuleHost>();
        var health = application.Services.GetRequiredService<HealthCheckService>();
        AssertStatusOnly(await Check(health, ModuleHealthChecks.ReadinessTag), HealthStatus.Unhealthy);

        await application.StartAsync(Cancellation);
        var address = Assert.Single(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        Assert.True(host.Report.IsReady);
        await AssertProbe(client, "/health/ready", HttpStatusCode.OK, "Healthy");
        await AssertProbe(client, "/health/live", HttpStatusCode.OK, "Healthy");
        await AssertProbe(client, "/work", HttpStatusCode.OK, "done");
        AssertStatusOnly(await Check(health, ModuleHealthChecks.ReadinessTag), HealthStatus.Healthy);

        await host.StopAsync(Cancellation);
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(original.Length));
        var refused = await Assert.ThrowsAsync<ModuleCompositionException>(() => host.StartAsync(Cancellation));
        Assert.Equal("MDC211", Assert.Single(refused.Diagnostics).Code);
        Assert.False(host.Report.IsReady);
        Assert.False(host.Work.IsAccepting);
        Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.Refused, module.State));
        Assert.Equal(3, fixture.Constructed);
        await AssertProbe(client, "/health/ready", HttpStatusCode.ServiceUnavailable, "Unhealthy");
        await AssertProbe(client, "/health/live", HttpStatusCode.OK, "Healthy");
        await AssertProbe(client, "/work", HttpStatusCode.ServiceUnavailable, "Unhealthy");
        AssertStatusOnly(await Check(health, ModuleHealthChecks.ReadinessTag), HealthStatus.Unhealthy);

        File.WriteAllBytes(file, original);
        await host.StartAsync(Cancellation);
        Assert.Equal(6, fixture.Constructed);
        await AssertProbe(client, "/health/ready", HttpStatusCode.OK, "Healthy");
        await application.StopAsync(Cancellation);
        Assert.Empty(fixture.ActiveResources);
    }

    [Fact(DisplayName = "TM-G02-001 the standard host activates only the locked release and recovers once it is restored")]
    public async Task TM_G02_001_The_standard_host_activates_only_the_locked_release_and_recovers_once_it_is_restored()
    {
        using var fixture = new LifecycleFixture();
        var graph = LifecycleFixture.Graph();
        File.WriteAllText(lockPath, new DeploymentLock(graph.Select(Package)).ToJson(), new UTF8Encoding(false));
        var file = Path.Combine(content, "B", "Modularyn.Modules.B.dll");
        var original = File.ReadAllBytes(file);

        using (var first = Application(fixture, graph))
        {
            Assert.True(first.Services.GetRequiredService<ModuleComposer>().VerifiesTrust);
            await first.StartAsync(Cancellation);
            Assert.True(first.Services.GetRequiredService<ModuleHost>().Report.IsReady);
            await first.StopAsync(Cancellation);
        }
        Assert.Equal(3, fixture.Constructed);

        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(original.Length));
        using (var altered = Application(fixture, graph))
        {
            var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => altered.StartAsync(Cancellation));
            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("MDC211", diagnostic.Code);
            Assert.Equal(new[] { "B" }, diagnostic.ModuleIds);
            Assert.False(altered.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
            var host = altered.Services.GetRequiredService<ModuleHost>();
            Assert.Null(host.Report.Plan);
            Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.Refused, module.State));
            Assert.Empty(host.Report.Events);
        }
        Assert.Equal(3, fixture.Constructed);

        using (var unlisted = Application(fixture, [.. graph, new("D", LifecycleFixture.One)]))
        {
            File.WriteAllBytes(file, original);
            var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => unlisted.StartAsync(Cancellation));
            Assert.Equal("MDC202", Assert.Single(exception.Diagnostics).Code);
        }
        Assert.Equal(3, fixture.Constructed);

        using (var restored = Application(fixture, graph))
        {
            await restored.StartAsync(Cancellation);
            Assert.True(restored.Services.GetRequiredService<ModuleHost>().Report.IsReady);
            await restored.StopAsync(Cancellation);
        }
        Assert.Equal(6, fixture.Constructed);
        Assert.All(new[] { "A", "B", "C" }, id => Assert.Equal(new[] { "initialized" }, fixture.DurableEffects(id)));
        Assert.Empty(fixture.ActiveResources);
    }

    [Fact]
    public void Registering_more_than_one_deployment_lock_is_refused()
    {
        var services = new ServiceCollection();
        services.AddModularynDeploymentTrust(lockPath, content);
        Assert.Throws<InvalidOperationException>(() => services.AddModularynDeploymentTrust(lockPath, content));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddModularynDeploymentTrust(" "));
    }

    private IHost Application(LifecycleFixture fixture, IEnumerable<ModuleManifest> selection)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynDeploymentTrust(lockPath, content);
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(selection));
        return builder.Build();
    }

    private LockedModule Package(ModuleManifest manifest)
    {
        var directory = Path.Combine(content, manifest.Id);
        Directory.CreateDirectory(directory);
        var bytes = RandomNumberGenerator.GetBytes(2048);
        File.WriteAllBytes(Path.Combine(directory, $"Modularyn.Modules.{manifest.Id}.dll"), bytes);
        return LockedModule.Describe(manifest, [new LockedFile($"{manifest.Id}/Modularyn.Modules.{manifest.Id}.dll", Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
    }

    private static Task<HealthReport> Check(HealthCheckService health, string tag) =>
        health.CheckHealthAsync(registration => registration.Tags.Contains(tag), Cancellation);

    private static void AssertStatusOnly(HealthReport report, HealthStatus status)
    {
        Assert.Equal(status, report.Status);
        Assert.All(report.Entries.Values, entry =>
        {
            Assert.Null(entry.Description);
            Assert.Null(entry.Exception);
            Assert.Empty(entry.Data);
        });
    }

    private static async Task AssertProbe(HttpClient client, string path, HttpStatusCode status, string body)
    {
        using var response = await client.GetAsync(path, Cancellation);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync(Cancellation));
    }

    private static void AssertRefusedBeforeActivation(ModuleHost host, LifecycleFixture fixture)
    {
        Assert.False(host.Report.IsReady);
        Assert.Null(host.Report.Plan);
        Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.Refused, module.State));
        Assert.Empty(host.Report.Events);
        Assert.Equal(0, fixture.Constructed);
        Assert.Empty(fixture.Calls);
    }
}
