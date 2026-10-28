using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// The fixture module's use cases, as a business module writes them over the evaluator: resolve within the tenant, then
/// authorize the resource; build lists through the visibility filter; and recheck authority, at the current profile,
/// before each step of long-running work.
/// </summary>
internal static class ItemWork
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static async Task AddAsync(PostgreSqlModule items, TenantContext context, string owner, params string[] codes)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: Cancellation);
        foreach (var code in codes) work.Context.Add(new Item { Code = code, Owner = owner, Title = "Title of " + code });
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
    }

    /// <summary>Resolves the item within the context's tenant; another tenant's item is not found, exactly as a missing one.</summary>
    public static async Task<Item?> FindAsync(PostgreSqlModule items, TenantContext context, string code, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken);
        return await ((ItemsContext)work.Context).Items.AsNoTracking().SingleOrDefaultAsync(row => row.Code == code, cancellationToken);
    }

    /// <summary>The visible items and their count, both built through the same filter, so hidden items affect neither.</summary>
    public static async Task<(string[] Codes, int Count)> ListAsync(IPermissionEvaluator evaluator, PostgreSqlModule items, TenantContext context,
        ExecutionPrincipal? principal, CancellationToken cancellationToken)
    {
        var visibility = await evaluator.VisibleAsync(new AuthorizationRequest(context, principal, Items.Read), Items.ResourceType, cancellationToken);
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken);
        var visible = visibility.Apply(((ItemsContext)work.Context).Items.AsNoTracking(), row => row.Owner, row => row.Code);
        return (await visible.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(cancellationToken), await visible.CountAsync(cancellationToken));
    }

    /// <summary>
    /// Exports the visible items in chunks, rechecking the export permission and the principal at the current profile before
    /// each chunk and before storing the result. A refusal ends the export with nothing downloadable.
    /// </summary>
    public static async Task<ItemOperation> ExportAsync(IServiceProvider services, PostgreSqlModule items, TenantContext context, ExecutionPrincipal? principal,
        Func<int, Task>? afterChunk, CancellationToken cancellationToken)
    {
        var evaluator = services.GetRequiredService<IPermissionEvaluator>();
        var operation = await StartAsync(items, context, "export", cancellationToken);
        var codes = (await ListAsync(evaluator, items, context, principal, cancellationToken)).Codes;
        var lines = new List<string>();
        for (var chunk = 0; ; chunk++)
        {
            var decision = await evaluator.AuthorizeAsync(new AuthorizationRequest(context, principal, Items.Export) { RequireCurrent = true }, cancellationToken);
            if (!decision.Permitted) return await FinishAsync(items, context, operation.Id, "denied", decision.Code!, null, cancellationToken);
            if (chunk * 2 >= codes.Length) break;
            lines.AddRange(codes.Skip(chunk * 2).Take(2));
            if (afterChunk is not null) await afterChunk(chunk);
        }
        return await FinishAsync(items, context, operation.Id, "completed", "", string.Join("\n", lines), cancellationToken);
    }

    /// <summary>
    /// Updates items in batches, each committed in its own unit, rechecking the bulk-update permission at the current
    /// profile before each batch. A refusal stops before the next batch: committed batches stay, and the operation is
    /// recorded as partially applied and denied, with nothing left in progress.
    /// </summary>
    public static async Task<ItemOperation> BulkUpdateAsync(IServiceProvider services, PostgreSqlModule items, TenantContext context, ExecutionPrincipal? principal,
        IReadOnlyList<string> codes, int batchSize, Func<int, Task>? afterBatch, CancellationToken cancellationToken, Guid? existing = null)
    {
        var evaluator = services.GetRequiredService<IPermissionEvaluator>();
        var operationId = existing ?? (await StartAsync(items, context, "bulk-update", cancellationToken)).Id;
        for (var batch = 0; batch * batchSize < codes.Count; batch++)
        {
            var decision = await evaluator.AuthorizeAsync(new AuthorizationRequest(context, principal, Items.BulkUpdate) { RequireCurrent = true }, cancellationToken);
            if (!decision.Permitted)
            {
                var committed = await BatchesAsync(items, context, operationId, cancellationToken);
                return await FinishAsync(items, context, operationId, committed > 0 ? "partially-applied" : "denied", decision.Code!, null, cancellationToken);
            }
            await using (var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: cancellationToken))
            {
                var db = (ItemsContext)work.Context;
                var targets = codes.Skip(batch * batchSize).Take(batchSize).ToArray();
                foreach (var item in await db.Items.Where(row => targets.Contains(row.Code)).ToListAsync(cancellationToken)) item.Revision++;
                var operation = await db.Operations.SingleAsync(row => row.Id == operationId, cancellationToken);
                operation.Batches++;
                Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(cancellationToken)).State);
            }
            if (afterBatch is not null) await afterBatch(batch + 1);
        }
        return await FinishAsync(items, context, operationId, "completed", "", null, cancellationToken);
    }

    public static async Task<ItemOperation> StartAsync(PostgreSqlModule items, TenantContext context, string kind, CancellationToken cancellationToken)
    {
        var operation = new ItemOperation { Id = Guid.NewGuid(), Kind = kind, State = "running" };
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: cancellationToken);
        work.Context.Add(operation);
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(cancellationToken)).State);
        return operation;
    }

    /// <summary>Queues the item job for the rest of an operation, with the producing unit's context captured beside it.</summary>
    public static async Task<Guid> EnqueueAsync(PostgreSqlModule items, TenantContext context, Guid operation, TimeProvider time, CancellationToken cancellationToken)
    {
        var job = new ItemJob { Id = Guid.NewGuid(), Operation = operation, Propagated = PropagatedTenantContext.Capture(context, time).ToJson() };
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: cancellationToken);
        work.Context.Add(job);
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(cancellationToken)).State);
        return job.Id;
    }

    private static async Task<int> BatchesAsync(PostgreSqlModule items, TenantContext context, Guid operation, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken);
        return await ((ItemsContext)work.Context).Operations.Where(row => row.Id == operation).Select(row => row.Batches).SingleAsync(cancellationToken);
    }

    private static async Task<ItemOperation> FinishAsync(PostgreSqlModule items, TenantContext context, Guid id, string state, string disposition, string? content,
        CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: cancellationToken);
        var db = (ItemsContext)work.Context;
        var operation = await db.Operations.SingleAsync(row => row.Id == id, cancellationToken);
        (operation.State, operation.Disposition) = (state, disposition);
        // The result becomes downloadable in the same transaction that completes the operation, and never before.
        if (content is not null) db.Add(new ItemExport { Id = id, Content = content });
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(cancellationToken)).State);
        return operation;
    }
}

/// <summary>
/// The fixture module's endpoints. Each declares its tenancy and its permission, so it runs only after the evaluator
/// permits; a handler that acts on one item then resolves it in the tenant and asks for that resource.
/// </summary>
internal static class ItemEndpoints
{
    public static void Map(WebApplication application, AuthorizationDatabase database)
    {
        var items = database.ItemsModule;
        application.MapGet("/t/{tenant}/items/{code}", async (string code, HttpContext http) =>
        {
            var (context, principal, evaluator) = Request(http);
            return await ResourceAsync(evaluator, items, context, principal, code, Items.Read, http.RequestAborted, item =>
                Task.FromResult(Results.Json(new { code = item.Code, title = item.Title, revision = item.Revision })));
        }).RequireTenantMember().RequirePermission(Items.Read);

        Task<IResult> Update(string code, HttpContext http)
        {
            var (context, principal, evaluator) = Request(http);
            return ResourceAsync(evaluator, items, context, principal, code, Items.Update, http.RequestAborted, async item =>
            {
                await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: http.RequestAborted);
                var row = await ((ItemsContext)work.Context).Items.SingleAsync(entry => entry.Code == item.Code, http.RequestAborted);
                row.Revision++;
                await work.CompleteAsync(http.RequestAborted);
                return Results.NoContent();
            });
        }
        application.MapPut("/t/{tenant}/items/{code}", Update).RequireTenantMember().RequirePermission(Items.Update);
        // A declared integration endpoint for the same use case: its own route, the same declaration and the same decision.
        application.MapPost("/t/{tenant}/integration/items/{code}", Update).RequireTenantMember().RequirePermission(Items.Update);

        application.MapGet("/t/{tenant}/items", async (HttpContext http) =>
        {
            var (context, principal, evaluator) = Request(http);
            var (codes, count) = await ItemWork.ListAsync(evaluator, items, context, principal, http.RequestAborted);
            return Results.Json(new { items = codes, total = count });
        }).RequireTenantMember().RequirePermission(Items.Read);

        application.MapPost("/t/{tenant}/exports", async (HttpContext http) =>
        {
            var (context, principal, _) = Request(http);
            var operation = await ItemWork.ExportAsync(http.RequestServices, items, context, principal, null, http.RequestAborted);
            return Results.Json(new { operation = operation.Id, state = operation.State, disposition = operation.Disposition });
        }).RequireTenantMember().RequirePermission(Items.Export);

        // The member's effective authority, as a bootstrap response would report it.
        application.MapGet("/t/{tenant}/authority", async (HttpContext http) =>
        {
            var (context, principal, evaluator) = Request(http);
            var described = await evaluator.DescribeAsync(context, principal, http.RequestAborted);
            return described is null ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Json(new { permissions = described.Permissions.Order(StringComparer.Ordinal).ToArray(), version = described.AuthorityVersion });
        }).RequireTenantMember();

        // Permission administration over HTTP: the administration itself decides against the actor's ceiling.
        application.MapPost("/t/{tenant}/authority/grants", async (Grant grant, HttpContext http) =>
        {
            var (context, principal, _) = Request(http);
            var administration = http.RequestServices.GetRequiredService<AuthorityAdministration>();
            var member = new ActorReference(ActorKind.User, grant.Member);
            try
            {
                if (grant.Revoke) await administration.RevokePermissionAsync(context, principal!, member, grant.Permission, http.RequestAborted);
                else await administration.GrantPermissionAsync(context, principal!, member, grant.Permission, http.RequestAborted);
                return Results.NoContent();
            }
            catch (AuthorityChangeException exception)
            {
                return Results.Json(new { code = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
            }
        }).RequireTenantMember().RequirePermission(AuthorizationModule.ManagePermission);
    }

    private static async Task<IResult> ResourceAsync(IPermissionEvaluator evaluator, PostgreSqlModule items, TenantContext context, ExecutionPrincipal? principal,
        string code, string permission, CancellationToken cancellationToken, Func<Item, Task<IResult>> act)
    {
        // Resolved within the tenant first; a missing item and a hidden one answer alike, revealing nothing.
        var item = await ItemWork.FindAsync(items, context, code, cancellationToken);
        if (item is null) return NotFound();
        var decision = await evaluator.AuthorizeAsync(new AuthorizationRequest(context, principal, permission) { Resource = Items.Reference(item) }, cancellationToken);
        if (!decision.Permitted)
            return decision.Code == AuthorizationCodes.ResourceNotFound ? NotFound()
                : Results.Json(new { code = decision.Code }, statusCode: StatusCodes.Status403Forbidden);
        return await act(item);
    }

    private static IResult NotFound() => Results.Json(new { code = AuthorizationCodes.ResourceNotFound }, statusCode: StatusCodes.Status404NotFound);

    private static (TenantContext Context, ExecutionPrincipal? Principal, IPermissionEvaluator Evaluator) Request(HttpContext http) =>
        (http.RequestServices.GetRequiredService<TenantExecution>().Context!, http.RequestServices.GetRequiredService<IExecutionPrincipalAccessor>().Current,
            http.RequestServices.GetRequiredService<IPermissionEvaluator>());

    internal sealed record Grant(string Member, string Permission, bool Revoke = false);
}
