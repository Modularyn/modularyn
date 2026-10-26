using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Persistence.EfCore;

public static class IdentityPostgreSqlServiceCollectionExtensions
{
    /// <summary>
    /// Registers the identity module's PostgreSQL stores over <paramref name="module"/>, which <see cref="IdentityPersistence.CreateModule"/>
    /// creates and which the host also lists in its <see cref="PostgreSqlModuleRegistry"/>, so that its migrations are applied
    /// and activation waits for them. Password verifiers use the policy the application registers.
    /// </summary>
    public static IServiceCollection AddModularynIdentityPostgreSql(this IServiceCollection services, PostgreSqlModule module)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(module);
        if (module.ModuleId != IdentityPersistence.ModuleId || module.ContextType != typeof(IdentityModuleContext))
            throw new ArgumentException("Create the identity module's PostgreSQL module with IdentityPersistence.CreateModule.", nameof(module));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IAccountDirectory)))
            throw new InvalidOperationException("The identity stores are already registered. Register them once.");
        services.TryAddSingleton<IPasswordHasher<IdentityAccount>>(provider => new PasswordHasher<IdentityAccount>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = provider.GetRequiredService<PasswordPolicy>().HashIterations
        })));
        services.AddSingleton<IAccountDirectory>(provider => new IdentityAccountDirectory(module,
            provider.GetRequiredService<IPasswordHasher<IdentityAccount>>(), provider.GetRequiredService<PasswordPolicy>()));
        services.AddSingleton<ISessionStore>(new SessionStore(module));
        services.AddSingleton<IVerificationLedger>(new VerificationLedger(module));
        services.AddSingleton<IDelegationStore>(new DelegationStore(module));
        services.AddSingleton<IAuthenticatorSeedStore>(new AuthenticatorSeedStore(module));
        services.AddSingleton<IRecoveryCodeStore>(new RecoveryCodeStore(module));
        services.AddSingleton<IPasskeyCredentialStore>(new PasskeyCredentialStore(module));
        services.AddSingleton<ITenantMembershipStore>(new TenantMembershipStore(module));
        return services;
    }
}
