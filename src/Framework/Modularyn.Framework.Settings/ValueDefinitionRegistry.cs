using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Settings.Abstractions;

namespace Modularyn.Framework.Settings;

/// <summary>Exactly one runtime contract for each setting or feature asset in the selected module manifests.</summary>
public sealed class ValueDefinitionRegistry : ICompositionValidator
{
    private readonly IReadOnlyList<ScopedValueDefinition> definitions;

    public ValueDefinitionRegistry(IEnumerable<ModuleManifest> manifests, IEnumerable<ScopedValueDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(definitions);
        var copy = definitions.ToArray();
        if (copy.Any(definition => definition is null)
            || copy.Select(definition => (definition.ModuleId, definition.AssetName)).Distinct().Count() != copy.Length)
            throw new InvalidOperationException("Each setting or feature definition has one owner and one contract.");
        this.definitions = Array.AsReadOnly(copy);
        if (!Matches(manifests.ToArray())) throw new InvalidOperationException("Setting and feature contracts must match their manifest declarations and versions exactly.");
    }

    public IReadOnlyList<ScopedValueDefinition> Definitions => definitions;
    public int Order => 200;

    public ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<CompositionDiagnostic>>(Matches(selection.Modules) ? []
            : [new("MDC300", "Setting and feature contracts must match the selected manifest definitions before activation.", selection.Modules.Select(module => module.Id))]);
    }

    internal void Require(ScopedValueDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definitions.Any(registered => ReferenceEquals(registered, definition))) throw new InvalidOperationException("Use a registered module definition.");
    }

    private bool Matches(IReadOnlyList<ModuleManifest> manifests)
    {
        if (manifests.Any(manifest => manifest is null) || manifests.Select(manifest => manifest.Id).Distinct().Count() != manifests.Count) return false;
        var assets = manifests.SelectMany(manifest => manifest.Definitions
            .Where(asset => asset.Name.StartsWith("setting.", StringComparison.Ordinal) || asset.Name.StartsWith("feature.", StringComparison.Ordinal))
            .Select(asset => (manifest.Id, asset.Name, asset.Version.Value))).ToArray();
        return assets.Length == definitions.Count && assets.Distinct().Count() == assets.Length
            && definitions.All(definition => assets.Contains((definition.ModuleId, definition.AssetName, definition.Version.Value)));
    }
}
