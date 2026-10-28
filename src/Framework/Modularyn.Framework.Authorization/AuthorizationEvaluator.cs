using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization;

/// <summary>Authority could not be read, so no decision or description can rest on it.</summary>
public sealed class AuthorizationUnavailableException() : InvalidOperationException("Authorization data is unavailable; nothing was permitted.");

/// <summary>
/// The one evaluator. A decision needs an active tenant context, an authenticated principal that is the context's actor,
/// a permission the composition declares, the execution's ceiling, and the effective subject's authority in the tenant:
/// read from the store for a current-profile decision, and otherwise from a principal-scoped cache entry no older than the
/// declared bound. A resource is permitted only in the context's tenant, to its owner or to the holder of a current grant
/// on it, read from the store every time. Anything unavailable refuses.
/// </summary>
internal sealed partial class AuthorizationEvaluator(AuthorizationRegistry registry, IAuthoritySource source, IAuthorityCache cache, AuthorityCacheHealth health,
    TenantWorkRunner runner, AuthorizationClock clock, TimeProvider time, TimeSpan bound, IServiceProvider services, ILogger<AuthorizationEvaluator> logger)
    : IPermissionEvaluator
{
    public async ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.Now;
        var definition = registry.FindPermission(request.Permission);
        var freshness = request.RequireCurrent || definition?.Freshness == PermissionFreshness.Current ? PermissionFreshness.Current : PermissionFreshness.Bounded;
        AuthorizationDecision Deny(string code, AuthoritySnapshot? snapshot = null, AuthorizationSource from = AuthorizationSource.None) =>
            AuthorizationDecision.Deny(code, request.Permission, now, freshness, from, snapshot?.Version, snapshot?.ReadAt);

        var context = request.Context;
        if (!context.IsActive || context.Scope != TenantScope.Tenant) return Deny(AuthorizationCodes.NoTenantContext);
        if (request.Principal is not { } principal) return Deny(AuthorizationCodes.Unauthenticated);
        if (definition is null) return Deny(AuthorizationCodes.UndefinedPermission);
        if (SubjectOf(context, principal) is not { } subject) return Deny(AuthorizationCodes.PrincipalMismatch);
        if (!principal.IsWithinCeiling(definition.Name, context.TenantId, now)) return Deny(AuthorizationCodes.OutsideCeiling);
        try
        {
            if (await PrincipalRefusalAsync(context, principal, subject, freshness, cancellationToken).ConfigureAwait(false) is { } refused)
                return Deny(refused);
            // A service acting as itself has exactly its declared scope, which its ceiling already checked.
            if (IsServiceItself(principal, subject))
                return AuthorizationDecision.Permit(definition.Name, now, freshness, AuthorizationSource.ServiceScope, null, null);
            var (snapshot, from) = freshness == PermissionFreshness.Current
                ? (await source.ReadAsync(context, subject, cancellationToken).ConfigureAwait(false), AuthorizationSource.Store)
                : await CachedAsync(context, subject, cancellationToken).ConfigureAwait(false);
            if (!snapshot.Holds(definition)) return Deny(AuthorizationCodes.NotHeld, snapshot, from);
            if (request.Resource is { } resource)
            {
                // Resolved within the effective tenant, then owned or currently granted: a grant revoked a moment ago no longer counts.
                if (resource.TenantId != context.TenantId) return Deny(AuthorizationCodes.ResourceNotFound, snapshot, from);
                if (resource.Owner != subject
                    && !await source.HasResourceGrantAsync(context, subject, resource, definition.Name, now, cancellationToken).ConfigureAwait(false))
                    return Deny(AuthorizationCodes.ResourceNotFound, snapshot, from);
            }
            if (!context.IsActive) return Deny(AuthorizationCodes.NoTenantContext, snapshot, from);
            return AuthorizationDecision.Permit(definition.Name, now, freshness, from, snapshot.Version, snapshot.ReadAt);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            Unavailable(logger, request.Permission);
            return Deny(AuthorizationCodes.Unavailable);
        }
    }

    public async ValueTask<ResourceVisibility> VisibleAsync(AuthorizationRequest request, string resourceType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PermissionNames.IsValid(resourceType)) throw new ArgumentException("A resource type is a stable identifier such as item.", nameof(resourceType));
        if (request.Resource is not null) throw new ArgumentException("Ask for visibility without naming one resource.", nameof(request));
        var decision = await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted || request.Principal is not { } principal || SubjectOf(request.Context, principal) is not { } subject)
            return new ResourceVisibility(decision, null, []);
        try
        {
            var granted = await source.GrantedResourcesAsync(request.Context, subject, resourceType, decision.Permission, clock.Now, cancellationToken)
                .ConfigureAwait(false);
            return new ResourceVisibility(decision, subject.Kind == ActorKind.User ? subject.Subject : null, granted);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            Unavailable(logger, request.Permission);
            return new ResourceVisibility(AuthorizationDecision.Deny(AuthorizationCodes.Unavailable, decision.Permission, clock.Now, decision.Freshness), null, []);
        }
    }

    public async ValueTask<EffectiveAuthority?> DescribeAsync(TenantContext context, ExecutionPrincipal? principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = clock.Now;
        if (!context.IsActive || context.Scope != TenantScope.Tenant || principal is null || SubjectOf(context, principal) is not { } subject) return null;
        var within = registry.Permissions.Where(definition => principal.IsWithinCeiling(definition.Name, context.TenantId, now)).ToArray();
        if (IsServiceItself(principal, subject))
            return new EffectiveAuthority(subject, within.Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal), null, now,
                AuthorizationSource.ServiceScope);
        try
        {
            if (await PrincipalRefusalAsync(context, principal, subject, PermissionFreshness.Bounded, cancellationToken).ConfigureAwait(false) is not null)
                return null;
            var (snapshot, from) = await CachedAsync(context, subject, cancellationToken).ConfigureAwait(false);
            return new EffectiveAuthority(subject, within.Where(snapshot.Holds).Select(definition => definition.Name).ToHashSet(StringComparer.Ordinal),
                snapshot.Version, snapshot.ReadAt, from);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            Unavailable(logger, "effective authority");
            throw new AuthorizationUnavailableException();
        }
    }

    /// <summary>
    /// Whose authority applies. An interactive user acts with its effective subject's authority, and must be the actor the
    /// tenant context was established for. A service identity acts either as itself, in a context it is the actor of, or
    /// for the producer of propagated work, the user that the revalidated context names, never with more than its scope.
    /// </summary>
    internal static ActorReference? SubjectOf(TenantContext context, ExecutionPrincipal principal)
    {
        if (principal.ServiceScope is not null)
        {
            if (context.Actor.Kind == ActorKind.User) return context.Actor;
            return context.Actor.Subject == principal.Actor.Id ? context.Actor : null;
        }
        if (principal.Actor.Kind != IdentityKind.User || context.Actor.Kind != ActorKind.User || context.Actor.Subject != principal.Actor.Id) return null;
        return new ActorReference(ActorKind.User, principal.EffectiveSubject.Id);
    }

    private static bool IsServiceItself(ExecutionPrincipal principal, ActorReference subject) =>
        principal.ServiceScope is not null && subject.Kind == ActorKind.Service;

    /// <summary>
    /// The checks on the principal itself. A current-profile decision rechecks its session, delegation or service identity
    /// and the tenant membership of its actor and subject now. A bounded one relies on the membership the context checked
    /// when it was established, except for a subject other than the actor, such as a delegation's, which is checked now.
    /// </summary>
    private async ValueTask<string?> PrincipalRefusalAsync(TenantContext context, ExecutionPrincipal principal, ActorReference subject,
        PermissionFreshness freshness, CancellationToken cancellationToken)
    {
        if (freshness == PermissionFreshness.Current)
        {
            if (services.GetService<IPrincipalFreshness>() is not { } recheck) return AuthorizationCodes.Unavailable;
            if (await recheck.RecheckAsync(principal, cancellationToken).ConfigureAwait(false) != FreshnessStatus.Current) return AuthorizationCodes.PrincipalNotCurrent;
        }
        var members = new List<ActorReference>();
        if (subject.Kind == ActorKind.User && (freshness == PermissionFreshness.Current || subject != context.Actor)) members.Add(subject);
        if (freshness == PermissionFreshness.Current && context.Actor.Kind == ActorKind.User && context.Actor != subject) members.Add(context.Actor);
        if (members.Count == 0) return null;
        if (services.GetService<ITenantMembership>() is not { } membership) return AuthorizationCodes.Unavailable;
        foreach (var member in members)
            if (!await membership.IsMemberAsync(member, context.TenantId!.Value, cancellationToken).ConfigureAwait(false)) return AuthorizationCodes.SubjectNotMember;
        return null;
    }

    /// <summary>
    /// Bounded authority from the cache, filled in a revalidated unit of its own. When the cache itself fails, or may have
    /// missed an invalidation, the decision is taken from the store and nothing is cached; when the store fails, the caller
    /// refuses.
    /// </summary>
    private async Task<(AuthoritySnapshot Snapshot, AuthorizationSource Source)> CachedAsync(TenantContext context, ActorReference subject,
        CancellationToken cancellationToken)
    {
        if (!health.Trusted) return (await source.ReadAsync(context, subject, cancellationToken).ConfigureAwait(false), AuthorizationSource.Store);
        var filled = false;
        try
        {
            var snapshot = await cache.GetOrCreateAsync(context, subject, bound, async _ =>
            {
                filled = true;
                return await FillAsync(context, subject).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return (snapshot, filled ? AuthorizationSource.Store : AuthorizationSource.Cache);
        }
        catch (AuthoritySourceException) { throw; }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken) && exception is not TenantCacheStaleException)
        {
            CacheUnavailable(logger);
            return (await source.ReadAsync(context, subject, cancellationToken).ConfigureAwait(false), AuthorizationSource.Store);
        }
    }

    // A shared fill can outlive the request that started it, so it runs in its own revalidated unit, as SEC-04's propagated
    // work does, and gives up at the declared bound.
    private async Task<AuthoritySnapshot> FillAsync(TenantContext context, ActorReference subject)
    {
        try
        {
            var propagated = PropagatedTenantContext.Capture(context, time);
            using var deadline = new CancellationTokenSource(bound, time);
            AuthoritySnapshot? snapshot = null;
            var outcome = await runner.RunAsync(propagated, async (unit, token) =>
            {
                snapshot = await source.ReadAsync(unit.Context, subject, token).ConfigureAwait(false);
            }, deadline.Token).ConfigureAwait(false);
            return outcome.Disposition == TenantWorkDisposition.Completed && snapshot is not null ? snapshot : throw new AuthoritySourceException();
        }
        catch (AuthoritySourceException) { throw; }
        catch (Exception) { throw new AuthoritySourceException(); }
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    [LoggerMessage(EventId = 8300, Level = LogLevel.Warning, Message = "Authorization of {Permission} was refused because its data is unavailable.")]
    private static partial void Unavailable(ILogger logger, string permission);

    [LoggerMessage(EventId = 8301, Level = LogLevel.Warning, Message = "The authority cache failed; the decision was taken from the store and nothing was cached.")]
    private static partial void CacheUnavailable(ILogger logger);

    private sealed class AuthoritySourceException() : Exception("The authority store could not be read.");
}
