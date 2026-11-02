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
        // The session names its holder, so a database view of a stalled run shows which process and test hold the lock.
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, ApplicationName = HolderName() }
            .ConnectionString);
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

    /// <summary>
    /// Acquires the lock and builds the fixture that holds it. If building the fixture throws, the lock is released before the
    /// failure propagates, so a fixture that fails before its own cleanup can run never leaves the database locked for the
    /// tests after it. A fixture's later setup, such as composition, migration and validation, releases it through the
    /// fixture's own disposal.
    /// </summary>
    internal static async Task<T> HoldAsync<T>(string connectionString, Func<PostgreSqlTestLock, T> create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(create);
        var held = await AcquireAsync(connectionString, cancellationToken);
        try
        {
            return create(held);
        }
        catch
        {
            await held.DisposeAsync();
            throw;
        }
    }

    // "test-lock <process id> <test method>", within PostgreSQL's 63-byte application name: the method's name only, never a value.
    private static string HolderName()
    {
        var test = Xunit.TestContext.Current.Test?.TestDisplayName ?? "none";
        var method = test.Split('(')[0];
        method = method[(method.LastIndexOf('.') + 1)..];
        var name = "test-lock " + Environment.ProcessId + " " + method;
        return name.Length <= 63 ? name : name[..63];
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the unpooled session releases the lock.
        await connection.DisposeAsync();
    }
}
