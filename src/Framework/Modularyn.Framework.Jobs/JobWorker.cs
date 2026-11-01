using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Jobs;

public sealed class JobWorker
{
    public const string ExecutePermission = "jobs.execute";
    private readonly JobStore store;
    private readonly AuthorizedWorkRunner runner;
    private readonly IPermissionEvaluator evaluator;
    private readonly IReadOnlyDictionary<string, JobDefinition> definitions;

    public JobWorker(JobStore store, AuthorizedWorkRunner runner, IPermissionEvaluator evaluator, IEnumerable<JobDefinition> definitions)
    {
        this.store = store;
        this.runner = runner;
        this.evaluator = evaluator;
        var entries = definitions.ToArray();
        if (entries.Any(entry => !PermissionNames.IsValid(entry.Kind) || !PermissionNames.IsValid(entry.Permission) || entry.Handle is null))
            throw new ArgumentException("Every handler needs a declared kind, permission and implementation.", nameof(definitions));
        this.definitions = entries.ToDictionary(entry => entry.Kind, StringComparer.Ordinal);
    }

    public async Task<int> RunDueAsync(TenantContext host, ExecutionPrincipal principal, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var now = store.Now;
        await using var scan = await EfCoreUnitOfWork.BeginAsync(store.Module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidates = await (await scan.Context.AcrossTenantsAsync<JobRecord>(cancellationToken).ConfigureAwait(false)).Query.AsNoTracking()
            .Where(row => row.DueAt <= now && row.AvailableAt <= now && (row.State == JobState.Queued || row.State == JobState.Retry
                || row.State == JobState.Running && row.LeaseUntil <= now))
            .OrderBy(row => row.AvailableAt).ThenBy(row => row.Id).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var completed = 0;
        foreach (var candidate in candidates)
        {
            var definition = definitions.GetValueOrDefault(candidate.Kind);
            TenantWorkOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(principal, candidate.Propagated, definition?.Permission ?? ExecutePermission, async (unit, _, token) =>
                {
                    if (unit.Context.TenantId != candidate.TenantId) throw new InvalidOperationException("The job is outside its producer's tenant.");
                    JobClaim? claim;
                    try { claim = await store.ClaimAsync(unit.Context, candidate.Id, token).ConfigureAwait(false); }
                    catch (DbUpdateException) { return; }
                    if (claim is null) return;
                    try
                    {
                        if (definition is null) throw new InvalidOperationException("The job kind has no registered handler.");
                        await using var work = await EfCoreUnitOfWork.BeginAsync(store.Module, unit.Context, cancellationToken: token).ConfigureAwait(false);
                        var execution = new JobExecution(store, work, principal, claim, candidate.Payload, definition.Permission, evaluator);
                        await execution.CheckpointAsync(token).ConfigureAwait(false);
                        await definition.Handle(execution, token).ConfigureAwait(false);
                        await execution.CheckpointAsync(token).ConfigureAwait(false);
                        // Read after any renewals. The final tracked version is the fence: cancellation or a replacement
                        // owner that commits first makes the entire effect transaction lose its concurrency check.
                        var row = await work.Context.Set<JobRecord>().SingleAsync(row => row.Id == claim.Id, token).ConfigureAwait(false);
                        if (row.CancellationRequested) throw new JobCancellationException();
                        if (row.State != JobState.Running || row.Lease != claim.Lease || row.LeaseUntil <= store.Now) throw new JobLeaseLostException();
                        row.State = JobState.Completed;
                        row.Code = "Completed";
                        var attempt = await work.Context.Set<JobAttempt>().SingleAsync(entry => entry.JobId == row.Id && entry.Lease == claim.Lease, token).ConfigureAwait(false);
                        attempt.State = JobAttemptState.Completed;
                        attempt.Code = "Completed";
                        attempt.EndedAt = store.Now;
                        await work.FlushAsync(token).ConfigureAwait(false);
                        await execution.CheckpointAsync(token).ConfigureAwait(false);
                        if ((await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed) completed++;
                        else await store.FinishAsync(unit.Context, claim, JobAttemptState.LeaseLost, "ConcurrentDisposition", token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (JobCancellationException)
                    {
                        await store.FinishAsync(unit.Context, claim, JobAttemptState.Cancelled, "CancellationAcknowledged", token).ConfigureAwait(false);
                    }
                    catch (JobLeaseLostException)
                    {
                        await store.FinishAsync(unit.Context, claim, JobAttemptState.LeaseLost, "LeaseLost", token).ConfigureAwait(false);
                    }
                    catch (JobAuthorizationException exception)
                    {
                        await store.FinishAsync(unit.Context, claim, exception.Transient ? JobAttemptState.Failed : JobAttemptState.Denied,
                            exception.Code, token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Unknown commit acknowledgement is reconciled by reading the original job and lease. A committed
                        // completion is terminal, so FinishAsync cannot turn it into a retry.
                        await store.FinishAsync(unit.Context, claim, JobAttemptState.Failed,
                            definition is null ? "UnsupportedKind" : "HandlerFailed", token).ConfigureAwait(false);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A failure outside the handler's own attempt, such as a stored reference that names another tenant
                // than its row, takes the bounded deferral policy instead of ending the pass for every other job. The
                // disposition is guarded by the scanned version, so it never overwrites a claim that committed meanwhile.
                outcome = new(TenantWorkDisposition.Deferred, "DispatchFailed", null);
            }
            if (outcome.Disposition != TenantWorkDisposition.Completed)
                await RecordRefusalAsync(host, candidate, outcome, cancellationToken).ConfigureAwait(false);
        }
        return completed;
    }

    private async Task RecordRefusalAsync(TenantContext host, JobRecord row, TenantWorkOutcome outcome, CancellationToken token)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(store.Module, host, cancellationToken: token).ConfigureAwait(false);
        var now = store.Now;
        var deferrals = row.Deferrals + (outcome.Disposition == TenantWorkDisposition.Deferred ? 1 : 0);
        var state = row.CancellationRequested ? JobState.CancelledDuringRun
            : outcome.Disposition == TenantWorkDisposition.Denied ? JobState.Denied
            : deferrals >= store.Policy.MaxAttempts ? JobState.Failed : JobState.Retry;
        var code = row.CancellationRequested ? "CancellationAcknowledged" : outcome.Code ?? "Deferred";
        var changed = await work.Sql.UpdateForTenantAsync<JobRecord, Guid, string>(row.TenantId, entry => entry.Id, row.Id, row.Version,
            entry => entry.Code, code, token).ConfigureAwait(false);
        if (!changed.Succeeded) return;
        changed = await work.Sql.UpdateForTenantAsync<JobRecord, Guid, JobState>(row.TenantId, entry => entry.Id, row.Id, changed.Version!.Value,
            entry => entry.State, state, token).ConfigureAwait(false);
        if (!changed.Succeeded) return;
        changed = await work.Sql.UpdateForTenantAsync<JobRecord, Guid, DateTimeOffset>(row.TenantId, entry => entry.Id, row.Id, changed.Version!.Value,
            entry => entry.AvailableAt, now + store.Policy.Backoff(deferrals), token).ConfigureAwait(false);
        if (!changed.Succeeded) return;
        changed = await work.Sql.UpdateForTenantAsync<JobRecord, Guid, int>(row.TenantId, entry => entry.Id, row.Id, changed.Version!.Value,
            entry => entry.Deferrals, deferrals, token).ConfigureAwait(false);
        if (!changed.Succeeded) return;
        // Revalidation may refuse an expired running job. Close its previous attempt in the same
        // host disposition transaction; it must not remain Running after ownership has ended.
        if (row.State == JobState.Running && row.Lease is { } lease)
        {
            var attempt = await (await work.Context.AcrossTenantsAsync<JobAttempt>(token).ConfigureAwait(false)).Query.AsNoTracking()
                .SingleAsync(entry => entry.TenantId == row.TenantId && entry.JobId == row.Id && entry.Lease == lease, token).ConfigureAwait(false);
            var ended = await work.Sql.UpdateForTenantAsync<JobAttempt, Guid, DateTimeOffset?>(row.TenantId, entry => entry.Lease, lease, attempt.Version,
                entry => entry.EndedAt, now, token).ConfigureAwait(false);
            if (!ended.Succeeded) return;
            ended = await work.Sql.UpdateForTenantAsync<JobAttempt, Guid, string>(row.TenantId, entry => entry.Lease, lease, ended.Version!.Value,
                entry => entry.Code, code, token).ConfigureAwait(false);
            if (!ended.Succeeded) return;
            ended = await work.Sql.UpdateForTenantAsync<JobAttempt, Guid, JobAttemptState>(row.TenantId, entry => entry.Lease, lease, ended.Version!.Value,
                entry => entry.State, row.CancellationRequested ? JobAttemptState.Cancelled
                    : state == JobState.Denied ? JobAttemptState.Denied : JobAttemptState.LeaseLost, token).ConfigureAwait(false);
            if (!ended.Succeeded) return;
        }
        changed = await work.Sql.UpdateForTenantAsync<JobRecord, Guid, Guid?>(row.TenantId, entry => entry.Id, row.Id, changed.Version!.Value,
            entry => entry.Lease, null, token).ConfigureAwait(false);
        if (!changed.Succeeded) return;
        changed = await work.Sql.UpdateForTenantAsync<JobRecord, Guid, DateTimeOffset?>(row.TenantId, entry => entry.Id, row.Id, changed.Version!.Value,
            entry => entry.LeaseUntil, null, token).ConfigureAwait(false);
        if (!changed.Succeeded) return;
        await work.CompleteAsync(token).ConfigureAwait(false);
    }
}
