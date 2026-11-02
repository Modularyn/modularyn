using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Modularyn.Framework.Audit;
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

/// <summary>
/// A declared key ring's store held in memory, shared by every host a test builds over one database, so references that
/// one host sealed open in the host that replaces it, as they do across a real restart over a persisted ring.
/// </summary>
internal sealed class MemoryKeyStore : IXmlRepository
{
    private readonly List<XElement> elements = [];

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (elements) return elements.Select(element => new XElement(element)).ToArray();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (elements) elements.Add(new XElement(element));
    }
}

internal sealed class DurableDatabase : IAsyncDisposable
{
    /// <summary>The key ring every host over this database seals and opens propagated references with.</summary>
    internal MemoryKeyStore Keys { get; } = new();

    private readonly PostgreSqlTestLock testLock;
    private readonly string[] schemas;
    private DurableDatabase(PostgreSqlPersistenceOptions options, PostgreSqlTestLock testLock)
    {
        Options = options;
        this.testLock = testLock;
        var suffix = Guid.NewGuid().ToString("N");
        schemas = ["dw_directory_" + suffix, "dw_identity_" + suffix, "dw_authority_" + suffix,
            "dw_producer_" + suffix, "dw_consumer_" + suffix, "dw_jobs_" + suffix, "dw_audit_" + suffix];
        Directory = TenantDirectoryModule.Create(options, schemas[0]);
        Identity = IdentityPersistence.CreateModule(options, schemas[1]);
        Authority = AuthorizationModule.Create(options, schemas[2]);
        Producer = PostgreSqlModule.Create(DurableModel.ProducerId, schemas[3], options, module => new DurableProducerContext(module));
        Consumer = PostgreSqlModule.Create(DurableModel.ConsumerId, schemas[4], options, module => new DurableConsumerContext(module));
        Jobs = BackgroundJobsPersistence.Create(options, schemas[5]);
        Audit = AuditModule.Create(options, schemas[6]);
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
            BackgroundJobsDefinition.Manifest(version),
            AuditModule.Manifest(version)
        ];
        Registry = new AuthorizationRegistry(Manifests,
            AuthorizationModule.Permissions(version).Concat(DurableModel.Permissions).Concat(BackgroundJobsDefinition.Permissions(version))
                .Append(IdentityRetentionJob.Permission(version)).Concat(AuditModule.Permissions(version)),
            TenantDirectoryModule.HostOperations(version).Concat(IdentityHostOperations.Definitions(version)).Concat(AuthorizationModule.HostOperations(version))
                .Append(DurableModel.DispatchDefinition).Concat(BackgroundJobsDefinition.HostOperations(version)).Concat(AuditModule.HostOperations(version)));
    }
    internal PostgreSqlPersistenceOptions Options { get; }
    internal PostgreSqlModule Directory { get; }
    internal PostgreSqlModule Identity { get; }
    internal PostgreSqlModule Authority { get; }
    internal PostgreSqlModule Producer { get; }
    internal PostgreSqlModule Consumer { get; }
    internal PostgreSqlModule Jobs { get; }
    internal PostgreSqlModule Audit { get; }
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
            var registry = new PostgreSqlModuleRegistry([database.Directory, database.Identity, database.Authority, database.Producer, database.Consumer, database.Jobs,
                database.Audit]);
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

    /// <summary>The audit records the store holds, read directly, as a test may, oldest first.</summary>
    internal async Task<AuditRow[]> AuditAsync(string? action = null)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT \"Id\", \"At\", \"Action\", \"Outcome\", \"Tenant\", \"ActorKind\", \"Actor\", \"Subject\", "
            + "\"ResourceType\", \"Resource\", \"Detail\", \"Unit\", \"Operation\", \"Reason\", \"Correlation\" FROM \"" + schemas[6]
            + "\".\"AuditRecords\" WHERE @action IS NULL OR \"Action\" = @action ORDER BY \"At\", \"Id\"", connection);
        command.Parameters.Add(new NpgsqlParameter("action", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)action ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(Token);
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync(Token))
            rows.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetInt32(5), reader.GetString(6), Text(reader, 7), Text(reader, 8), Text(reader, 9),
                Text(reader, 10), reader.IsDBNull(11) ? null : reader.GetGuid(11), Text(reader, 12), Text(reader, 13), Text(reader, 14)));
        return rows.ToArray();
        static string? Text(NpgsqlDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
    }

    internal string DirectorySchema => schemas[0];
    internal string IdentitySchema => schemas[1];
    internal string AuditSchema => schemas[6];
    internal string AuthoritySchema => schemas[2];
    internal string ProducerSchema => schemas[3];

    internal async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync(Token))!;
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

/// <summary>One audit record as the database holds it.</summary>
internal sealed record AuditRow(Guid Id, DateTimeOffset At, string Action, int Outcome, Guid? Tenant, int ActorKind, string Actor, string? Subject,
    string? ResourceType, string? Resource, string? Detail, Guid? Unit, string? Operation, string? Reason, string? Correlation);
