using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Modularyn.Framework.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Registers the diagnostic sources. Configure ordinary ILogger providers and
    /// subscribe an exporter to ModularynDiagnostics.SourceName and MeterName separately.
    /// An optional IDiagnosticRedactor is resolved when the sources are created.
    /// </summary>
    public static IServiceCollection AddModularynObservability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(provider => new ModularynDiagnostics(provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetService<IDiagnosticRedactor>()));
        return services;
    }
}
