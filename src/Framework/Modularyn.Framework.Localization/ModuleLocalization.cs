using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Localization;

/// <summary>Immutable, module-owned resource dictionaries bound to a versioned manifest asset.</summary>
public sealed class ModuleLocalizationResource
{
    private static readonly Regex CultureName = new(@"\A(?:[a-z]{2,3}(?:-[A-Z][a-z]{3})?(?:-(?:[A-Z]{2}|[0-9]{3}))?)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public ModuleLocalizationResource(string moduleId, string name, SemanticVersion version, string defaultCulture,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> cultures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(cultures);
        ModuleId = moduleId;
        Name = name;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        DefaultCulture = Culture(defaultCulture);
        var copy = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (culture, strings) in cultures)
        {
            if (strings is null || strings.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
                throw new ArgumentException("Resources require named keys and non-null translations.");
            if (!copy.TryAdd(Culture(culture), new ReadOnlyDictionary<string, string>(strings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))))
                throw new ArgumentException("Declare each resource culture once.");
        }
        if (!copy.ContainsKey(DefaultCulture)) throw new ArgumentException("A resource must supply its default culture.");
        Cultures = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(copy);
    }

    public string ModuleId { get; }
    public string Name { get; }
    public SemanticVersion Version { get; }
    public string DefaultCulture { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Cultures { get; }
    public ModuleAsset Declaration => new("localization." + Name, Version);

    internal static string Culture(string culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (!CultureName.IsMatch(culture))
            throw new ArgumentException("Use a canonical language tag with an optional script and region, or the empty invariant tag.");
        return culture;
    }
}

public sealed record LocalizedText(string Value, string SourceCulture);
public sealed record LocalizationSnapshot(string ModuleId, string Resource, string Version, string Culture,
    IReadOnlyDictionary<string, LocalizedText> Strings);

/// <summary>
/// Bootstrap snapshots use requested culture, its parents, default culture and invariant culture, in that order for
/// each key. Missing keys are absent. Resources have no tenant overrides; no setting, flag or permission is exposed.
/// </summary>
public sealed class ModuleLocalization : ICompositionValidator
{
    private readonly IReadOnlyList<ModuleLocalizationResource> resources;

    public ModuleLocalization(IEnumerable<ModuleManifest> manifests, IEnumerable<ModuleLocalizationResource> resources)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(resources);
        var copy = resources.ToArray();
        if (copy.Any(resource => resource is null) || copy.Select(resource => (resource.ModuleId, resource.Name)).Distinct().Count() != copy.Length)
            throw new ArgumentException("Each module resource has one owner.");
        this.resources = Array.AsReadOnly(copy);
        if (!Matches(manifests.ToArray())) throw new InvalidOperationException("Localization resources must match their manifest declarations and versions.");
    }

    public int Order => 210;

    public LocalizationSnapshot Bootstrap(string moduleId, string resourceName, string culture)
    {
        var resource = resources.SingleOrDefault(resource => resource.ModuleId == moduleId && resource.Name == resourceName)
            ?? throw new InvalidOperationException("The module resource is not registered.");
        var requested = ModuleLocalizationResource.Culture(culture);
        var fallbacks = new List<string>();
        if (requested.Length == 0) fallbacks.Add("");
        var current = requested;
        while (current.Length != 0)
        {
            fallbacks.Add(current);
            var separator = current.LastIndexOf('-');
            current = separator < 0 ? "" : current[..separator];
        }
        fallbacks.Add(resource.DefaultCulture);
        fallbacks.Add("");
        var strings = new SortedDictionary<string, LocalizedText>(StringComparer.Ordinal);
        foreach (var fallback in fallbacks.Distinct(StringComparer.Ordinal))
            if (resource.Cultures.TryGetValue(fallback, out var translations))
                foreach (var (key, value) in translations) strings.TryAdd(key, new(value, fallback));
        return new(moduleId, resourceName, resource.Version.Value, requested, new ReadOnlyDictionary<string, LocalizedText>(strings));
    }

    public ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<CompositionDiagnostic>>(Matches(selection.Modules) ? []
            : [new("MDC301", "Module localization resources must match the selected manifest definitions before activation.", selection.Modules.Select(module => module.Id))]);
    }

    private bool Matches(IReadOnlyList<ModuleManifest> manifests)
    {
        if (manifests.Any(manifest => manifest is null) || manifests.Select(manifest => manifest.Id).Distinct().Count() != manifests.Count) return false;
        var assets = manifests.SelectMany(manifest => manifest.Definitions.Where(asset => asset.Name.StartsWith("localization.", StringComparison.Ordinal))
            .Select(asset => (manifest.Id, asset.Name, asset.Version.Value))).ToArray();
        return assets.Length == resources.Count && assets.Distinct().Count() == assets.Length
            && resources.All(resource => assets.Contains((resource.ModuleId, resource.Declaration.Name, resource.Version.Value)));
    }
}

public static class LocalizationServiceCollectionExtensions
{
    public static IServiceCollection AddModularynLocalization(this IServiceCollection services, ModuleLocalization localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        if (services.Any(service => service.ServiceType == typeof(ModuleLocalization))) throw new InvalidOperationException("Register localization once.");
        services.AddSingleton(localization);
        services.AddSingleton<ICompositionValidator>(localization);
        return services;
    }
}
