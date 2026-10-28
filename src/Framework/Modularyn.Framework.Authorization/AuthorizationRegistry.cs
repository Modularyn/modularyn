using System.Collections.Frozen;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Authorization;

/// <summary>
/// Exactly one runtime definition for each permission and host-operation asset in the selected manifests, bound by owner,
/// name and version. Only what the composition declares exists: a permission of a module that is not composed cannot be
/// granted and confers nothing even where a grant row survives, and a host operation reaches only modules that are
/// composed. It also refuses activation while a manifest and the definitions disagree (MDC310, TM-G02-002).
/// </summary>
public sealed class AuthorizationRegistry : ICompositionValidator
{
    private readonly FrozenDictionary<string, PermissionDefinition> permissions;
    private readonly FrozenDictionary<string, HostOperationDefinition> operations;

    public AuthorizationRegistry(IEnumerable<ModuleManifest> manifests, IEnumerable<PermissionDefinition> permissions,
        IEnumerable<HostOperationDefinition> hostOperations)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(hostOperations);
        var permissionCopy = permissions.ToArray();
        var operationCopy = hostOperations.ToArray();
        if (permissionCopy.Any(definition => definition is null) || operationCopy.Any(definition => definition is null))
            throw new ArgumentException("Definitions cannot be null.");
        // A permission and a host operation each have one owner, whatever module declares them.
        if (permissionCopy.Select(definition => definition.Name).Distinct(StringComparer.Ordinal).Count() != permissionCopy.Length)
            throw new InvalidOperationException("Each permission has one owner and one definition.");
        if (operationCopy.Select(definition => definition.Operation.Id).Distinct(StringComparer.Ordinal).Count() != operationCopy.Length)
            throw new InvalidOperationException("Each host operation has one owner and one definition.");
        this.permissions = permissionCopy.ToFrozenDictionary(definition => definition.Name, StringComparer.Ordinal);
        operations = operationCopy.ToFrozenDictionary(definition => definition.Operation.Id, StringComparer.Ordinal);
        Permissions = Array.AsReadOnly(permissionCopy);
        HostOperations = Array.AsReadOnly(operationCopy);
        if (!Matches(manifests.ToArray()))
            throw new InvalidOperationException("Permission and host-operation definitions must match their manifest declarations, versions and composed modules exactly.");
    }

    public IReadOnlyList<PermissionDefinition> Permissions { get; }
    public IReadOnlyList<HostOperationDefinition> HostOperations { get; }
    public int Order => 210;

    /// <summary>The composed definition of a permission, or null when no composed module declares it.</summary>
    public PermissionDefinition? FindPermission(string? name) => name is not null && permissions.TryGetValue(name, out var definition) ? definition : null;

    /// <summary>The composed definition of a host operation, or null when no composed module declares it.</summary>
    public HostOperationDefinition? FindHostOperation(string? id) => id is not null && operations.TryGetValue(id, out var definition) ? definition : null;

    public ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<CompositionDiagnostic>>(Matches(selection.Modules) ? []
            : [new("MDC310", "Permission and host-operation definitions must match the selected manifests before activation.",
                selection.Modules.Select(module => module.Id))]);
    }

    private bool Matches(IReadOnlyList<ModuleManifest> manifests)
    {
        if (manifests.Any(manifest => manifest is null) || manifests.Select(manifest => manifest.Id).Distinct(StringComparer.Ordinal).Count() != manifests.Count)
            return false;
        var declared = manifests.SelectMany(manifest => manifest.Definitions
            .Where(asset => asset.Name.StartsWith(PermissionDefinition.AssetPrefix, StringComparison.Ordinal)
                || asset.Name.StartsWith(HostOperationDefinition.AssetPrefix, StringComparison.Ordinal))
            .Select(asset => (manifest.Id, asset.Name, asset.Version.Value))).ToArray();
        var defined = Permissions.Select(definition => (definition.ModuleId, definition.AssetName, definition.Version.Value))
            .Concat(HostOperations.Select(definition => (definition.ModuleId, definition.AssetName, definition.Version.Value))).ToArray();
        var composed = manifests.Select(manifest => manifest.Id).ToHashSet(StringComparer.Ordinal);
        return declared.Length == defined.Length && declared.Distinct().Count() == declared.Length && defined.All(declared.Contains)
            // An operation reaches only entities of composed modules.
            && HostOperations.All(definition => definition.Operation.Reach.All(entity => composed.Contains(entity.ModuleId)));
    }
}
