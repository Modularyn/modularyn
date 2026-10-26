using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

/// <summary>
/// The one path every credential verification takes (TM-G02-015): it locks the per-source and per-subject counters,
/// refuses without verification while either limit applies, and records the outcome before the locks are released.
/// </summary>
internal sealed class VerificationGate(IVerificationLedger ledger, IdentityPolicies policies)
{
    // A failure is recorded even when the caller has gone away, so abandoning a request cannot avoid being counted.
    private static readonly TimeSpan RecordingTimeout = TimeSpan.FromSeconds(30);

    internal async Task<GateEntry> EnterAsync(ThrottleKey source, ThrottleKey subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entry = await ledger.AcquireAsync([source, subject], now, cancellationToken).ConfigureAwait(false);
        var policy = policies.Throttling;
        var bySource = VerificationThrottle.EvaluateSource(policy, entry.Get(source), now);
        var bySubject = VerificationThrottle.EvaluateSubject(policy, entry.Get(subject), now);
        DateTimeOffset? refusedUntil = null;
        foreach (var decision in new[] { bySource, bySubject }.Where(decision => !decision.Allowed))
            if (refusedUntil is null || decision.RetryAt > refusedUntil) refusedUntil = decision.RetryAt;
        return new GateEntry(entry, source, subject, policy, now, refusedUntil);
    }

    internal sealed class GateEntry(IVerificationLedgerEntry entry, ThrottleKey source, ThrottleKey subject, ThrottlePolicy policy,
        DateTimeOffset now, DateTimeOffset? refusedUntil) : IAsyncDisposable
    {
        /// <summary>Set when a limit applies: the attempt must be refused without verifying anything.</summary>
        internal DateTimeOffset? RefusedUntil { get; } = refusedUntil;

        internal async Task FailedAsync()
        {
            entry.Set(source, VerificationThrottle.SourceFailed(policy, entry.Get(source), now));
            entry.Set(subject, VerificationThrottle.SubjectFailed(policy, entry.Get(subject), now));
            using var timeout = new CancellationTokenSource(RecordingTimeout);
            await entry.CommitAsync(timeout.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// Clears the subject's failures. The source's failures stay counted, so succeeding with one account cannot reset the
        /// block on a source guessing at others.
        /// </summary>
        internal async Task SucceededAsync()
        {
            entry.Set(subject, ThrottleCounter.Empty);
            using var timeout = new CancellationTokenSource(RecordingTimeout);
            await entry.CommitAsync(timeout.Token).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => entry.DisposeAsync();
    }
}
