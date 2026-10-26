using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

/// <summary>Each test instance owns fresh schemas inside the supplied database and drops only those schemas.</summary>
public abstract class PostgreSqlTestDatabase : IAsyncLifetime
{
    private readonly List<string> schemas = [];
    private PostgreSqlTestLock? testLock;
    protected string Schema { get; } = "ac005_" + Guid.NewGuid().ToString("N");
    protected PostgreSqlPersistenceOptions Options { get; private set; } = null!;
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    protected static CompositionEnvironment EnvironmentVersion { get; } = new(SemanticVersion.Parse("10.0.14"), SemanticVersion.Parse("1.0.0"));

    public async ValueTask InitializeAsync()
    {
        // Reuse the repository's connection convention and probe unchanged.
        var target = PostgreSqlTarget.FromEnvironment();
        var session = await PostgreSqlProbe.ConnectAsync(target, Cancellation);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        Assert.Equal("UTF8", session.Parameters.GetValueOrDefault("server_encoding"));
        Options = new(System.Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!);
        testLock = await PostgreSqlTestLock.AcquireAsync(Options.ConnectionString, Cancellation);
        await CreateSchemaAsync(Schema);
    }

    protected async Task CreateSchemaAsync(string schema)
    {
        schemas.Add(schema);
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("CREATE SCHEMA " + Quote(schema), connection);
        await command.ExecuteNonQueryAsync(Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        await using var held = testLock;
        if (schemas.Count == 0) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        foreach (var schema in schemas.AsEnumerable().Reverse())
        {
            await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS " + Quote(schema) + " CASCADE", connection);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
    }

    protected PostgreSqlModule SampleModule() => PostgreSqlModule.Create("Sample", Schema, Options, module => new SampleContext(module));

    protected static ModuleManifest Manifest(PostgreSqlModule module, IEnumerable<ModuleDependency>? dependencies = null)
    {
        using var context = module.CreateContext();
        return new(module.ModuleId, SemanticVersion.Parse("1.0.0"), dependencies: dependencies,
            migrations: context.GetService<IMigrationsAssembly>().Migrations.Keys.Select(id => new ModuleAsset(id, SemanticVersion.Parse("1.0.0"))));
    }

    protected static async Task<ActivationPlan> PlanAsync(params ModuleManifest[] manifests)
    {
        var composition = await new ModuleComposer().ComposeAsync(new(manifests, EnvironmentVersion), Cancellation);
        Assert.Empty(composition.Diagnostics);
        return Assert.IsType<ActivationPlan>(composition.Plan);
    }

    protected static async Task<ActivationPlan> MigrateAsync(PostgreSqlModule module)
    {
        var plan = await PlanAsync(Manifest(module));
        var result = await new PostgreSqlMigrationCoordinator(new([module])).ApplyAsync(plan, cancellationToken: Cancellation);
        Assert.Empty(result.Diagnostics);
        return plan;
    }

    protected async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    protected string Table(string name) => Quote(Schema) + "." + Quote(name);
    protected static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    protected async Task<string> ProgressStateAsync(string migrationId) => await ScalarAsync<string>(
        "SELECT \"State\" FROM " + Table(PostgreSqlModule.ProgressTable) + " WHERE \"MigrationId\" = @id", new NpgsqlParameter("id", migrationId));

    protected async Task AssertCompleteAsync(PostgreSqlModule module)
    {
        var diagnostics = await new MigrationActivationValidator(new([module])).ValidateAsync(new([Manifest(module)], EnvironmentVersion), Cancellation);
        Assert.Empty(diagnostics);
    }
}

internal sealed class CheckpointObserver(Func<MigrationCheckpoint, CancellationToken, ValueTask> callback) : IMigrationCheckpointObserver
{
    public ValueTask ReachedAsync(MigrationCheckpoint checkpoint, CancellationToken cancellationToken) => callback(checkpoint, cancellationToken);
}
