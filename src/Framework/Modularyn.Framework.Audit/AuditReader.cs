using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Audit;

/// <summary>Which records a read selects: an optional time window and action, and at most <see cref="Limit"/> records, oldest first.</summary>
public sealed record AuditQuery
{
    public const int MaximumLimit = 500;

    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? Until { get; init; }
    public string? Action { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>One audit record as a read returns it. It holds references and codes only.</summary>
public sealed record AuditRecord(Guid Id, DateTimeOffset At, string Action, AuditOutcome Outcome, Guid? TenantId, ActorReference Actor,
    ActorReference? Subject, string? ResourceType, string? ResourceId, string? Detail, Guid? UnitId, string? OperationId, string? Reason,
    string? CorrelationId);

/// <summary>An audit read was refused, with the stable code of the decision that refused it and no record content.</summary>
public sealed class AuditAccessException(string code) : InvalidOperationException("The audit read was refused.")
{
    public string Code { get; } = code;
}

/// <summary>
/// Reads audit records under their own authority, separate from every administration permission and from the audit
/// writing that the framework alone does (TM-G02-052). A tenant's records need <see cref="AuditModule.ReadPermission"/> at
/// the current profile through the evaluator, in that tenant's context; every tenant's records, and the host's own, need
/// an active host scope granted for <see cref="AuditModule.HostReadOperation"/>. Each read is itself recorded, in its own
/// committed transaction, before any record is returned, and a read that cannot be recorded is refused. Nothing here or
/// anywhere else changes or deletes a record.
/// </summary>
public sealed class AuditReader
{
    private readonly AuditStore store;
    private readonly IServiceProvider services;

    internal AuditReader(AuditStore store, IServiceProvider services)
    {
        this.store = store;
        this.services = services;
    }

    public async Task<IReadOnlyList<AuditRecord>> ReadAsync(TenantContext context, ExecutionPrincipal? principal, AuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > AuditQuery.MaximumLimit) throw new ArgumentOutOfRangeException(nameof(query), "Read 1 to 500 records at a time.");
        if (query.Action is { Length: > 100 }) throw new ArgumentOutOfRangeException(nameof(query), "An action is at most 100 characters.");
        if (!context.IsActive) throw new AuditAccessException(AuthorizationCodes.NoTenantContext);
        ActorReference? subject;
        string action;
        if (context.Scope == TenantScope.Host)
        {
            if (context.HostGrant?.OperationId != AuditModule.HostReadOperation.Id) throw new AuditAccessException(AuditCodes.HostReadRequired);
            subject = null;
            action = AuditActions.AuditHostRead;
        }
        else
        {
            var evaluator = services.GetService<IPermissionEvaluator>() ?? throw new AuditAccessException(AuthorizationCodes.Unavailable);
            var decision = await evaluator.AuthorizeAsync(new(context, principal, AuditModule.ReadPermission) { RequireCurrent = true }, cancellationToken)
                .ConfigureAwait(false);
            if (!decision.Permitted) throw new AuditAccessException(decision.Code!);
            subject = AuditEntry.InContext(AuditActions.AuditRead, context, principal).Subject;
            action = AuditActions.AuditRead;
        }
        // The read is on the record before anything is read; without that record nothing is returned.
        await store.RecordAloneAsync(new AuditEntry(action, context.Actor)
        {
            TenantId = context.TenantId, Subject = subject, UnitId = context.UnitId, OperationId = context.HostGrant?.OperationId,
            Reason = context.HostGrant?.Reason, Detail = "limit " + query.Limit + (query.Action is null ? "" : " action " + query.Action)
                + (query.From is null ? "" : " from") + (query.Until is null ? "" : " until")
        }, cancellationToken).ConfigureAwait(false);
        await using var work = await EfCoreUnitOfWork.BeginAsync(store.Module, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var rows = ((AuditContext)work.Context).Records.AsNoTracking();
        if (context.Scope == TenantScope.Tenant)
        {
            var tenant = context.TenantId;
            rows = rows.Where(row => row.Tenant == tenant);
        }
        // Bounds compare at the precision the records keep.
        if (query.From is { } start)
        {
            var from = UtcMicroseconds.Normalize(start);
            rows = rows.Where(row => row.At >= from);
        }
        if (query.Until is { } end)
        {
            var until = UtcMicroseconds.Normalize(end);
            rows = rows.Where(row => row.At < until);
        }
        if (query.Action is { } named) rows = rows.Where(row => row.Action == named);
        var found = await rows.OrderBy(row => row.At).ThenBy(row => row.Id).Take(query.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return found.Select(Read).ToArray();
    }

    private static AuditRecord Read(AuditRecordRow row) => new(row.Id, row.At, row.Action, (AuditOutcome)row.Outcome, row.Tenant,
        new ActorReference((ActorKind)row.ActorKind, row.Actor),
        row is { SubjectKind: { } kind, Subject: { } subject } ? new ActorReference((ActorKind)kind, subject) : null,
        row.ResourceType, row.Resource, row.Detail, row.Unit, row.Operation, row.Reason, row.Correlation);
}
