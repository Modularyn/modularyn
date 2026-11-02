using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Features;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Persistence.EfCore;
using Modularyn.Modules.Files.Persistence.FileSystem;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.HttpApi;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>A local account with its generated password.</summary>
internal sealed record Account(string Name, Guid Id, string Password)
{
    public ActorReference Actor => new(ActorKind.User, Id.ToString("D"));
}

/// <summary>One unit of work: a request's tenant context and the principal its session validation yields, ended with its scope.</summary>
internal sealed record AdversarialUnit(AsyncServiceScope Scope, TenantContext Context, ExecutionPrincipal Principal) : IAsyncDisposable
{
    public T Get<T>() where T : notnull => Scope.ServiceProvider.GetRequiredService<T>();
    public ValueTask DisposeAsync() => Scope.DisposeAsync();
}

/// <summary>The entitlement gate of the deployment: every feature a tenant turns on is entitled. It supplies no authority.</summary>
internal sealed class EntitledGate : IFeatureGate
{
    public ValueTask<bool> AllowsAsync(TenantContext context, FeatureDefinition feature, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}

/// <summary>
/// One host process over the adversarial database, composed as a host composes these packages: SEC-02's ingress and key
/// ring, SEC-04's tenancy, identity with its session authentication, SEC-05's authorization, SEC-07's audit store, settings
/// and features, and the Files service over the filesystem provider with the allowlist release policy. Nothing is a test
/// double. A declared service identity is the deployment's standing host operator, for the fixture's own setup; a host
/// authority a user holds is a stored grant under the elevated policy. Disposing the host and composing another over the
/// same database is a restart; composing one over a recovery database is the recovery deployment.
/// </summary>
internal sealed class AdversarialHost : IAsyncDisposable
{
    internal const string Service = "svc-adversarial";
    internal const string AppHost = "app.example.test";
    internal const string HostA = "tenant-a.example.test";
    internal const string HostB = "tenant-b.example.test";
    internal const long MaximumFileLength = 64 * 1024;
    internal static readonly string[] PublicOrigins = ["https://" + AppHost, "https://" + HostA, "https://" + HostB];
    private readonly ServiceProvider provider;
    private readonly List<IAsyncDisposable> scopes = [];

    private AdversarialHost(ServiceProvider provider, AdversarialDatabase database)
    {
        this.provider = provider;
        Database = database;
    }

    internal AdversarialDatabase Database { get; }
    internal IServiceProvider Services => provider;
    internal FileService Files => Get<FileService>();
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>The deployment configuration: standing operator, freshness bound, public origins, declared proxies and key ring.</summary>
    internal static Dictionary<string, string?> Configuration(AdversarialDatabase database, IReadOnlyList<string>? proxies = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Modularyn:Authorization:FreshnessBound"] = "00:00:30",
            ["Modularyn:Authorization:HostOperators:0:Kind"] = "Service",
            ["Modularyn:Authorization:HostOperators:0:Subject"] = Service,
            ["Modularyn:DataProtection:ApplicationName"] = "Modularyn.AdversarialTests"
        };
        var index = 0;
        foreach (var operation in database.Registry.HostOperations)
            values["Modularyn:Authorization:HostOperators:0:Operations:" + index++] = operation.Operation.Id;
        for (index = 0; index < PublicOrigins.Length; index++) values["Modularyn:Ingress:PublicOrigins:" + index] = PublicOrigins[index];
        for (index = 0; index < (proxies?.Count ?? 0); index++) values["Modularyn:Ingress:Proxies:" + index] = proxies![index];
        return values;
    }

    /// <summary>The services as a host composes them.</summary>
    internal static void Compose(IServiceCollection services, IConfiguration configuration, AdversarialDatabase database,
        IEnumerable<string>? servicePermissions = null)
    {
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddModularynIngress(configuration);
        // The declared key ring that seals propagated references, shared by every host over the database and its recovery.
        services.AddModularynDataProtection(configuration, keyStore: _ => database.Keys);
        services.AddModularynTenancy(database.Directory);
        services.AddModularynAudit(database.Audit);
        services.AddModularynIdentity(configuration);
        services.AddModularynIdentityPostgreSql(database.Identity);
        services.AddModularynServiceIdentity(Service, servicePermissions ?? database.Registry.Permissions.Select(permission => permission.Name));
        services.AddModularynIdentityHttp();
        services.AddModularynAuthorization(configuration, database.Authorization, database.Registry);
        services.AddModularynSettings(database.Settings, database.Values);
        services.AddModularynFeatures();
        services.AddSingleton<IFeatureGate>(new EntitledGate());
        services.AddSingleton(provider => new FileService(new FileMetadataStore(database.Files, provider.GetRequiredService<IAuditTrail>()),
            new FileSystemBlobStore(database.Root, MaximumFileLength), provider.GetRequiredService<IPermissionEvaluator>(), provider.GetRequiredService<TimeProvider>(),
            release: new AllowlistFileReleasePolicy(["text/plain"], MaximumFileLength)));
    }

    /// <summary>A host over the database; <paramref name="servicePermissions"/> narrows the declared service identity's scope.</summary>
    internal static AdversarialHost Create(AdversarialDatabase database, IEnumerable<string>? servicePermissions = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Configuration(database)).Build();
        var services = new ServiceCollection();
        Compose(services, configuration, database, servicePermissions);
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }), database);
    }

    internal T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    /// <summary>The standing operator's host scope for the operation, granted by the elevated policy and audited first.</summary>
    internal async Task<TenantContext> HostAsync(string operationId)
    {
        var scope = Get<IServiceIdentityScopes>().Begin(Service);
        scopes.Add(scope);
        var decision = await scope.Services.GetRequiredService<TenantExecution>().EstablishServiceHostAsync(scope.Principal, operationId,
            "Adversarial fixture setup", Cancellation);
        Assert.True(decision.Succeeded, decision.Code);
        return decision.Context!;
    }

    internal async Task<Guid> TenantAsync(string name, string domain)
    {
        var host = await HostAsync(TenantDirectoryAdministration.OperationId);
        var administration = Get<TenantDirectoryAdministration>();
        var tenant = await administration.CreateTenantAsync(host, name, Cancellation);
        await administration.AddDomainAsync(host, tenant, domain, Cancellation);
        return tenant;
    }

    internal async Task<Account> AccountAsync(string name, params Guid[] tenants)
    {
        var password = "pw-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var created = await Get<IAccountService>().CreateLocalAccountAsync(IdentityAuthority.Host(await HostAsync(IdentityHostOperations.AccountsId)), name,
            new SecretText(password), Cancellation);
        Assert.Equal(AccountCreationStatus.Created, created.Status);
        var account = new Account(name, created.AccountId!.Value, password);
        foreach (var tenant in tenants)
            Assert.True(await Get<ITenantMemberships>().AddMemberAsync(IdentityAuthority.Host(await HostAsync(IdentityHostOperations.MembershipsId)), account.Id,
                tenant, Cancellation));
        return account;
    }

    /// <summary>Names the account an owner of the tenant, the root of authority there: its administrator, without host authority.</summary>
    internal async Task OwnerAsync(Account account, Guid tenant) => Assert.True(await Get<HostAuthorityAdministration>().AddTenantOwnerAsync(
        await HostAsync(AuthorizationModule.TenantOwnersOperation.Id), tenant, account.Actor, Cancellation));

    /// <summary>A stored host-authority grant under the elevated policy, made by an operator who holds the operation itself.</summary>
    internal async Task HostGrantAsync(TenantContext grantor, Account account, string operationId) => Assert.True(
        await Get<HostAuthorityAdministration>().GrantAsync(grantor, account.Actor, operationId, Cancellation));

    /// <summary>A real session for the account, and the principal its validation yields, as a request would carry it.</summary>
    internal async Task<(IssuedSession Session, ExecutionPrincipal Principal)> SignInAsync(Account account)
    {
        var result = await Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(account.Name, new SecretText(account.Password), "203.0.113.4"),
            Cancellation);
        Assert.Equal(SignInStatus.Succeeded, result.Status);
        var active = await Get<ISessionService>().ValidateAsync(result.Session!.Token, Cancellation);
        Assert.NotNull(active);
        return (result.Session, active.ToPrincipal());
    }

    /// <summary>The claims identity's session authentication yields for the account.</summary>
    internal static ClaimsPrincipal Claims(Account account) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, account.Id.ToString("D"))],
        IdentityHttpServiceCollectionExtensions.AuthenticationScheme));

    /// <summary>Resolves a unit for the account from the given request, without asserting the decision.</summary>
    internal async Task<(TenantDecision Decision, AdversarialUnit? Unit)> TryEnterAsync(Account account, Func<ClaimsPrincipal, TenantResolutionRequest> request)
    {
        var principal = (await SignInAsync(account)).Principal;
        var scope = provider.CreateAsyncScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(principal));
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(request(Claims(account)), Cancellation);
        if (decision.Succeeded) return (decision, new(scope, decision.Context!, principal));
        await scope.DisposeAsync();
        return (decision, null);
    }

    /// <summary>A unit for the account in the tenant, established through tenancy's resolution and its current membership.</summary>
    internal async Task<AdversarialUnit> EnterAsync(Account account, Guid tenant)
    {
        var (decision, unit) = await TryEnterAsync(account, claims => TenantResolutionRequest.ForMember(claims,
            [new TenantCandidate(TenantCandidateSource.Header, tenant.ToString("D"))]));
        Assert.True(decision.Succeeded, decision.Code);
        return unit!;
    }

    /// <summary>The declared service identity's own unit in the tenant, as a worker's is.</summary>
    internal async Task<AdversarialUnit> EnterServiceAsync(Guid tenant)
    {
        var service = Get<IServiceIdentityScopes>().Begin(Service);
        scopes.Add(service);
        var scope = provider.CreateAsyncScope();
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishServiceAsync(service.Principal, tenant, Cancellation);
        Assert.True(decision.Succeeded, decision.Code);
        return new(scope, decision.Context!, service.Principal);
    }

    internal async Task GrantAsync(AdversarialUnit owner, Account member, params string[] permissions)
    {
        foreach (var permission in permissions)
            Assert.True(await Get<AuthorityAdministration>().GrantPermissionAsync(owner.Context, owner.Principal, member.Actor, permission, Cancellation));
    }

    internal ValueTask<AuthorizationDecision> AuthorizeAsync(AdversarialUnit unit, string permission) =>
        Get<IPermissionEvaluator>().AuthorizeAsync(new AuthorizationRequest(unit.Context, unit.Principal, permission), Cancellation);

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes) await scope.DisposeAsync();
        await provider.DisposeAsync();
    }
}

/// <summary>
/// The adversarial cases' two tenants as the corpus prepares them: tenant-A, administered by ta-admin, with user-a holding
/// every item and file permission; tenant-B with user-b, item-b1, file-b1 and a download grant on file-b1; and host-1, who
/// holds host authority for the items report only. Every identifier of tenant-B's records is known to the test.
/// </summary>
internal sealed class AdversarialTenants
{
    internal static readonly string[] ItemPermissions =
        [AdversarialItems.Read, AdversarialItems.Write, AdversarialItems.Delete, AdversarialItems.Export, AdversarialItems.Import];
    internal static readonly string[] FilePermissionNames =
        [FilePermissions.Upload, FilePermissions.Read, FilePermissions.Delete, FilePermissions.Restore, FilePermissions.Release, FilePermissions.Grant];

    internal required Guid TenantA { get; init; }
    internal required Guid TenantB { get; init; }
    internal required Account TaAdmin { get; init; }
    internal required Account UserA { get; init; }
    internal required Account OwnerB { get; init; }
    internal required Account UserB { get; init; }
    internal required Account Host1 { get; init; }
    internal required Item ItemA1 { get; init; }
    internal required Item ItemB1 { get; init; }
    internal required FileVersionInfo FileA1 { get; init; }
    internal required FileVersionInfo FileB1 { get; init; }
    internal required string GrantB1 { get; init; }
    internal required string GrantA1 { get; init; }
    internal static byte[] ContentA1 { get; } = "tenant-a notes\n"u8.ToArray();
    internal static byte[] ContentB1 { get; } = "tenant-b confidential\n"u8.ToArray();
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    internal static async Task<AdversarialTenants> SeedAsync(AdversarialHost host)
    {
        var tenantA = await host.TenantAsync("Tenant A", AdversarialHost.HostA);
        var tenantB = await host.TenantAsync("Tenant B", AdversarialHost.HostB);
        var taAdmin = await host.AccountAsync("ta-admin", tenantA);
        var userA = await host.AccountAsync("user-a", tenantA);
        var ownerB = await host.AccountAsync("tb-admin", tenantB);
        var userB = await host.AccountAsync("user-b", tenantB);
        var host1 = await host.AccountAsync("host-1");
        await host.OwnerAsync(taAdmin, tenantA);
        await host.OwnerAsync(ownerB, tenantB);
        // host-1 holds the report under a stored grant, made by an operator who holds the report itself.
        await host.HostGrantAsync(await host.HostAsync(AuthorizationModule.HostAuthorityOperation.Id), host1, AdversarialItems.Report.Id);

        await using (var adminA = await host.EnterAsync(taAdmin, tenantA))
            await host.GrantAsync(adminA, userA, [.. ItemPermissions, .. FilePermissionNames]);
        await using (var adminB = await host.EnterAsync(ownerB, tenantB))
            await host.GrantAsync(adminB, userB, [.. ItemPermissions, .. FilePermissionNames]);

        Item itemA1, itemB1;
        FileVersionInfo fileA1, fileB1;
        string grantA1, grantB1;
        await using (var a = await host.EnterAsync(userA, tenantA))
        {
            itemA1 = await AddItemAsync(host, a, "a-1", "Tenant A's first item");
            fileA1 = await UploadAsync(host, a, ContentA1, "a1.txt");
            grantA1 = (await host.Files.IssueGrantAsync(a.Context, a.Principal, fileA1.FileId, fileA1.VersionId, FileGrantPurpose.Download, TimeSpan.FromHours(1),
                Cancellation)).Token.Reveal();
        }
        await using (var b = await host.EnterAsync(userB, tenantB))
        {
            itemB1 = await AddItemAsync(host, b, "b1-only", "Tenant B's item-b1");
            fileB1 = await UploadAsync(host, b, ContentB1, "b1.txt");
            grantB1 = (await host.Files.IssueGrantAsync(b.Context, b.Principal, fileB1.FileId, fileB1.VersionId, FileGrantPurpose.Download, TimeSpan.FromHours(1),
                Cancellation)).Token.Reveal();
        }
        return new()
        {
            TenantA = tenantA, TenantB = tenantB, TaAdmin = taAdmin, UserA = userA, OwnerB = ownerB, UserB = userB, Host1 = host1, ItemA1 = itemA1,
            ItemB1 = itemB1, FileA1 = fileA1, FileB1 = fileB1, GrantA1 = grantA1, GrantB1 = grantB1
        };
    }

    internal static async Task<Item> AddItemAsync(AdversarialHost host, AdversarialUnit unit, string code, string name)
    {
        Assert.True((await host.AuthorizeAsync(unit, AdversarialItems.Write)).Permitted);
        await using var work = await EfCoreUnitOfWork.BeginAsync(host.Database.Items, unit.Context, cancellationToken: Cancellation);
        var item = new Item { Id = Guid.NewGuid(), Code = code, Name = name };
        work.Context.Add(item);
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        return item;
    }

    internal static async Task<FileVersionInfo> UploadAsync(AdversarialHost host, AdversarialUnit unit, byte[] content, string name)
    {
        var uploaded = await host.Files.UploadAsync(unit.Context, unit.Principal, new FileUploadRequest(null, name, "documents", "project-1", content.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(content))), new MemoryStream(content), Cancellation);
        return await host.Files.ReleaseAsync(unit.Context, unit.Principal, uploaded.FileId, uploaded.VersionId, Cancellation);
    }
}
