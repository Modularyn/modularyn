using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// Test doubles for the seams that identity (SEC-03), authorization (SEC-05) and audit (SEC-07) will supply. They are
// scaffolding for the tenancy decisions under test, not evidence for those orders' controls.

internal sealed class TestIdentity : ITenantActorResolver
{
    internal const string AuthenticationType = "test";
    internal const string SubjectClaim = "sub";
    internal const string KindClaim = "actor-kind";

    public ActorReference? Resolve(ClaimsPrincipal principal)
    {
        var identity = principal.Identities.FirstOrDefault(item => item.IsAuthenticated && item.AuthenticationType == AuthenticationType);
        if (identity?.FindFirst(SubjectClaim)?.Value is not { } subject) return null;
        return new(identity.FindFirst(KindClaim)?.Value == "service" ? ActorKind.Service : ActorKind.User, subject);
    }

    internal static ClaimsPrincipal User(string subject, params string[] tenantClaims) => Principal(subject, "user", tenantClaims);

    internal static ClaimsPrincipal Service(string subject) => Principal(subject, "service", []);

    internal static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal Principal(string subject, string kind, string[] tenantClaims)
    {
        var claims = new List<Claim> { new(SubjectClaim, subject), new(KindClaim, kind) };
        claims.AddRange(tenantClaims.Select(value => new Claim(TenantClaimTypes.Tenant, value)));
        return new(new ClaimsIdentity(claims, AuthenticationType));
    }
}

internal sealed class TestMembership : ITenantMembership
{
    private readonly ConcurrentDictionary<(string Subject, Guid Tenant), bool> members = new();

    internal bool Failing { get; set; }
    internal int Checks;

    internal void Add(string subject, Guid tenant) => members[(subject, tenant)] = true;

    internal void Remove(string subject, Guid tenant) => members.TryRemove((subject, tenant), out _);

    public ValueTask<bool> IsMemberAsync(ActorReference actor, Guid tenantId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Checks);
        if (Failing) throw new InvalidOperationException("The membership store is unavailable.");
        return ValueTask.FromResult(members.ContainsKey((actor.Subject, tenantId)));
    }
}

/// <summary>
/// Grants host operations to subjects. An operation's reach is what the test declares for it, as its owning module would;
/// an operation the test never declares reaches nothing, so host scope granted for it reads and changes no entity.
/// </summary>
internal sealed class TestHostAuthority : IHostAuthority
{
    private readonly ConcurrentDictionary<string, HostOperation> operations = new(StringComparer.Ordinal)
    {
        [TenantDirectoryAdministration.OperationId] = TenantDirectoryAdministration.Operation
    };
    private readonly ConcurrentDictionary<(string Subject, string Operation), bool> grants = new();

    internal void Declare(HostOperation operation) => operations[operation.Id] = operation;

    internal void Grant(string subject, string operation) => grants[(subject, operation)] = true;

    internal void Grant(string subject, HostOperation operation)
    {
        Declare(operation);
        Grant(subject, operation.Id);
    }

    internal void Revoke(string subject, string operation) => grants.TryRemove((subject, operation), out _);

    public ValueTask<HostOperation?> AuthorizeAsync(ActorReference actor, string operationId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(grants.ContainsKey((actor.Subject, operationId)) ? operations.GetValueOrDefault(operationId) ?? new HostOperation(operationId, []) : null);
}

/// <summary>
/// Stands in for identity's service identity registry and its freshness recheck: every service subject is declared, with
/// one test scope, and current until the test retires it. Begin can be made to fail as it does once the host's service
/// provider is disposed during shutdown.
/// </summary>
internal sealed class TestServiceIdentities : IServiceIdentityScopes, IPrincipalFreshness
{
    internal static readonly IReadOnlySet<string> Scope = new HashSet<string>(StringComparer.Ordinal) { "test.work" };
    private readonly ConcurrentDictionary<string, bool> retired = new(StringComparer.Ordinal);

    internal bool Disposed { get; set; }

    internal void Retire(string subject) => retired[subject] = true;

    internal void Restore(string subject) => retired.TryRemove(subject, out _);

    internal static ExecutionPrincipal Principal(string subject) => ExecutionPrincipal.ForService(new PrincipalReference(subject, IdentityKind.Service), Scope);

    public ServiceIdentityScope Begin(string serviceIdentity)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(IServiceProvider));
        if (retired.ContainsKey(serviceIdentity)) throw new InvalidOperationException("The service identity is not declared.");
        return new(Principal(serviceIdentity), NoServices.Instance, NoServices.Instance);
    }

    public ValueTask<FreshnessStatus> RecheckAsync(ExecutionPrincipal principal, CancellationToken cancellationToken) =>
        ValueTask.FromResult(principal.ServiceScope is { } scope && scope.SetEquals(Scope) && !retired.ContainsKey(principal.Actor.Id)
            ? FreshnessStatus.Current : FreshnessStatus.SubjectUnavailable);

    private sealed class NoServices : IServiceProvider, IAsyncDisposable
    {
        internal static readonly NoServices Instance = new();
        public object? GetService(Type serviceType) => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class RecordingAudit : IHostScopeAudit
{
    internal ConcurrentQueue<HostScopeAuditRecord> Records { get; } = new();
    internal bool Failing { get; set; }

    public ValueTask RecordAsync(HostScopeAuditRecord record, CancellationToken cancellationToken)
    {
        if (Failing) throw new InvalidOperationException("The audit store is unavailable.");
        Records.Enqueue(record);
        return ValueTask.CompletedTask;
    }
}

/// <summary>An in-memory directory for unit tests of the resolution rules; real-provider tests use the PostgreSQL directory.</summary>
internal sealed class MemoryDirectory : ITenantDirectory
{
    private readonly ConcurrentDictionary<Guid, TenantDirectoryEntry> tenants = new();
    private readonly ConcurrentDictionary<string, Guid> domains = new(StringComparer.Ordinal);

    internal bool Unavailable { get; set; }
    internal int Lookups;

    internal Guid Add(string name, TenantState state = TenantState.Active, string? domain = null)
    {
        var id = Guid.NewGuid();
        tenants[id] = new(id, name, state);
        if (domain is not null) domains[domain] = id;
        return id;
    }

    internal void Suspend(Guid tenant) => tenants[tenant] = tenants[tenant] with { State = TenantState.Suspended };

    public ValueTask<TenantDirectoryEntry?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Lookups);
        if (Unavailable) throw new InvalidOperationException("The directory is unavailable.");
        return ValueTask.FromResult(tenants.GetValueOrDefault(tenantId));
    }

    public ValueTask<Guid?> FindTenantByDomainAsync(string host, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Lookups);
        if (Unavailable) throw new InvalidOperationException("The directory is unavailable.");
        return ValueTask.FromResult(domains.TryGetValue(host, out var id) ? id : (Guid?)null);
    }
}

/// <summary>All four seams, registered together or selectively.</summary>
internal sealed class TestSeams
{
    internal TestIdentity Identity { get; } = new();
    internal TestMembership Membership { get; } = new();
    internal TestHostAuthority Authority { get; } = new();
    internal RecordingAudit Audit { get; } = new();
    internal TestServiceIdentities ServiceIdentities { get; } = new();

    internal void Register(IServiceCollection services, bool identity = true, bool membership = true, bool authority = true, bool audit = true)
    {
        services.TryAddSingleton<IServiceIdentityScopes>(ServiceIdentities);
        services.TryAddSingleton<IPrincipalFreshness>(ServiceIdentities);
        if (identity) services.AddSingleton<ITenantActorResolver>(Identity);
        if (membership) services.AddSingleton<ITenantMembership>(Membership);
        if (authority) services.AddSingleton<IHostAuthority>(Authority);
        if (audit) services.AddSingleton<IHostScopeAudit>(Audit);
    }
}

/// <summary>Builds the tenancy services over an in-memory directory, the way AddModularynTenancy builds them over PostgreSQL.</summary>
internal static class UnitServices
{
    internal static ServiceProvider Build(MemoryDirectory directory, TestSeams seams, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantDirectory>(directory);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantExecution>();
        services.AddSingleton<TenantWorkRunner>();
        services.AddSingleton<TenantCache>();
        seams.Register(services);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}

internal static class Candidates
{
    internal static TenantCandidate Header(Guid tenant) => new(TenantCandidateSource.Header, TenantIdentifier.Format(tenant));
    internal static TenantCandidate Header(string value) => new(TenantCandidateSource.Header, value);
    internal static TenantCandidate Route(string value) => new(TenantCandidateSource.Route, value);
    internal static TenantCandidate Claim(string value) => new(TenantCandidateSource.Claim, value);
    internal static TenantCandidate Host(string value) => new(TenantCandidateSource.Host, value);
}

internal static class Units
{
    /// <summary>Establishes a member context in a new scope. The caller disposes the scope, which ends the context.</summary>
    internal static async Task<(AsyncServiceScope Scope, TenantContext Context)> EnterAsync(IServiceProvider services, string subject, Guid tenant)
    {
        var scope = services.CreateAsyncScope();
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
            TenantResolutionRequest.ForMember(TestIdentity.User(subject), [Candidates.Header(tenant)]), TestContext.Current.CancellationToken);
        Assert.True(decision.Succeeded, decision.Code);
        return (scope, decision.Context!);
    }

    /// <summary>Establishes a host-scope context in a new scope under the given operation.</summary>
    internal static async Task<(AsyncServiceScope Scope, TenantContext Context)> EnterHostAsync(IServiceProvider services, string subject, string operation,
        bool service = false)
    {
        var scope = services.CreateAsyncScope();
        var decision = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
            TenantResolutionRequest.ForHostOperation(service ? TestIdentity.Service(subject) : TestIdentity.User(subject), operation,
                "Test host operation " + operation), TestContext.Current.CancellationToken);
        Assert.True(decision.Succeeded, decision.Code);
        return (scope, decision.Context!);
    }
}
