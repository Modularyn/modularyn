using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Localization;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Tooling.Samples.TeamWorkspace;

public static class WorkspaceDefinition
{
    public const string Id = "Modularyn.Tooling.Samples.TeamWorkspace";
    public const string Read = "workspace.read";
    public const string Write = "workspace.write";
    public const string Digest = "workspace.digest";
    public const string DigestKind = "workspace.daily-digest";
    public const string ReleaseKind = "workspace.release-attachment";
    public const string ActivityConsumer = "workspace.activity";
    public const string NoteChanged = "workspace.note-changed";
    public const string Worker = "workspace-worker";
    public static readonly SemanticVersion Version = SemanticVersion.Parse("1.0.0");
    public static readonly HostOperation Dispatch = new("workspace.dispatch",
        new[] { "OutboxEvents", "InboxEvents", "Jobs", "JobAttempts", "JobSchedules" }.Select(table => new HostReach(Id, table)));
    public static readonly SettingDefinition Title = new(Id, "title", Version, ValueKind.Text, "Team workspace",
        TimeSpan.FromSeconds(30), new ValueValidation(maxLength: 80), clientVisible: true);
    public static readonly FeatureDefinition Digests = new(Id, "digests", Version, true, TimeSpan.FromSeconds(30), clientVisible: true);
    public static readonly ModuleLocalizationResource Text = new(Id, "workspace", Version, "en",
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["NewNote"] = "Create a note", ["Saved"] = "Note saved" },
            ["es"] = new Dictionary<string, string> { ["NewNote"] = "Crear una nota", ["Saved"] = "Nota guardada" }
        });
    public static IReadOnlyList<PermissionDefinition> Permissions { get; } = new[] { Read, Write, Digest, ScheduleWorker.SchedulePermission, JobStore.CancelPermission }
        .Select(permission => new PermissionDefinition(Id, permission, Version, PermissionFreshness.Current)).ToArray();
    public static readonly HostOperationDefinition DispatchDefinition = new(Id, Version, Dispatch);
    public static ModuleManifest Manifest { get; } = new(Id, Version,
        dependencies: new[] { "Modularyn.Framework.Tenancy", "Modularyn.Modules.Identity", "Modularyn.Framework.Authorization",
            "Modularyn.Framework.Audit", "Modularyn.Framework.Settings", "Modularyn.Modules.Files" }
            .Select(id => new ModuleDependency(id, VersionRange.Exact(Version))),
        routes: WorkspaceApi.Inventory.Endpoints.Select(endpoint => new ModuleRoute(endpoint.Method, endpoint.Route)),
        definitions: Permissions.Select(item => item.Declaration).Append(DispatchDefinition.Declaration)
            .Concat([Title.Declaration, Digests.Declaration, Text.Declaration]),
        migrations: [new(WorkspaceInitial.Id, Version)],
        backgroundHandlers: [new(ActivityConsumer, Version), new(DigestKind, Version), new(ReleaseKind, Version)]);
    public static PostgreSqlModule Create(PostgreSqlPersistenceOptions options, string schema = "team_workspace") =>
        PostgreSqlModule.Create(Id, schema, options, module => new WorkspaceContext(module));
}
