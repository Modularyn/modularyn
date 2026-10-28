using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;

namespace Modularyn.Framework.Authorization;

/// <summary>
/// The worker entry point of authorization (TM-G02-019, TM-G02-027). A job, message or file step runs through SEC-04's
/// <see cref="TenantWorkRunner"/>, which revalidates the propagated tenant and its producer; then, before any work, the
/// evaluator decides the permission for the running principal, such as the job's service identity, against the
/// producer's current authority. A captured decision is never replayed: a producer whose grant was revoked after it
/// enqueued the work is refused, and the work is recorded as denied.
/// </summary>
public sealed class AuthorizedWorkRunner(TenantWorkRunner runner, IPermissionEvaluator evaluator)
{
    /// <summary>
    /// Runs the work only when the permission is permitted now. A refusal is <see cref="TenantWorkDisposition.Denied"/>, with
    /// the decision's code, or <see cref="TenantWorkDisposition.Deferred"/> when authorization data is unavailable.
    /// </summary>
    public async Task<TenantWorkOutcome> RunAsync(ExecutionPrincipal principal, string propagated, string permission,
        Func<TenantWorkUnit, AuthorizationDecision, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(permission);
        AuthorizationDecision? decision = null;
        var outcome = await runner.RunAsync(propagated, async (unit, token) =>
        {
            // Running work always applies the current profile: the decision is made now, from the store.
            decision = await evaluator.AuthorizeAsync(new(unit.Context, principal, permission) { RequireCurrent = true }, token).ConfigureAwait(false);
            if (decision.Permitted) await work(unit, decision, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (outcome.Disposition != TenantWorkDisposition.Completed || decision is not { Permitted: false } refused) return outcome;
        return new(refused.IsTransient ? TenantWorkDisposition.Deferred : TenantWorkDisposition.Denied, refused.Code, null);
    }
}
