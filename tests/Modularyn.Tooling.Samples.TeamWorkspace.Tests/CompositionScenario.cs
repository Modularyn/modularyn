using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Observability;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;
using Xunit;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

/// <summary>
/// Test modules in the sample's actual module host. Only the registration selection changes; its composer, validators,
/// work gate, telemetry and lifetime remain the sample's. Files effects use the existing authenticated tenant fixture.
/// </summary>
internal sealed class CompositionScenario : IAsyncDisposable
{
    internal static readonly string[] Ids = ["A", "B", "C"];
    internal static readonly VersionRange BRange = new(SemanticVersion.Parse("2.0.0"), true, SemanticVersion.Parse("2.2.0"), true);
    private const string Container = "g02-lifecycle";
    private readonly List<ModuleRegistration> registrations = [];
    private SmokeUser user = null!;
    private Guid tenant;
    internal SmokeHost Host { get; private set; } = null!;
    internal ModuleHost Modules => Host.Modules;
    internal IReadOnlyList<ModuleManifest> Selection { get; }
    internal List<string> Calls { get; } = [];
    internal int Constructed { get; private set; }
    internal bool FailB { get; set; }

    private CompositionScenario(ModuleManifest[] graph)
    {
        // A, B, C arrive in reverse dependency order, before the sample's unchanged manifests.
        Selection = [.. graph, .. WorkspaceComposition.Declarations()];
        foreach (var manifest in graph)
            registrations.Add(new(manifest, _ => { Constructed++; return new EffectModule(this, manifest.Id); }));
        registrations.AddRange(WorkspaceComposition.Declarations().Select(manifest => ModuleRegistration.Create<ComposedModule>(manifest)));
    }

    internal static ModuleManifest[] Graph(string bVersion = "2.1.0", string? mutation = null)
    {
        var one = WorkspaceDefinition.Version;
        var runtime = VersionRange.Exact(SemanticVersion.Parse(Environment.Version.ToString(3)));
        var providers = new[] { new ModuleRequirement("G02PostgreSQL", VersionRange.Exact(one)),
            new ModuleRequirement("G02Filesystem", VersionRange.Exact(one)) };
        ModuleManifest[] graph =
        [
            new("A", one, [new("B", BRange)], requiredProviders: providers, runtimeVersions: runtime, frameworkVersions: VersionRange.Exact(one)),
            new("B", SemanticVersion.Parse(bVersion), [new("C", VersionRange.Exact(one))], runtimeVersions: runtime, frameworkVersions: VersionRange.Exact(one)),
            new("C", one, mutation == "cycle" ? [new("A", VersionRange.Exact(one))] : [],
                providedProviders: [new("G02PostgreSQL", one), new("G02Filesystem", one)],
                runtimeVersions: runtime, frameworkVersions: VersionRange.Exact(one))
        ];
        return mutation == "missing" ? graph[..2] : graph;
    }

    internal static async Task<CompositionScenario> CreateAsync(ModuleManifest[] graph, CancellationToken token)
    {
        var scenario = new CompositionScenario(graph);
        try
        {
            scenario.Host = await SmokeHost.CreateAsync(token, scenario.Selection, scenario.Configure);
            var composer = scenario.Host.Services.GetRequiredService<ModuleComposer>();
            Assert.True(composer.VerifiesTrust);
            Assert.True(composer.VerifiesMigrations);
            scenario.tenant = await scenario.Host.TenantAsync(token);
            scenario.user = await scenario.Host.UserAsync(scenario.tenant, owner: true, token);
            return scenario;
        }
        catch { await scenario.DisposeAsync(); throw; }
    }

    private void Configure(WebApplicationBuilder builder)
    {
        // Replace the closed registration list, keeping the very same validator registrations. Neither trust nor
        // migration verification is removed, and unverified composition is never enabled.
        builder.Services.Replace(ServiceDescriptor.Singleton<ModuleHost>(provider => new ModuleHost(registrations,
            provider.GetRequiredService<WorkspaceComposition>().Environment,
            provider.GetRequiredService<ModuleComposer>(), provider, provider.GetRequiredService<TimeProvider>(),
            telemetry: provider.GetRequiredService<ModularynDiagnostics>(), workGate: provider.GetRequiredService<ModuleWorkGate>(),
            lifetime: provider.GetRequiredService<IHostApplicationLifetime>(),
            shutdownTimeout: provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout)));
    }

    internal string[] ActiveResources => Directory.GetFiles(Host.LifecycleDirectory, "*.active")
        .Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToArray();

    // Query afresh through Files on every call, including after the failed activation and after recovery. No count
    // or uploaded identity is cached in the fixture. The immutable bytes are read from the actual filesystem store.
    internal async Task<EffectObservation[]> EffectsAsync(CancellationToken token)
    {
        await using var scope = await Host.EnterAsync(user, tenant, token);
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<TenantExecution>().Context!;
        var principal = services.GetRequiredService<IExecutionPrincipalAccessor>().Current!;
        var files = services.GetRequiredService<IFiles>();
        var found = await files.ListFilesAsync(context, principal, Container, cancellationToken: token);
        var observations = new List<EffectObservation>();
        foreach (var file in found.OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            Assert.Contains(id, Ids);
            var versions = await files.ListAsync(context, principal, file.FileId, token);
            var version = Assert.Single(versions);
            await using var read = await files.ReadAsync(context, principal, file.FileId, version.VersionId, cancellationToken: token);
            using var bytes = new MemoryStream();
            await read.Content.CopyToAsync(bytes, token);
            Assert.Equal(Content(id), bytes.ToArray());
            Assert.Equal(Digest(Content(id)), version.Sha256);
            Assert.Equal(Content(id).Length, version.Length);
            observations.Add(new(id, versions.Count, version.Length, version.Sha256, version.FileId, version.VersionId));
        }
        return [.. observations];
    }

    private static byte[] Content(string id) => Encoding.UTF8.GetBytes("Initialized module " + id + ".\n");
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }

    private sealed class EffectModule(CompositionScenario scenario, string id) : IModule
    {
        private FileStream? resource;

        public async Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            scenario.Calls.Add(id + ":initialize");
            // CreateNew fails if an earlier attempt left a resource behind, even if a cached inventory missed it.
            resource = new FileStream(Path.Combine(scenario.Host.LifecycleDirectory, id + ".active"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            await using var scope = await scenario.Host.EnterAsync(scenario.user, scenario.tenant, cancellationToken);
            var services = scope.ServiceProvider;
            var tenantContext = services.GetRequiredService<TenantExecution>().Context!;
            var principal = services.GetRequiredService<IExecutionPrincipalAccessor>().Current!;
            var files = services.GetRequiredService<IFiles>();
            var existing = await files.ListFilesAsync(tenantContext, principal, Container, cancellationToken: cancellationToken);
            var matching = existing.Where(file => file.Name == id + ".txt").ToArray();
            Assert.True(matching.Length <= 1);
            if (matching.Length == 0)
            {
                var bytes = Content(id);
                using var content = new MemoryStream(bytes, writable: false);
                var version = await files.UploadAsync(tenantContext, principal,
                    new(null, id + ".txt", Container, "lifecycle", bytes.Length, Digest(bytes)), content, cancellationToken);
                await files.ReleaseAsync(tenantContext, principal, version.FileId, version.VersionId, cancellationToken);
            }
            if (id == "B" && scenario.FailB) throw new InvalidOperationException("Controlled initialization failure in B.");
        }

        public Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scenario.Calls.Add(id + ":start");
            return Task.CompletedTask;
        }

        public async Task StopAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            scenario.Calls.Add(id + ":stop");
            if (resource is not null) await resource.DisposeAsync();
            File.Delete(Path.Combine(scenario.Host.LifecycleDirectory, id + ".active"));
        }
    }
}

internal sealed record EffectObservation(string Module, int Versions, long Length, string Sha256, Guid FileId, Guid VersionId);
