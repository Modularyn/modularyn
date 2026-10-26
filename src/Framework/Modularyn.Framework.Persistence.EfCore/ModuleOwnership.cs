using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

public static class ModuleOwnership
{
    internal static void ValidateModel(IMutableModel model, PostgreSqlModule module)
    {
        foreach (var entity in model.GetEntityTypes())
        {
            Check(entity.GetSchema(), entity.GetTableName(), module);
            if (entity.GetViewName() is not null || entity.GetSqlQuery() is not null || entity.GetFunctionName() is not null)
                Refuse("Views, SQL query mappings and function mappings need a separately supported ownership contract.");
            foreach (var fragment in entity.GetMappingFragments(StoreObjectType.Table))
                Check(fragment.StoreObject.Schema, fragment.StoreObject.Name, module);
        }
        foreach (var sequence in model.GetSequences()) Check(sequence.Schema, sequence.Name, module);
    }

    /// <summary>Closed operation vocabulary: raw SQL and unrecognized operations cannot escape the module schema.</summary>
    public static void ValidateMigration(IReadOnlyList<MigrationOperation> operations, PostgreSqlModule module) =>
        ValidateMigration(operations, module, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Also keeps tenant data out of migrations and the tenant in every key, unique constraint, index and reference of a
    /// tenant-carrying table, given each such table's tenant column.
    /// </summary>
    internal static void ValidateMigration(IReadOnlyList<MigrationOperation> operations, PostgreSqlModule module,
        IReadOnlyDictionary<string, string> tenantTables)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(tenantTables);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case EnsureSchemaOperation schema when schema.Name == module.Schema:
                    break;
                case CreateTableOperation table:
                    Check(table.Schema, table.Name, module);
                    ValidateMigration(table.Columns.Cast<MigrationOperation>().ToArray(), module, tenantTables);
                    foreach (var foreignKey in table.ForeignKeys) ValidateForeignKey(foreignKey, module, tenantTables);
                    if (table.CheckConstraints.Count > 0)
                        Refuse("SQL check expressions need a separately supported ownership contract.");
                    if (table.PrimaryKey is { } primaryKey) RequireTenant(tenantTables, table.Name, primaryKey.Columns);
                    foreach (var unique in table.UniqueConstraints) RequireTenant(tenantTables, table.Name, unique.Columns);
                    break;
                case AddForeignKeyOperation foreignKey:
                    ValidateForeignKey(foreignKey, module, tenantTables);
                    break;
                case RenameTableOperation table:
                    Check(table.Schema, table.Name, module);
                    Check(table.NewSchema ?? table.Schema, table.NewName ?? table.Name, module);
                    break;
                case RenameSequenceOperation sequence:
                    Check(sequence.Schema, sequence.Name, module);
                    Check(sequence.NewSchema ?? sequence.Schema, sequence.NewName ?? sequence.Name, module);
                    break;
                case TableOperation table:
                    Check(table.Schema, table.Name, module);
                    break;
                case DropTableOperation table:
                    Check(table.Schema, table.Name, module);
                    break;
                case ColumnOperation column:
                    Check(column.Schema, column.Table, module);
                    if (column.ComputedColumnSql is not null || column.DefaultValueSql is not null)
                        Refuse("SQL column expressions need a separately supported ownership contract.");
                    break;
                case CreateIndexOperation index:
                    Check(index.Schema, index.Table, module);
                    if (index.Filter is not null || Equals(index["Npgsql:CreatedConcurrently"], true))
                        Refuse("Only transactional indexes without SQL predicates are supported.");
                    RequireTenant(tenantTables, index.Table, index.Columns);
                    break;
                case DropIndexOperation index:
                    Check(index.Schema, index.Table, module);
                    break;
                case RenameIndexOperation index:
                    Check(index.Schema, index.Table, module);
                    break;
                case AddPrimaryKeyOperation key:
                    Check(key.Schema, key.Table, module);
                    RequireTenant(tenantTables, key.Table, key.Columns);
                    break;
                case AddUniqueConstraintOperation key:
                    Check(key.Schema, key.Table, module);
                    RequireTenant(tenantTables, key.Table, key.Columns);
                    break;
                case DropPrimaryKeyOperation key:
                    Check(key.Schema, key.Table, module);
                    break;
                case DropUniqueConstraintOperation key:
                    Check(key.Schema, key.Table, module);
                    break;
                case DropForeignKeyOperation key:
                    Check(key.Schema, key.Table, module);
                    break;
                case DropColumnOperation column:
                    Check(column.Schema, column.Table, module);
                    break;
                case RenameColumnOperation column:
                    Check(column.Schema, column.Table, module);
                    break;
                case CreateSequenceOperation sequence:
                    Check(sequence.Schema, sequence.Name, module);
                    break;
                case AlterSequenceOperation sequence:
                    Check(sequence.Schema, sequence.Name, module);
                    break;
                case DropSequenceOperation sequence:
                    Check(sequence.Schema, sequence.Name, module);
                    break;
                case RestartSequenceOperation sequence:
                    Check(sequence.Schema, sequence.Name, module);
                    break;
                case InsertDataOperation data:
                    Check(data.Schema, data.Table, module);
                    RefuseTenantData(tenantTables, data.Table);
                    break;
                case UpdateDataOperation data:
                    Check(data.Schema, data.Table, module);
                    RefuseTenantData(tenantTables, data.Table);
                    break;
                case DeleteDataOperation data:
                    Check(data.Schema, data.Table, module);
                    RefuseTenantData(tenantTables, data.Table);
                    break;
                default:
                    Refuse("The migration contains an unsupported operation. Use transactional operations on owned objects.");
                    break;
            }
        }
    }

    private static void ValidateForeignKey(AddForeignKeyOperation key, PostgreSqlModule module, IReadOnlyDictionary<string, string> tenantTables)
    {
        Check(key.Schema, key.Table, module);
        Check(key.PrincipalSchema, key.PrincipalTable, module);
        if (!tenantTables.TryGetValue(key.PrincipalTable, out var principalTenant)) return;
        if (!tenantTables.TryGetValue(key.Table, out var dependentTenant) || key.PrincipalColumns is null
            || key.PrincipalColumns.Length != key.Columns.Length
            || !Enumerable.Range(0, key.Columns.Length).Any(index => key.Columns[index] == dependentTenant && key.PrincipalColumns[index] == principalTenant))
            throw new PersistenceContractException("MPS032", "A reference to a tenant-scoped table must come from a tenant-scoped table and carry the same tenant.");
    }

    private static void RequireTenant(IReadOnlyDictionary<string, string> tenantTables, string table, string[] columns)
    {
        if (tenantTables.TryGetValue(table, out var tenant) && !columns.Contains(tenant, StringComparer.Ordinal))
            throw new PersistenceContractException("MPS032", "Every key, unique constraint and index of a tenant-scoped table must include its tenant column.");
    }

    private static void RefuseTenantData(IReadOnlyDictionary<string, string> tenantTables, string table)
    {
        if (tenantTables.ContainsKey(table))
            throw new PersistenceContractException("MPS039",
                "A migration cannot seed or change tenant-scoped rows. Tenant data changes only inside a tenant unit of work.");
    }

    private static void Check(string? schema, string? name, PostgreSqlModule module)
    {
        if (schema != module.Schema || string.IsNullOrWhiteSpace(name)
            || name is PostgreSqlModule.HistoryTable or PostgreSqlModule.ProgressTable or PostgreSqlModule.OwnershipTable)
            Refuse("A module can map and migrate only its own schema; framework history and progress tables are reserved.");
    }

    private static void Refuse(string message) => throw new PersistenceContractException("MPS004", message);
}
