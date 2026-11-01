using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Files.Application.Contracts;

/// <summary>
/// The permissions the Files module declares, and the resource its files are to authorization. A file's owner is the
/// actor that created it, derived on the server; members reach other files through authorization's resource grants.
/// </summary>
public static class FilePermissions
{
    public const string ModuleId = "Modularyn.Modules.Files";
    public const string ResourceType = "files.file";

    /// <summary>Creates files and adds versions to them.</summary>
    public const string Upload = "files.upload";

    /// <summary>Lists files and versions and reads released content, directly or through a download grant.</summary>
    public const string Read = "files.read";

    /// <summary>Soft-deletes a version and revokes its grants.</summary>
    public const string Delete = "files.delete";

    /// <summary>Lists retained versions and restores them after verifying their bytes.</summary>
    public const string Restore = "files.restore";

    /// <summary>Runs the deployment's release policy over a pending version.</summary>
    public const string Release = "files.release";

    /// <summary>Issues, lists and revokes download grants.</summary>
    public const string Grant = "files.grant";

    /// <summary>
    /// Upload decides at the bounded profile. Every action that delivers, removes or decides content, or issues the
    /// credentials that deliver it, reads current authority at each decision.
    /// </summary>
    public static IReadOnlyList<PermissionDefinition> Definitions(SemanticVersion version) =>
    [
        new(ModuleId, Upload, version),
        new(ModuleId, Read, version, PermissionFreshness.Current),
        new(ModuleId, Delete, version, PermissionFreshness.Current),
        new(ModuleId, Restore, version, PermissionFreshness.Current),
        new(ModuleId, Release, version, PermissionFreshness.Current),
        new(ModuleId, Grant, version, PermissionFreshness.Current)
    ];

    /// <summary>A file as authorization decides on it, after the module resolved it within the effective tenant.</summary>
    public static ResourceReference Resource(Guid tenantId, Guid fileId, ActorReference? owner) => new(tenantId, ResourceType, fileId.ToString("D"), owner);
}
