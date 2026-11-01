using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Jobs;

public sealed class JobStore(PostgreSqlModule module, TimeProvider clock, JobPolicy policy)
{
    public const string CancelPermission = "jobs.cancel";
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
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await work.Context.Set<JobRecord>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || IsTerminal(row.State)) return false;
        row.CancellationRequested = true;
        if (row.State != JobState.Running)
        {
            row.State = JobState.CancelledBeforeRun;
            row.Code = "CancelledBeforeDispatch";
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
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await work.Context.Set<JobSchedule>().SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null || !row.Enabled) return false;
        row.Enabled = false;
        row.Code = "Stopped";
        return (await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
    }

    internal static bool IsTerminal(JobState state) => state is JobState.Completed or JobState.Failed or JobState.CancelledBeforeRun
        or JobState.CancelledDuringRun or JobState.Denied;

    private static void ValidatePayload(string kind, string payload)
    {
        if (!PermissionNames.IsValid(kind) || payload is null || payload.Length > 1_048_576)
            throw new ArgumentException("Declare a job kind and a bounded payload.");
    }
}
