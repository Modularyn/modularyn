using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Modules.Identity.HttpApi;

public static class IdentityHttpServiceCollectionExtensions
{
    public const string AuthenticationScheme = "Modularyn.Session";

    /// <summary>
    /// Registers session authentication as the default scheme and the principal accessor that reads the request's session.
    /// The ingress places authentication after its transport checks and before antiforgery validation, so call
    /// <c>UseModularynIngress</c> and do not call <c>UseAuthentication</c> yourself. Startup is refused unless the ingress
    /// policy is registered, because the session cookie relies on its HTTPS enforcement.
    /// </summary>
    public static IServiceCollection AddModularynIdentityHttp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IdentityHttpCheck)))
            throw new InvalidOperationException("The identity HTTP adapter is already registered. Register it once.");
        services.AddHttpContextAccessor();
        services.AddAuthentication(AuthenticationScheme)
            .AddScheme<SessionAuthenticationOptions, SessionAuthenticationHandler>(AuthenticationScheme, _ => { });
        services.Replace(ServiceDescriptor.Singleton<IExecutionPrincipalAccessor, HttpExecutionPrincipalAccessor>());
        services.TryAddSingleton<Framework.Tenancy.Abstractions.ITenantActorResolver, SessionTenantActorResolver>();
        services.AddSingleton<IdentityHttpCheck>();
        services.AddSingleton<IValidateOptions<IdentityHttpCheck>, IdentityHttpValidation>();
        services.AddOptions<IdentityHttpCheck>().ValidateOnStart();
        return services;
    }
}

/// <summary>Carries no values; its startup validation refuses a host whose session cookie would not be confined to HTTPS.</summary>
internal sealed class IdentityHttpCheck;

internal sealed class IdentityHttpValidation(IServiceProvider services) : IValidateOptions<IdentityHttpCheck>
{
    public ValidateOptionsResult Validate(string? name, IdentityHttpCheck options)
    {
        var failures = new List<string>();
        if (services.GetService<PublicOrigins>() is null)
            failures.Add("Identity sessions need the HTTP ingress policy. Register it with AddModularynIngress.");
        var schemes = services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
        if (schemes.Schemes.SingleOrDefault(scheme => scheme.Name == IdentityHttpServiceCollectionExtensions.AuthenticationScheme)?.HandlerType != typeof(SessionAuthenticationHandler))
            failures.Add("The session authentication scheme was replaced after the identity HTTP adapter registered it.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
