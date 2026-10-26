using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Observability;

namespace Modularyn.Framework.Hosting;

public static class ModuleHostingExtensions
{
    /// <summary>
    /// Adds module startup and shutdown to the .NET host used by ASP.NET Core applications.
    /// Register validators and a TimeProvider in the same service collection before building the host.
    /// </summary>
    public static IServiceCollection AddModularynModules(this IServiceCollection services,
        CompositionEnvironment environment, IEnumerable<ModuleRegistration> modules, ModuleHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(modules);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ModuleHost)))
            throw new InvalidOperationException("A Modularyn module host is already registered. Supply the full selection in one registration.");
        var registrations = modules.ToArray();
        if (registrations.Any(item => item is null)) throw new ArgumentException("Registrations cannot contain null entries.", nameof(modules));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddModularynObservability();
        services.TryAddSingleton<ModuleComposer>();
        services.TryAddSingleton<ModuleWorkGate>();
        services.AddSingleton(provider => new ModuleHost(registrations, environment,
            provider.GetRequiredService<ModuleComposer>(), provider, provider.GetRequiredService<TimeProvider>(), options,
            provider.GetRequiredService<ModularynDiagnostics>(), provider.GetRequiredService<ModuleWorkGate>(),
            provider.GetService<IHostApplicationLifetime>(), provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout));
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ModuleHost>());
        ModuleHealthChecks.Register(services, registrations);
        return services;
    }
}
