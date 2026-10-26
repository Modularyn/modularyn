using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
