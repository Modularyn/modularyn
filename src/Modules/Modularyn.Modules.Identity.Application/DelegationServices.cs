using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

internal sealed class DelegationService(IDelegationStore store, IAccountDirectory accounts, IdentityPolicies policies, TimeProvider clock)
    : IDelegationService
{
    public async Task<DelegationIssueResult> IssueAsync(DelegationRequest request, ExecutionPrincipal issuedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(issuedBy);
        var subject = new PrincipalReference(request.SubjectAccountId.ToString("D"), IdentityKind.User);
        var actor = new PrincipalReference(request.ActorAccountId.ToString("D"), IdentityKind.User);
        // Only the subject, through their own session and not under any delegation, can delegate their authority. Issuing a
        // delegation for someone else is an administrative act whose permission belongs to authorization.
        if (issuedBy.IsDelegated || issuedBy.ServiceScope is not null || issuedBy.SessionId is null || issuedBy.Actor != subject)
            return new(DelegationIssueStatus.IssuerNotPermitted, null);
        if (actor == subject) return new(DelegationIssueStatus.ActorUnavailable, null);
        if (await accounts.FindAsync(request.ActorAccountId, cancellationToken).ConfigureAwait(false) is not { Disabled: false })
            return new(DelegationIssueStatus.ActorUnavailable, null);
        if (await accounts.FindAsync(request.SubjectAccountId, cancellationToken).ConfigureAwait(false) is not { Disabled: false })
            return new(DelegationIssueStatus.SubjectUnavailable, null);

        var now = clock.GetUtcNow();
        DelegationGrant grant;
        try
        {
            grant = DelegationGrant.Issue(Guid.NewGuid(), actor, subject, request.Scope, request.Purpose, request.Tenant,
                request.NotBefore ?? now, request.ExpiresAt, issuedBy.Actor, now, policies.Delegation);
        }
        catch (ArgumentException)
        {
            return new(DelegationIssueStatus.InvalidWindow, null);
        }
        await store.CreateAsync(grant, cancellationToken).ConfigureAwait(false);
        return new(DelegationIssueStatus.Issued, grant.Id);
    }

    public async Task<bool> RevokeAsync(Guid grantId, ExecutionPrincipal revokedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revokedBy);
        var grant = await store.FindAsync(grantId, cancellationToken).ConfigureAwait(false);
        // Either party may end a delegation; revoking only ever removes authority.
        if (grant is null || (revokedBy.Actor != grant.Actor && revokedBy.Actor != grant.Subject)) return false;
        return await store.RevokeAsync(grantId, revokedBy.Actor, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionPrincipal?> ResolveAsync(Guid grantId, ExecutionPrincipal actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        // A delegation is exercised only from the actor's own interactive session, never from another delegation or a service.
        if (actor.IsDelegated || actor.ServiceScope is not null || actor.SessionId is not { } sessionId || actor.AuthenticatedAt is not { } authenticatedAt)
            return null;
        var grant = await store.FindAsync(grantId, cancellationToken).ConfigureAwait(false);
        if (grant is null || grant.Actor != actor.Actor || grant.StateAt(clock.GetUtcNow()) != DelegationState.Active) return null;
        if (!Guid.TryParseExact(grant.Subject.Id, "D", out var subjectId)
            || await accounts.FindAsync(subjectId, cancellationToken).ConfigureAwait(false) is not { Disabled: false })
            return null;
        return ExecutionPrincipal.ForDelegation(actor.Actor, sessionId, authenticatedAt, grant.Subject, grant.Authority);
    }

    public async Task<DelegationRecordView?> DescribeAsync(Guid grantId, CancellationToken cancellationToken)
    {
        var grant = await store.FindAsync(grantId, cancellationToken).ConfigureAwait(false);
        if (grant is null) return null;
        var authority = grant.Authority;
        return new(grant.Id, grant.Actor, grant.Subject, authority.Scope, authority.Purpose, authority.Tenant, grant.IssuedAt,
            authority.NotBefore, authority.ExpiresAt, grant.IssuedBy, grant.RevokedAt, grant.RevokedBy);
    }
}

internal sealed class ServiceIdentityScopes(ServiceIdentityRegistry registry) : IServiceIdentityScopes
{
    public ServiceIdentityScope Begin(string serviceIdentity)
    {
        var declaration = registry.Find(serviceIdentity)
            ?? throw new InvalidOperationException("The service identity is not declared. Declare it with AddModularynServiceIdentity.");
        return new ServiceIdentityScope(ExecutionPrincipal.ForService(declaration.Principal, declaration.Scope));
    }
}

/// <summary>Rechecks a principal against the stores for a security-sensitive operation (TM-G02-017).</summary>
internal sealed class PrincipalFreshness(ISessionStore sessions, IDelegationStore delegations, IAccountDirectory accounts, ServiceIdentityRegistry registry,
    IdentityPolicies policies, TimeProvider clock) : IPrincipalFreshness
{
    public async ValueTask<FreshnessStatus> RecheckAsync(ExecutionPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var now = clock.GetUtcNow();
        if (principal.ServiceScope is { } scope)
            return registry.Find(principal.Actor.Id) is { } declared && declared.Scope.SetEquals(scope) ? FreshnessStatus.Current : FreshnessStatus.SubjectUnavailable;
        if (principal.SessionId is not { } sessionId) return FreshnessStatus.SessionEnded;
        var lookup = await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (lookup is null || lookup.Session.AccountId.ToString("D") != principal.Actor.Id || SessionRules.Evaluate(lookup, policies.Sessions, now) is not null)
            return FreshnessStatus.SessionEnded;
        if (principal.Delegation is not { } delegation) return FreshnessStatus.Current;
        var grant = await delegations.FindAsync(delegation.GrantId, cancellationToken).ConfigureAwait(false);
        if (grant is null || grant.Actor != principal.Actor || grant.Subject != principal.EffectiveSubject || grant.StateAt(now) != DelegationState.Active)
            return FreshnessStatus.DelegationEnded;
        return Guid.TryParseExact(grant.Subject.Id, "D", out var subjectId)
            && await accounts.FindAsync(subjectId, cancellationToken).ConfigureAwait(false) is { Disabled: false }
            ? FreshnessStatus.Current : FreshnessStatus.SubjectUnavailable;
    }
}
