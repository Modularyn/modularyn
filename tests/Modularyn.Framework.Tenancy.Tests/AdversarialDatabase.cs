using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Persistence.EfCore;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// The items module as the adversarial cases declare it: the permissions its endpoints require, a host report that reaches
/// its items and nothing else, and the values its bootstrap configuration exposes. The tables are this project's own items
/// model, with a per-tenant unique code and a note that references an item through the tenant.
/// </summary>
internal static class AdversarialItems
{
    public const string ModuleId = "Items";
    public const string Read = "items.read";
    public const string Write = "items.write";
    public const string Delete = "items.delete";
    public const string Export = "items.export";
    public const string Import = "items.import";
    public static readonly SemanticVersion Version = SemanticVersion.Parse("1.0.0");

    public static IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(ModuleId, Read, Version), new(ModuleId, Write, Version), new(ModuleId, Delete, Version),
        new(ModuleId, Export, Version, PermissionFreshness.Current), new(ModuleId, Import, Version, PermissionFreshness.Current)
    ];

    /// <summary>A cross-tenant report over the items table and nothing else.</summary>
    public static HostOperation Report { get; } = new("test.items.report", [new HostReach(ModuleId, "Items")]);

    /// <summary>The work dispatcher's operation: it reads queued work across tenants and records a disposition on it.</summary>
    public static HostOperation Dispatch { get; } = new("test.items.dispatch", [new HostReach(ModuleId, "WorkItems")]);

    public static IReadOnlyList<HostOperationDefinition> HostOperations { get; } = [new(ModuleId, Version, Report), new(ModuleId, Version, Dispatch)];

    /// <summary>A client-visible text a tenant or a user may override, a hidden one, and a client-visible feature.</summary>
    public static SettingDefinition Banner { get; } = new(ModuleId, "items.banner", Version, ValueKind.Text, "standard", TimeSpan.FromMinutes(5),
        clientVisible: true);
    public static SettingDefinition Hidden { get; } = new(ModuleId, "items.hidden", Version, ValueKind.Text, "internal", TimeSpan.FromMinutes(5));
    public static FeatureDefinition Preview { get; } = new(ModuleId, "items.preview", Version, false, TimeSpan.FromMinutes(5), clientVisible: true);
}

/// <summary>
/// One real-provider fixture for the adversarial cases: the tenant directory, identity, authorization, the audit store,
/// settings, the Files metadata and the items module, each in a fresh schema of the database that
/// <c>MODULARYN_TEST_POSTGRESQL</c> names, composed, migrated through CODE-03's coordinator and gated by its activation
/// validator, with the filesystem provider over an isolated root. <see cref="RecoveryAsync"/> migrates a second, empty set of
/// the same schemas under the same lock, as an isolated recovery deployment of the same release. Disposal drops only these
/// schemas and roots.
/// </summary>
internal sealed class AdversarialDatabase : IAsyncDisposable
{
    internal static readonly CompositionEnvironment Environment = new(SemanticVersion.Parse("10.0.14"), AdversarialItems.Version);
    private static readonly string[] Names = ["directory", "identity", "authorization", "audit", "settings", "files", "items"];
    private readonly PostgreSqlTestLock? testLock;
    private readonly List<AdversarialDatabase> recoveries = [];
    private readonly List<string> roots = [];

    private AdversarialDatabase(PostgreSqlPersistenceOptions options, PostgreSqlTestLock? testLock, Events.Tests.MemoryKeyStore keys)
    {
        Options = options;
        this.testLock = testLock;
        Keys = keys;
        var suffix = Guid.NewGuid().ToString("N");
        Schemas = Names.ToDictionary(name => name, name => "ad_" + name + "_" + suffix, StringComparer.Ordinal);
        Directory = TenantDirectoryModule.Create(options, Schemas["directory"]);
        Identity = IdentityPersistence.CreateModule(options, Schemas["identity"]);
        Authorization = AuthorizationModule.Create(options, Schemas["authorization"]);
        Audit = AuditModule.Create(options, Schemas["audit"]);
        Settings = SettingsModule.Create(options, Schemas["settings"]);
        Files = FilesMetadataModule.Create(options, Schemas["files"]);
        Items = PostgreSqlModule.Create(AdversarialItems.ModuleId, Schemas["items"], options, module => new ItemsContext(module));
        var version = AdversarialItems.Version;
        Manifests =
        [
            TenantDirectoryModule.Manifest(version),
            new ModuleManifest(IdentityPersistence.ModuleId, version, definitions: IdentityHostOperations.Definitions(version).Select(entry => entry.Declaration),
                migrations: IdentityPersistence.Migrations),
            AuthorizationModule.Manifest(version),
            AuditModule.Manifest(version),
            SettingsModule.Manifest(version),
            FilesMetadataModule.Manifest(version, FilePermissions.Definitions(version).Select(entry => entry.Declaration)),
            ItemsManifest()
        ];
        Registry = new AuthorizationRegistry(Manifests,
            AuthorizationModule.Permissions(version).Concat(SettingsModule.Permissions(version)).Concat(AuditModule.Permissions(version))
                .Concat(FilePermissions.Definitions(version)).Concat(AdversarialItems.Permissions),
            TenantDirectoryModule.HostOperations(version).Concat(IdentityHostOperations.Definitions(version)).Concat(AuthorizationModule.HostOperations(version))
                .Concat(SettingsModule.HostOperations(version)).Concat(AuditModule.HostOperations(version)).Concat(AdversarialItems.HostOperations));
        Values = new ValueDefinitionRegistry(Manifests, [AdversarialItems.Banner, AdversarialItems.Hidden, AdversarialItems.Preview]);
        Root = NewRoot();
    }

    internal PostgreSqlPersistenceOptions Options { get; }
    internal IReadOnlyDictionary<string, string> Schemas { get; }
    internal PostgreSqlModule Directory { get; }
    internal PostgreSqlModule Identity { get; }
    internal PostgreSqlModule Authorization { get; }
    internal PostgreSqlModule Audit { get; }
    internal PostgreSqlModule Settings { get; }
    internal PostgreSqlModule Files { get; }
    internal PostgreSqlModule Items { get; }
    internal IReadOnlyList<ModuleManifest> Manifests { get; }
    internal AuthorizationRegistry Registry { get; }
    internal ValueDefinitionRegistry Values { get; }

    /// <summary>The declared key ring every host over this database seals and opens propagated references with.</summary>
    internal Events.Tests.MemoryKeyStore Keys { get; }

    /// <summary>The filesystem provider's root for this deployment's file content.</summary>
    internal string Root { get; }

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    internal static async Task<AdversarialDatabase> CreateAsync()
    {
        var session = await PostgreSqlProbe.ConnectAsync(PostgreSqlTarget.FromEnvironment(), Cancellation);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        var options = new PostgreSqlPersistenceOptions(System.Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!);
        var database = new AdversarialDatabase(options, await PostgreSqlTestLock.AcquireAsync(options.ConnectionString, Cancellation), new());
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

    /// <summary>
    /// An isolated recovery deployment of the same release: every schema migrated afresh and empty, a new file root, and the
    /// same declared key ring, which the deployment restores from its separate backup. Disposed with this database.
    /// </summary>
    internal async Task<AdversarialDatabase> RecoveryAsync()
    {
        var recovery = new AdversarialDatabase(Options, null, Keys);
        recoveries.Add(recovery);
        await recovery.MigrateAsync();
        return recovery;
    }

    private async Task MigrateAsync()
    {
        var composition = await new ModuleComposer([Registry, Values]).ComposeAsync(new(Manifests, Environment), Cancellation);
        Assert.Empty(composition.Diagnostics);
        var registry = new PostgreSqlModuleRegistry([Directory, Identity, Authorization, Audit, Settings, Files, Items]);
        var migrated = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(composition.Plan!, cancellationToken: Cancellation);
        Assert.Empty(migrated.Diagnostics);
        Assert.Empty(await new MigrationActivationValidator(registry).ValidateAsync(composition.Plan!.Selection, Cancellation));
    }

    private ModuleManifest ItemsManifest()
    {
        using var context = Items.CreateContext();
        return new(AdversarialItems.ModuleId, AdversarialItems.Version,
            definitions: AdversarialItems.Permissions.Select(entry => entry.Declaration).Concat(AdversarialItems.HostOperations.Select(entry => entry.Declaration))
                .Append(AdversarialItems.Banner.Declaration).Append(AdversarialItems.Hidden.Declaration).Append(AdversarialItems.Preview.Declaration),
            migrations: context.GetService<IMigrationsAssembly>().Migrations.Keys.Select(id => new ModuleAsset(id, AdversarialItems.Version)));
    }

    internal string Table(string module, string table) => "\"" + Schemas[module] + "\".\"" + table + "\"";

    internal string NewRoot()
    {
        // Resolve the platform's temporary-directory aliases before configuring a provider that refuses links.
        var path = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(path)!;
        foreach (var segment in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var info = new DirectoryInfo(Path.Combine(current, segment));
            current = info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(true)!.FullName;
        }
        var root = Path.Combine(current, "modularyn-adversarial-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        roots.Add(root);
        return root;
    }

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

    /// <summary>Rows read directly from PostgreSQL, bypassing the adapter, as an independent inventory of one table.</summary>
    internal async Task<List<string[]>> RowsAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(Cancellation))
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(column => reader.IsDBNull(column) ? "" : Convert.ToString(reader.GetValue(column),
                System.Globalization.CultureInfo.InvariantCulture)!).ToArray());
        return rows;
    }

    /// <summary>The audit store's records, read directly, oldest first.</summary>
    internal async Task<List<AuditedRow>> AuditAsync()
    {
        var rows = new List<AuditedRow>();
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT \"Action\", \"Actor\", \"Tenant\", \"Operation\", \"Unit\", \"ResourceType\" FROM " + Table("audit", "AuditRecords")
            + " ORDER BY \"At\", \"Id\"", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        while (await reader.ReadAsync(Cancellation))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    /// <summary>
    /// Everything this deployment holds for one tenant, read directly rather than through the adapter: every row of every table
    /// with a tenant column, in every schema, as JSON, and every content file under the tenant's directory of the file root with
    /// its SHA-256. Audit records, which name a tenant without a tenant column, are not part of it.
    /// </summary>
    internal async Task<List<string>> TenantInventoryAsync(Guid tenant)
    {
        var inventory = new List<string>();
        foreach (var table in await RowsAsync("SELECT table_schema, table_name FROM information_schema.columns WHERE column_name = 'TenantId' "
            + "AND table_schema = ANY(@schemas) ORDER BY 1, 2", new NpgsqlParameter("schemas", Schemas.Values.ToArray())))
            foreach (var row in await RowsAsync("SELECT row_to_json(t)::text FROM \"" + table[0] + "\".\"" + table[1] + "\" t WHERE \"TenantId\" = @tenant ORDER BY 1",
                new NpgsqlParameter("tenant", tenant)))
                inventory.Add(table[1] + ":" + row[0]);
        var directory = Path.Combine(Root, tenant.ToString("N"));
        if (System.IO.Directory.Exists(directory))
            foreach (var file in System.IO.Directory.EnumerateFiles(directory).Where(path => !Path.GetFileName(path).StartsWith('.')).Order(StringComparer.Ordinal))
                inventory.Add("file:" + Path.GetFileName(file) + ":" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
        return inventory;
    }

    /// <summary>
    /// A coordinated backup restored into <paramref name="target"/>, an isolated recovery deployment of the same release: every
    /// row of every table of this deployment's schemas, with its stored values, copied in one transaction with each table after
    /// the tables it references, and the file root copied byte for byte. The schemas' own records, such as their migration
    /// history, are the target's, which the same release migrated.
    /// </summary>
    internal async Task RestoreIntoAsync(AdversarialDatabase target)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var transaction = await connection.BeginTransactionAsync(Cancellation);
        foreach (var module in Names)
        {
            var tables = new List<string>();
            var references = new List<(string From, string To)>();
            await using (var list = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = @schema AND table_type = 'BASE TABLE' "
                + "AND left(table_name, 2) <> '__' ORDER BY table_name", connection, transaction))
            {
                list.Parameters.AddWithValue("schema", Schemas[module]);
                await using var reader = await list.ExecuteReaderAsync(Cancellation);
                while (await reader.ReadAsync(Cancellation)) tables.Add(reader.GetString(0));
            }
            await using (var list = new NpgsqlCommand("SELECT source.relname, target.relname FROM pg_constraint c JOIN pg_class source ON source.oid = c.conrelid "
                + "JOIN pg_class target ON target.oid = c.confrelid JOIN pg_namespace n ON n.oid = source.relnamespace WHERE c.contype = 'f' AND n.nspname = @schema",
                connection, transaction))
            {
                list.Parameters.AddWithValue("schema", Schemas[module]);
                await using var reader = await list.ExecuteReaderAsync(Cancellation);
                while (await reader.ReadAsync(Cancellation)) references.Add((reader.GetString(0), reader.GetString(1)));
            }
            var order = new List<string>();
            while (order.Count < tables.Count)
                order.Add(tables.First(table => !order.Contains(table)
                    && references.All(reference => reference.From != table || reference.To == table || order.Contains(reference.To))));
            // The restore replaces what the recovery deployment holds, such as rows its migrations seeded, with the backup's.
            foreach (var table in Enumerable.Reverse(order))
            {
                await using var clear = new NpgsqlCommand("DELETE FROM \"" + target.Schemas[module] + "\".\"" + table + "\"", connection, transaction);
                await clear.ExecuteNonQueryAsync(Cancellation);
            }
            foreach (var table in order)
            {
                await using var copy = new NpgsqlCommand("INSERT INTO \"" + target.Schemas[module] + "\".\"" + table + "\" OVERRIDING SYSTEM VALUE SELECT * FROM \""
                    + Schemas[module] + "\".\"" + table + "\"", connection, transaction);
                await copy.ExecuteNonQueryAsync(Cancellation);
            }
        }
        await transaction.CommitAsync(Cancellation);
        foreach (var file in System.IO.Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target.Root, Path.GetRelativePath(Root, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var connection = new NpgsqlConnection(Options.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            foreach (var database in recoveries.Append(this))
                foreach (var schema in database.Schemas.Values.Reverse())
                {
                    await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE", connection);
                    await command.ExecuteNonQueryAsync(timeout.Token);
                }
        }
        finally
        {
            foreach (var root in recoveries.SelectMany(database => database.roots).Concat(roots))
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
            if (testLock is not null) await testLock.DisposeAsync();
        }
    }
}

/// <summary>One audit record as the store holds it: its action, actor, tenant, operation, unit and resource type.</summary>
internal sealed record AuditedRow(string Action, string Actor, Guid? Tenant, string? Operation, Guid? Unit, string? ResourceType);
