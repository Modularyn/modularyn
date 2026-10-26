using System.Collections.ObjectModel;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Modularity;

/// <summary>A plan can only be produced by successful composition validation.</summary>
public sealed class ActivationPlan
{
    internal ActivationPlan(CompositionSelection selection, IEnumerable<ModuleManifest> modules,
        Dictionary<string, SortedSet<string>> dependencies)
    {
        Selection = selection;
        Modules = Array.AsReadOnly(modules.ToArray());
        Dependencies = new ReadOnlyDictionary<string, IReadOnlyList<string>>(dependencies.ToDictionary(
            pair => pair.Key, pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.ToArray()), StringComparer.Ordinal));
    }
    public CompositionSelection Selection { get; }
    public IReadOnlyList<ModuleManifest> Modules { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies { get; }
}

public sealed class CompositionResult
{
    internal CompositionResult(ActivationPlan? plan, IEnumerable<CompositionDiagnostic> diagnostics)
    {
        Plan = plan;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }
    public ActivationPlan? Plan { get; }
    public IReadOnlyList<CompositionDiagnostic> Diagnostics { get; }
    public bool Succeeded => Plan is not null;
}

public static class CompositionDiagnosticCodes
{
    public const string MissingDependency = "MDC001";
    public const string DependencyCycle = "MDC002";
    public const string IncompatibleDependency = "MDC003";
    public const string ExclusiveProvisionConflict = "MDC004";
    public const string RouteConflict = "MDC005";
    public const string DuplicateModule = "MDC006";
    public const string MissingProvision = "MDC007";
    public const string IncompatibleProvision = "MDC008";
    public const string IncompatibleRuntime = "MDC009";
    public const string IncompatibleFramework = "MDC010";
    public const string DuplicateDeclaration = "MDC011";
}
