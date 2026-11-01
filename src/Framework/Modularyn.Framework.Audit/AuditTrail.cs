using System.Diagnostics;
using System.Text.RegularExpressions;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Observability;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Audit;

public enum AuditOutcome
{
    /// <summary>The action took effect: a change committed with its record, or an access was granted after its record.</summary>
    Succeeded = 1,

    /// <summary>The action was refused. Recorded only where its caller records a refusal.</summary>
    Refused = 2
}

/// <summary>
/// One privileged action as its caller describes it for audit: the action's stable identifier, who acted and for whom,
/// in which tenant, on what resource, with what detail, in which unit and under which operation and reason. Every value
/// is a reference or a code: an identifier, a permission name, a refusal code, never a secret, a credential, a token, a
/// session identifier, a payload value or personal data beyond the actor's and subject's references. The store refuses
/// an entry that breaks these forms (<c>MAU001</c>), and so refuses its change.
/// </summary>
public sealed record AuditEntry
{
    public AuditEntry(string action, ActorReference actor)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
    }

    /// <summary>A stable identifier of lowercase segments, such as <c>events.replayed</c>.</summary>
    public string Action { get; }

    public ActorReference Actor { get; }
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Succeeded;
    public Guid? TenantId { get; init; }

    /// <summary>The effective subject when it differs from the actor, such as a delegation's subject or a member a change names.</summary>
    public ActorReference? Subject { get; init; }

    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }

    /// <summary>Further references or codes, such as the permissions a grant names or the code a replay reset.</summary>
    public string? Detail { get; init; }

    public Guid? UnitId { get; init; }
    public string? OperationId { get; init; }
    public string? Reason { get; init; }

    /// <summary>Diagnostic linkage only; the current trace's when null.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// An entry for an action taken in <paramref name="context"/>'s unit: its actor, tenant and unit, and the principal's
    /// effective subject when it is another than the actor, as under a delegation.
    /// </summary>
    public static AuditEntry InContext(string action, TenantContext context, ExecutionPrincipal? principal = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var subject = principal is { EffectiveSubject: { } effective } && (effective.Id != context.Actor.Subject
                || (effective.Kind == IdentityKind.User) != (context.Actor.Kind == ActorKind.User))
            ? new ActorReference(effective.Kind == IdentityKind.User ? ActorKind.User : ActorKind.Service, effective.Id) : null;
        return new(action, context.Actor) { TenantId = context.TenantId, Subject = subject, UnitId = context.UnitId };
    }
}

/// <summary>
/// Records a privileged change in the change's own transaction (TM-G02-051): the record is appended through the unit of
/// work that makes the change, so it commits exactly when the change commits, and a change whose record cannot be written
/// does not commit. Supplied by <see cref="AuditServiceCollectionExtensions.AddModularynAudit"/>.
/// </summary>
public interface IAuditTrail
{
    ValueTask RecordAsync(IUnitOfWork work, AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Records an access that precedes what it allows and changes nothing, such as acting under a delegation or reading
    /// audit, in its own committed transaction before the access exists. A failure refuses the access.
    /// </summary>
    ValueTask RecordAccessAsync(AuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>An audit record could not be formed or written, so the action it describes was refused. Its message holds no value.</summary>
public sealed class AuditRecordException(string message) : InvalidOperationException(message)
{
    public string Code { get; } = AuditCodes.InvalidRecord;
}

/// <summary>Stable audit codes. A refusal carries no record content.</summary>
public static class AuditCodes
{
    /// <summary>An entry breaks the record's forms, holds a secret form, or has no transactional unit to commit with.</summary>
    public const string InvalidRecord = "MAU001";

    /// <summary>A host-level audit or diagnostics read needs an active host scope granted for its own read operation.</summary>
    public const string HostReadRequired = "MAU002";
}

/// <summary>Stable action identifiers for the records the framework writes.</summary>
public static class AuditActions
{
    public const string HostScopeGranted = "tenancy.host-scope-granted";
    public const string CrossTenantRead = "persistence.cross-tenant-read";
    public const string SoftDeletedRead = "persistence.soft-deleted-read";
    public const string AuditRead = "audit.read";
    public const string AuditHostRead = "audit.host-read";
    public const string DiagnosticsHostRead = "diagnostics.host-read";

    /// <summary>The action an authority change is recorded under.</summary>
    public static string Of(AuthorityChangeKind kind) => kind switch
    {
        AuthorityChangeKind.RoleDefined => "authority.role-defined",
        AuthorityChangeKind.RoleRemoved => "authority.role-removed",
        AuthorityChangeKind.RoleAssigned => "authority.role-assigned",
        AuthorityChangeKind.RoleUnassigned => "authority.role-unassigned",
        AuthorityChangeKind.PermissionGranted => "authority.permission-granted",
        AuthorityChangeKind.PermissionRevoked => "authority.permission-revoked",
        AuthorityChangeKind.ResourceGranted => "authority.resource-granted",
        AuthorityChangeKind.ResourceRevoked => "authority.resource-revoked",
        AuthorityChangeKind.HostAuthorityGranted => "authority.host-authority-granted",
        AuthorityChangeKind.HostAuthorityRevoked => "authority.host-authority-revoked",
        AuthorityChangeKind.TenantOwnerAdded => "authority.tenant-owner-added",
        AuthorityChangeKind.TenantOwnerRemoved => "authority.tenant-owner-removed",
        AuthorityChangeKind.MembershipAdded => "authority.membership-added",
        AuthorityChangeKind.MembershipRemoved => "authority.membership-removed",
        _ => throw new AuditRecordException("The authority change has no audit action.")
    };
}

/// <summary>
/// The audit store: authority's, tenancy's and the persistence adapter's audit seams and the general audit trail, over the
/// audit module's append-only table. A change's record is appended in the change's own unit of work; a host-scope grant,
/// a filter bypass and an audit read, which precede the access they allow rather than change anything, are recorded in
/// their own committed transaction before that access exists. Either way a record that cannot be written refuses its
/// action.
/// </summary>
internal sealed partial class AuditStore(PostgreSqlModule module, AuditClock clock) : IAuditTrail, IAuthorityAudit, IHostScopeAudit, IFilterBypassAudit
{
    internal const int MaxDetailLength = 2000;

    internal PostgreSqlModule Module { get; } = module;

    public ValueTask RecordAsync(IUnitOfWork work, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return AppendAsync(work, () => Row(entry, Guid.NewGuid(), clock.Now), cancellationToken);
    }

    public ValueTask RecordAsync(IUnitOfWork work, AuthorityChangeRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return AppendAsync(work, () =>
        {
            var (type, resource) = record.Target.IndexOf(':', StringComparison.Ordinal) is var colon and >= 0
                ? (record.Target[..colon], record.Target[(colon + 1)..]) : (record.Target, null);
            var entry = new AuditEntry(AuditActions.Of(record.Kind), record.Actor)
            {
                TenantId = record.TenantId, Subject = record.Member, ResourceType = type, ResourceId = resource,
                Detail = record.Permissions.Count == 0 ? null : string.Join(' ', record.Permissions), CorrelationId = record.CorrelationId
            };
            // The change's own identity and time, as the authority record it changes holds them.
            return Row(entry, record.ChangeId, AuditClock.Require(record.At));
        }, cancellationToken);
    }

    public ValueTask RecordAsync(HostScopeAuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var entry = new AuditEntry(AuditActions.HostScopeGranted, record.Actor)
        {
            UnitId = record.UnitId, OperationId = record.OperationId, Reason = record.Reason, Detail = "source " + record.Source,
            CorrelationId = record.CorrelationId
        };
        return WriteAloneAsync(Row(entry, Guid.NewGuid(), AuditClock.Require(record.At)), cancellationToken);
    }

    public ValueTask RecordAsync(FilterBypassRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var entry = new AuditEntry(record.Bypass == FilterBypass.CrossTenant ? AuditActions.CrossTenantRead : AuditActions.SoftDeletedRead, record.Actor)
        {
            TenantId = record.TenantId, UnitId = record.UnitId, ResourceType = "entity", ResourceId = record.ModuleId + "/" + record.Table,
            OperationId = record.OperationId, Reason = record.Reason, Detail = record.Scope == TenantScope.Host ? "scope host" : "scope tenant",
            CorrelationId = record.CorrelationId
        };
        return WriteAloneAsync(Row(entry, Guid.NewGuid(), clock.Now), cancellationToken);
    }

    public ValueTask RecordAccessAsync(AuditEntry entry, CancellationToken cancellationToken) =>
        WriteAloneAsync(Row(entry ?? throw new ArgumentNullException(nameof(entry)), Guid.NewGuid(), clock.Now), cancellationToken);

    /// <summary>
    /// Appends a change's record through the change's own unit. A record that cannot be formed rolls the unit back before
    /// the refusal propagates, and one that cannot be written leaves the unit unable to commit, so in neither case can the
    /// change commit without its record.
    /// </summary>
    private async ValueTask AppendAsync(IUnitOfWork work, Func<AuditRecordRow> form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        AuditRecordRow row;
        try
        {
            if (work is not EfCoreUnitOfWork) throw new AuditRecordException("A change's audit record is written in the change's own PostgreSQL unit of work.");
            row = form();
        }
        catch (Exception exception) when (exception is AuditRecordException or ArgumentException)
        {
            await work.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await ((EfCoreUnitOfWork)work).AppendAsync(Module, row, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteAloneAsync(AuditRecordRow row, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, UnitOfWorkMode.New, cancellationToken: cancellationToken).ConfigureAwait(false);
        await work.AppendAsync(Module, row, cancellationToken).ConfigureAwait(false);
        if ((await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State != UnitOfWorkState.Committed)
            throw new AuditRecordException("The audit record did not commit, so the access it records was refused.");
    }

    /// <summary>
    /// Forms a record, refusing any field outside its form and any text holding a recognized secret form: audit records
    /// follow the same redaction rules as logs, and refusing the record refuses its action.
    /// </summary>
    internal static AuditRecordRow Row(AuditEntry entry, Guid id, DateTimeOffset at)
    {
        if (!Enum.IsDefined(entry.Outcome) || !Matches(Action(), entry.Action, 100)
            || entry.ResourceType is not null && !Matches(Type(), entry.ResourceType, 100)
            || entry.ResourceId is not null && !Matches(Reference(), entry.ResourceId, 400)
            || entry.Detail is not null && !Matches(Text(), entry.Detail, MaxDetailLength)
            || entry.OperationId is not null && !Matches(Operation(), entry.OperationId, 200)
            || entry.Reason is not null && (!Matches(Text(), entry.Reason, 500) || entry.Reason != entry.Reason.Trim())
            || entry.CorrelationId is not null && !Matches(Correlation(), entry.CorrelationId, 64)
            || entry.TenantId == Guid.Empty || entry.UnitId == Guid.Empty || id == Guid.Empty)
            throw new AuditRecordException("The audit entry breaks the record's forms, so its action was refused.");
        if (new[] { entry.Actor.Subject, entry.Subject?.Subject, entry.ResourceId, entry.Detail, entry.Reason }.Any(SecretForms.Contains))
            throw new AuditRecordException("The audit entry holds a secret form, which an audit record never holds, so its action was refused.");
        return new AuditRecordRow
        {
            Id = id, At = at, Action = entry.Action, Outcome = (int)entry.Outcome, Tenant = entry.TenantId, ActorKind = (int)entry.Actor.Kind,
            Actor = entry.Actor.Subject, SubjectKind = entry.Subject is null ? null : (int)entry.Subject.Kind, Subject = entry.Subject?.Subject,
            ResourceType = entry.ResourceType, Resource = entry.ResourceId, Detail = entry.Detail, Unit = entry.UnitId, Operation = entry.OperationId,
            Reason = entry.Reason, Correlation = entry.CorrelationId ?? Activity.Current?.TraceId.ToHexString()
        };
    }

    private static bool Matches(Regex form, string value, int length) => value.Length <= length && form.IsMatch(value);

    [GeneratedRegex(@"\A[a-z][a-z0-9-]*(?:\.[a-z][a-z0-9-]*)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Action();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]*(?:\.[a-z][a-z0-9-]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Type();

    [GeneratedRegex(@"\A[!-~]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();

    [GeneratedRegex(@"\A[ -~]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Text();

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Operation();

    [GeneratedRegex(@"\A[A-Za-z0-9:._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Correlation();
}
