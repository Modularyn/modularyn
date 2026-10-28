using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization;

public static class AuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the evaluator, permission and host-authority administration, tenancy's host-authority seam, the worker
    /// entry point and the policy handler, over <paramref name="module"/>, which <see cref="AuthorizationModule.Create"/>
    /// returns and the host also lists in its migration registry. Register tenancy first. Options come from
    /// <paramref name="sectionPath"/> and are validated before startup. Audit (SEC-07) supplies <see cref="IAuthorityAudit"/>;
    /// while it is absent every authority change is refused. Identity supplies the freshness and membership seams; while
    /// either is absent every current-profile decision is refused.
    /// </summary>
    public static IServiceCollection AddModularynAuthorization(this IServiceCollection services, IConfiguration configuration, PostgreSqlModule module,
        AuthorizationRegistry registry, string sectionPath = "Modularyn:Authorization")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registry);
        AuthorizationModule.Require(module);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ITenantDirectory)))
            throw new InvalidOperationException("Authorization decides within tenant contexts; call AddModularynTenancy first.");
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AuthorizationRegistry)))
            throw new InvalidOperationException("Authorization is already registered. Register it once.");
        services.AddModularynDeploymentOptions<AuthorizationSecurityOptions>(configuration, sectionPath);
        services.AddSingleton<IValidateOptions<AuthorizationSecurityOptions>>(new AuthorizationSecurityOptionsValidation(sectionPath, registry));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(registry);
        services.AddSingleton<ICompositionValidator>(registry);
        services.AddSingleton(provider => new AuthorizationClock(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAuthoritySource>(provider => new PostgreSqlAuthoritySource(module, provider.GetRequiredService<AuthorizationClock>()));
        services.AddSingleton<IAuthorityCache>(provider => new TenantAuthorityCache(provider.GetRequiredService<TenantCache>()));
        services.AddSingleton(provider => new AuthorityCacheHealth(provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<AuthorizationSecurityOptions>>().Value.FreshnessBound));
        services.AddSingleton<IPermissionEvaluator>(provider => new AuthorizationEvaluator(registry, provider.GetRequiredService<IAuthoritySource>(),
            provider.GetRequiredService<IAuthorityCache>(), provider.GetRequiredService<AuthorityCacheHealth>(), provider.GetRequiredService<TenantWorkRunner>(),
            provider.GetRequiredService<AuthorizationClock>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IOptions<AuthorizationSecurityOptions>>().Value.FreshnessBound, provider,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<AuthorizationEvaluator>()));
        services.AddSingleton(provider => new AuthorityAdministration(module, registry, provider.GetRequiredService<IAuthorityCache>(),
            provider.GetRequiredService<AuthorityCacheHealth>(), provider.GetRequiredService<AuthorizationClock>(), provider, provider.GetRequiredService<ILoggerFactory>().CreateLogger<AuthorityAdministration>()));
        services.AddSingleton<IHostAuthority>(provider => new HostAuthority(registry, provider.GetRequiredService<IOptions<AuthorizationSecurityOptions>>(), module));
        services.AddSingleton(provider => new HostAuthorityAdministration(module, registry, provider.GetRequiredService<IHostAuthority>(),
            provider.GetRequiredService<ITenantDirectory>(), provider.GetRequiredService<IAuthorityCache>(), provider.GetRequiredService<AuthorizationClock>(),
            provider.GetRequiredService<AuthorityAdministration>()));
        services.AddSingleton<AuthorizedWorkRunner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionAuthorizationHandler>());
        services.AddHostedService<AuthorizationEntryPointReporter>();
        return services;
    }
}
