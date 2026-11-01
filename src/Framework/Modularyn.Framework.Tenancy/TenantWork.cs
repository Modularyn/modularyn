using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Tenancy;

public enum TenantWorkDisposition
{
    /// <summary>The work ran in its revalidated context.</summary>
    Completed,
    /// <summary>Revalidation refused the reference for good; record the disposition and do not retry.</summary>
    Denied,
    /// <summary>A directory or seam was unavailable; retry under the caller's bounded policy.</summary>
    Deferred
}

/// <summary>The outcome of one unit of propagated work, with the context it ran in, for its disposition record.</summary>
public sealed record TenantWorkOutcome(TenantWorkDisposition Disposition, string? Code, TenantContext? Context);

/// <summary>The services and context of one unit of propagated work.</summary>
public sealed record TenantWorkUnit(IServiceProvider Services, TenantContext Context);

/// <summary>
/// Runs propagated work (an event, job or file-processing step) in a new service scope whose context is reconstructed
/// only from the revalidated reference. The scope, and with it the context, ends when the work ends, on every path,
/// so a reused worker carries nothing into its next unit.
/// </summary>
public sealed class TenantWorkRunner(IServiceScopeFactory scopes, IPropagationSeal? seal = null)
{
    /// <summary>
    /// Opens the stored reference under its seal and the binding its store expects, before anything runs. A malformed one
    /// is denied (<see cref="TenancyCodes.Malformed"/>), as is one whose seal or binding does not verify, such as one copied
    /// from another row (<see cref="TenancyCodes.Unsealed"/>); without an available seal the work is deferred.
    /// </summary>
    public Task<TenantWorkOutcome> RunAsync(string propagated, PropagationBinding binding, Func<TenantWorkUnit, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(work);
        if (!PropagatedTenantContext.TryParse(propagated, out _))
            return Task.FromResult(new TenantWorkOutcome(TenantWorkDisposition.Denied, TenancyCodes.Malformed, null));
        if (seal is not { IsAvailable: true })
            return Task.FromResult(new TenantWorkOutcome(TenantWorkDisposition.Deferred, TenancyCodes.SeamUnavailable, null));
        return PropagatedTenantContext.TryOpen(propagated, binding, seal, out var opened)
            ? RunAsync(opened!, work, cancellationToken)
            : Task.FromResult(new TenantWorkOutcome(TenantWorkDisposition.Denied, TenancyCodes.Unsealed, null));
    }

    public async Task<TenantWorkOutcome> RunAsync(PropagatedTenantContext propagated, Func<TenantWorkUnit, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propagated);
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = scopes.CreateAsyncScope();
        var execution = scope.ServiceProvider.GetRequiredService<TenantExecution>();
        try
        {
            var decision = await execution.EstablishPropagatedAsync(propagated, cancellationToken).ConfigureAwait(false);
            if (!decision.Succeeded)
                return new(decision.Terminal ? TenantWorkDisposition.Denied : TenantWorkDisposition.Deferred, decision.Code, null);
            await work(new(scope.ServiceProvider, decision.Context!), cancellationToken).ConfigureAwait(false);
            return new(TenantWorkDisposition.Completed, null, decision.Context);
        }
        finally { execution.End(); }
    }
}
