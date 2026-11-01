using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Jobs;

/// <summary>
/// The durable job store of one module. Operator cancellations and schedule stops are recorded through
/// <paramref name="audit"/> in their own transaction; without it neither is made.
/// </summary>
public sealed class JobStore(PostgreSqlModule module, TimeProvider clock, JobPolicy policy, IAuditTrail? audit = null)
{
    public const string CancelPermission = "jobs.cancel";
    public const string CancelledAction = "jobs.cancelled";
    public const string ScheduleStoppedAction = "jobs.schedule-stopped";
    public PostgreSqlModule Module { get; } = module;
    public JobPolicy Policy { get; } = policy;
    internal DateTimeOffset Now => UtcMicroseconds.Now(clock);

    public Guid Enqueue(EfCoreUnitOfWork work, JobRequest request) => Enqueue(work, request, null);

    internal Guid Enqueue(EfCoreUnitOfWork work, JobRequest request, Guid? scheduleId)
    {
        RequireProducer(work);
        ValidatePayload(request.Kind, request.Payload);
        var row = new JobRecord
        {
            Id = Guid.NewGuid(), Kind = request.Kind, Payload = request.Payload,
            Propagated = PropagatedTenantContext.Capture(work.Context.Tenant!, clock).ToJson(), CreatedAt = Now,
            DueAt = UtcMicroseconds.Normalize(request.DueAt), AvailableAt = UtcMicroseconds.Normalize(request.DueAt), ScheduleId = scheduleId
        };
        work.Context.Add(row);
        return row.Id;
    }

    public Guid Schedule(EfCoreUnitOfWork work, string kind, string payload, DailySchedule schedule, DateOnly firstDate)
    {
        RequireProducer(work);
        ValidatePayload(kind, payload);
        var row = new JobSchedule
        {
            Id = Guid.NewGuid(), Kind = kind, Payload = payload, TimeZone = schedule.TimeZone, LocalTime = schedule.LocalTime,
            Misfire = schedule.Misfire, NextDate = firstDate, NextCheckAt = Now,
            Propagated = PropagatedTenantContext.Capture(work.Context.Tenant!, clock).ToJson()
        };
        work.Context.Add(row);
        return row.Id;
    }

    public async Task<bool> CancelAsync(TenantContext context, ExecutionPrincipal principal, IPermissionEvaluator evaluator, Guid id,
        CancellationToken cancellationToken = default)
    {
        var decision = await evaluator.AuthorizeAsync(new(context, principal, CancelPermission) { RequireCurrent = true }, cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted) return false;
        var trail = RequireAudit();
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await work.Context.Set<JobRecord>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || IsTerminal(row.State)) return false;
        // Who cancelled which job and when, with the state and attempt it was in, recorded with the cancellation itself.
        await trail.RecordAsync(work, AuditEntry.InContext(CancelledAction, context, principal) with
        {
            ResourceType = "job", ResourceId = row.Id.ToString("D"), Detail = $"kind {row.Kind} state {row.State} attempts {row.Attempts}"
        }, cancellationToken).ConfigureAwait(false);
        row.CancellationRequested = true;
        if (row.State != JobState.Running)
        {
            row.State = JobState.CancelledBeforeRun;
            row.Code = "CancelledBeforeDispatch";
            row.Payload = DurablePayloads.Redacted;
        }
        else row.Code = "CancellationRequested";
        return (await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    internal async Task<JobClaim?> ClaimAsync(TenantContext context, Guid id, CancellationToken token)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: token).ConfigureAwait(false);
        var row = await work.Context.Set<JobRecord>().SingleOrDefaultAsync(row => row.Id == id, token).ConfigureAwait(false);
        var now = Now;
        if (row is null || IsTerminal(row.State) || row.AvailableAt > now || row.DueAt > now
            || row.State == JobState.Running && row.LeaseUntil > now) return null;
        if (row.State == JobState.Running && row.Lease is { } previous)
        {
            var lost = await work.Context.Set<JobAttempt>().SingleAsync(attempt => attempt.JobId == id && attempt.Lease == previous, token).ConfigureAwait(false);
            lost.State = row.CancellationRequested ? JobAttemptState.Cancelled : JobAttemptState.LeaseLost;
            lost.EndedAt = now;
            lost.Code = row.CancellationRequested ? "CancellationAcknowledged" : "LeaseExpired";
        }
        if (row.CancellationRequested || row.Attempts >= Policy.MaxAttempts)
        {
            row.State = row.CancellationRequested ? JobState.CancelledDuringRun : JobState.Failed;
            row.Code = row.CancellationRequested ? "CancellationAcknowledged" : "RetryExhausted";
            row.Payload = DurablePayloads.Redacted;
            await work.CompleteAsync(token).ConfigureAwait(false);
            return null;
        }
        row.Lease = Guid.NewGuid();
        row.LeaseUntil = now + Policy.Lease;
        row.State = JobState.Running;
        row.Deferrals = 0;
        row.Attempts++;
        row.Code = "Running";
        work.Context.Add(new JobAttempt
        {
            JobId = row.Id, Lease = row.Lease.Value, Number = row.Attempts, StartedAt = now, State = JobAttemptState.Running
        });
        var claim = new JobClaim(row.Id, row.Lease.Value, row.Attempts, row.LeaseUntil.Value);
        return (await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed ? claim : null;
    }

    /// <summary>Renewal checks the original token and never revives an already expired lease.</summary>
    internal async Task<bool> RenewAsync(TenantContext context, JobClaim claim, CancellationToken token)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: token).ConfigureAwait(false);
        var row = await work.Context.Set<JobRecord>().SingleOrDefaultAsync(row => row.Id == claim.Id, token).ConfigureAwait(false);
        if (row is null || row.State != JobState.Running || row.Lease != claim.Lease || row.LeaseUntil <= Now || row.CancellationRequested) return false;
        row.LeaseUntil = Now + Policy.Lease;
        return (await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    internal async Task FinishAsync(TenantContext context, JobClaim claim, JobAttemptState outcome, string code, CancellationToken token)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: token).ConfigureAwait(false);
        var row = await work.Context.Set<JobRecord>().SingleAsync(row => row.Id == claim.Id, token).ConfigureAwait(false);
        if (row.State != JobState.Running || row.Lease != claim.Lease) return;
        if (row.CancellationRequested) (outcome, code) = (JobAttemptState.Cancelled, "CancellationAcknowledged");
        else if (row.LeaseUntil <= Now) (outcome, code) = (JobAttemptState.LeaseLost, "LeaseExpired");
        var attempt = await work.Context.Set<JobAttempt>().SingleAsync(entry => entry.JobId == row.Id && entry.Lease == claim.Lease, token).ConfigureAwait(false);
        attempt.State = outcome;
        attempt.Code = code;
        attempt.EndedAt = Now;
        row.Code = code;
        row.State = outcome switch
        {
            JobAttemptState.Cancelled => JobState.CancelledDuringRun,
            JobAttemptState.Denied => JobState.Denied,
            _ => row.Attempts >= Policy.MaxAttempts ? JobState.Failed : JobState.Retry
        };
        row.AvailableAt = Now + Policy.Backoff(row.Attempts);
        row.LeaseUntil = null;
        row.Lease = null;
        Redact(row);
        await work.CompleteAsync(token).ConfigureAwait(false);
    }

    internal void RequireProducer(EfCoreUnitOfWork work)
    {
        if (!ReferenceEquals(work.Context.Module, Module) || work.TransactionId is null || work.State != UnitOfWorkState.Active
            || work.Context.Tenant is not { IsActive: true, Scope: TenantScope.Tenant })
            throw new InvalidOperationException("Durable jobs require the producing module's active tenant transaction.");
    }

    public async Task<bool> StopScheduleAsync(TenantContext context, ExecutionPrincipal principal, IPermissionEvaluator evaluator, Guid id,
        CancellationToken cancellationToken = default)
    {
        var decision = await evaluator.AuthorizeAsync(new(context, principal, ScheduleWorker.SchedulePermission) { RequireCurrent = true }, cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted) return false;
        var trail = RequireAudit();
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await work.Context.Set<JobSchedule>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || !row.Enabled) return false;
        await trail.RecordAsync(work, AuditEntry.InContext(ScheduleStoppedAction, context, principal) with
        {
            ResourceType = "job.schedule", ResourceId = row.Id.ToString("D"), Detail = "kind " + row.Kind + " next " + row.NextDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        row.Enabled = false;
        row.Code = "Stopped";
        row.Payload = DurablePayloads.Redacted;
        return (await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    private IAuditTrail RequireAudit() => audit
        ?? throw new InvalidOperationException("An operator's cancellation or schedule stop is recorded in audit, and this store has no audit trail, so nothing is changed.");

    /// <summary>
    /// The unit's tenant's jobs, newest first, under the diagnostics permission at the current profile: identity, kind,
    /// state, budgets, code and times, and of each payload only whether it is still held and its length.
    /// </summary>
    public async Task<IReadOnlyList<JobDiagnostic>> HistoryAsync(TenantContext context, ExecutionPrincipal? principal, IPermissionEvaluator? evaluator,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await DiagnosticsAccess.RequireTenantAsync(evaluator, context, principal, cancellationToken).ConfigureAwait(false);
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var rows = await work.Context.Set<JobRecord>().AsNoTracking().OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id).Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new JobDiagnostic(row.Id, row.Kind, row.State, row.Attempts, row.Deferrals, row.Code, row.CreatedAt, row.DueAt,
            row.ScheduleId, PayloadSummary.Of(row.Payload))).ToArray();
    }

    /// <summary>A job that can no longer run keeps no payload: it is redacted in the transaction that ends the job.</summary>
    internal static void Redact(JobRecord row)
    {
        if (IsTerminal(row.State)) row.Payload = DurablePayloads.Redacted;
    }

    internal static bool IsTerminal(JobState state) => state is JobState.Completed or JobState.Failed or JobState.CancelledBeforeRun
        or JobState.CancelledDuringRun or JobState.Denied;

    private static void ValidatePayload(string kind, string payload)
    {
        if (!PermissionNames.IsValid(kind)) throw new ArgumentException("Declare a job kind and a bounded payload.");
        // The payload rule: references only, never a secret form or a propagated context (TM-G02-039).
        DurablePayloads.Require(payload, nameof(payload));
    }
}
