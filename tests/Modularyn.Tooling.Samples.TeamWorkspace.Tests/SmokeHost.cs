using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.HttpApi;
using Modularyn.Repository.Tests;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;
using Npgsql;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

internal sealed class SmokeClock : TimeProvider
{
    private long ticks = TimeProvider.System.GetUtcNow().UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan value) => Interlocked.Add(ref ticks, value.Ticks);
}

internal sealed class SmokeUser(Guid id, string name, string password)
{
    internal Guid Id { get; } = id;
    internal string Name { get; } = name;
    internal string Password { get; } = password;
    public override string ToString() => nameof(SmokeUser);
}

internal sealed class SmokeHost : IAsyncDisposable
{
    private readonly PostgreSqlTestLock databaseLock;
    private readonly string connectionString;
    private readonly string directory;
    private readonly string seedService = "seed-" + Guid.NewGuid().ToString("N");
    private readonly List<IAsyncDisposable> scopes = [];
    private X509Certificate2? certificate;
    private WebApplication? app;
    private SmokeHost(string connectionString, PostgreSqlTestLock databaseLock, string directory)
    {
        this.connectionString = connectionString;
        this.databaseLock = databaseLock;
        this.directory = directory;
    }
    internal SmokeClock Clock { get; } = new();
    internal CapturedLogs Logs { get; } = new();
    internal Uri Origin { get; private set; } = null!;
    internal IServiceProvider Services => app!.Services;
    internal WorkspaceComposition Composition { get; private set; } = null!;
    internal string BlobDirectory => Path.Combine(directory, "blobs");
    internal string LifecycleDirectory => Path.Combine(directory, "lifecycle");
    internal ModuleHost Modules => Services.GetRequiredService<ModuleHost>();
    internal int ServerVersionNumber { get; private set; }
    internal IReadOnlyList<LockedFile> ImplementationFiles { get; private set; } = [];

    internal static async Task<SmokeHost> StartAsync(CancellationToken token, bool developmentSeed = true)
    {
        var host = await CreateAsync(token, developmentSeed: developmentSeed);
        try { await host.StartApplicationAsync(token); return host; }
        catch { await host.DisposeAsync(); throw; }
    }

    // A lifecycle fixture supplies its full selection before startup. Migration still runs separately over the
    // unchanged sample selection and its lock, and every activation validator stays registered on the final host.
    internal static async Task<SmokeHost> CreateAsync(CancellationToken token,
        IReadOnlyList<ModuleManifest>? selection = null, Action<WebApplicationBuilder>? configure = null, bool developmentSeed = false)
    {
        var session = await PostgreSqlProbe.ConnectAsync(PostgreSqlTarget.FromEnvironment(), token);
        Check.That(session.ServerVersionNumber is >= 180000 and < 190000 && session.Parameters.GetValueOrDefault("is_superuser") == "off", "database-role-and-version");
        var connection = Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!;
        var held = await PostgreSqlTestLock.AcquireAsync(connection, token);
        var host = new SmokeHost(connection, held, RunDirectory.NewPath()) { ServerVersionNumber = session.ServerVersionNumber };
        try { await host.InitializeAsync(token, selection, configure, developmentSeed); return host; }
        catch { await host.DisposeAsync(); throw; }
    }

    private async Task InitializeAsync(CancellationToken token, IReadOnlyList<ModuleManifest>? selection,
        Action<WebApplicationBuilder>? configure, bool developmentSeed)
    {
        RunDirectory.Create(directory);
        var keys = Path.Combine(directory, "keys");
        var secrets = Path.Combine(directory, "secrets");
        var blobs = Path.Combine(directory, "blobs");
        KeyRingDirectory.Create(keys);
        KeyRingDirectory.Create(secrets);
        KeyRingDirectory.Create(blobs);
        KeyRingDirectory.Create(LifecycleDirectory);
        await File.WriteAllTextAsync(Path.Combine(secrets, "database"), connectionString, token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(secrets, "database"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Composition = new(new PostgreSqlPersistenceOptions(connectionString), "tw_" + Guid.NewGuid().ToString("N")[..24] + "_");
        var deploymentLock = Path.Combine(directory, "deployment-lock.json");
        var described = DeploymentArtifacts.Describe();
        ImplementationFiles = described.Modules.First().Files;
        await File.WriteAllTextAsync(deploymentLock, described.ToJson(), token);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Modularyn", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var signed = request.CreateSelfSigned(Clock.GetUtcNow().AddDays(-1), Clock.GetUtcNow().AddDays(2));
        certificate = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pkcs12), null);
        var lease = new TcpListener(IPAddress.Loopback, 0);
        lease.Start();
        var port = ((IPEndPoint)lease.LocalEndpoint).Port;
        lease.Stop();
        Origin = new Uri("https://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var config = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Testing", ["Workspace:DevelopmentSeed"] = developmentSeed ? "true" : "false",
            ["Workspace:Database"] = "secret:database", ["Workspace:SchemaPrefix"] = Composition.Workspace.Schema[..^"workspace".Length],
            ["Workspace:BlobDirectory"] = blobs, ["Workspace:DeploymentLock"] = deploymentLock,
            ["Modularyn:Secrets:Directory"] = secrets,
            ["Modularyn:DataProtection:ApplicationName"] = "Modularyn.TeamWorkspace.Smoke",
            ["Modularyn:DataProtection:Directory"] = keys,
            ["Modularyn:Ingress:PublicOrigins:0"] = Origin.GetLeftPart(UriPartial.Authority),
            ["Modularyn:Ingress:HttpsPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Modularyn:Ingress:Limits:MaxRequestBodyBytes"] = TeamWorkspaceHost.MaximumAttachmentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Modularyn:Authorization:HostOperators:0:Kind"] = "Service",
            ["Modularyn:Authorization:HostOperators:0:Subject"] = seedService,
            ["Modularyn:Authorization:HostOperators:1:Kind"] = "Service",
            ["Modularyn:Authorization:HostOperators:1:Subject"] = WorkspaceDefinition.Worker,
            ["Modularyn:Authorization:HostOperators:1:Operations:0"] = WorkspaceDefinition.Dispatch.Id
        };
        var operations = new[] { TenantDirectoryAdministration.OperationId, IdentityHostOperations.AccountsId,
            IdentityHostOperations.MembershipsId, AuthorizationModule.TenantOwnersOperation.Id, AuthorizationModule.HostAuthorityOperation.Id };
        for (var index = 0; index < operations.Length; index++) config["Modularyn:Authorization:HostOperators:0:Operations:" + index] = operations[index];
        void Configure(WebApplicationBuilder builder)
        {
            builder.Services.AddSingleton<TimeProvider>(Clock);
            // Test deployment authority exists only in this isolated fixture, with a generated identity and real audited scopes.
            builder.Services.AddModularynServiceIdentity(seedService, [WorkspaceDefinition.Read]);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Logs);
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port, listener => listener.UseHttps(certificate!)));
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        app = await TeamWorkspaceHost.BuildAsync(configuration, Configure, token);
        Composition = Services.GetRequiredService<WorkspaceComposition>();
        await TeamWorkspaceHost.MigrateAsync(app, token);
        if (selection is not null)
        {
            await app.DisposeAsync();
            app = null;
            await File.WriteAllTextAsync(deploymentLock,
                new DeploymentLock(selection.Select(manifest => LockedModule.Describe(manifest, ImplementationFiles))).ToJson(), token);
            app = await TeamWorkspaceHost.BuildAsync(configuration, builder => { Configure(builder); configure?.Invoke(builder); }, token);
            Composition = Services.GetRequiredService<WorkspaceComposition>();
        }
    }

    internal Task StartApplicationAsync(CancellationToken token) => app!.StartAsync(token);
    internal Task StopApplicationAsync(CancellationToken token) => app!.StopAsync(token);

    internal HttpClient Client()
    {
        var expected = certificate!.GetCertHashString();
        return new(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer is not null && peer.GetCertHashString() == expected
        }) { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(40) };
    }

    private async Task<TenantContext> HostScopeAsync(string operation, CancellationToken token)
    {
        var scope = Services.GetRequiredService<IServiceIdentityScopes>().Begin(seedService);
        scopes.Add(scope);
        var decision = await scope.Services.GetRequiredService<TenantExecution>().EstablishServiceHostAsync(scope.Principal, operation, "Create isolated smoke data", token);
        Check.That(decision.Succeeded, "seed-host-scope");
        return decision.Context!;
    }

    /// <summary>Stores host authority for a user, granted in the fixture service's audited host scope.</summary>
    internal async Task GrantHostAuthorityAsync(Guid user, IEnumerable<string> operations, CancellationToken token)
    {
        var host = await HostScopeAsync(AuthorizationModule.HostAuthorityOperation.Id, token);
        foreach (var operation in operations)
            Check.That(await Services.GetRequiredService<HostAuthorityAdministration>().GrantAsync(host, new(ActorKind.User, user.ToString("D")), operation, token),
                "grant-host-authority");
    }

    internal async Task<Guid> TenantAsync(CancellationToken token) => await Services.GetRequiredService<TenantDirectoryAdministration>()
        .CreateTenantAsync(await HostScopeAsync(TenantDirectoryAdministration.OperationId, token), "Smoke workspace", token);

    internal async Task<SmokeUser> UserAsync(Guid tenant, bool owner, CancellationToken token)
    {
        var name = "member-" + Guid.NewGuid().ToString("N");
        var password = "pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var created = await Services.GetRequiredService<IAccountService>().CreateLocalAccountAsync(
            IdentityAuthority.Host(await HostScopeAsync(IdentityHostOperations.AccountsId, token)), name, new SecretText(password), token);
        Check.That(created.Status == AccountCreationStatus.Created, "create-account");
        var user = new SmokeUser(created.AccountId!.Value, name, password);
        Check.That(await Services.GetRequiredService<ITenantMemberships>().AddMemberAsync(
            IdentityAuthority.Host(await HostScopeAsync(IdentityHostOperations.MembershipsId, token)), user.Id, tenant, token), "create-membership");
        if (owner) Check.That(await Services.GetRequiredService<HostAuthorityAdministration>().AddTenantOwnerAsync(
            await HostScopeAsync(AuthorizationModule.TenantOwnersOperation.Id, token), tenant, new(ActorKind.User, user.Id.ToString("D")), token), "create-tenant-owner");
        return user;
    }

    internal async Task<AsyncServiceScope> EnterAsync(SmokeUser user, Guid tenant, CancellationToken token)
    {
        var signIn = await Services.GetRequiredService<ISignInService>().SignInWithPasswordAsync(new(user.Name, new SecretText(user.Password), ClientSources.Unknown), token);
        Check.That(signIn.Status == SignInStatus.Succeeded, "step-session");
        var session = await Services.GetRequiredService<ISessionService>().ValidateAsync(signIn.Session!.Token, token);
        var scope = Services.CreateAsyncScope();
        try
        {
            Check.That(scope.ServiceProvider.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(session!.ToPrincipal()), "step-principal");
            var claims = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D"))],
                IdentityHttpServiceCollectionExtensions.AuthenticationScheme));
            var resolution = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(TenantResolutionRequest.ForMember(claims,
                [new(TenantCandidateSource.Header, tenant.ToString("D"))]), token);
            Check.That(resolution.Succeeded, "step-tenant");
            return scope;
        }
        catch { await scope.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var scope in scopes) await scope.DisposeAsync();
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            certificate?.Dispose();
            if (Composition is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(timeout.Token);
                foreach (var module in Composition.Modules.Modules.Reverse())
                {
                    await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + module.Schema + "\" CASCADE", connection);
                    await command.ExecuteNonQueryAsync(timeout.Token);
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            finally { await databaseLock.DisposeAsync(); }
        }
    }
}

/// <summary>
/// The fixture's run directory, which holds the generated secret file with the database password, the key ring and the
/// BLOBs. It lives in the system's temporary directory, never in the repository, and is created owner-only.
/// </summary>
internal static class RunDirectory
{
    /// <summary>A new path in the temporary directory, its links resolved, since the BLOB store refuses links in its path.</summary>
    internal static string NewPath()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(path)!;
        foreach (var segment in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var info = new DirectoryInfo(Path.Combine(current, segment));
            current = info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(true)!.FullName;
        }
        return Path.Combine(current, "modularyn-team-workspace-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>Creates it owner-only: mode 0700 on Unix, and on Windows an access list protected to the current account.</summary>
    internal static void Create(string path) => KeyRingDirectory.Create(path);
}

/// <summary>One log entry as the host's logger factory passed it on, after its filters: level, category and text with any exception.</summary>
internal sealed record CapturedLog(LogLevel Level, string Category, string Text);

internal sealed class CapturedLogs : ILoggerProvider
{
    internal ConcurrentQueue<CapturedLog> Entries { get; } = new();
    internal IEnumerable<string> Lines => Entries.Select(entry => entry.Text);
    public ILogger CreateLogger(string categoryName) => new Capture(Entries, categoryName);
    public void Dispose() { }
    private sealed class Capture(ConcurrentQueue<CapturedLog> entries, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new(logLevel, category, formatter(state, exception) + (exception is null ? "" : "\n" + exception)));
    }
}
