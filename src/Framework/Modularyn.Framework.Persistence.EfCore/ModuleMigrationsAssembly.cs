using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

/// <summary>Uses the registered schema for relocatable, module-owned EF migrations.</summary>
public abstract class ModuleMigration : Migration
{
    private PostgreSqlModule? module;
    protected string Schema => module?.Schema ?? throw new InvalidOperationException("The migration must be created by its module context.");
    internal void Bind(PostgreSqlModule owner) => module = owner;
}

/// <summary>Discovers only migrations explicitly attributed to the owning context, in that context's assembly.</summary>
public sealed class ModuleMigrationsAssembly : IMigrationsAssembly
{
    private readonly PostgreSqlModule module;

    public ModuleMigrationsAssembly(ICurrentDbContext currentContext)
    {
        module = ((ModuleDbContext)currentContext.Context).Module;
        Assembly = module.ContextType.Assembly;
        var migrations = new SortedDictionary<string, TypeInfo>(StringComparer.Ordinal);
        foreach (var type in Assembly.DefinedTypes.Where(type => !type.IsAbstract && typeof(Migration).IsAssignableFrom(type)
                     && type.GetCustomAttribute<DbContextAttribute>()?.ContextType == module.ContextType))
        {
            var id = type.GetCustomAttribute<MigrationAttribute>()?.Id;
            if (string.IsNullOrWhiteSpace(id) || !migrations.TryAdd(id, type))
                throw new PersistenceContractException("MPS005", "Every module migration needs a unique explicit migration identity.");
        }
        Migrations = new System.Collections.ObjectModel.ReadOnlyDictionary<string, TypeInfo>(migrations);
        var snapshots = Assembly.DefinedTypes.Where(type => !type.IsAbstract && typeof(ModelSnapshot).IsAssignableFrom(type)
            && type.GetCustomAttribute<DbContextAttribute>()?.ContextType == module.ContextType).ToArray();
        if (snapshots.Length > 1)
            throw new PersistenceContractException("MPS005", "A context owns at most one model snapshot.");
        ModelSnapshot = snapshots.Length == 0 ? null : (ModelSnapshot?)Activator.CreateInstance(snapshots[0].AsType());
    }

    public IReadOnlyDictionary<string, TypeInfo> Migrations { get; }
    public ModelSnapshot? ModelSnapshot { get; }
    public Assembly Assembly { get; }

    public string? FindMigrationId(string nameOrId) => Migrations.Keys.FirstOrDefault(id =>
        string.Equals(id, nameOrId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(id[(id.IndexOf('_') + 1)..], nameOrId, StringComparison.OrdinalIgnoreCase));

    public Migration CreateMigration(TypeInfo migrationClass, string activeProvider)
    {
        if (!Migrations.Values.Contains(migrationClass))
            throw new PersistenceContractException("MPS005", "The migration belongs to another context.");
        var migration = (Migration?)Activator.CreateInstance(migrationClass.AsType())
            ?? throw new PersistenceContractException("MPS005", "The migration could not be constructed.");
        if (migration is ModuleMigration owned) owned.Bind(module);
        migration.ActiveProvider = activeProvider;
        ModuleOwnership.ValidateMigration(migration.UpOperations, module);
        return migration;
    }
}
