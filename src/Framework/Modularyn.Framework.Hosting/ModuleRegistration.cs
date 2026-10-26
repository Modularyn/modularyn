using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Hosting;

/// <summary>
/// Associates a manifest with a factory that creates one fresh instance per activation attempt.
/// The factory runs only after composition succeeds. StopAsync owns instance resource cleanup.
/// </summary>
public sealed class ModuleRegistration
{
    public ModuleRegistration(ModuleManifest manifest, Func<IServiceProvider, IModule> factory)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }
    public ModuleManifest Manifest { get; }
    public Func<IServiceProvider, IModule> Factory { get; }

    public static ModuleRegistration Create<TModule>(ModuleManifest manifest) where TModule : class, IModule =>
        new(manifest, services => ActivatorUtilities.CreateInstance<TModule>(services));
}

public sealed class ModuleHostOptions
{
    /// <summary>Independent cleanup deadline after failed or cancelled startup.</summary>
    public TimeSpan FailedStartCleanupTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
