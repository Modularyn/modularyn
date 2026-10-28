using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// Authority held in memory, for unit tests of the evaluator's decisions; the PostgreSQL source is exercised by the
/// real-provider tests. It can be made unavailable, and counts its reads.
/// </summary>
internal sealed class MemoryAuthority : IAuthoritySource
{
    private readonly ConcurrentDictionary<(Guid Tenant, string Subject), ConcurrentDictionary<string, bool>> held = new();
    private readonly ConcurrentDictionary<(Guid Tenant, string Subject, string Type, string Id, string Permission), DateTimeOffset?> resources = new();
    internal bool Unavailable { get; set; }
    internal int Reads;

    internal void Grant(Guid tenant, string subject, params string[] permissions)
    {
        var set = held.GetOrAdd((tenant, subject), _ => new());
        foreach (var permission in permissions) set[permission] = true;
    }

    internal void Revoke(Guid tenant, string subject, string permission)
    {
        if (held.TryGetValue((tenant, subject), out var set)) set.TryRemove(permission, out _);
    }

    internal void Share(Guid tenant, string subject, string type, string id, string permission, DateTimeOffset? expiresAt = null) =>
        resources[(tenant, subject, type, id, permission)] = expiresAt;

    public Task<AuthoritySnapshot> ReadAsync(TenantContext context, ActorReference subject, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        if (Unavailable) throw new InvalidOperationException("The authority store is unavailable.");
        var permissions = held.TryGetValue((context.TenantId!.Value, subject.Subject), out var set) ? set.Keys.ToArray() : [];
        return Task.FromResult(new AuthoritySnapshot(subject, false, permissions, [], null, DateTimeOffset.UtcNow));
    }

    public Task<bool> HasResourceGrantAsync(TenantContext context, ActorReference subject, ResourceReference resource, string permission, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Unavailable) throw new InvalidOperationException("The authority store is unavailable.");
        return Task.FromResult(resources.TryGetValue((context.TenantId!.Value, subject.Subject, resource.Type, resource.Id, permission), out var until)
            && (until is null || until > now));
    }

    public Task<IReadOnlyList<string>> GrantedResourcesAsync(TenantContext context, ActorReference subject, string resourceType, string permission,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Unavailable) throw new InvalidOperationException("The authority store is unavailable.");
        return Task.FromResult<IReadOnlyList<string>>(resources.Where(entry => entry.Key.Tenant == context.TenantId && entry.Key.Subject == subject.Subject
            && entry.Key.Type == resourceType && entry.Key.Permission == permission && (entry.Value is null || entry.Value > now)).Select(entry => entry.Key.Id).ToArray());
    }
}

/// <summary>Freshness as identity answers it, with sessions the test ends.</summary>
internal sealed class TestFreshness : IPrincipalFreshness
{
    private readonly ConcurrentDictionary<Guid, bool> ended = new();
    internal int Checks;

    internal void End(Guid sessionId) => ended[sessionId] = true;

    public ValueTask<FreshnessStatus> RecheckAsync(ExecutionPrincipal principal, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Checks);
        return ValueTask.FromResult(principal.SessionId is { } session && ended.ContainsKey(session) ? FreshnessStatus.SessionEnded : FreshnessStatus.Current);
    }
}

/// <summary>Collects endpoint conventions, so a declaration can be applied to an endpoint built without a server.</summary>
internal sealed class ConventionCollector : IEndpointConventionBuilder
{
    private readonly List<Action<EndpointBuilder>> conventions = [];

    public void Add(Action<EndpointBuilder> convention) => conventions.Add(convention);

    internal Endpoint Build(string route, RequestDelegate handler)
    {
        var builder = new RouteEndpointBuilder(handler, RoutePatternFactory.Parse(route), 0);
        foreach (var convention in conventions) convention(builder);
        return builder.Build();
    }
}

/// <summary>
/// The evaluator and its entry points over an in-memory directory and authority, composed as
/// <c>AddModularynAuthorization</c> composes them, with the store replaced.
/// </summary>
internal sealed class UnitAuthorization : IAsyncDisposable
{
    internal static readonly SemanticVersion Version = SemanticVersion.Parse("1.0.0");
    private readonly ServiceProvider provider;
    private readonly List<AsyncServiceScope> scopes = [];

    private UnitAuthorization(ServiceProvider provider, MemoryDirectory directory, TestSeams seams, MemoryAuthority authority, TestFreshness freshness)
    {
        this.provider = provider;
        Directory = directory;
        Seams = seams;
        Authority = authority;
        Freshness = freshness;
    }

    public MemoryDirectory Directory { get; }
    public TestSeams Seams { get; }
    public MemoryAuthority Authority { get; }
    public TestFreshness Freshness { get; }
    public IServiceProvider Services => provider;

    internal static AuthorizationRegistry Registry()
    {
        var manifests = new[]
        {
            AuthorizationModule.Manifest(Version),
            new ModuleManifest(Items.ModuleId, Version, definitions: Items.Permissions.Select(definition => definition.Declaration))
        };
        return new AuthorizationRegistry(manifests, AuthorizationModule.Permissions(Version).Concat(Items.Permissions), AuthorizationModule.HostOperations(Version));
    }

    public static UnitAuthorization Create(Action<IServiceCollection>? configure = null)
    {
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        var authority = new MemoryAuthority();
        var freshness = new TestFreshness();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantDirectory>(directory);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantExecution>();
        services.AddSingleton<TenantWorkRunner>();
        services.AddSingleton<TenantCache>();
        seams.Register(services, authority: false);
        services.AddSingleton<IPrincipalFreshness>(freshness);
        services.AddScoped<ExecutionPrincipalScope>();
        services.AddScoped<IExecutionPrincipalAccessor>(scope => scope.GetRequiredService<ExecutionPrincipalScope>());
        services.AddAuthorizationCore();
        // No connection is opened: the store is replaced below, and the module only names the schema it would use.
        var module = AuthorizationModule.Create(new PostgreSqlPersistenceOptions("Host=localhost;Database=unused"));
        services.AddModularynAuthorization(new ConfigurationBuilder().Build(), module, Registry());
        services.Replace(ServiceDescriptor.Singleton<IAuthoritySource>(authority));
        // Host contexts come from tenancy's stand-in here; the real elevated policy reads PostgreSQL and is tested there.
        services.Replace(ServiceDescriptor.Singleton<IHostAuthority>(seams.Authority));
        configure?.Invoke(services);
        return new(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }), directory, seams, authority, freshness);
    }

    public T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    /// <summary>A user's unit in the tenant: its own scope with the tenant context established and the principal held by the scope.</summary>
    public async Task<Unit> EnterAsync(string subject, Guid tenant, ExecutionPrincipal? principal = null)
    {
        principal ??= ExecutionPrincipal.ForUser(new PrincipalReference(subject, IdentityKind.User), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var (scope, context) = await Units.EnterAsync(provider, subject, tenant);
        Assert.True(scope.ServiceProvider.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(principal));
        scopes.Add(scope);
        return new Unit(scope, context, principal);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes) await scope.DisposeAsync();
        await provider.DisposeAsync();
    }
}
