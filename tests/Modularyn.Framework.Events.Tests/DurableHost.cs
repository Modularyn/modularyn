using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.HttpApi;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Xunit;

namespace Modularyn.Framework.Events.Tests;

internal sealed class DurableClock : TimeProvider
{
    private long ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks + 7;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public DateTimeOffset Now => UtcMicroseconds.Now(this);
    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    public void Set(DateTimeOffset at) => Interlocked.Exchange(ref ticks, at.UtcTicks);
}

// Audit is an observation seam for the future audit store, not evidence of its durability or policy.
internal sealed class DurableAudit : IHostScopeAudit, IAuthorityAudit
{
    internal ConcurrentQueue<HostScopeAuditRecord> Hosts { get; } = new();
    public ValueTask RecordAsync(HostScopeAuditRecord record, CancellationToken cancellationToken)
    {
        Hosts.Enqueue(record);
        return ValueTask.CompletedTask;
    }
    public ValueTask RecordAsync(AuthorityChangeRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed record DurableUser(Guid Id, string Name, string Password)
{
    public ActorReference Actor => new(ActorKind.User, Id.ToString("D"));
}
internal sealed record DurableUnit(AsyncServiceScope Scope, TenantContext Context, ExecutionPrincipal Principal) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Scope.DisposeAsync();
}

internal sealed class DurableHost : IAsyncDisposable
{
    internal const string Service = "svc-durable";
    private readonly ServiceProvider provider;
    private readonly List<IAsyncDisposable> scopes = [];
    private DurableHost(ServiceProvider provider, DurableDatabase database, DurableClock clock, DurableAudit audit)
    {
        this.provider = provider;
        Database = database;
        Clock = clock;
        Audit = audit;
    }
    internal DurableDatabase Database { get; }
    internal DurableClock Clock { get; }
    internal DurableAudit Audit { get; }
    internal IServiceProvider Services => provider;
    internal static CancellationToken Token => DurableDatabase.Token;
    internal T Get<T>() where T : notnull => provider.GetRequiredService<T>();
    internal EventStore Outbox => new(Database.Producer, Clock);
    internal EventStore Inbox => new(Database.Consumer, Clock);
    internal JobStore JobStore => new(Database.Producer, Clock, new JobPolicy());

    internal static DurableHost Create(DurableDatabase database, DurableClock clock, IEnumerable<string>? servicePermissions = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Modularyn:Authorization:HostOperators:0:Kind"] = "Service",
            ["Modularyn:Authorization:HostOperators:0:Subject"] = Service,
            ["Modularyn:Ingress:PublicOrigins:0"] = "https://durable.example.test"
        };
        var index = 0;
        foreach (var operation in database.Registry.HostOperations)
            values["Modularyn:Authorization:HostOperators:0:Operations:" + index++] = operation.Operation.Id;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddModularynIngress(configuration);
        services.AddModularynTenancy(database.Directory);
        var audit = new DurableAudit();
        services.AddSingleton<IHostScopeAudit>(audit);
        services.AddSingleton<IAuthorityAudit>(audit);
        services.AddModularynIdentity(configuration);
        services.AddModularynIdentityPostgreSql(database.Identity);
        services.AddModularynServiceIdentity(Service, servicePermissions ?? database.Registry.Permissions.Select(permission => permission.Name));
        services.AddModularynIdentityHttp();
        services.AddModularynAuthorization(configuration, database.Authority, database.Registry);
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }), database, clock, audit);
    }

    internal async Task<TenantContext> HostAsync(string? operation = null)
    {
        var scope = Get<IServiceIdentityScopes>().Begin(Service);
        scopes.Add(scope);
        var decision = await scope.Services.GetRequiredService<TenantExecution>().EstablishServiceHostAsync(scope.Principal,
            operation ?? DurableModel.Dispatch.Id, "Durable provider test dispatch", Token);
        Assert.True(decision.Succeeded, decision.Code);
        return decision.Context!;
    }

    internal ExecutionPrincipal WorkerPrincipal()
    {
        var scope = Get<IServiceIdentityScopes>().Begin(Service);
        scopes.Add(scope);
        return scope.Principal;
    }

    internal async Task<Guid> TenantAsync() => await Get<TenantDirectoryAdministration>().CreateTenantAsync(
        await HostAsync(TenantDirectoryAdministration.OperationId), "Durable tenant", Token);

    internal async Task<DurableUser> UserAsync(Guid tenant, bool owner = false)
    {
        var name = "user-" + Guid.NewGuid().ToString("N");
        var password = "pw-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        var created = await Get<IAccountService>().CreateLocalAccountAsync(IdentityAuthority.Host(await HostAsync(IdentityHostOperations.AccountsId)),
            name, new SecretText(password), Token);
        Assert.Equal(AccountCreationStatus.Created, created.Status);
        var user = new DurableUser(created.AccountId!.Value, name, password);
        Assert.True(await Get<ITenantMemberships>().AddMemberAsync(IdentityAuthority.Host(await HostAsync(IdentityHostOperations.MembershipsId)), user.Id, tenant, Token));
        if (owner) Assert.True(await Get<HostAuthorityAdministration>().AddTenantOwnerAsync(await HostAsync(AuthorizationModule.TenantOwnersOperation.Id), tenant, user.Actor, Token));
        return user;
    }

    internal async Task<DurableUnit> EnterAsync(DurableUser user, Guid tenant)
    {
        var signedIn = await Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(user.Name, new SecretText(user.Password), "203.0.113.2"), Token);
        Assert.Equal(SignInStatus.Succeeded, signedIn.Status);
        var session = await Get<ISessionService>().ValidateAsync(signedIn.Session!.Token, Token);
        Assert.NotNull(session);
        var scope = provider.CreateAsyncScope();
        var principal = session.ToPrincipal();
        scope.ServiceProvider.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(principal);
        var claims = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D"))],
            IdentityHttpServiceCollectionExtensions.AuthenticationScheme));
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(TenantResolutionRequest.ForMember(claims,
            [new TenantCandidate(TenantCandidateSource.Header, tenant.ToString("D"))]), Token);
        Assert.True(decision.Succeeded, decision.Code);
        return new(scope, decision.Context!, principal);
    }

    internal async Task<DurableUnit> EnterServiceAsync(Guid tenant)
    {
        var principal = WorkerPrincipal();
        var scope = provider.CreateAsyncScope();
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishServiceAsync(principal, tenant, Token);
        Assert.True(decision.Succeeded, decision.Code);
        return new(scope, decision.Context!, principal);
    }

    internal async Task GrantAsync(DurableUnit owner, DurableUser member, params string[] permissions)
    {
        foreach (var permission in permissions)
            Assert.True(await Get<AuthorityAdministration>().GrantPermissionAsync(owner.Context, owner.Principal, member.Actor, permission, Token));
    }

    internal EventPump Pump(IEventObserver? observer = null, EventOrdering ordering = EventOrdering.MonotonicSnapshot, EventRetryPolicy? retry = null) =>
        new(Outbox, Inbox, new EventConsumer(DurableModel.Consumer, DurableModel.Apply,
            [new("item.changed", 1, (execution, token) => DurableModel.EffectAsync(execution.Work, execution.Event.OrderingKey, execution.Event.Position, token))], ordering),
            Get<AuthorizedWorkRunner>(), Clock, retry ?? new EventRetryPolicy(), observer);

    internal JobWorker Worker(Func<JobExecution, CancellationToken, Task>? handler = null) => new(JobStore, Get<AuthorizedWorkRunner>(), Get<IPermissionEvaluator>(),
        [new("durable.counter", DurableModel.Apply, handler ?? ((execution, token) => DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token)))]);

    internal async Task<Guid> ProduceAsync(DurableUnit unit, string key, long position = 1, int schema = 1, bool commit = true)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(Database.Producer, unit.Context, cancellationToken: Token);
        await DurableModel.EffectAsync(work, key, position, Token);
        var id = Outbox.Append(work, new(DurableModel.Consumer, "item.changed", schema, key, position, "value-" + position));
        await work.FlushAsync(Token);
        if (commit) Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        return id;
    }

    internal async Task<Guid> QueueAsync(DurableUnit unit, string key, DateTimeOffset? due = null)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(Database.Producer, unit.Context, cancellationToken: Token);
        var id = JobStore.Enqueue(work, new("durable.counter", key, due ?? Clock.GetUtcNow()));
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        return id;
    }

    internal async Task<T[]> RowsAsync<T>(PostgreSqlModule module, TenantContext tenant) where T : class
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, tenant, cancellationToken: Token);
        return await work.Context.Set<T>().AsNoTracking().ToArrayAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes) await scope.DisposeAsync();
        await provider.DisposeAsync();
    }
}
