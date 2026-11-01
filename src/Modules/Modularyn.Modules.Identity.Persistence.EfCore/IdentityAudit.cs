using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Persistence.EfCore;

/// <summary>
/// Records identity's audited changes in the change's own unit of work: a tenant membership as an authority change, through
/// <see cref="IAuthorityAudit"/>, and every other administrative or security change through <see cref="IAuditTrail"/>.
/// Either seam is resolved when it is used; while it is absent, or when it fails, the change is refused and nothing commits.
/// </summary>
internal sealed class IdentityAudit(IServiceProvider services)
{
    internal async Task MembershipAsync(EfCoreUnitOfWork work, bool added, TenantContext host, Guid accountId, Guid tenantId, DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var audit = services.GetService<IAuthorityAudit>() ?? throw new IdentityAuditUnavailableException();
        try
        {
            await audit.RecordAsync(work, new AuthorityChangeRecord(Guid.NewGuid(), added ? AuthorityChangeKind.MembershipAdded : AuthorityChangeKind.MembershipRemoved,
                tenantId, host.Actor, new ActorReference(ActorKind.User, accountId.ToString("D")), "membership", [], at,
                Activity.Current?.TraceId.ToHexString()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new IdentityAuditUnavailableException();
        }
    }

    internal async Task ChangeAsync(EfCoreUnitOfWork work, IdentityChangeRecord change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var trail = services.GetService<IAuditTrail>() ?? throw new IdentityAuditUnavailableException();
        try
        {
            await trail.RecordAsync(work, new AuditEntry(change.Action, change.Actor)
            {
                TenantId = change.TenantId, UnitId = change.UnitId, OperationId = change.OperationId,
                Subject = new ActorReference(ActorKind.User, change.Account.ToString("D")),
                ResourceType = change.ResourceType ?? "account", ResourceId = change.ResourceId ?? change.Account.ToString("D"), Detail = change.Detail
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new IdentityAuditUnavailableException();
        }
    }
}
