using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// A test dispatcher that follows the propagation contract: it lists queued work across tenants in an audited host
/// scope, runs each item through <see cref="TenantWorkRunner"/> in its own revalidated unit, where the effect and the
/// item's completion share one transaction, and records a denial on the item from host scope. CODE-05 owns the real one.
/// </summary>
internal static class WorkDispatch
{
    internal const string Operation = "test.dispatch";

    internal sealed record Run(WorkItem Item, TenantWorkOutcome Outcome, ContextSnapshot? Context);

    internal static async Task EnqueueAsync(TenancyDatabase database, IServiceProvider services, string subject, Guid tenant, string kind, string payload)
    {
        var (scope, context) = await Units.EnterAsync(services, subject, tenant);
        await using (scope)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: TestContext.Current.CancellationToken);
            work.Context.Add(new WorkItem { Id = Guid.NewGuid(), Kind = kind, Payload = payload, Propagated = PropagatedTenantContext.Capture(context).ToJson() });
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(TestContext.Current.CancellationToken)).State);
        }
    }

    internal static async Task<List<Run>> DrainAsync(TenancyDatabase database, IServiceProvider services,
        Func<WorkItem, EfCoreUnitOfWork, CancellationToken, Task>? beforeCommit = null, CancellationToken cancellationToken = default)
    {
        var runs = new List<Run>();
        var (scope, host) = await Units.EnterHostAsync(services, "dispatcher", Operation, service: true);
        await using (scope)
        {
            List<WorkItem> queued;
            await using (var read = await EfCoreUnitOfWork.BeginAsync(database.Items, host, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken))
                queued = await read.Context.AcrossTenants<WorkItem>().Query.AsNoTracking().Where(item => item.State == "queued")
                    .OrderBy(item => item.Payload).ToListAsync(cancellationToken);
            var runner = services.GetRequiredService<TenantWorkRunner>();
            foreach (var item in queued)
            {
                var outcome = await runner.RunAsync(item.Propagated, async (unit, token) =>
                {
                    await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: token);
                    var items = (ItemsContext)work.Context;
                    // The tenant filter finds the item only in its own tenant's unit.
                    var row = await items.Work.SingleAsync(candidate => candidate.Id == item.Id, token);
                    row.State = "completed";
                    row.Attempts++;
                    items.Items.Add(new Item { Id = Guid.NewGuid(), Code = "done-" + item.Payload, Name = item.Kind });
                    if (beforeCommit is not null) await beforeCommit(item, work, token);
                    Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(token)).State);
                }, cancellationToken);
                if (outcome.Disposition == TenantWorkDisposition.Denied)
                {
                    await using var write = await EfCoreUnitOfWork.BeginAsync(database.Items, host, cancellationToken: cancellationToken);
                    var recorded = await write.Sql.UpdateForTenantAsync<WorkItem, Guid, string>(item.TenantId, row => row.Id, item.Id, item.Version,
                        row => row.State, "denied:" + outcome.Code, cancellationToken);
                    Assert.True(recorded.Succeeded);
                    Assert.Equal(UnitOfWorkState.Committed, (await write.CompleteAsync(cancellationToken)).State);
                }
                runs.Add(new(item, outcome, outcome.Context?.Snapshot()));
            }
        }
        return runs;
    }
}
