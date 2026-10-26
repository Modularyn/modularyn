using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Observability;

namespace Modularyn.Framework.Hosting.Tests;

/// <summary>Exercises real host callbacks with filesystem effects and owned resource markers.</summary>
internal sealed class LifecycleFixture : IDisposable
{
    internal static readonly SemanticVersion One = SemanticVersion.Parse("1.0.0");
    internal static readonly VersionRange BRange = new(SemanticVersion.Parse("2.0.0"), true, SemanticVersion.Parse("2.2.0"), true);
    internal static readonly CompositionEnvironment Environment = new(SemanticVersion.Parse("10.0.0"), One);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "modularyn-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider services = new ServiceCollection().BuildServiceProvider();
    private readonly List<ModuleHost> hosts = [];

    internal LifecycleFixture() => Directory.CreateDirectory(directory);
    internal List<string> Calls { get; } = [];
    internal List<(string Id, ModuleStage Stage, ModuleContext Context, CancellationToken Token)> Observations { get; } = [];
    internal HashSet<string> FailInitialization { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> FailStart { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> FailShutdown { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> IgnoreShutdownCancellation { get; } = new(StringComparer.Ordinal);
    internal Func<string, ModuleStage, CancellationToken, Task>? DuringStage { get; set; }
    internal int Constructed { get; private set; }

    internal static ModuleManifest[] Graph(string bVersion = "2.1.0")
    {
        var runtime = new VersionRange(SemanticVersion.Parse("10.0.0"), true, SemanticVersion.Parse("11.0.0"), false);
        ModuleRequirement[] providers = [new("FileStore", VersionRange.Exact(One))];
        return [
            new("A", One, [new("B", BRange)], requiredProviders: providers, runtimeVersions: runtime, frameworkVersions: VersionRange.Exact(One)),
            new("B", SemanticVersion.Parse(bVersion), [new("C", VersionRange.Exact(One))], requiredProviders: providers, runtimeVersions: runtime, frameworkVersions: VersionRange.Exact(One)),
            new("C", One, providedProviders: [new("FileStore", One)], runtimeVersions: runtime, frameworkVersions: VersionRange.Exact(One))];
    }

    internal ModuleRegistration[] Registrations(IEnumerable<ModuleManifest> manifests) => manifests.Select(manifest =>
        new ModuleRegistration(manifest, _ =>
        {
            Constructed++;
            Calls.Add(manifest.Id + ":construct");
            return new FileModule(this, manifest.Id);
        })).ToArray();

    internal ModuleHost Host(IEnumerable<ModuleManifest>? manifests = null, IEnumerable<ICompositionValidator>? validators = null,
        TimeProvider? clock = null, ModuleHostOptions? options = null, ModularynDiagnostics? telemetry = null,
        TimeSpan? shutdownTimeout = null)
    {
        var host = new ModuleHost(Registrations(manifests ?? Graph()), Environment, new ModuleComposer(validators), services,
            clock ?? TimeProvider.System, options, telemetry, shutdownTimeout: shutdownTimeout);
        hosts.Add(host);
        return host;
    }

    internal string[] ActiveResources => Directory.GetFiles(directory, "*.active").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToArray();
    internal string[] DurableEffects(string id) => File.ReadAllLines(Path.Combine(directory, id + ".effect"));
    internal string[] LifecycleCalls => Calls.Where(call => !call.EndsWith(":construct", StringComparison.Ordinal)).ToArray();

    public void Dispose()
    {
        foreach (var host in hosts) host.Dispose();
        services.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private sealed class FileModule(LifecycleFixture fixture, string id) : IModule
    {
        private readonly string active = Path.Combine(fixture.directory, id + ".active");
        private readonly string effect = Path.Combine(fixture.directory, id + ".effect");

        public async Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            Observe(ModuleStage.Initialization, context, cancellationToken, "initialize");
            cancellationToken.ThrowIfCancellationRequested();
            // CreateNew detects a resource remnant from an earlier activation attempt.
            await using (var resource = new FileStream(active, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await resource.WriteAsync("owned"u8.ToArray(), cancellationToken);
            // A durable initialization effect is keyed by module identity across attempts.
            if (!File.Exists(effect))
                await File.WriteAllTextAsync(effect, "initialized\n", cancellationToken);
            if (fixture.DuringStage is not null) await fixture.DuringStage(id, ModuleStage.Initialization, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (fixture.FailInitialization.Contains(id)) throw new InvalidOperationException($"Initialization fault in {id}.");
        }

        public async Task StartAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            Observe(ModuleStage.Start, context, cancellationToken, "start");
            cancellationToken.ThrowIfCancellationRequested();
            if (fixture.DuringStage is not null) await fixture.DuringStage(id, ModuleStage.Start, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (fixture.FailStart.Contains(id)) throw new InvalidOperationException($"Start fault in {id}.");
        }

        public async Task StopAsync(ModuleContext context, CancellationToken cancellationToken)
        {
            Observe(ModuleStage.Shutdown, context, cancellationToken, "stop");
            if (!fixture.IgnoreShutdownCancellation.Contains(id)) cancellationToken.ThrowIfCancellationRequested();
            if (fixture.DuringStage is not null) await fixture.DuringStage(id, ModuleStage.Shutdown, cancellationToken);
            if (!fixture.IgnoreShutdownCancellation.Contains(id)) cancellationToken.ThrowIfCancellationRequested();
            if (fixture.FailShutdown.Contains(id)) throw new InvalidOperationException($"Shutdown fault in {id}.");
            File.Delete(active);
        }

        private void Observe(ModuleStage stage, ModuleContext context, CancellationToken token, string call)
        {
            fixture.Calls.Add(id + ":" + call);
            fixture.Observations.Add((id, stage, context, token));
        }
    }
}
