using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Files.Persistence.EfCore;
using Modularyn.Modules.Files.Persistence.FileSystem;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Modularyn.Repository.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

// The migration lock and the shared test lock cover the database, so real-provider fixtures run one at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FilesPostgreSqlCollection
{
    public const string Name = "PostgreSQL files application";
}

/// <summary>
/// A clock the test moves explicitly, starting at a whole second, for wall time and for the elapsed time caches measure.
/// Timers stay on real time, so waits and upload deadlines elapse as they would in a host.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private readonly DateTimeOffset origin;
    private long elapsed;

    public TestClock()
    {
        var now = DateTime.UtcNow.Ticks;
        origin = new DateTimeOffset(now - now % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    public DateTimeOffset Now => GetUtcNow();
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref elapsed);
    public override DateTimeOffset GetUtcNow() => origin.AddTicks(GetTimestamp());
    public void Advance(TimeSpan by) => Interlocked.Add(ref elapsed, by.Ticks);
}

/// <summary>Records authority changes before they commit, as SEC-07's audit will.</summary>
internal sealed class RecordingAuthorityAudit : IAuthorityAudit
{
    internal ConcurrentQueue<AuthorityChangeRecord> Records { get; } = new();

    public ValueTask RecordAsync(AuthorityChangeRecord record, CancellationToken cancellationToken)
    {
        Records.Enqueue(record);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A local account with its generated password.</summary>
internal sealed record User(string Name, Guid Id, string Password)
{
    public ActorReference Actor => new(ActorKind.User, Id.ToString("D"));
}

/// <summary>One unit of work: a request's tenant context and principal, ended when its scope is disposed.</summary>
internal sealed record Member(AsyncServiceScope Scope, TenantContext Context, ExecutionPrincipal Principal, User User) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Scope.DisposeAsync();
}

/// <summary>A tenant with its owner, the root of authority there, and the owner's open unit.</summary>
internal sealed record Tenant(Guid Id, User Owner, Member OwnerUnit);

/// <summary>
/// One host over real providers: the tenant directory, identity, SEC-05's authorization and the Files metadata in fresh
/// schemas of the database that <c>MODULARYN_TEST_POSTGRESQL</c> names, composed, migrated through CODE-03's coordinator
/// and gated by its activation validator, and the filesystem provider over an isolated root. Only the request's
/// authentication, through tenancy's claims stand-in, and SEC-07's audit seams are test doubles.
/// </summary>
internal sealed class FilesHost : IAsyncDisposable
{
    internal const string Operator = "operator-1";
    internal const string MemberRole = "file-members";
    internal static readonly SemanticVersion Version = SemanticVersion.Parse("1.0.0");
    private static readonly CompositionEnvironment Environment = new(SemanticVersion.Parse("10.0.14"), Version);
    private readonly PostgreSqlTestLock testLock;
    private readonly string[] schemas;
    private readonly List<string> roots = [];
    private readonly ConcurrentBag<AsyncServiceScope> scopes = [];
    private ServiceProvider provider = null!;

    private FilesHost(PostgreSqlPersistenceOptions options, PostgreSqlTestLock testLock)
    {
        Options = options;
        this.testLock = testLock;
        var suffix = Guid.NewGuid().ToString("N");
        schemas = ["fa_directory_" + suffix, "fa_identity_" + suffix, "fa_authorization_" + suffix, "fa_files_" + suffix];
        Directory = TenantDirectoryModule.Create(options, schemas[0]);
        Identity = IdentityPersistence.CreateModule(options, schemas[1]);
        Authorization = AuthorizationModule.Create(options, schemas[2]);
        FilesModule = FilesMetadataModule.Create(options, schemas[3]);
        Manifests =
        [
            TenantDirectoryModule.Manifest(Version),
            new ModuleManifest(IdentityPersistence.ModuleId, Version, definitions: IdentityHostOperations.Definitions(Version).Select(definition => definition.Declaration),
                migrations: IdentityPersistence.Migrations),
            AuthorizationModule.Manifest(Version),
            FilesMetadataModule.Manifest(Version, FilePermissions.Definitions(Version).Select(definition => definition.Declaration))
        ];
        Registry = new AuthorizationRegistry(Manifests, AuthorizationModule.Permissions(Version).Concat(FilePermissions.Definitions(Version)),
            TenantDirectoryModule.HostOperations(Version).Concat(IdentityHostOperations.Definitions(Version)).Concat(AuthorizationModule.HostOperations(Version)));
        Root = NewRoot();
    }

    internal PostgreSqlPersistenceOptions Options { get; }
    internal PostgreSqlModule Directory { get; }
    internal PostgreSqlModule Identity { get; }
    internal PostgreSqlModule Authorization { get; }
    internal PostgreSqlModule FilesModule { get; }
    internal IReadOnlyList<ModuleManifest> Manifests { get; }
    internal AuthorizationRegistry Registry { get; }
    internal string Root { get; }
    internal TestClock Clock { get; } = new();
    internal RecordingAuthorityAudit Audit { get; } = new();
    internal IServiceProvider Services => provider;
    internal IPermissionEvaluator Evaluator => Get<IPermissionEvaluator>();
    internal FileMetadataStore Metadata => new(FilesModule);
    internal static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    internal static async Task<FilesHost> CreateAsync()
    {
        var session = await PostgreSqlProbe.ConnectAsync(PostgreSqlTarget.FromEnvironment(), Cancellation);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        var options = new PostgreSqlPersistenceOptions(System.Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!);
        var host = new FilesHost(options, await PostgreSqlTestLock.AcquireAsync(options.ConnectionString, Cancellation));
        try
        {
            var composition = await new ModuleComposer([host.Registry]).ComposeAsync(new(host.Manifests, Environment), Cancellation);
            Assert.Empty(composition.Diagnostics);
            var registry = new PostgreSqlModuleRegistry([host.Directory, host.Identity, host.Authorization, host.FilesModule]);
            var migrated = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(composition.Plan!, cancellationToken: Cancellation);
            Assert.Empty(migrated.Diagnostics);
            Assert.Empty(await new MigrationActivationValidator(registry).ValidateAsync(composition.Plan!.Selection, Cancellation));
            host.provider = host.Compose();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private ServiceProvider Compose()
    {
        var values = new Dictionary<string, string?>
        {
            ["Modularyn:Authorization:FreshnessBound"] = "00:00:30",
            ["Modularyn:Authorization:HostOperators:0:Kind"] = "User",
            ["Modularyn:Authorization:HostOperators:0:Subject"] = Operator
        };
        var index = 0;
        foreach (var operation in Registry.HostOperations) values["Modularyn:Authorization:HostOperators:0:Operations:" + index++] = operation.Operation.Id;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddModularynTenancy(Directory);
        new TestSeams().Register(services, identity: true, membership: false, authority: false, audit: true);
        services.AddSingleton<IAuthorityAudit>(Audit);
        services.AddModularynIdentity(configuration);
        services.AddModularynIdentityPostgreSql(Identity);
        services.AddModularynServiceIdentity(FileWorker.Service, [FilePermissions.Read, FilePermissions.Release]);
        services.AddModularynAuthorization(configuration, Authorization, Registry);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    internal T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    /// <summary>The Files service as a deployment composes it, over this host's evaluator and a store with the given limits.</summary>
    internal FileService Files(FilesPolicy? policy = null, IFileReleasePolicy? release = null, IFileRecoverySchedule? recovery = null,
        IFileStorageObserver? observer = null, long maximumLength = 1024 * 1024, int readSlots = FileSystemBlobStore.DefaultReadSlots, string? root = null) =>
        new(Metadata, new FileSystemBlobStore(root ?? Root, maximumLength, observer, readSlots), Evaluator, Clock, policy, release, recovery, observer);

    /// <summary>The standing operator's host scope for the operation, granted by the real elevated policy and audited first.</summary>
    internal async Task<TenantContext> HostAsync(string operationId)
    {
        var scope = provider.CreateAsyncScope();
        scopes.Add(scope);
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
            TenantResolutionRequest.ForHostOperation(TestIdentity.User(Operator), operationId, "Files test fixture"), Cancellation);
        Assert.True(decision.Succeeded, decision.Code);
        return decision.Context!;
    }

    /// <summary>A new tenant whose owner holds every declared permission, and defines the role that file members receive.</summary>
    internal async Task<Tenant> TenantAsync(string name)
    {
        var id = await Get<TenantDirectoryAdministration>().CreateTenantAsync(await HostAsync(TenantDirectoryAdministration.OperationId), name, Cancellation);
        var owner = await UserAsync("owner-" + id.ToString("N")[..8], id);
        Assert.True(await Get<HostAuthorityAdministration>().AddTenantOwnerAsync(await HostAsync(AuthorizationModule.TenantOwnersOperation.Id), id, owner.Actor,
            Cancellation));
        var unit = await EnterAsync(owner, id);
        Assert.True(await Get<AuthorityAdministration>().DefineRoleAsync(unit.Context, unit.Principal, MemberRole,
            [FilePermissions.Upload, FilePermissions.Read, FilePermissions.Delete, FilePermissions.Restore, FilePermissions.Grant,
                AuthorizationModule.SharePermission], [], Cancellation));
        return new(id, owner, unit);
    }

    internal async Task<User> UserAsync(string name, params Guid[] tenants)
    {
        var password = "pw-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var created = await Get<IAccountService>().CreateLocalAccountAsync(IdentityAuthority.Host(await HostAsync(IdentityHostOperations.AccountsId)),
            name, new SecretText(password), Cancellation);
        Assert.Equal(AccountCreationStatus.Created, created.Status);
        var user = new User(name, created.AccountId!.Value, password);
        foreach (var tenant in tenants) await AddMemberAsync(user, tenant);
        return user;
    }

    /// <summary>A member of the tenant who holds the file members' role, or exactly the named permissions when they are given.</summary>
    internal async Task<User> MemberAsync(Tenant tenant, string name, params string[] permissions)
    {
        var user = await UserAsync(name, tenant.Id);
        var administration = Get<AuthorityAdministration>();
        if (permissions.Length == 0)
            Assert.True(await administration.AssignRoleAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, MemberRole, Cancellation));
        foreach (var permission in permissions)
            Assert.True(await administration.GrantPermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, permission, Cancellation));
        return user;
    }

    /// <summary>Grants the member one more permission directly, from the tenant's owner.</summary>
    internal async Task GrantAsync(Tenant tenant, User user, string permission) => Assert.True(await Get<AuthorityAdministration>().GrantPermissionAsync(
        tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, permission, Cancellation));

    internal async Task AddMemberAsync(User user, Guid tenant) => Assert.True(await Get<ITenantMemberships>().AddMemberAsync(
        IdentityAuthority.Host(await HostAsync(IdentityHostOperations.MembershipsId)), user.Id, tenant, Cancellation));

    internal async Task RemoveMemberAsync(User user, Guid tenant) => Assert.True(await Get<ITenantMemberships>().RemoveMemberAsync(
        IdentityAuthority.Host(await HostAsync(IdentityHostOperations.MembershipsId)), user.Id, tenant, Cancellation));

    /// <summary>A real session for the user, and the principal its validation yields, as a request would carry it.</summary>
    internal async Task<(IssuedSession Session, ExecutionPrincipal Principal)> SignInAsync(User user)
    {
        var result = await Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(user.Name, new SecretText(user.Password), "203.0.113.1"),
            Cancellation);
        Assert.Equal(SignInStatus.Succeeded, result.Status);
        var active = await Get<ISessionService>().ValidateAsync(result.Session!.Token, Cancellation);
        return (result.Session, active!.ToPrincipal());
    }

    /// <summary>A unit of work for the user in the tenant, established through tenancy's resolution and the user's current membership.</summary>
    internal async Task<Member> EnterAsync(User user, Guid tenant, ExecutionPrincipal? principal = null)
    {
        principal ??= (await SignInAsync(user)).Principal;
        var scope = provider.CreateAsyncScope();
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
            TenantResolutionRequest.ForMember(TestIdentity.User(user.Id.ToString("D")), [Candidates.Header(tenant)]), Cancellation);
        if (!decision.Succeeded)
        {
            await scope.DisposeAsync();
            Assert.Fail("The unit was refused with " + decision.Code);
        }
        scopes.Add(scope);
        return new(scope, decision.Context!, principal, user);
    }

    internal async Task ShareAsync(Member owner, User with, Guid fileId, string permission = FilePermissions.Read) =>
        Assert.True(await Get<AuthorityAdministration>().GrantResourceAsync(owner.Context, owner.Principal, with.Actor,
            FilePermissions.Resource(owner.Context.TenantId!.Value, fileId, owner.User.Actor), permission, null, Cancellation));

    internal async Task UnshareAsync(Member owner, User with, Guid fileId, string permission = FilePermissions.Read) =>
        Assert.True(await Get<AuthorityAdministration>().RevokeResourceAsync(owner.Context, owner.Principal, with.Actor,
            FilePermissions.Resource(owner.Context.TenantId!.Value, fileId, owner.User.Actor), permission, Cancellation));

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
        System.IO.Directory.CreateDirectory(root);
        roots.Add(root);
        return root;
    }

    internal string Content(Guid tenant, Guid version, string extension = ".blob") =>
        Path.Combine(Root, tenant.ToString("N"), version.ToString("N") + extension);

    /// <summary>The tenant's content and staging files, without the store's own lease and read slots.</summary>
    internal string[] Objects(Guid tenant) => System.IO.Directory.Exists(Path.Combine(Root, tenant.ToString("N")))
        ? System.IO.Directory.GetFiles(Path.Combine(Root, tenant.ToString("N"))).Where(path => !Path.GetFileName(path).StartsWith('.')).ToArray() : [];

    internal async Task<long> CountAsync(string table, Guid tenant) => await ScalarAsync<long>(
        "SELECT COUNT(*) FROM \"" + schemas[3] + "\".\"" + table + "\" WHERE \"TenantId\" = @tenant", new NpgsqlParameter("tenant", tenant));

    internal async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync(Cancellation))!;
    }

    /// <summary>Every text and byte value the Files schema holds, to show what is and is not stored.</summary>
    internal async Task<List<object>> StoredValuesAsync()
    {
        var values = new List<object>();
        await using var connection = new NpgsqlConnection(Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        foreach (var table in new[] { "Files", "FileRevisions", "FileUploads", "FileGrants" })
        {
            await using var command = new NpgsqlCommand("SELECT * FROM \"" + schemas[3] + "\".\"" + table + "\"", connection);
            await using var reader = await command.ExecuteReaderAsync(Cancellation);
            while (await reader.ReadAsync(Cancellation))
                for (var column = 0; column < reader.FieldCount; column++)
                    if (!await reader.IsDBNullAsync(column, Cancellation)) values.Add(reader.GetValue(column));
        }
        return values;
    }

    internal static string Digest(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
    internal static byte[] Bytes(int count, int seed = 0) => Enumerable.Range(0, count).Select(index => (byte)((index + seed) % 251)).ToArray();

    /// <summary>Plain text of exactly the given length, which the default policy accepts.</summary>
    internal static byte[] Text(int count, char letter = 'a') => Enumerable.Range(0, count).Select(index => (byte)(index % 64 == 63 ? '\n' : letter)).ToArray();

    internal static FileUploadRequest Request(byte[] content, Guid? fileId = null, string name = "notes.txt", string container = "documents") =>
        new(fileId, name, container, "project-1", content.LongLength, Digest(content));

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
            foreach (var scope in scopes) await scope.DisposeAsync();
            if (provider is not null) await provider.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var connection = new NpgsqlConnection(Options.ConnectionString);
            await connection.OpenAsync(timeout.Token);
            foreach (var schema in schemas.AsEnumerable().Reverse())
            {
                await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE", connection);
                await command.ExecuteNonQueryAsync(timeout.Token);
            }
        }
        finally
        {
            foreach (var root in roots) if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>A release policy the test configures: a fixed verdict, a failure, and a record of what it saw.</summary>
internal sealed class TestReleasePolicy(FileReleaseVerdict verdict = FileReleaseVerdict.Release) : IFileReleasePolicy
{
    internal FileReleaseVerdict Verdict { get; set; } = verdict;
    internal bool Failing { get; set; }
    internal List<(FileReleaseCandidate Candidate, byte[] Content)> Seen { get; } = [];

    public async ValueTask<FileReleaseVerdict> DecideAsync(FileReleaseCandidate candidate, Stream content, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        Seen.Add((candidate, copy.ToArray()));
        if (Failing) throw new InvalidOperationException("The scanner is unavailable.");
        return Verdict;
    }
}

/// <summary>The declared service identity that runs file work for its producer, within its scope.</summary>
internal static class FileWorker
{
    internal const string Service = "svc-files";
}

/// <summary>
/// A stream that delivers its bytes up to a gate and waits there until the test opens it or cancels the upload, so an
/// upload can be held while it streams.
/// </summary>
internal sealed class PausedStream(byte[] content, int pauseAt) : MemoryStream(content)
{
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Reached => reached.Task;
    internal void Resume() => resume.TrySetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position >= pauseAt)
        {
            reached.TrySetResult();
            await resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
        return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, pauseAt - Position)], cancellationToken);
    }
}

/// <summary>Once armed, holds the next upload that reaches the stage there until its cancellation, as a slow finalization would.</summary>
internal sealed class PauseAt(FileStorageStage stage) : IFileStorageObserver
{
    private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int armed;

    internal Guid VersionId { get; private set; }
    internal Task Reached => reached.Task;
    internal void Arm() => Interlocked.Exchange(ref armed, 1);

    public async ValueTask ReachedAsync(FileStorageStage reachedStage, Guid revisionId, CancellationToken cancellationToken)
    {
        if (reachedStage != stage || Interlocked.Exchange(ref armed, 0) == 0) return;
        VersionId = revisionId;
        reached.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
