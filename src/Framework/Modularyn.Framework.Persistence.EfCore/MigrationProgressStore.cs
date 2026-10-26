using Modularyn.Framework.Persistence.Abstractions;
using Npgsql;

namespace Modularyn.Framework.Persistence.EfCore;

internal static class MigrationProgressStore
{
    internal static async Task EnsureAsync(NpgsqlConnection connection, PostgreSqlModule module, CancellationToken token)
    {
        await ModuleSchemaOwnership.CheckAsync(connection, module, token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS " + PostgreSqlModule.Quote(module.Schema)
            + "; CREATE TABLE IF NOT EXISTS " + module.Table(PostgreSqlModule.OwnershipTable)
            + " (\"Id\" integer PRIMARY KEY CHECK (\"Id\" = 1), \"ModuleId\" text NOT NULL, \"Context\" text NOT NULL)"
            + "; INSERT INTO " + module.Table(PostgreSqlModule.OwnershipTable)
            + " (\"Id\", \"ModuleId\", \"Context\") VALUES (1, @module, @context) ON CONFLICT (\"Id\") DO NOTHING"
            + "; CREATE TABLE IF NOT EXISTS " + module.Table(PostgreSqlModule.ProgressTable)
            + " (\"MigrationId\" text PRIMARY KEY, \"State\" text NOT NULL, \"Attempt\" integer NOT NULL)", connection, transaction);
        command.Parameters.AddWithValue("module", module.ModuleId);
        command.Parameters.AddWithValue("context", module.ContextIdentity);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyDictionary<string, MigrationProgress>> ReadAsync(NpgsqlConnection connection,
        PostgreSqlModule module, CancellationToken token)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection))
        {
            exists.Parameters.AddWithValue("name", module.Table(PostgreSqlModule.ProgressTable));
            if (!(bool)(await exists.ExecuteScalarAsync(token).ConfigureAwait(false))!)
                return new Dictionary<string, MigrationProgress>(StringComparer.Ordinal);
        }
        await using var command = new NpgsqlCommand("SELECT \"MigrationId\", \"State\", \"Attempt\" FROM "
            + module.Table(PostgreSqlModule.ProgressTable) + " ORDER BY \"MigrationId\"", connection);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var rows = new Dictionary<string, MigrationProgress>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (!Enum.TryParse<MigrationState>(reader.GetString(1), out var state) || !Enum.IsDefined(state) || reader.GetInt32(2) < 1)
                throw new PersistenceContractException("MPS019", "Migration progress is invalid; reconcile it before activation.");
            var id = reader.GetString(0);
            rows.Add(id, new(module.ModuleId, id, state, reader.GetInt32(2)));
        }
        return rows;
    }

    internal static async Task WriteAsync(NpgsqlConnection connection, PostgreSqlModule module, string id,
        MigrationState state, int attempt, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("INSERT INTO " + module.Table(PostgreSqlModule.ProgressTable)
            + " (\"MigrationId\", \"State\", \"Attempt\") VALUES (@id, @state, @attempt)"
            + " ON CONFLICT (\"MigrationId\") DO UPDATE SET \"State\" = EXCLUDED.\"State\", \"Attempt\" = EXCLUDED.\"Attempt\"", connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("state", state.ToString());
        command.Parameters.AddWithValue("attempt", attempt);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    internal static async Task TryRecordFailureAsync(NpgsqlConnection connection, PostgreSqlModule module, string id,
        MigrationState state, int attempt)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await WriteAsync(connection, module, id, state, attempt, deadline.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is System.Data.Common.DbException or OperationCanceledException or InvalidOperationException)
        {
            // A lost connection can leave Applying. The next locked run reconciles it against EF history.
        }
    }
}
