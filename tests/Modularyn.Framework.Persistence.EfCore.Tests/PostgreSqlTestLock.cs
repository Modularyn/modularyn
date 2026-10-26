using Npgsql;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

/// <summary>
/// Serializes real-provider tests across test projects that share one test database. The adapter's migration lock covers
/// the whole database, so a test in one project could otherwise meet another project's migration as contention. Each
/// test holds this session-level advisory lock, under a key distinct from the migration lock, on an unpooled connection,
/// so the server releases it when the session ends even if the test process dies.
/// </summary>
internal sealed class PostgreSqlTestLock : IAsyncDisposable
{
    private const long Key = 0x4d6f64756c746573;
    private readonly NpgsqlConnection connection;

    private PostgreSqlTestLock(NpgsqlConnection connection) => this.connection = connection;

    internal static async Task<PostgreSqlTestLock> AcquireAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection) { CommandTimeout = 600 };
            command.Parameters.AddWithValue("key", Key);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the unpooled session releases the lock.
        await connection.DisposeAsync();
    }
}
