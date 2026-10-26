using System.Text.RegularExpressions;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Modularity;

/// <summary>Resolves the exact selected versions; it never discovers, installs or constructs modules.</summary>
public sealed class ModuleComposer
{
    private readonly ICompositionValidator[] validators;

    public ModuleComposer(IEnumerable<ICompositionValidator>? validators = null)
    {
        var supplied = validators?.ToArray() ?? [];
        if (supplied.Any(validator => validator is null)) throw new ArgumentException("Validators cannot contain null entries.", nameof(validators));
        this.validators = supplied.OrderBy(validator => validator.Order).ToArray();
    }

    public async Task<CompositionResult> ComposeAsync(CompositionSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<CompositionDiagnostic>();
        var modules = selection.Modules.OrderBy(module => module.Id, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in modules.GroupBy(module => module.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
            diagnostics.Add(new(CompositionDiagnosticCodes.DuplicateModule,
                $"Module '{duplicate.Key}' is selected more than once ({string.Join(", ", duplicate.Select(module => module.Version))}). Select exactly one version and registration.", [duplicate.Key]));
        if (diagnostics.Count > 0) return new(null, diagnostics);

        var byId = modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        var edges = modules.ToDictionary(module => module.Id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckEnvironment(module, selection.Environment, diagnostics);
            CheckDuplicates(module, diagnostics);
            foreach (var dependency in module.Dependencies.OrderBy(item => item.ModuleId, StringComparer.Ordinal))
            {
                if (!byId.TryGetValue(dependency.ModuleId, out var candidate))
                {
                    if (!dependency.Optional)
                        diagnostics.Add(new(CompositionDiagnosticCodes.MissingDependency,
                            $"Module '{module.Id}' requires missing module '{dependency.ModuleId}' in {dependency.Versions}. Select a compatible '{dependency.ModuleId}' or remove '{module.Id}'.",
                            [module.Id, dependency.ModuleId]));
                    continue;
                }
                edges[module.Id].Add(candidate.Id);
                if (!dependency.Versions.Contains(candidate.Version))
                    diagnostics.Add(new(CompositionDiagnosticCodes.IncompatibleDependency,
                        $"Module '{module.Id}' requires '{candidate.Id}' in {dependency.Versions}, but candidate '{candidate.Id}' is {candidate.Version}. Select a version satisfying {dependency.Versions}.",
                        [module.Id, candidate.Id]));
            }
        }

        CheckProvisions("contract", modules, module => module.ProvidedContracts, module => module.RequiredContracts, edges, diagnostics, cancellationToken);
        CheckProvisions("capability", modules, module => module.ProvidedCapabilities, module => module.RequiredCapabilities, edges, diagnostics, cancellationToken);
        CheckProvisions("provider", modules, module => module.ProvidedProviders, module => module.RequiredProviders, edges, diagnostics, cancellationToken);
        CheckRoutes(modules, diagnostics, cancellationToken);
        var order = OrderModules(byId, edges, diagnostics, cancellationToken);
        if (diagnostics.Count > 0) return new(null, diagnostics);

        foreach (var validator in validators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var additional = await validator.ValidateAsync(selection, cancellationToken).ConfigureAwait(false);
            if (additional is null || additional.Any(diagnostic => diagnostic is null))
                throw new InvalidOperationException($"Composition validator '{validator.GetType().Name}' returned invalid diagnostics.");
            diagnostics.AddRange(additional);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(diagnostics.Count == 0 ? new ActivationPlan(selection, order, edges) : null, diagnostics);
    }

    private static void CheckEnvironment(ModuleManifest module, CompositionEnvironment environment, List<CompositionDiagnostic> diagnostics)
    {
        if (!module.RuntimeVersions.Contains(environment.RuntimeVersion))
            diagnostics.Add(new(CompositionDiagnosticCodes.IncompatibleRuntime,
                $"Module '{module.Id}' requires .NET runtime {module.RuntimeVersions}, but candidate runtime is {environment.RuntimeVersion}. Select a compatible runtime or module version.", [module.Id]));
        if (!module.FrameworkVersions.Contains(environment.FrameworkVersion))
            diagnostics.Add(new(CompositionDiagnosticCodes.IncompatibleFramework,
                $"Module '{module.Id}' requires Modularyn Framework {module.FrameworkVersions}, but candidate framework is {environment.FrameworkVersion}. Select a compatible framework or module version.", [module.Id]));
    }

    private static void CheckDuplicates(ModuleManifest module, List<CompositionDiagnostic> diagnostics)
    {
        Check("dependency", module.Dependencies.Select(item => item.ModuleId));
        Check("provided contract", module.ProvidedContracts.Select(item => item.Name));
        Check("required contract", module.RequiredContracts.Select(item => item.Name));
        Check("provided capability", module.ProvidedCapabilities.Select(item => item.Name));
        Check("required capability", module.RequiredCapabilities.Select(item => item.Name));
        Check("provided provider", module.ProvidedProviders.Select(item => item.Name));
        Check("required provider", module.RequiredProviders.Select(item => item.Name));
        Check("definition", module.Definitions.Select(item => item.Name));
        Check("migration", module.Migrations.Select(item => item.Name));
        Check("background handler", module.BackgroundHandlers.Select(item => item.Name));
        Check("health check", module.HealthChecks.Select(item => item.Name));

        void Check(string kind, IEnumerable<string> names)
        {
            foreach (var name in names.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).Order(StringComparer.Ordinal))
                diagnostics.Add(new(CompositionDiagnosticCodes.DuplicateDeclaration,
                    $"Module '{module.Id}' declares {kind} '{name}' more than once. Keep one declaration for that name.", [module.Id]));
        }
    }

    private static void CheckProvisions(string kind, ModuleManifest[] modules,
        Func<ModuleManifest, IReadOnlyList<ModuleProvision>> provides,
        Func<ModuleManifest, IReadOnlyList<ModuleRequirement>> requires,
        Dictionary<string, SortedSet<string>> edges, List<CompositionDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var inventory = modules.SelectMany(module => provides(module).Select(provision => (Module: module, Provision: provision)))
            .GroupBy(item => item.Provision.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var (name, candidates) in inventory.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owners = candidates.Select(item => item.Module.Id).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length > 1 && candidates.Any(item => item.Provision.Exclusive))
                diagnostics.Add(new(CompositionDiagnosticCodes.ExclusiveProvisionConflict,
                    $"Exclusive {kind} '{name}' is provided by modules {string.Join(", ", owners.Select(id => $"'{id}'"))}. Select only one provider of '{name}'.", owners));
        }

        foreach (var module in modules)
        foreach (var requirement in requires(module).OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inventory.TryGetValue(requirement.Name, out var candidates))
            {
                if (!requirement.Optional)
                    diagnostics.Add(new(CompositionDiagnosticCodes.MissingProvision,
                        $"Module '{module.Id}' requires missing {kind} '{requirement.Name}' in {requirement.Versions}. Select a module providing that {kind} or remove '{module.Id}'.", [module.Id]));
                continue;
            }
            var compatible = candidates.Where(item => requirement.Versions.Contains(item.Provision.Version)).ToArray();
            if (compatible.Length == 0)
                diagnostics.Add(new(CompositionDiagnosticCodes.IncompatibleProvision,
                    $"Module '{module.Id}' requires {kind} '{requirement.Name}' in {requirement.Versions}, but candidates are {string.Join(", ", candidates.Select(item => $"'{item.Module.Id}' {item.Provision.Version}"))}. Select a compatible provider.",
                    new[] { module.Id }.Concat(candidates.Select(item => item.Module.Id)).Distinct(StringComparer.Ordinal)));
            foreach (var candidate in compatible.Where(item => item.Module.Id != module.Id)) edges[module.Id].Add(candidate.Module.Id);
        }
    }

    private static readonly Regex RouteParameter = new(@"\{(\*{0,2})[^{}:=?]+([^{}]*)\}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string RouteKey(string template) => RouteParameter.Replace(template.TrimEnd('/'), "{$1_$2}");

    private static void CheckRoutes(ModuleManifest[] modules, List<CompositionDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var routes = modules.SelectMany(module => module.Routes.Select(route => (Module: module, Route: route)))
            .GroupBy(item => RouteKey(item.Route.Template), StringComparer.OrdinalIgnoreCase);
        foreach (var group in routes)
        {
            var declarations = group.ToArray();
            for (var i = 0; i < declarations.Length; i++)
            for (var j = i + 1; j < declarations.Length; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var left = declarations[i];
                var right = declarations[j];
                if (left.Route.Method != right.Route.Method && left.Route.Method != "*" && right.Route.Method != "*") continue;
                diagnostics.Add(new(CompositionDiagnosticCodes.RouteConflict,
                    $"Module '{left.Module.Id}' route '{left.Route.Method} {left.Route.Template}' conflicts with module '{right.Module.Id}' route '{right.Route.Method} {right.Route.Template}'. Remove or change one route or HTTP method.",
                    new[] { left.Module.Id, right.Module.Id }.Distinct(StringComparer.Ordinal)));
            }
        }
    }

    private static List<ModuleManifest> OrderModules(Dictionary<string, ModuleManifest> modules,
        Dictionary<string, SortedSet<string>> edges, List<CompositionDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var order = new List<ModuleManifest>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();
        // Explicit frames avoid depending on the process stack for long dependency chains.
        var frames = new Stack<(string Id, IEnumerator<string> Dependencies)>();
        foreach (var id in modules.Keys.Order(StringComparer.Ordinal))
        {
            if (visited.Contains(id)) continue;
            Enter(id);
            while (frames.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!frame.Dependencies.MoveNext())
                {
                    frames.Pop().Dependencies.Dispose();
                    active.Remove(frame.Id);
                    path.RemoveAt(path.Count - 1);
                    order.Add(modules[frame.Id]);
                    continue;
                }
                var dependency = frame.Dependencies.Current;
                if (active.TryGetValue(dependency, out var start))
                {
                    var cycle = path.Skip(start).Append(dependency).ToArray();
                    diagnostics.Add(new(CompositionDiagnosticCodes.DependencyCycle,
                        $"Dependency cycle: {string.Join(" -> ", cycle)}. Remove or redirect one dependency in this cycle.", cycle));
                }
                else if (!visited.Contains(dependency)) Enter(dependency);
            }
        }
        return order;

        void Enter(string id)
        {
            visited.Add(id);
            active.Add(id, path.Count);
            path.Add(id);
            frames.Push((id, edges[id].GetEnumerator()));
        }
    }
}
