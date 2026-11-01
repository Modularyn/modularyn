using Modularyn.Framework.Authorization;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Sdk.Client.Tests;

// The coordinator lock and the shared test lock cover the database, so real-provider fixtures run one at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthorizationPostgreSqlCollection
{
    public const string Name = "PostgreSQL authorization";
}

/// <summary>
/// One real-provider fixture: the tenant directory, identity, authorization, settings and the items module, each in a
/// fresh schema of the database that <c>MODULARYN_TEST_POSTGRESQL</c> names, composed, migrated through CODE-03's
/// coordinator and gated by its activation validator. The connection is the repository's test role: no superuser,
/// CREATEDB or CREATE ROLE. Disposal drops only these schemas.
/// </summary>
internal sealed class AuthorizationDatabase : IAsyncDisposable
{
    internal static readonly SemanticVersion Version = SemanticVersion.Parse("1.0.0");
    private static readonly CompositionEnvironment Environment = new(SemanticVersion.Parse("10.0.14"), Version);
    private readonly PostgreSqlTestLock testLock;
    private readonly string[] schemas;

    private AuthorizationDatabase(PostgreSqlPersistenceOptions options, PostgreSqlTestLock testLock)
    {
        Options = options;
        this.testLock = testLock;
        var suffix = Guid.NewGuid().ToString("N");
        schemas = ["az_directory_" + suffix, "az_identity_" + suffix, "az_authorization_" + suffix, "az_settings_" + suffix, "az_items_" + suffix];
        Directory = TenantDirectoryModule.Create(options, schemas[0]);
        Identity = IdentityPersistence.CreateModule(options, schemas[1]);
        Authorization = AuthorizationModule.Create(options, schemas[2]);
        Settings = SettingsModule.Create(options, schemas[3]);
        ItemsModule = PostgreSqlModule.Create(Items.ModuleId, schemas[4], options, module => new ItemsContext(module));
        Manifests =
        [
            TenantDirectoryModule.Manifest(Version),
            new ModuleManifest(IdentityPersistence.ModuleId, Version, definitions: IdentityHostOperations.Definitions(Version).Select(definition => definition.Declaration),
                migrations: IdentityPersistence.Migrations),
            AuthorizationModule.Manifest(Version),
            SettingsModule.Manifest(Version),
            Items.Manifest(ItemsModule)
        ];
        Registry = new AuthorizationRegistry(Manifests,
            AuthorizationModule.Permissions(Version).Concat(SettingsModule.Permissions(Version)).Concat(Items.Permissions),
            TenantDirectoryModule.HostOperations(Version).Concat(IdentityHostOperations.Definitions(Version)).Concat(AuthorizationModule.HostOperations(Version))
                .Concat(SettingsModule.HostOperations(Version)));
        Values = new ValueDefinitionRegistry(Manifests, [Items.ExportEnabled, Items.PageSize, Items.ClientFeature, Items.Secret]);
    }

    internal PostgreSqlPersistenceOptions Options { get; }
    internal PostgreSqlModule Directory { get; }
    internal PostgreSqlModule Identity { get; }
    internal PostgreSqlModule Authorization { get; }
    internal PostgreSqlModule Settings { get; }
    internal PostgreSqlModule ItemsModule { get; }
    internal IReadOnlyList<ModuleManifest> Manifests { get; }
    internal AuthorizationRegistry Registry { get; }
    internal ValueDefinitionRegistry Values { get; }
    internal string AuthorizationSchema => schemas[2];
    internal string ItemsSchema => schemas[4];
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    internal static async Task<AuthorizationDatabase> CreateAsync()
    {
        var target = PostgreSqlTarget.FromEnvironment();
        var session = await PostgreSqlProbe.ConnectAsync(target, Cancellation);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        var options = new PostgreSqlPersistenceOptions(System.Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!);
        var database = new AuthorizationDatabase(options, await PostgreSqlTestLock.AcquireAsync(options.ConnectionString, Cancellation));
        try
        {
            var composition = await new ModuleComposer([database.Registry, database.Values]).ComposeAsync(new(database.Manifests, Environment), Cancellation);
            Assert.Empty(composition.Diagnostics);
            var registry = new PostgreSqlModuleRegistry([database.Directory, database.Identity, database.Authorization, database.Settings, database.ItemsModule]);
            var migrated = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(composition.Plan!, cancellationToken: Cancellation);
            Assert.Empty(migrated.Diagnostics);
            Assert.Empty(await new MigrationActivationValidator(registry).ValidateAsync(composition.Plan!.Selection, Cancellation));
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    internal string Table(string schema, string name) => "\"" + schema + "\".\"" + name + "\"";

    internal async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    internal async Task<int> ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(Cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        await using var held = testLock;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        foreach (var schema in schemas.AsEnumerable().Reverse())
        {
            await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE", connection);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
    }
}
