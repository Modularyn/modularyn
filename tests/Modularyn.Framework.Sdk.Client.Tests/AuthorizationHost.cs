using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Features;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Xunit;

namespace Modularyn.Framework.Sdk.Client.Tests;

/// <summary>
/// A clock the test moves explicitly, for wall time and for the elapsed time caches measure, starting at a whole second
/// so that the times identity and authorization store are the times the test names.
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private readonly DateTimeOffset origin;
    private long elapsed;

    public TestClock()
    {
        var now = TimeProvider.System.GetUtcNow().Ticks;
        origin = new DateTimeOffset(now - now % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    public DateTimeOffset Now => GetUtcNow();
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref elapsed);
    public override DateTimeOffset GetUtcNow() => origin.AddTicks(GetTimestamp());
    public void Advance(TimeSpan by) => Interlocked.Add(ref elapsed, by.Ticks);

    public void Set(DateTimeOffset at)
    {
        if (at < Now) throw new InvalidOperationException("The test clock only moves forward.");
        Interlocked.Exchange(ref elapsed, at.UtcTicks - origin.UtcTicks);
    }
}

/// <summary>Records authority changes before they commit, as SEC-07's audit will; it can be made to fail.</summary>
internal sealed class RecordingAuthorityAudit : IAuthorityAudit
{
    internal ConcurrentQueue<AuthorityChangeRecord> Records { get; } = new();
    internal bool Failing { get; set; }

    /// <summary>Runs inside the change's transaction, after its decision and before its commit, so a test can hold it there.</summary>
    internal Func<AuthorityChangeRecord, Task>? Holding { get; set; }

    public async ValueTask RecordAsync(AuthorityChangeRecord record, CancellationToken cancellationToken)
    {
        if (Failing) throw new InvalidOperationException("The audit store is unavailable.");
        Records.Enqueue(record);
        if (Holding is { } hold) await hold(record);
    }
}

/// <summary>A local account with its generated password.</summary>
internal sealed record User(string Name, Guid Id, string Password)
{
    public ActorReference Actor => new(ActorKind.User, Id.ToString("D"));
}

/// <summary>One unit of work: a request's or a job's tenant context and principal, ended when its scope is disposed.</summary>
internal sealed record Unit(AsyncServiceScope Scope, TenantContext Context, ExecutionPrincipal Principal) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Scope.DisposeAsync();
}

/// <summary>
/// One host process over the shared database: the real tenancy runtime, identity, authorization, settings and features.
/// Tests stand in only for the request's authentication, through tenancy's claims stand-in, and for SEC-07's audit.
/// A deployment option names <see cref="Operator"/> as the standing host operator for every declared host operation;
/// disposing the host and creating another over the same database is a restart.
/// </summary>
internal sealed class AuthorizationHost : IAsyncDisposable
{
    internal const string Operator = "operator-1";
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly ServiceProvider provider;
    private readonly ConcurrentBag<AsyncServiceScope> scopes = [];

    private AuthorizationHost(ServiceProvider provider, AuthorizationDatabase database, TestClock clock, TestSeams seams, RecordingAuthorityAudit audit)
    {
        this.provider = provider;
        Database = database;
        Clock = clock;
        Seams = seams;
        Audit = audit;
    }

    public AuthorizationDatabase Database { get; }
    public TestClock Clock { get; }
    public TestSeams Seams { get; }
    public RecordingAuthorityAudit Audit { get; }
    public IServiceProvider Services => provider;
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static Dictionary<string, string?> Configuration(AuthorizationDatabase database)
    {
        var values = new Dictionary<string, string?>
        {
            ["Modularyn:Authorization:FreshnessBound"] = "00:00:30",
            ["Modularyn:Authorization:HostOperators:0:Kind"] = "User",
            ["Modularyn:Authorization:HostOperators:0:Subject"] = Operator
        };
        var index = 0;
        foreach (var operation in database.Registry.HostOperations)
            values["Modularyn:Authorization:HostOperators:0:Operations:" + index++] = operation.Operation.Id;
        return values;
    }

    public static AuthorizationHost Create(AuthorizationDatabase database, TestClock clock, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Configuration(database)).Build();
        var seams = new TestSeams();
        var audit = new RecordingAuthorityAudit();
        var services = new ServiceCollection();
        Compose(services, configuration, database, clock, seams, audit);
        configure?.Invoke(services);
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }), database, clock, seams, audit);
    }

    /// <summary>The services as a host composes them, with the actor stand-in only when no session authentication supplies the actor.</summary>
    public static void Compose(IServiceCollection services, IConfiguration configuration, AuthorizationDatabase database, TestClock clock, TestSeams seams,
        RecordingAuthorityAudit audit, bool claimsActors = true)
    {
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddModularynTenancy(database.Directory);
        seams.Register(services, identity: claimsActors, membership: false, authority: false, audit: true);
        services.AddSingleton<IAuthorityAudit>(audit);
        services.AddModularynIdentity(configuration);
        services.AddModularynIdentityPostgreSql(database.Identity);
        services.AddModularynAuthorization(configuration, database.Authorization, database.Registry);
        services.AddModularynSettings(database.Settings, database.Values);
        services.AddModularynFeatures();
    }

    public T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    /// <summary>The standing operator's host scope for the operation, granted by the real elevated policy and audited first.</summary>
    public async Task<TenantContext> HostAsync(string operationId)
    {
        var scope = provider.CreateAsyncScope();
        scopes.Add(scope);
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
            TenantResolutionRequest.ForHostOperation(TestIdentity.User(Operator), operationId, "Authorization test fixture"), Cancellation);
        Assert.True(decision.Succeeded, decision.Code);
        return decision.Context!;
    }

    public async Task<Guid> TenantAsync(string name)
    {
        var host = await HostAsync(TenantDirectoryAdministration.OperationId);
        return await Get<TenantDirectoryAdministration>().CreateTenantAsync(host, name, Cancellation);
    }

    public async Task<User> UserAsync(string name, params Guid[] tenants)
    {
        var password = "pw-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var created = await Get<IAccountService>().CreateLocalAccountAsync(IdentityAuthority.Host(await HostAsync(IdentityHostOperations.AccountsId)),
            name, new SecretText(password), Cancellation);
        Assert.Equal(AccountCreationStatus.Created, created.Status);
        var user = new User(name, created.AccountId!.Value, password);
        foreach (var tenant in tenants) await AddMemberAsync(user, tenant);
        return user;
    }

    public async Task AddMemberAsync(User user, Guid tenant) => Assert.True(await Get<ITenantMemberships>().AddMemberAsync(
        IdentityAuthority.Host(await HostAsync(IdentityHostOperations.MembershipsId)), user.Id, tenant, Cancellation));

    public async Task RemoveMemberAsync(User user, Guid tenant) => Assert.True(await Get<ITenantMemberships>().RemoveMemberAsync(
        IdentityAuthority.Host(await HostAsync(IdentityHostOperations.MembershipsId)), user.Id, tenant, Cancellation));

    /// <summary>Names the user an owner of the tenant, the root of authority there, under the tenant-owners host operation.</summary>
    public async Task OwnerAsync(User user, Guid tenant) => Assert.True(await Get<HostAuthorityAdministration>().AddTenantOwnerAsync(
        await HostAsync(AuthorizationModule.TenantOwnersOperation.Id), tenant, user.Actor, Cancellation));

    /// <summary>A real session for the user, and the principal its validation yields, as a request would carry it.</summary>
    public async Task<(IssuedSession Session, ExecutionPrincipal Principal)> SignInAsync(User user, string source = "203.0.113.1")
    {
        var result = await Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(user.Name, new SecretText(user.Password), source), Cancellation);
        Assert.Equal(SignInStatus.Succeeded, result.Status);
        var active = await Get<ISessionService>().ValidateAsync(result.Session!.Token, Cancellation);
        return (result.Session, active!.ToPrincipal());
    }

    /// <summary>A unit of work for the user in the tenant, established through tenancy's resolution and the user's current membership.</summary>
    public async Task<Unit> EnterAsync(User user, Guid tenant, ExecutionPrincipal? principal = null)
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
        return new Unit(scope, decision.Context!, principal);
    }

    public ValueTask<AuthorizationDecision> AuthorizeAsync(Unit unit, string permission, ResourceReference? resource = null, bool current = false) =>
        Get<IPermissionEvaluator>().AuthorizeAsync(new AuthorizationRequest(unit.Context, unit.Principal, permission) { Resource = resource, RequireCurrent = current },
            Cancellation);

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes) await scope.DisposeAsync();
        await provider.DisposeAsync();
    }
}
