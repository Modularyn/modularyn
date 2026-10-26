using Modularyn.Framework.Persistence.Abstractions;
using Npgsql;

namespace Modularyn.Framework.Persistence.EfCore;

internal static class ModuleSchemaOwnership
{
    internal static async Task<bool> CheckAsync(NpgsqlConnection connection, PostgreSqlModule module, CancellationToken token)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
        exists.Parameters.AddWithValue("name", module.Table(PostgreSqlModule.OwnershipTable));
        if ((bool)(await exists.ExecuteScalarAsync(token).ConfigureAwait(false))!)
        {
            await using var owner = new NpgsqlCommand("SELECT \"ModuleId\", \"Context\" FROM " + module.Table(PostgreSqlModule.OwnershipTable), connection);
            await using var reader = await owner.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetString(0) != module.ModuleId
                || reader.GetString(1) != module.ContextIdentity || await reader.ReadAsync(token).ConfigureAwait(false))
                Refuse();
            return true;
        }
        await using var populated = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema)"
            + " OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = @schema)"
            + " OR EXISTS (SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname = @schema)", connection);
        populated.Parameters.AddWithValue("schema", module.Schema);
        if ((bool)(await populated.ExecuteScalarAsync(token).ConfigureAwait(false))!) Refuse();
        return false;
    }

    internal static async Task RequireAsync(NpgsqlConnection connection, PostgreSqlModule module, CancellationToken token)
    {
        if (!await CheckAsync(connection, module, token).ConfigureAwait(false))
            throw new PersistenceContractException("MPS022", "Run the owning module's migration coordinator before opening a unit of work.");
    }

    private static void Refuse() => throw new PersistenceContractException("MPS022",
        "The schema has another owner or unclaimed objects. Reconcile module ownership before using it.");
}
