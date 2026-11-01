using System.Globalization;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Features;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Api;

/// <summary>Only the declared presentation values cross this boundary. No provenance, authority or secret references do.</summary>
public sealed class BootstrapReader(ValueDefinitionRegistry registry, SettingsService settings, FeatureService features)
{
    public async Task<BootstrapValues> ReadAsync(TenantContext context, CancellationToken cancellationToken = default)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var flags = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var definition in registry.Definitions.Where(definition => definition.ClientVisible && !definition.IsSecret)
                     .OrderBy(definition => definition.ModuleId, StringComparer.Ordinal).ThenBy(definition => definition.Name, StringComparer.Ordinal))
        {
            var key = definition.ModuleId + ":" + definition.Name;
            if (definition is FeatureDefinition feature)
                flags.Add(key, await features.IsEnabledAsync(context, feature, cancellationToken).ConfigureAwait(false));
            else
            {
                var value = await settings.ResolveAsync(context, definition, cancellationToken).ConfigureAwait(false);
                // Integers are decimal strings so both clients preserve values beyond the JavaScript integer range.
                values.Add(key, !value.IsSet ? null : definition.Kind switch
                {
                    ValueKind.Boolean => value.Get<bool>(),
                    ValueKind.Integer => value.Get<long>().ToString(CultureInfo.InvariantCulture),
                    ValueKind.Text => value.Get<string>(),
                    _ => throw new InvalidOperationException("Only client-visible scalar values may be exposed.")
                });
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(values, flags);
    }
}

public sealed record BootstrapValues(IReadOnlyDictionary<string, object?> Settings, IReadOnlyDictionary<string, object?> Features);
