using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers migration application and the read-only pre-activation gate for the same module inventory.</summary>
    public static IServiceCollection AddModularynPostgreSqlPersistence(this IServiceCollection services, PostgreSqlModuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        if (services.Any(service => service.ServiceType == typeof(PostgreSqlModuleRegistry)))
            throw new InvalidOperationException("Register the complete PostgreSQL module inventory once.");
        services.AddSingleton(registry);
        services.AddSingleton<PostgreSqlMigrationCoordinator>();
        services.AddSingleton<ICompositionValidator, MigrationActivationValidator>();
        return services;
    }
}
