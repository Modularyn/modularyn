using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

internal sealed record PreparedModule(PostgreSqlModule Module, IReadOnlyList<string> MigrationIds);

internal static class MigrationPreflight
{
    internal static (IReadOnlyList<PreparedModule> Modules, IReadOnlyList<MigrationDiagnostic> Diagnostics) Prepare(
        PostgreSqlModuleRegistry registry, IEnumerable<ModuleManifest> manifests, CancellationToken token)
    {
        var prepared = new List<PreparedModule>();
        var diagnostics = new List<MigrationDiagnostic>();
        foreach (var manifest in manifests)
        {
            token.ThrowIfCancellationRequested();
            var module = registry.Modules.SingleOrDefault(module => module.ModuleId == manifest.Id);
            if (module is null)
            {
                if (manifest.Migrations.Count > 0)
                    diagnostics.Add(new("MGP001", manifest.Id, null, "Register the owning persistence context before applying or activating its migrations."));
                continue;
            }
            try
            {
                using var context = module.CreateContext();
                _ = context.Model;
                var assembly = context.GetService<IMigrationsAssembly>();
                var ids = assembly.Migrations.Keys.Order(StringComparer.Ordinal).ToArray();
                if (!ids.SequenceEqual(manifest.Migrations.Select(asset => asset.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                    || (ids.Length == 0 && context.Model.GetEntityTypes().Any()))
                    throw new PersistenceContractException("MPS020", "The manifest must declare exactly the owning context's migrations.");
                foreach (var migrationType in assembly.Migrations.Values)
                {
                    var migration = assembly.CreateMigration(migrationType, context.Database.ProviderName!);
                    var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, context.Model);
                    if (commands.Any(command => command.TransactionSuppressed))
                        throw new PersistenceContractException("MPS021", "Migration operations must participate in the EF migration transaction.");
                }
                prepared.Add(new(module, Array.AsReadOnly(ids)));
            }
            catch (PersistenceContractException exception)
            {
                diagnostics.Add(new(exception.Code, module.ModuleId, null, exception.Message));
            }
        }
        return (prepared.AsReadOnly(), diagnostics.AsReadOnly());
    }
}
