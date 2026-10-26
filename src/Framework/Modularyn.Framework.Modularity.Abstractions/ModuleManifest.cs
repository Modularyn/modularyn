namespace Modularyn.Framework.Modularity.Abstractions;

/// <summary>An immutable snapshot of a module's declared composition surface.</summary>
public sealed class ModuleManifest
{
    public ModuleManifest(string id, SemanticVersion version,
        IEnumerable<ModuleDependency>? dependencies = null,
        IEnumerable<ModuleProvision>? providedContracts = null,
        IEnumerable<ModuleRequirement>? requiredContracts = null,
        IEnumerable<ModuleProvision>? providedCapabilities = null,
        IEnumerable<ModuleRequirement>? requiredCapabilities = null,
        IEnumerable<ModuleProvision>? providedProviders = null,
        IEnumerable<ModuleRequirement>? requiredProviders = null,
        IEnumerable<ModuleRoute>? routes = null,
        IEnumerable<ModuleAsset>? definitions = null,
        IEnumerable<ModuleAsset>? migrations = null,
        IEnumerable<ModuleAsset>? backgroundHandlers = null,
        IEnumerable<ModuleAsset>? healthChecks = null,
        VersionRange? runtimeVersions = null, VersionRange? frameworkVersions = null)
    {
        Id = DeclarationGuard.Name(id);
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Dependencies = DeclarationGuard.Copy(dependencies);
        ProvidedContracts = DeclarationGuard.Copy(providedContracts);
        RequiredContracts = DeclarationGuard.Copy(requiredContracts);
        ProvidedCapabilities = DeclarationGuard.Copy(providedCapabilities);
        RequiredCapabilities = DeclarationGuard.Copy(requiredCapabilities);
        ProvidedProviders = DeclarationGuard.Copy(providedProviders);
        RequiredProviders = DeclarationGuard.Copy(requiredProviders);
        Routes = DeclarationGuard.Copy(routes);
        Definitions = DeclarationGuard.Copy(definitions);
        Migrations = DeclarationGuard.Copy(migrations);
        BackgroundHandlers = DeclarationGuard.Copy(backgroundHandlers);
        HealthChecks = DeclarationGuard.Copy(healthChecks);
        RuntimeVersions = runtimeVersions ?? VersionRange.Any;
        FrameworkVersions = frameworkVersions ?? VersionRange.Any;
    }

    public string Id { get; }
    public SemanticVersion Version { get; }
    public IReadOnlyList<ModuleDependency> Dependencies { get; }
    public IReadOnlyList<ModuleProvision> ProvidedContracts { get; }
    public IReadOnlyList<ModuleRequirement> RequiredContracts { get; }
    public IReadOnlyList<ModuleProvision> ProvidedCapabilities { get; }
    public IReadOnlyList<ModuleRequirement> RequiredCapabilities { get; }
    public IReadOnlyList<ModuleProvision> ProvidedProviders { get; }
    public IReadOnlyList<ModuleRequirement> RequiredProviders { get; }
    public IReadOnlyList<ModuleRoute> Routes { get; }
    public IReadOnlyList<ModuleAsset> Definitions { get; }
    public IReadOnlyList<ModuleAsset> Migrations { get; }
    public IReadOnlyList<ModuleAsset> BackgroundHandlers { get; }
    public IReadOnlyList<ModuleAsset> HealthChecks { get; }
    public VersionRange RuntimeVersions { get; }
    public VersionRange FrameworkVersions { get; }
}
