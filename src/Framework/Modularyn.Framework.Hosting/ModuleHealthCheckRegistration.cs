using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Modularyn.Framework.Hosting;

/// <summary>Binds a manifest health-check declaration to its ASP.NET Core implementation.</summary>
public sealed class ModuleHealthCheckRegistration
{
    public ModuleHealthCheckRegistration(string name, Func<IServiceProvider, IHealthCheck> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public string Name { get; }
    public Func<IServiceProvider, IHealthCheck> Factory { get; }

    /// <summary>Resolve a check registered in the health check's dependency injection scope.</summary>
    public static ModuleHealthCheckRegistration Create<TCheck>(string name) where TCheck : class, IHealthCheck =>
        new(name, services => services.GetRequiredService<TCheck>());
}
