using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization.Abstractions;

/// <summary>How fresh the authority behind a decision must be (TM-G02-017, ARC-016).</summary>
public enum PermissionFreshness
{
    /// <summary>A decision may rest on the subject's authority as read at most the release's declared freshness bound ago.</summary>
    Bounded = 1,

    /// <summary>
    /// Security-sensitive: every decision reads the subject's current authority from the store, and rechecks the principal's
    /// session, delegation and membership, so a revocation stops the operation at its next check.
    /// </summary>
    Current = 2
}

/// <summary>
/// A permission as its owning module declares it: a stable identifier such as <c>item.read</c>, bound to an identically
/// named and versioned asset in the module's manifest, and its freshness. Only a permission the composition declares can
/// be granted or confer authority, so a module that is not composed has no grantable permissions.
/// </summary>
public sealed class PermissionDefinition
{
    public const string AssetPrefix = "permission.";

    public PermissionDefinition(string moduleId, string name, SemanticVersion version, PermissionFreshness freshness = PermissionFreshness.Bounded)
    {
        ModuleId = DefinitionNames.Module(moduleId);
        if (!PermissionNames.IsValid(name))
            throw new ArgumentException("A permission is a stable identifier of lowercase ASCII segments, such as item.read.", nameof(name));
        if (!Enum.IsDefined(freshness)) throw new ArgumentOutOfRangeException(nameof(freshness));
        Name = name;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Freshness = freshness;
    }

    public string ModuleId { get; }
    public string Name { get; }
    public SemanticVersion Version { get; }
    public PermissionFreshness Freshness { get; }
    public string AssetName => AssetPrefix + Name;

    /// <summary>The asset the owning module's manifest lists in its definitions.</summary>
    public ModuleAsset Declaration => new(AssetName, Version);

    public override string ToString() => "Permission " + Name;
}

/// <summary>
/// A host operation as its owning module declares it, bound to an identically named and versioned manifest asset. The
/// operation names every entity a host scope granted for it may reach; the elevated policy grants nothing beyond them.
/// </summary>
public sealed class HostOperationDefinition
{
    public const string AssetPrefix = "host-operation.";

    public HostOperationDefinition(string moduleId, SemanticVersion version, HostOperation operation)
    {
        ModuleId = DefinitionNames.Module(moduleId);
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    public string ModuleId { get; }
    public SemanticVersion Version { get; }
    public HostOperation Operation { get; }
    public string AssetName => AssetPrefix + Operation.Id;
    public ModuleAsset Declaration => new(AssetName, Version);

    public override string ToString() => Operation.ToString();
}

internal static class DefinitionNames
{
    internal static string Module(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        if (moduleId != moduleId.Trim() || moduleId.Any(char.IsControl))
            throw new ArgumentException("A module identity has no surrounding whitespace or control characters.", nameof(moduleId));
        return moduleId;
    }
}
