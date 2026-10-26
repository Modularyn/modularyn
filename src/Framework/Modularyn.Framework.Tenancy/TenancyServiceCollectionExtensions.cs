using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Persistence.EfCore;

namespace Modularyn.Framework.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers tenant resolution over the PostgreSQL directory, the scoped execution context, the propagated-work runner
    /// and the tenant cache. The identity, membership, host-authority and audit seams are registered by their own
    /// packages; while one is absent, every operation that needs it is refused.
    /// </summary>
    public static IServiceCollection AddModularynTenancy(this IServiceCollection services, PostgreSqlModule directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        TenantDirectoryModule.Require(directory);
        if (services.Any(service => service.ServiceType == typeof(ITenantDirectory)))
            throw new InvalidOperationException("Register tenancy once, with its one directory module.");
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITenantDirectory>(new TenantDirectory(directory));
        services.AddSingleton(provider => new TenantDirectoryAdministration(directory, provider.GetService<PublicOrigins>()));
        services.AddScoped<TenantExecution>();
        services.AddSingleton<TenantWorkRunner>();
        services.AddSingleton<TenantCache>();
        return services;
    }
}
