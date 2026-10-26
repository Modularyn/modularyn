using System.Collections.Concurrent;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

/// <summary>
/// The one path every credential verification takes (TM-G02-015): it locks the per-source and per-subject counters,
/// refuses without verification while either limit applies, and records the outcome before the locks are released.
/// </summary>
/// <remarks>
/// The counters stay locked across the verification, so concurrent attempts on one counter are decided one at a time.
/// So that a flood of parallel attempts cannot hold a database connection each while it waits, this host admits one
/// attempt per counter at a time and refuses the others at once, before any database access, with a short retry time.
/// </remarks>
internal sealed class VerificationGate(IVerificationLedger ledger, IdentityPolicies policies)
{
    // A failure is recorded even when the caller has gone away, so abandoning a request cannot avoid being counted.
    private static readonly TimeSpan RecordingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<ThrottleKey, byte> inFlight = new();

    internal async Task<GateEntry> EnterAsync(ThrottleKey source, ThrottleKey subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!inFlight.TryAdd(source, 0)) return GateEntry.Busy(now + BusyRetry);
        if (!inFlight.TryAdd(subject, 0))
        {
            inFlight.TryRemove(source, out _);
            return GateEntry.Busy(now + BusyRetry);
        }
        try
        {
            var entry = await ledger.AcquireAsync([source, subject], now, cancellationToken).ConfigureAwait(false);
            var policy = policies.Throttling;
            var bySource = VerificationThrottle.EvaluateSource(policy, entry.Get(source), now);
            var bySubject = VerificationThrottle.EvaluateSubject(policy, entry.Get(subject), now);
            DateTimeOffset? refusedUntil = null;
            foreach (var decision in new[] { bySource, bySubject }.Where(decision => !decision.Allowed))
                if (refusedUntil is null || decision.RetryAt > refusedUntil) refusedUntil = decision.RetryAt;
            return new GateEntry(entry, source, subject, policy, now, refusedUntil, () => Release(source, subject));
        }
        catch
        {
            Release(source, subject);
            throw;
        }
    }

    private void Release(ThrottleKey source, ThrottleKey subject)
    {
        inFlight.TryRemove(source, out _);
        inFlight.TryRemove(subject, out _);
    }

    internal sealed class GateEntry : IAsyncDisposable
    {
        private readonly IVerificationLedgerEntry? entry;
        private readonly ThrottleKey? source;
        private readonly ThrottleKey? subject;
        private readonly ThrottlePolicy? policy;
        private readonly DateTimeOffset now;
        private Action? release;

        internal GateEntry(IVerificationLedgerEntry? entry, ThrottleKey? source, ThrottleKey? subject, ThrottlePolicy? policy, DateTimeOffset now,
            DateTimeOffset? refusedUntil, Action? release)
        {
            this.entry = entry;
            this.source = source;
            this.subject = subject;
            this.policy = policy;
            this.now = now;
            this.release = release;
            RefusedUntil = refusedUntil;
        }

        internal static GateEntry Busy(DateTimeOffset retryAt) => new(null, null, null, null, retryAt, retryAt, null);

        /// <summary>Set when a limit applies or another attempt on a counter is in flight: refuse without verifying anything.</summary>
        internal DateTimeOffset? RefusedUntil { get; }

        internal async Task FailedAsync()
        {
            var held = Held();
            held.Entry.Set(held.Source, VerificationThrottle.SourceFailed(held.Policy, held.Entry.Get(held.Source), now));
            held.Entry.Set(held.Subject, VerificationThrottle.SubjectFailed(held.Policy, held.Entry.Get(held.Subject), now));
            using var timeout = new CancellationTokenSource(RecordingTimeout);
            await held.Entry.CommitAsync(timeout.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// Clears the subject's failures. The source's failures stay counted, so succeeding with one account cannot reset the
        /// block on a source guessing at others.
        /// </summary>
        internal async Task SucceededAsync()
        {
            var held = Held();
            held.Entry.Set(held.Subject, ThrottleCounter.Empty);
            using var timeout = new CancellationTokenSource(RecordingTimeout);
            await held.Entry.CommitAsync(timeout.Token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (entry is not null) await entry.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref release, null)?.Invoke();
            }
        }

        private (IVerificationLedgerEntry Entry, ThrottleKey Source, ThrottleKey Subject, ThrottlePolicy Policy) Held() =>
            RefusedUntil is null && entry is not null ? (entry, source!, subject!, policy!)
                : throw new InvalidOperationException("A refused attempt records no outcome.");
    }
}
