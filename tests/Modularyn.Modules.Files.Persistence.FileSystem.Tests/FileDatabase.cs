using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Files.Persistence.EfCore;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Files.Persistence.FileSystem.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FilesPostgreSqlCollection
{
    public const string Name = "PostgreSQL files";
}

/// <summary>Actual PostgreSQL metadata, tenancy directory and membership rows, plus an isolated filesystem root.</summary>
internal sealed class FileDatabase : IAsyncDisposable
{
    private readonly PostgreSqlTestLock testLock;
    private readonly List<string> schemas = [];
    private readonly List<string> roots = [];
    internal static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private FileDatabase(PostgreSqlPersistenceOptions options, PostgreSqlTestLock testLock)
    {
        Options = options;
        this.testLock = testLock;
        var suffix = Guid.NewGuid().ToString("N");
        DirectoryModule = TenantDirectoryModule.Create(options, "ft_directory_" + suffix);
        FilesModule = FilesMetadataModule.Create(options, "ft_files_" + suffix);
        IdentitySchema = "ft_identity_" + suffix;
        schemas.AddRange([IdentitySchema, DirectoryModule.Schema, FilesModule.Schema]);
        Root = NewRoot();
    }

    internal PostgreSqlPersistenceOptions Options { get; }
    internal PostgreSqlModule DirectoryModule { get; }
    internal PostgreSqlModule FilesModule { get; }
    internal string IdentitySchema { get; }
    internal string Root { get; }
    internal Guid TenantA { get; private set; }
    internal Guid TenantB { get; private set; }
    internal ServiceProvider Services { get; private set; } = null!;
    internal FileMetadataStore Metadata => new(FilesModule);
    internal TimeProvider Clock { get; } = new PrecisionClock();

    internal static async Task<FileDatabase> CreateAsync()
    {
        var probe = await PostgreSqlProbe.ConnectAsync(PostgreSqlTarget.FromEnvironment(), Cancellation);
        Assert.InRange(probe.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", probe.Parameters.GetValueOrDefault("is_superuser"));
        var options = new PostgreSqlPersistenceOptions(Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!);
        var database = new FileDatabase(options, await PostgreSqlTestLock.AcquireAsync(options.ConnectionString, Cancellation));
        try
        {
            await MigrateAsync([database.DirectoryModule, database.FilesModule],
                [TenantDirectoryModule.Manifest(SemanticVersion.Parse("1.0.0")), FilesMetadataModule.Manifest(SemanticVersion.Parse("1.0.0"))]);
            await database.ExecuteAsync("CREATE SCHEMA " + Quote(database.IdentitySchema));
            await database.ExecuteAsync("CREATE TABLE " + Quote(database.IdentitySchema) + ".\"Members\" (\"TenantId\" uuid NOT NULL, \"Subject\" text NOT NULL, PRIMARY KEY (\"TenantId\", \"Subject\"))");
            await database.ExecuteAsync("CREATE TABLE " + Quote(database.IdentitySchema)
                + ".\"HostGrants\" (\"Subject\" text NOT NULL, \"Operation\" text NOT NULL, PRIMARY KEY (\"Subject\", \"Operation\"))");
            await database.ExecuteAsync("CREATE TABLE " + Quote(database.IdentitySchema)
                + ".\"HostAudit\" (\"UnitId\" uuid PRIMARY KEY, \"Subject\" text NOT NULL, \"Operation\" text NOT NULL, \"At\" timestamp(6) with time zone NOT NULL)");
            await database.ExecuteAsync("INSERT INTO " + Quote(database.IdentitySchema) + ".\"HostGrants\" VALUES ('fixture-host', @operation)",
                new NpgsqlParameter("operation", TenantDirectoryAdministration.OperationId));
            var services = new ServiceCollection();
            services.AddModularynTenancy(database.DirectoryModule);
            services.AddSingleton<ITenantActorResolver>(new FixtureActor());
            services.AddSingleton<ITenantMembership>(new DatabaseMembership(database));
            var hostBoundary = new DatabaseHostBoundary(database);
            services.AddSingleton<IHostAuthority>(hostBoundary);
            services.AddSingleton<IHostScopeAudit>(hostBoundary);
            database.Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            // The real directory is provisioned through its administration service in an explicit granted host scope.
            // The database-backed fixture seams are not an implementation or qualification of security policy.
            await using (var scope = database.Services.CreateAsyncScope())
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "fixture-host")], "storage-fixture"));
                var host = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
                    TenantResolutionRequest.ForHostOperation(principal, TenantDirectoryAdministration.OperationId,
                        "Create isolated storage fixtures."), Cancellation);
                Assert.True(host.Succeeded, host.Code);
                var administration = database.Services.GetRequiredService<TenantDirectoryAdministration>();
                database.TenantA = await administration.CreateTenantAsync(host.Context!, "Storage tenant A", Cancellation);
                database.TenantB = await administration.CreateTenantAsync(host.Context!, "Storage tenant B", Cancellation);
            }
            foreach (var tenant in new[] { database.TenantA, database.TenantB })
                await database.ExecuteAsync("INSERT INTO " + Quote(database.IdentitySchema) + ".\"Members\" VALUES (@id, 'file-owner')",
                    new NpgsqlParameter("id", tenant));
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    internal FileService Service(IFileStorageObserver? observer = null, long maximumLength = 1024 * 1024) =>
        new(Metadata, new FileSystemBlobStore(Root, maximumLength, observer), Clock, observer);

    internal async Task<TenantLease> EnterAsync(Guid tenant)
    {
        var scope = Services.CreateAsyncScope();
        try
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "file-owner")], "storage-fixture"));
            var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
                TenantResolutionRequest.ForMember(principal, [new(TenantCandidateSource.Header, TenantIdentifier.Format(tenant))]), Cancellation);
            Assert.True(decision.Succeeded, decision.Code);
            return new(scope, decision.Context!);
        }
        catch { await scope.DisposeAsync(); throw; }
    }

    internal async Task<PostgreSqlModule> NewRecoveryModuleAsync()
    {
        var module = FilesMetadataModule.Create(Options, "ft_restored_" + Guid.NewGuid().ToString("N"));
        schemas.Add(module.Schema);
        await MigrateAsync([module], [FilesMetadataModule.Manifest(SemanticVersion.Parse("1.0.0"))]);
        return module;
    }

    private static async Task MigrateAsync(PostgreSqlModule[] modules, ModuleManifest[] manifests)
    {
        var composition = await new ModuleComposer().ComposeAsync(new(manifests,
            new(SemanticVersion.Parse("10.0.14"), SemanticVersion.Parse("1.0.0"))), Cancellation);
        Assert.Empty(composition.Diagnostics);
        var registry = new PostgreSqlModuleRegistry(modules);
        var result = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(composition.Plan!, cancellationToken: Cancellation);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(await new MigrationActivationValidator(registry).ValidateAsync(composition.Plan!.Selection, Cancellation));
    }

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
        var root = Path.Combine(current, "modularyn-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        roots.Add(root);
        return root;
    }

    internal string Blob(Guid tenant, Guid version) => Path.Combine(Root, tenant.ToString("N"), version.ToString("N") + ".blob");
    internal string[] Objects(Guid tenant) => Directory.Exists(Path.Combine(Root, tenant.ToString("N")))
        ? Directory.GetFiles(Path.Combine(Root, tenant.ToString("N"))).Where(path => Path.GetFileName(path) != ".lease").ToArray() : [];

    internal async Task<long> CountAsync(string table, Guid tenant) => await ScalarAsync<long>(
        "SELECT COUNT(*) FROM " + Quote(FilesModule.Schema) + "." + Quote(table) + " WHERE \"TenantId\" = @tenant", new NpgsqlParameter("tenant", tenant));

    internal Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters) => ExecuteAsync(Cancellation, sql, parameters);

    private async Task ExecuteAsync(CancellationToken cancellationToken, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters) => ScalarAsync<T>(Cancellation, sql, parameters);

    private async Task<T> ScalarAsync<T>(CancellationToken cancellationToken, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    internal static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    internal static string Digest(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
    internal static byte[] Bytes(int count, int seed = 0) => Enumerable.Range(0, count).Select(index => (byte)((index + seed) % 251)).ToArray();
    internal static FileUploadRequest Request(byte[] content, Guid? fileId = null, string name = "document.bin") =>
        new(fileId, name, "documents", "file-owner", content.LongLength, Digest(content));

    internal static async Task<byte[]> ReadAllAsync(FileReadResult read)
    {
        await using (read)
        {
            using var output = new MemoryStream();
            await read.Content.CopyToAsync(output, Cancellation);
            return output.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var held = testLock;
        try
        {
            if (Services is not null) await Services.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var connection = new NpgsqlConnection(Options.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            foreach (var schema in schemas.AsEnumerable().Reverse())
            {
                await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS " + Quote(schema) + " CASCADE", connection);
                await command.ExecuteNonQueryAsync(timeout.Token);
            }
        }
        finally
        {
            foreach (var root in roots) if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixtureActor : ITenantActorResolver
    {
        public ActorReference? Resolve(ClaimsPrincipal principal) => principal.Identity?.IsAuthenticated == true
            && principal.FindFirst("sub")?.Value is { } subject ? new(ActorKind.User, subject) : null;
    }

    private sealed class DatabaseMembership(FileDatabase database) : ITenantMembership
    {
        public async ValueTask<bool> IsMemberAsync(ActorReference actor, Guid tenantId, CancellationToken cancellationToken) =>
            await database.ScalarAsync<bool>(cancellationToken, "SELECT EXISTS (SELECT 1 FROM " + Quote(database.IdentitySchema)
                + ".\"Members\" WHERE \"TenantId\" = @tenant AND \"Subject\" = @subject)",
                new NpgsqlParameter("tenant", tenantId), new NpgsqlParameter("subject", actor.Subject));
    }

    private sealed class DatabaseHostBoundary(FileDatabase database) : IHostAuthority, IHostScopeAudit
    {
        // The fixture grants only directory administration, whose reach is the directory module's own declaration.
        public async ValueTask<HostOperation?> AuthorizeAsync(ActorReference actor, string operationId, CancellationToken cancellationToken) =>
            operationId == TenantDirectoryAdministration.OperationId && await database.ScalarAsync<bool>(cancellationToken, "SELECT EXISTS (SELECT 1 FROM "
                + Quote(database.IdentitySchema) + ".\"HostGrants\" WHERE \"Subject\" = @subject AND \"Operation\" = @operation)",
                new NpgsqlParameter("subject", actor.Subject), new NpgsqlParameter("operation", operationId))
                ? TenantDirectoryAdministration.Operation : null;

        public async ValueTask RecordAsync(HostScopeAuditRecord record, CancellationToken cancellationToken) =>
            await database.ExecuteAsync(cancellationToken, "INSERT INTO " + Quote(database.IdentitySchema)
                + ".\"HostAudit\" VALUES (@unit, @subject, @operation, @at)", new NpgsqlParameter("unit", record.UnitId),
                new NpgsqlParameter("subject", record.Actor.Subject), new NpgsqlParameter("operation", record.OperationId),
                new NpgsqlParameter("at", FileTime.Normalize(record.At)));
    }

    private sealed class PrecisionClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.Zero).AddTicks(1234567);
    }
}

internal sealed class TenantLease(AsyncServiceScope scope, TenantContext context) : IAsyncDisposable
{
    internal TenantContext Context { get; } = context;
    public ValueTask DisposeAsync() => scope.DisposeAsync();
}
