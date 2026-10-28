using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
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

    internal void Register(IServiceCollection services, bool identity = true, bool membership = true, bool authority = true, bool audit = true)
    {
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
