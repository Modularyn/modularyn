using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Persistence.EfCore;

internal sealed class IdentityRetentionStore(PostgreSqlModule module, TimeProvider clock) : IIdentityRetentionStore
{
    public async Task<IdentityRetentionResult> PruneAsync(TenantContext host, int limit, CancellationToken cancellationToken)
    {
        if (host is not { IsActive: true, Scope: TenantScope.Host } || host.HostGrant?.OperationId != IdentityRetentionPolicy.OperationId)
            throw new InvalidOperationException("Identity retention requires its explicit audited host operation.");
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var now = UtcMicroseconds.Now(clock);
        var throttleCutoff = now - IdentityRetentionPolicy.ThrottleRetention;
        var sessionCutoff = now - IdentityRetentionPolicy.EndedSessionRetention;
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var counters = await work.Context.Set<ThrottleRow>().Where(row => row.UpdatedAt <= throttleCutoff
                && (row.BlockedUntil == null || row.BlockedUntil <= now)
                && (row.LastFailureAt == null || row.LastFailureAt <= throttleCutoff)
                && (row.WindowStartedAt == null || row.WindowStartedAt <= throttleCutoff))
            .OrderBy(row => row.UpdatedAt).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var sessions = await work.Context.Set<SessionRow>().Where(row => row.EndedAt <= sessionCutoff
                || row.AbsoluteExpiresAt <= sessionCutoff)
            .OrderBy(row => row.AbsoluteExpiresAt).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        work.Context.RemoveRange(counters);
        work.Context.RemoveRange(sessions);
        var committed = (await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State == UnitOfWorkState.Committed;
        return new(committed ? counters.Count : 0, committed ? sessions.Count : 0, committed);
    }
}
