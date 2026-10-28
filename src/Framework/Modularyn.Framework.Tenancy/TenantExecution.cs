using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Tenancy;

public enum TenantAccess
{
    /// <summary>The effective tenant needs the actor's current membership.</summary>
    Member,
    /// <summary>Host scope needs separately granted host authority and an audit record; it never comes from a tenant candidate.</summary>
    Host
}

public enum TenantCandidateSource { Host, Route, Header, Claim }

/// <summary>An untrusted value that may name a tenant. A host candidate is a host name; the others are tenant identifiers.</summary>
public sealed record TenantCandidate(TenantCandidateSource Source, string Value);

/// <summary>What a unit of work asks for, with every candidate it carries and the principal that asks.</summary>
public sealed class TenantResolutionRequest
{
    private TenantResolutionRequest(TenantAccess access, ClaimsPrincipal principal, IEnumerable<TenantCandidate> candidates,
        string? operationId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(candidates);
        var copy = candidates.ToArray();
        if (copy.Any(candidate => candidate is null || candidate.Value is null || !Enum.IsDefined(candidate.Source)))
            throw new ArgumentException("Candidates need a defined source and a value.", nameof(candidates));
        Access = access;
        Principal = principal;
        Candidates = Array.AsReadOnly(copy);
        OperationId = operationId;
        Reason = reason;
    }

    public TenantAccess Access { get; }
    public ClaimsPrincipal Principal { get; }
    public IReadOnlyList<TenantCandidate> Candidates { get; }
    public string? OperationId { get; }
    public string? Reason { get; }

    public static TenantResolutionRequest ForMember(ClaimsPrincipal principal, IEnumerable<TenantCandidate> candidates) =>
        new(TenantAccess.Member, principal, candidates, null, null);

    public static TenantResolutionRequest ForHostOperation(ClaimsPrincipal principal, string operationId, string reason,
        IEnumerable<TenantCandidate>? candidates = null)
    {
        if (!PropagatedTenantContext.ValidOperation(operationId))
            throw new ArgumentException("A host operation identity is 1 to 200 letters, digits, '.', '-' or '_'.", nameof(operationId));
        if (!PropagatedTenantContext.ValidReason(reason))
            throw new ArgumentException("A host operation reason is 1 to 500 characters with no surrounding whitespace or control characters.", nameof(reason));
        return new(TenantAccess.Host, principal, candidates ?? [], operationId, reason);
    }
}

/// <summary>Stable refusal codes. A refusal carries no candidate value, tenant name or seam detail.</summary>
public static class TenancyCodes
{
    public const string Malformed = "MTN001";
    public const string Ambiguous = "MTN002";
    public const string NoCandidate = "MTN003";
    public const string Conflict = "MTN004";
    public const string UnknownTenant = "MTN005";
    public const string Unauthenticated = "MTN006";
    public const string NotMember = "MTN007";
    public const string SeamUnavailable = "MTN008";
    public const string HostAuthorityRefused = "MTN009";
    public const string HostCandidates = "MTN010";
    public const string AuditUnavailable = "MTN011";
    public const string DirectoryUnavailable = "MTN012";
    public const string OriginNotConfigured = "MTN013";
}

/// <summary>
/// The outcome of establishing a context. A terminal refusal stays refused on retry with the same inputs; a transient one
/// arises from an unavailable directory or seam.
/// </summary>
public sealed record TenantDecision
{
    private TenantDecision(TenantContext? context, string? code, bool terminal)
    {
        Context = context;
        Code = code;
        Terminal = terminal;
    }

    public TenantContext? Context { get; }
    public string? Code { get; }
    public bool Terminal { get; }
    public bool Succeeded => Context is not null;

    internal static TenantDecision Established(TenantContext context) => new(context, null, false);
    internal static TenantDecision Refused(string code) => new(null, code, code is not (TenancyCodes.SeamUnavailable
        or TenancyCodes.AuditUnavailable or TenancyCodes.DirectoryUnavailable));
}

/// <summary>
/// The tenant context of one unit of work, registered as a scoped service: each request, job and message gets a new
/// instance in its own service scope. It is established once, never changes within the unit, and ends with the scope
/// or an explicit <see cref="End"/>, on every path. Ending also ends the context itself, so a leaked reference to it,
/// or a unit of work bound to it, can no longer reach tenant data. Nothing ambient carries it: work that does not
/// receive it explicitly has no context.
/// </summary>
public sealed class TenantExecution : IDisposable
{
    private const int Empty = 0, Established = 1, Ended = 2;
    private readonly IServiceProvider services;
    private readonly ITenantDirectory directory;
    private readonly TimeProvider time;
    private TenantContext? context;
    private int state;

    public TenantExecution(IServiceProvider services, ITenantDirectory directory, TimeProvider time)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The established context while the unit is active; null before, after a refusal, and after the unit ends.</summary>
    public TenantContext? Context => Volatile.Read(ref state) == Established && Volatile.Read(ref context) is { IsActive: true } current ? current : null;

    public bool HasEnded => Volatile.Read(ref state) == Ended;

    /// <summary>Validates the request's candidates and principal. Called at most once per unit; a refusal leaves the unit without a context.</summary>
    public async ValueTask<TenantDecision> EstablishAsync(TenantResolutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Begin();
        var decision = request.Access == TenantAccess.Host
            ? await ResolveHostAsync(request, cancellationToken).ConfigureAwait(false)
            : await ResolveMemberAsync(request, cancellationToken).ConfigureAwait(false);
        return Publish(decision);
    }

    /// <summary>
    /// Reconstructs a propagated context after revalidating it now: the tenant must still be active and the actor still
    /// a member, or, for host scope, still hold host authority, with a new audit record. The payload is never consulted.
    /// </summary>
    public async ValueTask<TenantDecision> EstablishPropagatedAsync(PropagatedTenantContext propagated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propagated);
        Begin();
        TenantDecision decision;
        if (propagated.Scope == TenantScope.Tenant)
        {
            var tenantId = propagated.TenantId!.Value;
            var refusal = await CheckTenantAsync(tenantId, cancellationToken).ConfigureAwait(false)
                ?? await CheckMembershipAsync(propagated.Actor, tenantId, cancellationToken).ConfigureAwait(false);
            decision = refusal ?? TenantDecision.Established(new(TenantScope.Tenant, tenantId, propagated.Actor, null,
                TenantContextSource.Propagated, Guid.NewGuid()));
        }
        else decision = await GrantHostAsync(propagated.Actor, propagated.OperationId!, propagated.Reason!,
            TenantContextSource.Propagated, cancellationToken).ConfigureAwait(false);
        return Publish(decision);
    }

    /// <summary>Ends the unit's context. Idempotent; called by the owning scope on every path.</summary>
    public void End()
    {
        Volatile.Write(ref state, Ended);
        Volatile.Read(ref context)?.End();
    }

    public void Dispose() => End();

    private void Begin()
    {
        var previous = Interlocked.CompareExchange(ref state, Established, Empty);
        if (previous == Ended) throw new ObjectDisposedException(nameof(TenantExecution), "The unit of work has ended.");
        if (previous != Empty) throw new InvalidOperationException("A unit of work establishes its tenant context once.");
    }

    private TenantDecision Publish(TenantDecision decision)
    {
        if (decision.Context is null) return decision;
        Volatile.Write(ref context, decision.Context);
        // An end that raced with establishment still ends the new context.
        if (Volatile.Read(ref state) == Ended) decision.Context.End();
        return decision;
    }

    private async ValueTask<TenantDecision> ResolveMemberAsync(TenantResolutionRequest request, CancellationToken cancellationToken)
    {
        // The principal's own tenant claims are candidates on every path, whoever builds the request.
        var claims = request.Principal.Identities.Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.FindAll(TenantClaimTypes.Tenant)).Select(claim => new TenantCandidate(TenantCandidateSource.Claim, claim.Value));
        // Syntax first, before any lookup: one canonical value per source.
        string? host = null;
        var tenants = new HashSet<Guid>();
        foreach (var source in request.Candidates.Concat(claims).GroupBy(candidate => candidate.Source))
        {
            var values = source.Select(candidate => candidate.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > 1) return TenantDecision.Refused(TenancyCodes.Ambiguous);
            if (source.Key == TenantCandidateSource.Host)
            {
                if (!TenantDomains.IsCanonicalHost(values[0])) return TenantDecision.Refused(TenancyCodes.Malformed);
                host = values[0];
            }
            else if (TenantIdentifier.TryParse(values[0], out var tenant)) tenants.Add(tenant);
            else return TenantDecision.Refused(TenancyCodes.Malformed);
        }
        var actor = ResolveActor(request.Principal, out var actorRefusal);
        if (actor is null) return actorRefusal!;
        if (host is not null)
        {
            Guid? domainTenant;
            try { domainTenant = await directory.FindTenantByDomainAsync(host, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
            {
                return TenantDecision.Refused(TenancyCodes.DirectoryUnavailable);
            }
            if (domainTenant is { } named) tenants.Add(named);
        }
        if (tenants.Count == 0) return TenantDecision.Refused(TenancyCodes.NoCandidate);
        if (tenants.Count > 1) return TenantDecision.Refused(TenancyCodes.Conflict);
        var tenantId = tenants.Single();
        var refusal = await CheckTenantAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? await CheckMembershipAsync(actor, tenantId, cancellationToken).ConfigureAwait(false);
        return refusal ?? TenantDecision.Established(new(TenantScope.Tenant, tenantId, actor, null, TenantContextSource.Request, Guid.NewGuid()));
    }

    private async ValueTask<TenantDecision> ResolveHostAsync(TenantResolutionRequest request, CancellationToken cancellationToken)
    {
        // Host scope is never derived from, or combined with, a tenant named by the request.
        if (request.Candidates.Any(candidate => candidate.Source is TenantCandidateSource.Route or TenantCandidateSource.Header))
            return TenantDecision.Refused(TenancyCodes.HostCandidates);
        foreach (var host in request.Candidates.Where(candidate => candidate.Source == TenantCandidateSource.Host).Select(candidate => candidate.Value).Distinct())
        {
            if (!TenantDomains.IsCanonicalHost(host)) return TenantDecision.Refused(TenancyCodes.Malformed);
            try
            {
                if (await directory.FindTenantByDomainAsync(host, cancellationToken).ConfigureAwait(false) is not null)
                    return TenantDecision.Refused(TenancyCodes.HostCandidates);
            }
            catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
            {
                return TenantDecision.Refused(TenancyCodes.DirectoryUnavailable);
            }
        }
        var actor = ResolveActor(request.Principal, out var actorRefusal);
        if (actor is null) return actorRefusal!;
        return await GrantHostAsync(actor, request.OperationId!, request.Reason!, TenantContextSource.HostAuthority, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TenantDecision> GrantHostAsync(ActorReference actor, string operationId, string reason, TenantContextSource source,
        CancellationToken cancellationToken)
    {
        if (services.GetService<IHostAuthority>() is not { } authority) return TenantDecision.Refused(TenancyCodes.SeamUnavailable);
        HostOperation? operation;
        try { operation = await authority.AuthorizeAsync(actor, operationId, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            return TenantDecision.Refused(TenancyCodes.SeamUnavailable);
        }
        // The grant carries the reach of exactly the operation asked for, never of another one the seam returned.
        if (operation is null || operation.Id != operationId) return TenantDecision.Refused(TenancyCodes.HostAuthorityRefused);
        // The record is written before the scope exists; without it there is no scope.
        if (services.GetService<IHostScopeAudit>() is not { } audit) return TenantDecision.Refused(TenancyCodes.AuditUnavailable);
        var unit = Guid.NewGuid();
        var at = time.GetUtcNow();
        try
        {
            await audit.RecordAsync(new(unit, actor, operationId, reason, source, at, Activity.Current?.TraceId.ToHexString()), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            return TenantDecision.Refused(TenancyCodes.AuditUnavailable);
        }
        return TenantDecision.Established(new(TenantScope.Host, null, actor, new(operation, reason, at), source, unit));
    }

    private ActorReference? ResolveActor(ClaimsPrincipal principal, out TenantDecision? refusal)
    {
        refusal = null;
        if (services.GetService<ITenantActorResolver>() is not { } resolver)
        {
            refusal = TenantDecision.Refused(TenancyCodes.SeamUnavailable);
            return null;
        }
        ActorReference? actor;
        try { actor = resolver.Resolve(principal); }
        catch (Exception)
        {
            refusal = TenantDecision.Refused(TenancyCodes.SeamUnavailable);
            return null;
        }
        if (actor is null) refusal = TenantDecision.Refused(TenancyCodes.Unauthenticated);
        return actor;
    }

    private async ValueTask<TenantDecision?> CheckTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await directory.FindTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
            return entry is { State: TenantState.Active } ? null : TenantDecision.Refused(TenancyCodes.UnknownTenant);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            return TenantDecision.Refused(TenancyCodes.DirectoryUnavailable);
        }
    }

    private async ValueTask<TenantDecision?> CheckMembershipAsync(ActorReference actor, Guid tenantId, CancellationToken cancellationToken)
    {
        if (services.GetService<ITenantMembership>() is not { } membership) return TenantDecision.Refused(TenancyCodes.SeamUnavailable);
        try
        {
            return await membership.IsMemberAsync(actor, tenantId, cancellationToken).ConfigureAwait(false)
                ? null : TenantDecision.Refused(TenancyCodes.NotMember);
        }
        catch (Exception exception) when (!IsCancellation(exception, cancellationToken))
        {
            return TenantDecision.Refused(TenancyCodes.SeamUnavailable);
        }
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}
