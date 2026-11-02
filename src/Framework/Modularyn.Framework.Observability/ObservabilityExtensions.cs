using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modularyn.Framework.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Registers the diagnostic sources. Configure ordinary ILogger providers and
    /// subscribe an exporter to ModularynDiagnostics.SourceName and MeterName separately.
    /// An optional IDiagnosticRedactor is resolved when the sources are created.
    /// The host's logger factory is wrapped so that no provider receives a recognized secret form (<see cref="SecretForms"/>)
    /// in a message, a structured value, a scope or an exception, whichever category logs it.
    /// </summary>
    public static IServiceCollection AddModularynObservability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(LogRedactionMarker)))
        {
            services.AddSingleton<LogRedactionMarker>();
            services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(provider => new RedactingLoggerFactory(new LoggerFactory(
                provider.GetServices<ILoggerProvider>(), provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
                provider.GetService<IOptions<LoggerFactoryOptions>>(), provider.GetService<IExternalScopeProvider>()))));
        }
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(provider => new ModularynDiagnostics(provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetService<IDiagnosticRedactor>()));
        return services;
    }

    /// <summary>Marks a service collection whose logger factory already redacts, so repeated registration wraps it once.</summary>
    private sealed class LogRedactionMarker;
}
