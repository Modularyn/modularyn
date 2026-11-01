using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.BackgroundJobs.Domain;

public static class BackgroundJobsDefinition
{
    public const string ModuleId = "Modularyn.Modules.BackgroundJobs";
    public const string MigrationId = "20261028001000_BackgroundJobs";
    public static HostOperation Dispatch { get; } = new("modularyn.jobs.dispatch",
        [new(ModuleId, "Jobs"), new(ModuleId, "JobAttempts"), new(ModuleId, "JobSchedules")]);
    public static IReadOnlyList<PermissionDefinition> Permissions(SemanticVersion version) =>
        [new(ModuleId, "jobs.execute", version, PermissionFreshness.Current), new(ModuleId, "jobs.cancel", version, PermissionFreshness.Current),
            new(ModuleId, "jobs.schedule", version, PermissionFreshness.Current)];
    public static IReadOnlyList<HostOperationDefinition> HostOperations(SemanticVersion version) => [new(ModuleId, version, Dispatch)];
    public static ModuleManifest Manifest(SemanticVersion version) => new(ModuleId, version,
        definitions: Permissions(version).Select(value => value.Declaration).Concat(HostOperations(version).Select(value => value.Declaration)),
        migrations: [new(MigrationId, version)]);
}
