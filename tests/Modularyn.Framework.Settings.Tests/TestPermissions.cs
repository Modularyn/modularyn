using System.Collections.Concurrent;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Settings.Tests;

/// <summary>
/// A stand-in for SEC-05's permission evaluator, as scaffolding for the settings decisions under test: it permits a
/// permission to a subject in a tenant that the test granted, for a principal that is the context's actor. The
/// authorization tests exercise the real evaluator with settings management.
/// </summary>
internal sealed class TestPermissions : IPermissionEvaluator
{
    private readonly ConcurrentDictionary<(string Subject, Guid Tenant, string Permission), bool> grants = new();
    internal int Decisions;

    internal void Grant(string subject, Guid tenant, string permission) => grants[(subject, tenant, permission)] = true;

    internal void Revoke(string subject, Guid tenant, string permission) => grants.TryRemove((subject, tenant, permission), out _);

    public ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref Decisions);
        var context = request.Context;
        var code = context is not { IsActive: true, Scope: TenantScope.Tenant } ? AuthorizationCodes.NoTenantContext
            : request.Principal is not { } principal ? AuthorizationCodes.Unauthenticated
            : principal.Actor.Id != context.Actor.Subject ? AuthorizationCodes.PrincipalMismatch
            : grants.ContainsKey((principal.EffectiveSubject.Id, context.TenantId!.Value, request.Permission)) ? null : AuthorizationCodes.NotHeld;
        var now = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(code is null
            ? AuthorizationDecision.Permit(request.Permission, now, PermissionFreshness.Current, AuthorizationSource.Store, null, now)
            : AuthorizationDecision.Deny(code, request.Permission, now, PermissionFreshness.Current));
    }

    public ValueTask<ResourceVisibility> VisibleAsync(AuthorizationRequest request, string resourceType, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Settings never ask for resource visibility.");

    public ValueTask<EffectiveAuthority?> DescribeAsync(TenantContext context, ExecutionPrincipal? principal, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Settings never describe effective authority.");
}
