using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Framework.Settings.Tests;

/// <summary>
/// A stand-in for identity's service identity registry, as scaffolding for the settings decisions under test: it declares
/// only <see cref="Worker"/>, with a service scope, and refuses every other name as an undeclared identity. The identity
/// module's tests exercise the real registry.
/// </summary>
internal sealed class TestServiceIdentityScopes(IServiceScopeFactory scopes) : IServiceIdentityScopes
{
    internal const string Worker = "worker";
    internal static readonly IReadOnlySet<string> Scope =
        new HashSet<string>(StringComparer.Ordinal) { SettingsModule.TenantOverridesPermission, SettingsModule.UserOverridesPermission };

    public ServiceIdentityScope Begin(string serviceIdentity)
    {
        if (serviceIdentity != Worker) throw new InvalidOperationException("The service identity is not declared.");
        var principal = ExecutionPrincipal.ForService(new PrincipalReference(Worker, IdentityKind.Service), Scope);
        var scope = scopes.CreateAsyncScope();
        if (!scope.ServiceProvider.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(principal))
        {
            scope.Dispose();
            throw new InvalidOperationException("A new service scope already held a principal.");
        }
        return new ServiceIdentityScope(principal, scope.ServiceProvider, scope);
    }
}

/// <summary>
/// A stand-in for identity's freshness recheck: <see cref="FreshnessStatus.Current"/> only for the declared worker service
/// with its declared scope while <see cref="Available"/>, and <see cref="FreshnessStatus.SubjectUnavailable"/> otherwise.
/// </summary>
internal sealed class TestServiceFreshness : IPrincipalFreshness
{
    internal bool Available { get; set; } = true;

    public ValueTask<FreshnessStatus> RecheckAsync(ExecutionPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var current = Available && principal.Actor is { Kind: IdentityKind.Service, Id: TestServiceIdentityScopes.Worker }
            && principal.ServiceScope is { } scope && scope.SetEquals(TestServiceIdentityScopes.Scope);
        return ValueTask.FromResult(current ? FreshnessStatus.Current : FreshnessStatus.SubjectUnavailable);
    }
}
