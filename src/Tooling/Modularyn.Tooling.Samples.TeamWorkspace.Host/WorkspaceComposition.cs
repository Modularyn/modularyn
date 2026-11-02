using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Persistence.EfCore;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.HttpApi;
using Modularyn.Modules.Identity.Persistence.EfCore;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

public sealed class WorkspaceComposition
{
    public WorkspaceComposition(PostgreSqlPersistenceOptions options, string prefix = "team_")
    {
        if (prefix.Length is < 1 or > 36 || prefix[0] is < 'a' or > 'z' || prefix.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new InvalidOperationException("Workspace:SchemaPrefix must be a bounded lowercase schema prefix.");
        Directory = TenantDirectoryModule.Create(options, prefix + "directory");
        Identity = IdentityPersistence.CreateModule(options, prefix + "identity");
        Authorization = AuthorizationModule.Create(options, prefix + "authorization");
        Audit = AuditModule.Create(options, prefix + "audit");
        Settings = SettingsModule.Create(options, prefix + "settings");
        Files = FilesMetadataModule.Create(options, prefix + "files");
        Workspace = WorkspaceDefinition.Create(options, prefix + "workspace");
        var version = WorkspaceDefinition.Version;
        Manifests = Declarations();
        Permissions = new AuthorizationRegistry(Manifests,
            AuthorizationModule.Permissions(version).Concat(AuditModule.Permissions(version)).Concat(SettingsModule.Permissions(version))
                .Concat(FilePermissions.Definitions(version)).Concat(WorkspaceDefinition.Permissions),
            TenantDirectoryModule.HostOperations(version).Concat(IdentityHostOperations.Definitions(version)).Concat(AuthorizationModule.HostOperations(version))
                .Concat(AuditModule.HostOperations(version)).Concat(SettingsModule.HostOperations(version)).Append(WorkspaceDefinition.DispatchDefinition)
                .Append(WorkspaceDefinition.ProvisionDefinition));
        Modules = new([Directory, Identity, Authorization, Audit, Settings, Files, Workspace]);
    }

    public PostgreSqlModule Directory { get; }
    public PostgreSqlModule Identity { get; }
    public PostgreSqlModule Authorization { get; }
    public PostgreSqlModule Audit { get; }
    public PostgreSqlModule Settings { get; }
    public PostgreSqlModule Files { get; }
    public PostgreSqlModule Workspace { get; }
    public IReadOnlyList<ModuleManifest> Manifests { get; }
    public AuthorizationRegistry Permissions { get; }
    public PostgreSqlModuleRegistry Modules { get; }
    public CompositionEnvironment Environment { get; } = new(SemanticVersion.Parse(System.Environment.Version.ToString(3)), WorkspaceDefinition.Version);

    public static IReadOnlyList<ModuleManifest> Declarations()
    {
        var version = WorkspaceDefinition.Version;
        return
        [
            TenantDirectoryModule.Manifest(version),
            new ModuleManifest(IdentityPersistence.ModuleId, version, routes: IdentityEndpoints.Routes,
                definitions: IdentityHostOperations.Definitions(version).Select(item => item.Declaration), migrations: IdentityPersistence.Migrations),
            AuthorizationModule.Manifest(version), AuditModule.Manifest(version), SettingsModule.Manifest(version),
            FilesMetadataModule.Manifest(version, FilePermissions.Definitions(version).Select(item => item.Declaration)), WorkspaceDefinition.Manifest
        ];
    }
}
