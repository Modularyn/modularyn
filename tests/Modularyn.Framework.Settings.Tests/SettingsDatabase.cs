using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsPostgreSqlCollection
{
    public const string Name = "PostgreSQL settings";
}

/// <summary>
/// PostgreSQL owns both the real directory and the scoped overrides. Identity and authority seams are only fixture
/// scaffolding; these tests make no claim about their security controls. Each fixture owns two fresh schemas.
/// </summary>
internal sealed class SettingsDatabase : IAsyncDisposable
{
    private readonly PostgreSqlTestLock testLock;
    private readonly List<AsyncServiceScope> scopes = [];
    private readonly string directorySchema;
    internal static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private SettingsDatabase(PostgreSqlPersistenceOptions options, PostgreSqlTestLock testLock)
    {
        Options = options;
        this.testLock = testLock;
        var suffix = Guid.NewGuid().ToString("N");
        directorySchema = "st_directory_" + suffix;
        Schema = "st_values_" + suffix;
        Directory = TenantDirectoryModule.Create(options, directorySchema);
        Module = SettingsModule.Create(options, Schema);
    }

    internal PostgreSqlPersistenceOptions Options { get; }
    internal string Schema { get; }
    internal PostgreSqlModule Directory { get; }
    internal PostgreSqlModule Module { get; }
    internal ManualTimeProvider Clock { get; } = new();
    internal TestSeams Seams { get; } = new();
    internal ValueDefinitionRegistry Definitions { get; } = TestDefinitions.Registry();
    internal ServiceProvider Services { get; private set; } = null!;
    internal SettingsService Settings => Services.GetRequiredService<SettingsService>();
    internal Guid TenantA { get; private set; }
    internal Guid TenantB { get; private set; }

    internal static async Task<SettingsDatabase> CreateAsync()
    {
        var target = PostgreSqlTarget.FromEnvironment();
        var session = await PostgreSqlProbe.ConnectAsync(target, Cancellation);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        var options = new PostgreSqlPersistenceOptions(Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!);
        var database = new SettingsDatabase(options, await PostgreSqlTestLock.AcquireAsync(options.ConnectionString, Cancellation));
        try
        {
            var manifests = new[] { TenantDirectoryModule.Manifest(TestDefinitions.Version), SettingsModule.Manifest(TestDefinitions.Version), TestDefinitions.Manifest };
            var composition = await new ModuleComposer([database.Definitions]).ComposeAsync(new(manifests,
                new CompositionEnvironment(SemanticVersion.Parse("10.0.14"), TestDefinitions.Version)), Cancellation);
            Assert.Empty(composition.Diagnostics);
            var registry = new PostgreSqlModuleRegistry([database.Directory, database.Module]);
            var migrated = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(composition.Plan!, cancellationToken: Cancellation);
            Assert.Empty(migrated.Diagnostics);
            Assert.Empty(await new MigrationActivationValidator(registry).ValidateAsync(composition.Plan!.Selection, Cancellation));
            database.Services = database.NewServices();
            database.Seams.Authority.Grant("host-1", TenantDirectoryAdministration.OperationId);
            database.Seams.Authority.Grant("host-1", SettingsService.GlobalOperationId);
            var (scope, host) = await Units.EnterHostAsync(database.Services, "host-1", TenantDirectoryAdministration.OperationId);
            await using (scope)
            {
                var directory = database.Services.GetRequiredService<TenantDirectoryAdministration>();
                database.TenantA = await directory.CreateTenantAsync(host, "Tenant A", Cancellation);
                database.TenantB = await directory.CreateTenantAsync(host, "Tenant B", Cancellation);
            }
            foreach (var tenant in new[] { database.TenantA, database.TenantB })
            foreach (var user in new[] { "user-a", "user-b", "worker" }) database.Seams.Membership.Add(user, tenant);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    internal ServiceProvider NewServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddModularynTenancy(Directory);
        Seams.Register(services);
        services.AddModularynSettings(Module, Definitions);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    internal async Task<TenantContext> EnterAsync(Guid tenant, string user = "user-a")
    {
        var (scope, context) = await Units.EnterAsync(Services, user, tenant);
        scopes.Add(scope);
        return context;
    }

    internal async Task<TenantContext> HostAsync()
    {
        var (scope, context) = await Units.EnterHostAsync(Services, "host-1", SettingsService.GlobalOperationId);
        scopes.Add(scope);
        return context;
    }

    internal async Task<string[]> InventoryAsync()
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT row_to_json(v)::text FROM " + Table("GlobalValues")
            + " v UNION ALL SELECT row_to_json(v)::text FROM " + Table("TenantValues") + " v", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var values = new List<string>();
        while (await reader.ReadAsync(Cancellation)) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    internal async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(Cancellation);
    }

    internal string Table(string name) => "\"" + Schema + "\".\"" + name + "\"";

    public async ValueTask DisposeAsync()
    {
        await using var held = testLock;
        foreach (var scope in scopes) await scope.DisposeAsync();
        if (Services is not null) await Services.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        foreach (var schema in new[] { Schema, directorySchema })
        {
            await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE", connection);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
    }
}
