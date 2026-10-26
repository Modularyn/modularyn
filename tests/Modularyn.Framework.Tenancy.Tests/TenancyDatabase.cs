using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// The coordinator lock and the shared test lock cover the database, so real-provider fixtures run one at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TenancyPostgreSqlCollection
{
    public const string Name = "PostgreSQL tenancy";
}

/// <summary>
/// One real-provider fixture: the tenant directory and the items module, each in a fresh schema of the supplied test
/// database, migrated through CODE-03's coordinator and gated by its activation validator. Disposal drops only those
/// schemas. Tenants A and B have their own domains; user-a is a member of A, user-b of B, ta-admin of A only, and host-1
/// holds host authority for directory administration.
/// </summary>
internal sealed class TenancyDatabase : IAsyncDisposable
{
    internal const string AppHost = "app.example.test";
    internal const string HostA = "tenant-a.example.test";
    internal const string HostB = "tenant-b.example.test";
    internal static readonly string[] PublicOrigins = ["https://" + AppHost, "https://" + HostA, "https://" + HostB];
    internal static CompositionEnvironment Environment { get; } = new(SemanticVersion.Parse("10.0.14"), SemanticVersion.Parse("1.0.0"));

    private readonly PostgreSqlTestLock testLock;

    private TenancyDatabase(PostgreSqlPersistenceOptions options, PostgreSqlTestLock testLock)
    {
        Options = options;
        this.testLock = testLock;
        var suffix = Guid.NewGuid().ToString("N");
        DirectorySchema = "tn_directory_" + suffix;
        ItemsSchema = "tn_items_" + suffix;
        Directory = TenantDirectoryModule.Create(options, DirectorySchema);
        Items = PostgreSqlModule.Create("Items", ItemsSchema, options, module => new ItemsContext(module));
    }

    internal PostgreSqlPersistenceOptions Options { get; }
    internal string DirectorySchema { get; }
    internal string ItemsSchema { get; }
    internal PostgreSqlModule Directory { get; }
    internal PostgreSqlModule Items { get; }
    internal TestSeams Seams { get; } = new();
    internal Guid TenantA { get; private set; }
    internal Guid TenantB { get; private set; }
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    internal static async Task<TenancyDatabase> CreateAsync()
    {
        var target = PostgreSqlTarget.FromEnvironment();
        var session = await PostgreSqlProbe.ConnectAsync(target, Cancellation);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        var options = new PostgreSqlPersistenceOptions(System.Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!);
        var database = new TenancyDatabase(options, await PostgreSqlTestLock.AcquireAsync(options.ConnectionString, Cancellation));
        try
        {
            await database.MigrateAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private async Task MigrateAsync()
    {
        var manifests = new[] { TenantDirectoryModule.Manifest(SemanticVersion.Parse("1.0.0")), ItemsManifest() };
        var composition = await new ModuleComposer().ComposeAsync(new(manifests, Environment), Cancellation);
        Assert.Empty(composition.Diagnostics);
        var registry = new PostgreSqlModuleRegistry([Directory, Items]);
        var result = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(composition.Plan!, cancellationToken: Cancellation);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(await new MigrationActivationValidator(registry).ValidateAsync(composition.Plan!.Selection, Cancellation));
    }

    private ModuleManifest ItemsManifest()
    {
        using var context = Items.CreateContext();
        return new(Items.ModuleId, SemanticVersion.Parse("1.0.0"),
            migrations: context.GetService<IMigrationsAssembly>().Migrations.Keys.Select(id => new ModuleAsset(id, SemanticVersion.Parse("1.0.0"))));
    }

    /// <summary>The tenancy services as a host registers them, with the test seams.</summary>
    internal ServiceProvider Services(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PublicOrigins(Microsoft.Extensions.Options.Options.Create(new IngressOptions { PublicOrigins = [.. PublicOrigins] })));
        services.AddModularynTenancy(Directory);
        Seams.Register(services);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    /// <summary>Creates tenants A and B through directory administration, under an audited host scope.</summary>
    internal async Task SeedAsync(IServiceProvider services)
    {
        Seams.Authority.Grant("host-1", TenantDirectoryAdministration.OperationId);
        var (scope, host) = await Units.EnterHostAsync(services, "host-1", TenantDirectoryAdministration.OperationId);
        await using (scope)
        {
            var administration = services.GetRequiredService<TenantDirectoryAdministration>();
            TenantA = await administration.CreateTenantAsync(host, "Tenant A", Cancellation);
            TenantB = await administration.CreateTenantAsync(host, "Tenant B", Cancellation);
            await administration.AddDomainAsync(host, TenantA, HostA, Cancellation);
            await administration.AddDomainAsync(host, TenantB, HostB, Cancellation);
        }
        Seams.Membership.Add("user-a", TenantA);
        Seams.Membership.Add("user-b", TenantB);
        Seams.Membership.Add("ta-admin", TenantA);
    }

    internal async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    /// <summary>Rows read directly from PostgreSQL, bypassing the adapter, as an independent inventory.</summary>
    internal async Task<List<(Guid Tenant, string Value)>> InventoryAsync(string table, string column)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT \"TenantId\", \"" + column + "\" FROM " + Table(table) + " ORDER BY 1, 2", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var rows = new List<(Guid, string)>();
        while (await reader.ReadAsync(Cancellation)) rows.Add((reader.GetGuid(0), reader.GetValue(1).ToString()!));
        return rows;
    }

    internal string Table(string name) => "\"" + ItemsSchema + "\".\"" + name + "\"";

    public async ValueTask DisposeAsync()
    {
        await using var held = testLock;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        foreach (var schema in new[] { ItemsSchema, DirectorySchema })
        {
            await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE", connection);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
    }
}
