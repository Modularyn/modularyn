using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

/// <summary>
/// A fixture that fails while it holds the shared test lock releases it. A lock that leaked would keep its unpooled session
/// open until the process ends, so every later real-provider test in the process would wait up to ten minutes for it.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class PostgreSqlTestLockTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static string ConnectionString => Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)
        ?? throw new InvalidOperationException("Set " + PostgreSqlTarget.Variable + " to run the real-provider tests.");

    [Fact]
    public async Task A_fixture_whose_construction_throws_releases_the_test_lock()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlTestLock.HoldAsync<object>(ConnectionString,
            _ => throw new InvalidOperationException("The fixture could not be built."), Cancellation));
        await AcquirePromptlyAsync();
    }

    [Fact]
    public async Task A_fixture_whose_setup_throws_releases_the_test_lock()
    {
        // The fixture's own schema already exists, so its setup fails at CREATE SCHEMA, after it has taken the lock.
        var fixture = new SchemaTaken();
        await fixture.TakeSchemaAsync();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(async () => await fixture.InitializeAsync());
            await AcquirePromptlyAsync();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // The next acquisition does not wait: a released lock is free at once, where a leaked one stays held until the process ends.
    private static async Task AcquirePromptlyAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var next = await PostgreSqlTestLock.AcquireAsync(ConnectionString, timeout.Token);
    }

    private sealed class SchemaTaken : PostgreSqlTestDatabase
    {
        internal async Task TakeSchemaAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Cancellation);
            await using var command = new NpgsqlCommand("CREATE SCHEMA \"" + Schema + "\"", connection);
            await command.ExecuteNonQueryAsync(Cancellation);
        }
    }
}
