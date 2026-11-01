using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Framework.Jobs;

public sealed record JobDefinition(string Kind, string Permission, Func<JobExecution, CancellationToken, Task> Handle);

/// <summary>All database effects use Work. Checkpoint checks durable cancellation, ownership and current authority.</summary>
public sealed class JobExecution
{
    private readonly JobStore store;
    private readonly IPermissionEvaluator evaluator;
    private readonly string permission;
    internal JobExecution(JobStore store, EfCoreUnitOfWork work, ExecutionPrincipal principal, JobClaim claim,
        string payload, string permission, IPermissionEvaluator evaluator)
    {
        this.store = store;
        this.evaluator = evaluator;
        this.permission = permission;
        Work = work;
        Principal = principal;
        Claim = claim;
        Payload = payload;
    }
    public EfCoreUnitOfWork Work { get; }
    public ExecutionPrincipal Principal { get; }
    public JobClaim Claim { get; }
    public string Payload { get; }

    public async Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var inspection = await EfCoreUnitOfWork.BeginAsync(store.Module, Work.Context.Tenant!, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await inspection.Context.Set<JobRecord>().AsNoTracking().SingleAsync(row => row.Id == Claim.Id, cancellationToken).ConfigureAwait(false);
        if (row.State != JobState.Running || row.Lease != Claim.Lease || row.LeaseUntil <= store.Now) throw new JobLeaseLostException();
        if (row.CancellationRequested) throw new JobCancellationException();
        var decision = await evaluator.AuthorizeAsync(new(Work.Context.Tenant!, Principal, permission) { RequireCurrent = true }, cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted) throw new JobAuthorizationException(decision.Code!, decision.IsTransient);
    }

    public async Task<bool> RenewLeaseAsync(CancellationToken cancellationToken = default)
    {
        await CheckpointAsync(cancellationToken).ConfigureAwait(false);
        return await store.RenewAsync(Work.Context.Tenant!, Claim, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class JobCancellationException() : OperationCanceledException("The job's durable cancellation was requested.");
public sealed class JobLeaseLostException() : InvalidOperationException("The job no longer owns an unexpired lease.");
internal sealed class JobAuthorizationException(string code, bool transient) : InvalidOperationException("Current authority refused the job step.")
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
}
