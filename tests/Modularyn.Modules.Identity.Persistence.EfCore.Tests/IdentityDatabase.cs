using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.Testing;

/// <summary>
/// The migration coordinator's advisory lock covers the whole test database, so the identity tests of one project run one
/// at a time, and each test also holds <see cref="IdentityDatabase"/>'s session lock across projects.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdentityPostgreSqlCollection
{
    public const string Name = "Identity PostgreSQL";
}

/// <summary>
/// A fresh schema for the identity module inside the test database that <c>MODULARYN_TEST_POSTGRESQL</c> names, created
/// for one test and dropped afterwards. The connection is the repository's test role: no superuser, CREATEDB or CREATE ROLE.
/// </summary>
internal sealed class IdentityDatabase : IAsyncDisposable
{
    public static readonly CompositionEnvironment Environment = new(SemanticVersion.Parse("10.0.14"), SemanticVersion.Parse("1.0.0"));

    // Serializes real-provider tests across the test projects that share one test database: each test holds this
    // session-level advisory lock, under a key distinct from the migration lock, on an unpooled connection that the server
    // releases when the session ends. The key is the one the persistence and tenancy fixtures use for the same purpose.
    private const long TestLockKey = 0x4d6f64756c746573;
    private NpgsqlConnection? testLock;

    private IdentityDatabase(string connectionString, string schema)
    {
        ConnectionString = connectionString;
        Schema = schema;
        Module = IdentityPersistence.CreateModule(new PostgreSqlPersistenceOptions(connectionString), schema);
    }

    public string ConnectionString { get; }
    public string Schema { get; }
    public PostgreSqlModule Module { get; }

    public ModuleManifest Manifest => new(IdentityPersistence.ModuleId, SemanticVersion.Parse("1.0.0"), migrations: IdentityPersistence.Migrations);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Creates the schema; with <paramref name="migrate"/>, also applies the module's migrations through the coordinator.</summary>
    public static async Task<IdentityDatabase> CreateAsync(bool migrate = true)
    {
        var target = PostgreSqlTarget.FromEnvironment();
        var session = await PostgreSqlProbe.ConnectAsync(target, Cancellation);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        Assert.Equal("UTF8", session.Parameters.GetValueOrDefault("server_encoding"));
        var database = new IdentityDatabase(System.Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!, "identity_" + Guid.NewGuid().ToString("N"));
        try
        {
            database.testLock = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
            await database.testLock.OpenAsync(Cancellation);
            await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", database.testLock) { CommandTimeout = 600 })
            {
                acquire.Parameters.AddWithValue("key", TestLockKey);
                await acquire.ExecuteNonQueryAsync(Cancellation);
            }
            await database.ExecuteAsync("CREATE SCHEMA " + Quote(database.Schema));
            if (migrate) await database.MigrateAsync();
            return database;
        }
        catch
        {
            // A failed setup still drops its schema and releases the test lock, so the next test is not left waiting.
            await database.DisposeAsync();
            throw;
        }
    }

    public async Task<ActivationPlan> PlanAsync()
    {
        var composition = await new ModuleComposer().ComposeAsync(new([Manifest], Environment), Cancellation);
        Assert.Empty(composition.Diagnostics);
        return Assert.IsType<ActivationPlan>(composition.Plan);
    }

    public async Task MigrateAsync() => Assert.Empty((await ApplyAsync()).Diagnostics);

    /// <summary>Runs the module's migration coordinator, waiting while another test project's coordinator holds the lock (MGP002).</summary>
    public async Task<MigrationRunResult> ApplyAsync()
    {
        var plan = await PlanAsync();
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var result = await new PostgreSqlMigrationCoordinator(new PostgreSqlModuleRegistry([Module])).ApplyAsync(plan, cancellationToken: Cancellation);
            if (result.Diagnostics is not [{ Code: "MGP002" }] || DateTime.UtcNow >= deadline) return result;
            await Task.Delay(TimeSpan.FromMilliseconds(100 + Random.Shared.Next(200)), Cancellation);
        }
    }

    /// <summary>Runs the read-only activation gate, waiting while another coordinator holds the lock (MDC202).</summary>
    public async Task<IReadOnlyList<CompositionDiagnostic>> ValidateActivationAsync()
    {
        var validator = new MigrationActivationValidator(new PostgreSqlModuleRegistry([Module]));
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            var diagnostics = await validator.ValidateAsync(new CompositionSelection([Manifest], Environment), Cancellation);
            if (diagnostics is not [{ Code: MigrationActivationValidator.Busy }] || DateTime.UtcNow >= deadline) return diagnostics;
            await Task.Delay(TimeSpan.FromMilliseconds(100 + Random.Shared.Next(200)), Cancellation);
        }
    }

    public string Table(string name) => Quote(Schema) + "." + Quote(name);

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public async Task<int> ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(Cancellation);
    }

    public async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    /// <summary>Every row of every table in the schema, each value rendered as text, as an export without the key store would show it.</summary>
    public async Task<IReadOnlyList<string>> DumpAsync()
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Cancellation);
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = @schema ORDER BY table_name", connection))
        {
            list.Parameters.AddWithValue("schema", Schema);
            await using var reader = await list.ExecuteReaderAsync(Cancellation);
            while (await reader.ReadAsync(Cancellation)) tables.Add(reader.GetString(0));
        }
        foreach (var table in tables)
        {
            await using var select = new NpgsqlCommand("SELECT * FROM " + Table(table), connection);
            await using var reader = await select.ExecuteReaderAsync(Cancellation);
            while (await reader.ReadAsync(Cancellation))
                for (var column = 0; column < reader.FieldCount; column++)
                    values.Add(reader.IsDBNull(column) ? "" : reader.GetValue(column) is byte[] bytes ? Convert.ToHexStringLower(bytes) : Convert.ToString(reader.GetValue(column),
                        System.Globalization.CultureInfo.InvariantCulture) ?? "");
        }
        return values;
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the unpooled session releases the test lock, after the schema is gone.
        await using var held = testLock;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS " + Quote(Schema) + " CASCADE", connection);
        await command.ExecuteNonQueryAsync(timeout.Token);
    }
}
