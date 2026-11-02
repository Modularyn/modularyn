using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers tenant resolution over the PostgreSQL directory, the scoped execution context, the seal over stored
    /// propagated references, the propagated-work runner and the tenant cache. The identity, membership, host-authority and audit seams are registered by their own
    /// packages; while one is absent, every operation that needs it is refused. The cache takes <paramref name="cache"/>,
    /// or the safe defaults when it is null; an invalid option is refused here, before the host starts.
    /// </summary>
    public static IServiceCollection AddModularynTenancy(this IServiceCollection services, PostgreSqlModule directory, TenantCacheOptions? cache = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        TenantDirectoryModule.Require(directory);
        var cacheOptions = TenantCacheOptions.Require(cache);
        if (services.Any(service => service.ServiceType == typeof(ITenantDirectory)))
            throw new InvalidOperationException("Register tenancy once, with its one directory module.");
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITenantDirectory>(new TenantDirectory(directory));
        services.AddSingleton(provider => new TenantDirectoryAdministration(directory, provider.GetService<PublicOrigins>(), provider.GetService<IAuditTrail>()));
        services.AddScoped<TenantExecution>();
        // Stored propagated references are sealed with the declared key ring, which SEC-02's AddModularynDataProtection declares.
        services.TryAddSingleton<IPropagationSeal>(provider => new DeclaredPropagationSeal(provider));
        services.AddSingleton<TenantWorkRunner>();
        services.AddSingleton(provider => new TenantCache(provider.GetRequiredService<TimeProvider>(), cacheOptions));
        return services;
    }
}
