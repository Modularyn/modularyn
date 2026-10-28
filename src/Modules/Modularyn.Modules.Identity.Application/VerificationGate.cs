using System.Collections.Concurrent;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

/// <summary>
/// The one path every credential verification takes (TM-G02-015, TH-G02-076). It locks the source's counter, the
/// subject's, and the source's own counter on the subject; refuses without verifying while any of their limits applies,
/// counting a refusal that the subject's limits caused against the source; and records the outcome before the locks are
/// released. <see cref="ThrottlePolicy"/> states the rule.
/// </summary>
/// <remarks>
/// The counters stay locked across the verification, so concurrent attempts on one counter are decided one at a time.
/// So that parallel attempts cannot each hold a database connection while they wait, this host admits one attempt per
/// subject at a time and refuses the others at once with a short retry time, and lets attempts from one source wait
/// their turn in memory, a few at most, before any database access: concurrent sign-ins from one network address wait
/// instead of failing, and a flood from one address is refused. An attempt from <see cref="ClientSources.Unknown"/> has
/// no source counters and does not wait.
/// </remarks>
internal sealed class VerificationGate(IVerificationLedger ledger, IdentityPolicies policies)
{
    /// <summary>How many attempts from one source may wait behind the one in progress on this host.</summary>
    internal const int SourceWaiters = 8;

    /// <summary>How long an attempt waits for its source's turn before it is refused with a short retry time.</summary>
    internal static readonly TimeSpan SourceWait = TimeSpan.FromSeconds(2);

    // A failure is recorded even when the caller has gone away, so abandoning a request cannot avoid being counted.
    private static readonly TimeSpan RecordingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<ThrottleKey, byte> subjectsInFlight = new();
    private readonly KeyedTurns sourceTurns = new(SourceWaiters, SourceWait);

    internal async Task<GateEntry> EnterAsync(string source, ThrottleKey subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var sourceKey = ClientSources.IsUnknown(source) ? null : ThrottleKey.ForSource(source);
        var onSubjectKey = sourceKey is null ? null : ThrottleKey.ForSourceOnSubject(sourceKey, subject);
        IDisposable? sourceTurn = null;
        if (sourceKey is not null && (sourceTurn = await sourceTurns.EnterAsync(sourceKey, cancellationToken).ConfigureAwait(false)) is null)
            return GateEntry.Busy(now + BusyRetry);
        if (!subjectsInFlight.TryAdd(subject, 0))
        {
            sourceTurn?.Dispose();
            return GateEntry.Busy(now + BusyRetry);
        }
        void Release()
        {
            subjectsInFlight.TryRemove(subject, out _);
            sourceTurn?.Dispose();
        }
        try
        {
            ThrottleKey[] keys = sourceKey is null ? [subject] : [sourceKey, subject, onSubjectKey!];
            var entry = await ledger.AcquireAsync(keys, now, cancellationToken).ConfigureAwait(false);
            try
            {
                var policy = policies.Throttling;
                var counters = new Counters(sourceKey, subject, onSubjectKey);
                var bySubject = VerificationThrottle.EvaluateSubject(policy, entry.Get(subject), now);
                var byOwnCounter = onSubjectKey is null ? ThrottleDecision.Allow
                    : VerificationThrottle.EvaluateSourceOnSubject(policy, entry.Get(onSubjectKey), now);
                // A blocked source is refused and nothing is recorded; its block already runs. The refusal names the latest of
                // the block's end, the subject's delay and the source's own delay, so a client that honors it is not refused
                // again, and counted again, by a limit that outlasts the block.
                if (sourceKey is not null && VerificationThrottle.EvaluateSource(policy, entry.Get(sourceKey), now) is { Allowed: false } blocked)
                    return new GateEntry(entry, counters, policy, now, Later(blocked.RetryAt, Later(bySubject.RetryAt, byOwnCounter.RetryAt)), Release);
                if (bySubject.Allowed && byOwnCounter.Allowed) return new GateEntry(entry, counters, policy, now, null, Release);

                // A refusal the subject's limits caused counts against the source, never against the subject, so a source
                // that keeps trying a delayed subject reaches its own block.
                var retryAt = Later(bySubject.RetryAt, byOwnCounter.RetryAt);
                if (sourceKey is not null)
                {
                    var counted = VerificationThrottle.SourceFailed(policy, entry.Get(sourceKey), now);
                    entry.Set(sourceKey, counted);
                    using var timeout = new CancellationTokenSource(RecordingTimeout);
                    await entry.CommitAsync(timeout.Token).ConfigureAwait(false);
                    retryAt = Later(retryAt, counted.BlockedUntil);
                }
                return new GateEntry(entry, counters, policy, now, retryAt, Release);
            }
            catch
            {
                await entry.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            Release();
            throw;
        }
    }

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    /// <summary>The counters one attempt holds: the source's, absent for an unknown source, the subject's and the source's own on it.</summary>
    internal sealed record Counters(ThrottleKey? Source, ThrottleKey Subject, ThrottleKey? SourceOnSubject);

    internal sealed class GateEntry : IAsyncDisposable
    {
        private readonly IVerificationLedgerEntry? entry;
        private readonly Counters? counters;
        private readonly ThrottlePolicy? policy;
        private readonly DateTimeOffset now;
        private Action? release;

        internal GateEntry(IVerificationLedgerEntry? entry, Counters? counters, ThrottlePolicy? policy, DateTimeOffset now, DateTimeOffset? refusedUntil,
            Action? release)
        {
            this.entry = entry;
            this.counters = counters;
            this.policy = policy;
            this.now = now;
            this.release = release;
            RefusedUntil = refusedUntil;
        }

        internal static GateEntry Busy(DateTimeOffset retryAt) => new(null, null, null, retryAt, retryAt, null);

        /// <summary>Set when a limit applies or another attempt on a counter is in flight: refuse without verifying anything.</summary>
        internal DateTimeOffset? RefusedUntil { get; }

        internal async Task FailedAsync()
        {
            var (held, keys, rules) = Held();
            // The source's own counter is seeded from the subject's count before this failure, so it never lags the subject.
            var subjectBefore = held.Get(keys.Subject);
            if (keys.Source is { } source) held.Set(source, VerificationThrottle.SourceFailed(rules, held.Get(source), now));
            held.Set(keys.Subject, VerificationThrottle.SubjectFailed(rules, subjectBefore, now));
            if (keys.SourceOnSubject is { } own) held.Set(own, VerificationThrottle.SourceOnSubjectFailed(rules, held.Get(own), subjectBefore, now));
            using var timeout = new CancellationTokenSource(RecordingTimeout);
            await held.CommitAsync(timeout.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// Clears the subject's failures and the source's own on it. The source's failures stay counted, so succeeding with
        /// one account cannot reset the block on a source guessing at others, and every other source keeps its own delay.
        /// </summary>
        internal async Task SucceededAsync()
        {
            var (held, keys, _) = Held();
            held.Set(keys.Subject, ThrottleCounter.Empty);
            if (keys.SourceOnSubject is { } own) held.Set(own, ThrottleCounter.Empty);
            using var timeout = new CancellationTokenSource(RecordingTimeout);
            await held.CommitAsync(timeout.Token).ConfigureAwait(false);
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

        private (IVerificationLedgerEntry Entry, Counters Keys, ThrottlePolicy Policy) Held() =>
            RefusedUntil is null && entry is not null ? (entry, counters!, policy!)
                : throw new InvalidOperationException("A refused attempt records no outcome.");
    }

    /// <summary>
    /// One turn at a time per key, in memory, with at most a fixed number of waiters behind it and a bounded wait. A key
    /// that nobody holds or waits for keeps no state.
    /// </summary>
    private sealed class KeyedTurns(int waiters, TimeSpan wait)
    {
        private readonly Dictionary<ThrottleKey, Slot> slots = [];

        /// <summary>The key's turn, or null when too many already wait for it or the wait ran out.</summary>
        internal async Task<IDisposable?> EnterAsync(ThrottleKey key, CancellationToken cancellationToken)
        {
            Slot slot;
            lock (slots)
            {
                if (!slots.TryGetValue(key, out slot!)) slots[key] = slot = new Slot();
                if (slot.Holders > waiters) return null;
                slot.Holders++;
            }
            var entered = false;
            try
            {
                entered = await slot.Turn.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!entered) Leave(key, slot, held: false);
            }
            return entered ? new Turn(this, key, slot) : null;
        }

        private void Leave(ThrottleKey key, Slot slot, bool held)
        {
            if (held) slot.Turn.Release();
            lock (slots)
            {
                if (--slot.Holders == 0) slots.Remove(key);
            }
        }

        private sealed class Slot
        {
            internal SemaphoreSlim Turn { get; } = new(1, 1);
            internal int Holders { get; set; }
        }

        private sealed class Turn(KeyedTurns owner, ThrottleKey key, Slot slot) : IDisposable
        {
            private int released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) == 0) owner.Leave(key, slot, held: true);
            }
        }
    }
}
