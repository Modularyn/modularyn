using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Features;

/// <summary>
/// Attachment point for permission and entitlement decisions. Implementations recheck the current actor and tenant
/// for the requested operation; a configured flag supplies no authority. Gate outcomes are never cached here.
/// </summary>
public interface IFeatureGate
{
    ValueTask<bool> AllowsAsync(TenantContext context, FeatureDefinition feature, CancellationToken cancellationToken);
}

public static class FeatureServiceCollectionExtensions
{
    /// <summary>Register after settings. A separately supplied gate owns permission and entitlement decisions.</summary>
    public static IServiceCollection AddModularynFeatures(this IServiceCollection services)
    {
        services.TryAddScoped<FeatureService>();
        return services;
    }
}

public sealed class FeatureService(SettingsService settings, IFeatureGate? gate = null)
{
    /// <summary>The configured flag and its provenance, for inspection; it is not an authorization decision.</summary>
    public Task<ResolvedValue> ResolveAsync(TenantContext context, FeatureDefinition feature, CancellationToken cancellationToken = default) =>
        settings.ResolveAsync(context, feature, cancellationToken);

    /// <summary>Use at the server operation boundary. An absent gate permits nothing.</summary>
    public async ValueTask<bool> IsEnabledAsync(TenantContext context, FeatureDefinition feature, CancellationToken cancellationToken = default)
    {
        var value = await ResolveAsync(context, feature, cancellationToken).ConfigureAwait(false);
        if (!value.Get<bool>() || gate is null) return false;
        var allowed = await gate.AllowsAsync(context, feature, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return context.IsActive && allowed;
    }

    /// <summary>Feature management needs the same authority as the settings override at that scope.</summary>
    public Task<Guid> SetAsync(TenantContext context, ExecutionPrincipal? principal, FeatureDefinition feature, ValueSource scope, bool enabled,
        Guid? expectedVersion = null, CancellationToken cancellationToken = default) =>
        settings.SetAsync(context, principal, feature, scope, enabled ? "true" : "false", expectedVersion, cancellationToken);

    public Task ResetAsync(TenantContext context, ExecutionPrincipal? principal, FeatureDefinition feature, ValueSource scope, Guid expectedVersion,
        CancellationToken cancellationToken = default) => settings.ResetAsync(context, principal, feature, scope, expectedVersion, cancellationToken);
}
