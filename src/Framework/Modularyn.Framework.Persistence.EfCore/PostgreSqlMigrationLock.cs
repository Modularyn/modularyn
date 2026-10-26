using Npgsql;

namespace Modularyn.Framework.Persistence.EfCore;

internal sealed class PostgreSqlMigrationLock : IAsyncDisposable
{
    // PostgreSQL scopes advisory locks to the current database. Every module and host uses this key.
    internal const long Key = 0x4d6f64756c617279;
    private PostgreSqlMigrationLock(NpgsqlConnection connection, bool acquired)
    {
        Connection = connection;
        Acquired = acquired;
    }

    internal NpgsqlConnection Connection { get; }
    internal bool Acquired { get; }

    internal static async Task<PostgreSqlMigrationLock> TryAcquireAsync(PostgreSqlPersistenceOptions options, CancellationToken token)
    {
        var connection = new NpgsqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", Key);
            return new(connection, (bool)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Acquired && Connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", Connection) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("key", Key);
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { await Connection.DisposeAsync().ConfigureAwait(false); }
    }
}
