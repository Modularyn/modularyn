using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Audit;

public static class AuditServiceCollectionExtensions
{
    /// <summary>
    /// Registers the audit store over <paramref name="module"/>, which <see cref="AuditModule.Create"/> returns and the host
    /// also lists in its migration registry: the general <see cref="IAuditTrail"/>, authorization's
    /// <see cref="IAuthorityAudit"/>, tenancy's <see cref="IHostScopeAudit"/> and <see cref="IFilterBypassAudit"/>, and the
    /// <see cref="AuditReader"/>. While audit is absent, every authority change, host scope, filter bypass and audited
    /// change that needs it is refused. Register a <see cref="TimeProvider"/> to replace the system clock.
    /// </summary>
    public static IServiceCollection AddModularynAudit(this IServiceCollection services, PostgreSqlModule module)
    {
        ArgumentNullException.ThrowIfNull(services);
        AuditModule.Require(module);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AuditStore)))
            throw new InvalidOperationException("Audit is already registered. Register it once, with its one module.");
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new AuditClock(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new AuditStore(module, provider.GetRequiredService<AuditClock>()));
        services.AddSingleton<IAuditTrail>(provider => provider.GetRequiredService<AuditStore>());
        services.AddSingleton<IAuthorityAudit>(provider => provider.GetRequiredService<AuditStore>());
        services.AddSingleton<IHostScopeAudit>(provider => provider.GetRequiredService<AuditStore>());
        services.AddSingleton<IFilterBypassAudit>(provider => provider.GetRequiredService<AuditStore>());
        services.AddSingleton(provider => new AuditReader(provider.GetRequiredService<AuditStore>(), provider));
        return services;
    }
}
