using Modularyn.Framework.Authorization;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.BackgroundJobs.Domain;
using Modularyn.Modules.BackgroundJobs.Persistence.EfCore;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Events.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DurablePostgreSqlCollection
{
    public const string Name = "PostgreSQL durable work";
}

internal sealed class DurableDatabase : IAsyncDisposable
{
    private readonly PostgreSqlTestLock testLock;
    private readonly string[] schemas;
    private DurableDatabase(PostgreSqlPersistenceOptions options, PostgreSqlTestLock testLock)
    {
        Options = options;
        this.testLock = testLock;
        var suffix = Guid.NewGuid().ToString("N");
        schemas = ["dw_directory_" + suffix, "dw_identity_" + suffix, "dw_authority_" + suffix,
            "dw_producer_" + suffix, "dw_consumer_" + suffix, "dw_jobs_" + suffix];
        Directory = TenantDirectoryModule.Create(options, schemas[0]);
        Identity = IdentityPersistence.CreateModule(options, schemas[1]);
        Authority = AuthorizationModule.Create(options, schemas[2]);
        Producer = PostgreSqlModule.Create(DurableModel.ProducerId, schemas[3], options, module => new DurableProducerContext(module));
        Consumer = PostgreSqlModule.Create(DurableModel.ConsumerId, schemas[4], options, module => new DurableConsumerContext(module));
        Jobs = BackgroundJobsPersistence.Create(options, schemas[5]);
        var version = DurableModel.Version;
        Manifests =
        [
            TenantDirectoryModule.Manifest(version),
            new ModuleManifest(IdentityPersistence.ModuleId, version,
                definitions: IdentityHostOperations.Definitions(version).Select(entry => entry.Declaration).Append(IdentityRetentionJob.Permission(version).Declaration),
                migrations: IdentityPersistence.Migrations),
            AuthorizationModule.Manifest(version),
            new ModuleManifest(DurableModel.ProducerId, version,
                definitions: DurableModel.Permissions.Select(entry => entry.Declaration).Append(DurableModel.DispatchDefinition.Declaration),
                migrations: [new(DurableProducerInitial.Id, version)]),
            new ModuleManifest(DurableModel.ConsumerId, version, migrations: [new(DurableConsumerInitial.Id, version)]),
            BackgroundJobsDefinition.Manifest(version)
        ];
        Registry = new AuthorizationRegistry(Manifests,
            AuthorizationModule.Permissions(version).Concat(DurableModel.Permissions).Concat(BackgroundJobsDefinition.Permissions(version))
                .Append(IdentityRetentionJob.Permission(version)),
            TenantDirectoryModule.HostOperations(version).Concat(IdentityHostOperations.Definitions(version)).Concat(AuthorizationModule.HostOperations(version))
                .Append(DurableModel.DispatchDefinition).Concat(BackgroundJobsDefinition.HostOperations(version)));
    }
    internal PostgreSqlPersistenceOptions Options { get; }
    internal PostgreSqlModule Directory { get; }
    internal PostgreSqlModule Identity { get; }
    internal PostgreSqlModule Authority { get; }
    internal PostgreSqlModule Producer { get; }
    internal PostgreSqlModule Consumer { get; }
    internal PostgreSqlModule Jobs { get; }
    internal IReadOnlyList<ModuleManifest> Manifests { get; }
    internal AuthorizationRegistry Registry { get; }
    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<DurableDatabase> CreateAsync()
    {
        var target = PostgreSqlTarget.FromEnvironment();
        var session = await PostgreSqlProbe.ConnectAsync(target, Token);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        var options = new PostgreSqlPersistenceOptions(Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!);
        var database = new DurableDatabase(options, await PostgreSqlTestLock.AcquireAsync(options.ConnectionString, Token));
        try
        {
            var composed = await new ModuleComposer([database.Registry]).ComposeAsync(new(database.Manifests,
                new CompositionEnvironment(SemanticVersion.Parse("10.0.14"), DurableModel.Version)), Token);
            Assert.Empty(composed.Diagnostics);
            var registry = new PostgreSqlModuleRegistry([database.Directory, database.Identity, database.Authority, database.Producer, database.Consumer, database.Jobs]);
            var migrated = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(composed.Plan!, cancellationToken: Token);
            Assert.Empty(migrated.Diagnostics);
            Assert.Empty(await new MigrationActivationValidator(registry).ValidateAsync(composed.Plan!.Selection, Token));
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    internal async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(Token);
    }

    internal async Task TerminateSessionAsync(int processId)
    {
        // The fixture terminates only its own role's named session; no elevated database role is needed.
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
        command.Parameters.AddWithValue("pid", processId);
        Assert.True(await command.ExecuteScalarAsync(Token) is true);
    }

    public async ValueTask DisposeAsync()
    {
        await using var held = testLock;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(timeout.Token);
        foreach (var schema in schemas.Reverse())
        {
            await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE", connection);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
    }
}
