using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Hosting;

/// <summary>
/// Associates a manifest with a factory that creates one fresh instance per activation attempt.
/// The factory runs only after composition succeeds. StopAsync owns instance resource cleanup.
/// </summary>
public sealed class ModuleRegistration
{
    public ModuleRegistration(ModuleManifest manifest, Func<IServiceProvider, IModule> factory,
        IEnumerable<ModuleHealthCheckRegistration>? healthChecks = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        var checks = healthChecks?.ToArray() ?? [];
        if (checks.Any(check => check is null)) throw new ArgumentException("Health checks cannot contain null entries.", nameof(healthChecks));
        if (checks.Select(check => check.Name).Distinct(StringComparer.Ordinal).Count() != checks.Length)
            throw new ArgumentException("Bind each module health check at most once.", nameof(healthChecks));
        HealthChecks = Array.AsReadOnly(checks);
    }
    public ModuleManifest Manifest { get; }
    public Func<IServiceProvider, IModule> Factory { get; }
    public IReadOnlyList<ModuleHealthCheckRegistration> HealthChecks { get; }

    public static ModuleRegistration Create<TModule>(ModuleManifest manifest,
        IEnumerable<ModuleHealthCheckRegistration>? healthChecks = null) where TModule : class, IModule =>
        new(manifest, services => ActivatorUtilities.CreateInstance<TModule>(services), healthChecks);
}

public sealed class ModuleHostOptions
{
    /// <summary>Independent cleanup deadline after failed or cancelled startup.</summary>
    public TimeSpan FailedStartCleanupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Permits activation when no composition trust validator is registered. Without it the host refuses every
    /// selection it cannot verify. Set it only for development and test hosts; a deployed host registers its
    /// release's deployment lock with <c>AddModularynDeploymentTrust</c>.
    /// </summary>
    public bool AllowUnverifiedComposition { get; init; }
}
