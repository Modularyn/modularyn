using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the identity module's application services and binds <see cref="IdentitySecurityOptions"/> from
    /// <paramref name="sectionPath"/>, validated before startup. The persistence adapter registers the stores; startup is
    /// refused while any of them is missing. Register a <see cref="TimeProvider"/> to replace the system clock; the module reads it
    /// through its own clock, which normalizes every time to whole microseconds (<see cref="StoredTime"/>).
    /// </summary>
    public static IServiceCollection AddModularynIdentity(this IServiceCollection services, IConfiguration configuration, string sectionPath = "Modularyn:Identity")
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IdentityPolicies)))
            throw new InvalidOperationException("The identity module is already registered. Register it once.");
        services.AddModularynDeploymentOptions<IdentitySecurityOptions>(configuration, sectionPath);
        services.AddSingleton<IValidateOptions<IdentitySecurityOptions>>(new IdentitySecurityOptionsValidation(sectionPath));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IdentityClock>();
        services.AddSingleton(provider => IdentityPolicies.From(provider.GetRequiredService<IOptions<IdentitySecurityOptions>>().Value));
        services.AddSingleton(provider => provider.GetRequiredService<IdentityPolicies>().Passwords);
        services.AddSingleton(provider => new ServiceIdentityRegistry(provider.GetServices<ServiceIdentityDeclaration>()));
        services.AddSingleton<VerificationGate>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<ISessionService>(provider => provider.GetRequiredService<SessionService>());
        services.AddSingleton<ISignInService, SignInService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IDelegationService, DelegationService>();
        services.AddSingleton<IServiceIdentityScopes, ServiceIdentityScopes>();
        services.AddSingleton<IPrincipalFreshness, PrincipalFreshness>();
        services.AddSingleton<CredentialKeyRing>();
        services.AddSingleton<IAuthenticatorSeeds, AuthenticatorSeedService>();
        services.AddSingleton<IRecoveryCodes, RecoveryCodeService>();
        services.AddSingleton<IPasskeyCredentials, PasskeyCredentialService>();
        services.AddSingleton<ITenantMemberships, TenantMembershipService>();
        services.TryAddSingleton<Framework.Tenancy.Abstractions.ITenantMembership, IdentityTenantMembership>();
        // Each service scope, a request's or a unit of work's, holds its own principal; nothing ambient carries one. A
        // singleton that captured it would hold the root provider's for every request, so resolving it outside a scope is
        // refused even in a host that does not validate scopes.
        services.AddSingleton<RootServices>();
        services.AddScoped(provider => provider.GetRequiredService<RootServices>().IsRoot(provider)
            ? throw new InvalidOperationException("The execution principal belongs to one unit of work. Resolve it from that unit's service scope, "
                + "never from a singleton or the root provider.")
            : new ExecutionPrincipalScope());
        services.Replace(ServiceDescriptor.Scoped<IExecutionPrincipalAccessor>(provider => provider.GetRequiredService<ExecutionPrincipalScope>()));
        services.AddSingleton<IValidateOptions<IdentityStoresCheck>, IdentityStoresValidation>();
        services.AddOptions<IdentityStoresCheck>().ValidateOnStart();
        return services;
    }

    /// <summary>
    /// Declares a background service identity and the exact permissions it may exercise. Jobs and handlers run as it through
    /// <see cref="IServiceIdentityScopes"/>; a name may be declared once.
    /// </summary>
    public static IServiceCollection AddModularynServiceIdentity(this IServiceCollection services, string name, IEnumerable<string> scope)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton(new ServiceIdentityDeclaration(name, scope));
    }
}

/// <summary>Carries no values; its startup validation refuses a host whose identity stores are not all registered.</summary>
internal sealed class IdentityStoresCheck;

internal sealed class IdentityStoresValidation(IServiceProvider services) : IValidateOptions<IdentityStoresCheck>
{
    private static readonly Type[] Stores =
    [
        typeof(IAccountDirectory), typeof(ISessionStore), typeof(IVerificationLedger), typeof(IDelegationStore),
        typeof(IAuthenticatorSeedStore), typeof(IRecoveryCodeStore), typeof(IPasskeyCredentialStore), typeof(ITenantMembershipStore)
    ];

    public ValidateOptionsResult Validate(string? name, IdentityStoresCheck options)
    {
        var known = services.GetRequiredService<IServiceProviderIsService>();
        var missing = Stores.Where(store => !known.IsService(store)).Select(store => store.Name).ToArray();
        return missing.Length == 0 ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"The identity module's stores are not registered ({string.Join(", ", missing)}). Register its persistence adapter.");
    }
}

/// <summary>Identifies the root provider, which singletons resolve their dependencies from.</summary>
internal sealed class RootServices(IServiceProvider root)
{
    internal bool IsRoot(IServiceProvider provider) => ReferenceEquals(provider, root);
}
