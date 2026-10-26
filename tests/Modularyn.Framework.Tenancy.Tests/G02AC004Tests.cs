using System.Net;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// The AC-004 reference cases that the G-02 evidence plan assigns to SEC-04: B01, P01 and R01. Identity, membership,
// host authority and audit come through test doubles of the SEC-03, SEC-05 and SEC-07 seams, so AC-003's security
// dependency stays unmet. Events and jobs use a test work table and dispatcher that follow the propagation contract;
// CODE-05 supplies the durable outbox and job store.
[Collection(TenancyPostgreSqlCollection.Name)]
public sealed class G02AC004Tests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    [Fact]
    public async Task AC_004_B01_WorkerReuseAndContinuationsKeepContextSeparate_Unit()
    {
        var directory = new MemoryDirectory();
        var tenantA = directory.Add("Tenant A");
        var tenantB = directory.Add("Tenant B");
        var seams = new TestSeams();
        seams.Membership.Add("user-a", tenantA);
        seams.Membership.Add("user-b", tenantB);
        seams.Authority.Grant("maintenance", "test.maintenance");
        await using var services = UnitServices.Build(directory, seams);
        var runner = services.GetRequiredService<TenantWorkRunner>();
        var cache = services.GetRequiredService<TenantCache>();
        var a = await CaptureAsync(services, "user-a", tenantA);
        var b = await CaptureAsync(services, "user-b", tenantB);
        var host = await CaptureHostAsync(services, "maintenance", "test.maintenance");

        // 1. Tenant-A, tenant-B and host-scope units run consecutively on one worker. Each starts with nothing from
        //    its predecessor: a scope the worker creates has no context, there is no ambient transaction, and the
        //    predecessor's context has ended.
        var inside = new List<TenantContext>();
        foreach (var reference in new[] { a, b, host })
        {
            await using (var probe = services.CreateAsyncScope())
                Assert.Null(probe.ServiceProvider.GetRequiredService<TenantExecution>().Context);
            Assert.Null(Transaction.Current);
            var outcome = await runner.RunAsync(reference, (unit, _) =>
            {
                Assert.Same(unit.Context, unit.Services.GetRequiredService<TenantExecution>().Context);
                Assert.All(inside, previous => Assert.False(previous.IsActive));
                Assert.Null(Transaction.Current);
                inside.Add(unit.Context);
                return Task.CompletedTask;
            }, Cancellation);
            Assert.Equal(TenantWorkDisposition.Completed, outcome.Disposition);
        }
        Assert.Equal(new Guid?[] { tenantA, tenantB, null }, inside.Select(context => context.TenantId));
        Assert.Equal(["user-a", "user-b", "maintenance"], inside.Select(context => context.Actor.Subject));
        Assert.Equal(TenantScope.Host, inside[2].Scope);
        Assert.Equal(ActorKind.Service, inside[2].Actor.Kind);
        Assert.All(inside, context => Assert.False(context.IsActive));
        Assert.Equal(3, inside.Select(context => context.UnitId).Distinct().Count());
        Log($"Consecutive units: {string.Join("; ", inside.Select(context => context.Snapshot()))}.");

        // 2. Tenant-A's continuation resumes on the thread pool while tenant-B's unit is still running; each keeps its own.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TenantContext? beforeA = null, afterA = null, duringB = null, afterResumeB = null;
        TenantExecution? leakedExecution = null;
        TenantContext? leakedContext = null;
        Task<TenantContext?>? inheritedProbe = null;
        var leakRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(TenantContext? Execution, bool Active)>? leaked = null;
        var aRun = Task.Run(() => runner.RunAsync(a, async (unit, _) =>
        {
            var execution = unit.Services.GetRequiredService<TenantExecution>();
            beforeA = execution.Context;
            // Work that the unit starts without passing its context has none, and work that keeps a reference to it
            // after the unit ends finds it ended.
            inheritedProbe = Task.Run(async () =>
            {
                await using var nested = services.CreateAsyncScope();
                return nested.ServiceProvider.GetRequiredService<TenantExecution>().Context;
            });
            leakedExecution = execution;
            leakedContext = unit.Context;
            leaked = Task.Run(async () =>
            {
                await leakRelease.Task;
                return (leakedExecution.Context, leakedContext.IsActive);
            });
            await released.Task;
            afterA = execution.Context;
            resumed.SetResult();
        }, Cancellation), Cancellation);
        var bOutcome = await runner.RunAsync(b, async (unit, _) =>
        {
            var execution = unit.Services.GetRequiredService<TenantExecution>();
            duringB = execution.Context;
            released.SetResult();
            await resumed.Task;
            afterResumeB = execution.Context;
        }, Cancellation);
        await aRun;
        Assert.Equal(TenantWorkDisposition.Completed, bOutcome.Disposition);
        Assert.Equal(tenantA, beforeA!.TenantId);
        Assert.Same(beforeA, afterA);
        Assert.Equal(tenantB, duringB!.TenantId);
        Assert.Same(duringB, afterResumeB);
        Assert.Null(await inheritedProbe!);
        leakRelease.SetResult();
        var leftover = await leaked!;
        Assert.Null(leftover.Execution);
        Assert.False(leftover.Active);
        Log("The tenant-A continuation resumed during tenant-B's unit with tenant-A's context; tenant-B kept its own; leaked work found none.");

        // 3. A tenant-A unit fails after building a cache entry; the next unit on the worker, tenant-B's, sees neither
        //    its context nor its entry.
        var entry = new TenantCacheEntry("Items", "1", "item-codes", TimeSpan.FromMinutes(5), principalScoped: false);
        TenantContext? failed = null;
        string? failedKey = null;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(a, async (unit, _) =>
        {
            failed = unit.Context;
            failedKey = cache.KeyFor(unit.Context, entry);
            Assert.Equal("tenant-a codes", await cache.GetOrCreateAsync(unit.Context, entry, _ => Task.FromResult("tenant-a codes"), Cancellation));
            throw new InvalidOperationException("The unit failed mid-work.");
        }, Cancellation));
        Assert.Equal("The unit failed mid-work.", failure.Message);
        Assert.False(failed!.IsActive);
        Assert.Throws<InvalidOperationException>(() => cache.KeyFor(failed, entry));
        var built = false;
        string? bValue = null, bKey = null;
        Assert.Equal(TenantWorkDisposition.Completed, (await runner.RunAsync(b, async (unit, _) =>
        {
            Assert.Equal(tenantB, unit.Services.GetRequiredService<TenantExecution>().Context!.TenantId);
            bKey = cache.KeyFor(unit.Context, entry);
            bValue = await cache.GetOrCreateAsync(unit.Context, entry, _ =>
            {
                built = true;
                return Task.FromResult("tenant-b codes");
            }, Cancellation);
        }, Cancellation)).Disposition);
        Assert.True(built);
        Assert.Equal("tenant-b codes", bValue);
        Assert.NotEqual(failedKey, bKey);
        Log($"After the failed tenant-A unit, tenant-B's unit built its own entry under {bKey}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_B01_WorkerReuseAndContinuationsKeepContextSeparate_Isolation()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        database.Seams.Authority.Grant("maintenance", "test.maintenance");
        await AddItemAsync(database, services, "user-a", database.TenantA, "a-1");
        await AddItemAsync(database, services, "user-b", database.TenantB, "b-1");
        var a = await CaptureAsync(services, "user-a", database.TenantA);
        var b = await CaptureAsync(services, "user-b", database.TenantB);
        var host = await CaptureHostAsync(services, "maintenance", "test.maintenance");
        var runner = services.GetRequiredService<TenantWorkRunner>();

        // 1. One worker runs tenant-A, tenant-B and host-scope units over pooled PostgreSQL connections. Each unit's
        //    transaction starts with no transaction identifier assigned and sees only its own scope's rows.
        var transactions = new List<Guid?>();
        var seen = new List<string>();
        foreach (var reference in new[] { a, b })
        {
            await runner.RunAsync(reference, async (unit, token) =>
            {
                await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: token);
                Assert.Equal(DBNull.Value, await TransactionIdAsync(work, token));
                transactions.Add(work.TransactionId);
                seen.Add(string.Join(",", await ((ItemsContext)work.Context).Items.Select(row => row.Code).ToArrayAsync(token)));
                Log($"Unit {unit.Context.Snapshot()} ran on backend {((NpgsqlConnection)work.Context.Database.GetDbConnection()).ProcessID}.");
            }, Cancellation);
        }
        await runner.RunAsync(host, async (unit, token) =>
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: token);
            Assert.Equal(DBNull.Value, await TransactionIdAsync(work, token));
            transactions.Add(work.TransactionId);
            Assert.Null(unit.Context.TenantId);
            // The host-scope unit inherits neither tenant: plain tenant queries are refused, and only the explicit,
            // audited cross-tenant read reaches tenant data.
            Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                ((ItemsContext)work.Context).Items.CountAsync(token))).Code);
            seen.Add(string.Join(",", await work.Context.AcrossTenants<Item>().Query.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(token)));
        }, Cancellation);
        Assert.Equal(["a-1", "b-1", "a-1,b-1"], seen);
        Assert.Equal(3, transactions.Distinct().Count());

        // 2. A tenant-A unit holds its transaction across an await while tenant-B's unit runs and commits.
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? aBefore = null, aAfter = null;
        Guid? aTransactionBefore = null, aTransactionAfter = null;
        var aRun = Task.Run(() => runner.RunAsync(a, async (unit, token) =>
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: token);
            aTransactionBefore = work.TransactionId;
            aBefore = string.Join(",", await ((ItemsContext)work.Context).Items.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(token));
            await released.Task;
            aTransactionAfter = work.TransactionId;
            aAfter = string.Join(",", await ((ItemsContext)work.Context).Items.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(token));
        }, Cancellation), Cancellation);
        await runner.RunAsync(b, async (unit, token) =>
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: token);
            ((ItemsContext)work.Context).Items.Add(new Item { Id = Guid.NewGuid(), Code = "b-2", Name = "during A's await" });
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(token)).State);
            Assert.NotEqual(aTransactionBefore, work.TransactionId);
        }, Cancellation);
        released.SetResult();
        await aRun;
        Assert.Equal("a-1", aBefore);
        Assert.Equal("a-1", aAfter);
        Assert.Equal(aTransactionBefore, aTransactionAfter);

        // 3. A tenant-A unit fails mid-transaction after flushing a row and building a cache entry; tenant-B's next unit
        //    on the worker starts clean, and nothing of tenant-A's failed unit remains visible anywhere.
        var cache = services.GetRequiredService<TenantCache>();
        var entry = new TenantCacheEntry("Items", "1", "item-codes", TimeSpan.FromMinutes(5), principalScoped: false);
        string? failedKey = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(a, async (unit, token) =>
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: token);
            ((ItemsContext)work.Context).Items.Add(new Item { Id = Guid.NewGuid(), Code = "a-failed", Name = "rolled back" });
            await work.FlushAsync(token);
            failedKey = cache.KeyFor(unit.Context, entry);
            await cache.GetOrCreateAsync(unit.Context, entry, _ => Task.FromResult(new[] { "a-1", "a-failed" }), token);
            throw new InvalidOperationException("The tenant-A unit failed mid-transaction.");
        }, Cancellation));
        var cacheBuilt = false;
        await runner.RunAsync(b, async (unit, token) =>
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: token);
            Assert.Equal(DBNull.Value, await TransactionIdAsync(work, token));
            Assert.Equal(["b-1", "b-2"], await ((ItemsContext)work.Context).Items.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(token));
            Assert.NotEqual(failedKey, cache.KeyFor(unit.Context, entry));
            Assert.Equal(["b-1", "b-2"], await cache.GetOrCreateAsync(unit.Context, entry, _ =>
            {
                cacheBuilt = true;
                return Task.FromResult(new[] { "b-1", "b-2" });
            }, token));
        }, Cancellation);
        Assert.True(cacheBuilt);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Items") + " WHERE \"Code\" = 'a-failed'"));
        Assert.Equal([(database.TenantA, "a-1"), (database.TenantB, "b-1"), (database.TenantB, "b-2")],
            (await database.InventoryAsync("Items", "Code")).OrderBy(row => row.Value));
        Log("After the failed tenant-A unit: no transaction at tenant-B's entry, no tenant-A row or cache entry visible, and the flushed row rolled back.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_P01_IngressWorkersAndCachesKeepValidatedTenantContext_Integration()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using (var seeding = database.Services()) await database.SeedAsync(seeding);
        await using var server = await TenancyServer.StartAsync(database);
        database.Seams.Authority.Grant("dispatcher", WorkDispatch.Operation);

        // 1. Each member creates, reads and lists items on its tenant's domain; both code-1 items coexist.
        var a1 = await TenancyServer.ReadAsync<ItemView>(await server.SendAsync(HttpMethod.Post, "/items?code=code-1&name=A1", TenancyDatabase.HostA, "user-a"));
        var a2 = await TenancyServer.ReadAsync<ItemView>(await server.SendAsync(HttpMethod.Post, "/items?code=code-2&name=A2", TenancyDatabase.HostA, "user-a"));
        var b1 = await TenancyServer.ReadAsync<ItemView>(await server.SendAsync(HttpMethod.Post, "/items?code=code-1&name=B1", TenancyDatabase.HostB, "user-b"));
        Assert.Equal([database.TenantA, database.TenantA, database.TenantB], new[] { a1.Tenant, a2.Tenant, b1.Tenant });
        Assert.Equal(a1, await TenancyServer.ReadAsync<ItemView>(await server.SendAsync(HttpMethod.Get, "/items/" + a1.Id, TenancyDatabase.HostA, "user-a")));
        Assert.Equal([a1, a2], await TenancyServer.ReadAsync<ItemView[]>(await server.SendAsync(HttpMethod.Get, "/items", TenancyDatabase.HostA, "user-a")));
        Assert.Equal([b1], await TenancyServer.ReadAsync<ItemView[]>(await server.SendAsync(HttpMethod.Get, "/items", TenancyDatabase.HostB, "user-b")));
        var contextA = await TenancyServer.ReadAsync<ContextSnapshot>(await server.SendAsync(HttpMethod.Get, "/context", TenancyDatabase.HostA, "user-a"));
        var contextB = await TenancyServer.ReadAsync<ContextSnapshot>(await server.SendAsync(HttpMethod.Get, "/context", TenancyDatabase.HostB, "user-b"));
        Assert.Equal(("Tenant", database.TenantA, "user-a", "Request"), (contextA.Scope, contextA.Tenant, contextA.Actor, contextA.Source));
        Assert.Equal(("Tenant", database.TenantB, "user-b", "Request"), (contextB.Scope, contextB.Tenant, contextB.Actor, contextB.Source));
        Assert.Equal([(database.TenantA, "code-1"), (database.TenantA, "code-2"), (database.TenantB, "code-1")],
            (await database.InventoryAsync("Items", "Code")).OrderBy(row => row.Tenant == database.TenantA ? 0 : 1).ThenBy(row => row.Value));
        Assert.All(server.Observed, snapshot => Assert.Equal(snapshot.Actor == "user-a" ? database.TenantA : database.TenantB, snapshot.Tenant));
        Assert.Equal(server.Observed.Count, server.Observed.Select(snapshot => snapshot.Unit).Distinct().Count());

        // 2. Alternating jobs and events for both tenants, enqueued over HTTP, run on one worker; each unit records only
        //    its own tenant and actor, in a fresh scope.
        foreach (var (host, subject, kind, payload) in new[]
        {
            (TenancyDatabase.HostA, "user-a", "job", "1-a-job"), (TenancyDatabase.HostB, "user-b", "job", "2-b-job"),
            (TenancyDatabase.HostA, "user-a", "event", "3-a-event"), (TenancyDatabase.HostB, "user-b", "event", "4-b-event")
        })
            Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, $"/work?kind={kind}&payload={payload}", host, subject)).StatusCode);
        var runs = await WorkDispatch.DrainAsync(database, server.Services, cancellationToken: Cancellation);
        Assert.Equal(["1-a-job", "2-b-job", "3-a-event", "4-b-event"], runs.Select(run => run.Item.Payload));
        Assert.All(runs, run =>
        {
            Assert.Equal(TenantWorkDisposition.Completed, run.Outcome.Disposition);
            Assert.Equal(run.Item.TenantId, run.Context!.Tenant);
            Assert.Equal(run.Item.TenantId == database.TenantA ? "user-a" : "user-b", run.Context.Actor);
            Assert.Equal("Propagated", run.Context.Source);
            Assert.False(run.Outcome.Context!.IsActive);
        });
        Assert.Equal(4, runs.Select(run => run.Context!.Unit).Distinct().Count());
        foreach (var run in runs) Log($"Work {run.Item.Payload}: {run.Context}.");

        // 3. Warmed list caches are returned only to their own tenant.
        var firstA = await TenancyServer.ReadAsync<CachedCodes>(await server.SendAsync(HttpMethod.Get, "/cached/items", TenancyDatabase.HostA, "user-a"));
        var firstB = await TenancyServer.ReadAsync<CachedCodes>(await server.SendAsync(HttpMethod.Get, "/cached/items", TenancyDatabase.HostB, "user-b"));
        var againA = await TenancyServer.ReadAsync<CachedCodes>(await server.SendAsync(HttpMethod.Get, "/cached/items", TenancyDatabase.HostA, "user-a"));
        var againB = await TenancyServer.ReadAsync<CachedCodes>(await server.SendAsync(HttpMethod.Get, "/cached/items", TenancyDatabase.HostB, "user-b"));
        Assert.True(firstA.Built);
        Assert.True(firstB.Built);
        Assert.False(againA.Built);
        Assert.False(againB.Built);
        Assert.Equal(["code-1", "code-2", "done-1-a-job", "done-3-a-event"], againA.Codes);
        Assert.Equal(["code-1", "done-2-b-job", "done-4-b-event"], againB.Codes);
        Assert.NotEqual(firstA.Key, firstB.Key);
        Assert.Equal(firstA.Key, againA.Key);
        Log($"Cache keys: tenant A {firstA.Key}; tenant B {firstB.Key}. Second reads were hits.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_P01_IngressWorkersAndCachesKeepValidatedTenantContext_Isolation()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using (var seeding = database.Services()) await database.SeedAsync(seeding);
        await using var server = await TenancyServer.StartAsync(database);
        var a1 = await TenancyServer.ReadAsync<ItemView>(await server.SendAsync(HttpMethod.Post, "/items?code=code-1&name=A1", TenancyDatabase.HostA, "user-a"));
        var b1 = await TenancyServer.ReadAsync<ItemView>(await server.SendAsync(HttpMethod.Post, "/items?code=code-1&name=B1", TenancyDatabase.HostB, "user-b"));

        // The effective tenant is the one established after the domain and membership checks, whichever candidate names it.
        var viaRoute = await server.SendAsync(HttpMethod.Get, "/t/" + TenantIdentifier.Format(database.TenantA) + "/items", TenancyDatabase.AppHost, "user-a");
        Assert.Equal([a1], await TenancyServer.ReadAsync<ItemView[]>(viaRoute));
        var viaHeader = await server.SendAsync(HttpMethod.Get, "/items", TenancyDatabase.AppHost, "user-b",
            request => request.Headers.Add(TenancyHttp.HeaderName, TenantIdentifier.Format(database.TenantB)));
        Assert.Equal([b1], await TenancyServer.ReadAsync<ItemView[]>(viaHeader));
        // Each tenant's identifiers resolve only in that tenant: the other tenant's item is absent, not forbidden.
        Assert.Equal(HttpStatusCode.NotFound, (await server.SendAsync(HttpMethod.Get, "/items/" + b1.Id, TenancyDatabase.HostA, "user-a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.SendAsync(HttpMethod.Get, "/items/" + a1.Id, TenancyDatabase.HostB, "user-b")).StatusCode);

        // Principal-scoped entries, such as bootstrap values, vary by principal within a tenant as well as by tenant.
        var cache = server.Services.GetRequiredService<TenantCache>();
        var bootstrap = new TenantCacheEntry("Bootstrap", "1", "client-values", TimeSpan.FromMinutes(1), principalScoped: true);
        var keys = new Dictionary<string, string>();
        foreach (var (subject, tenant) in new[] { ("user-a", database.TenantA), ("ta-admin", database.TenantA), ("user-b", database.TenantB) })
        {
            var (scope, context) = await Units.EnterAsync(server.Services, subject, tenant);
            await using (scope)
            {
                keys[subject] = cache.KeyFor(context, bootstrap);
                Assert.Equal("values for " + subject, await cache.GetOrCreateAsync(context, bootstrap, _ => Task.FromResult("values for " + subject), Cancellation));
            }
        }
        Assert.Equal(3, keys.Values.Distinct().Count());
        foreach (var (subject, tenant) in new[] { ("user-a", database.TenantA), ("ta-admin", database.TenantA), ("user-b", database.TenantB) })
        {
            var (scope, context) = await Units.EnterAsync(server.Services, subject, tenant);
            await using (scope)
                Assert.Equal("values for " + subject, await cache.GetOrCreateAsync<string>(context, bootstrap,
                    _ => throw new InvalidOperationException("A warmed entry must be a hit."), Cancellation));
        }

        // Alternating units on one worker: each has a new scope and its own context; none shares a service instance.
        database.Seams.Authority.Grant("dispatcher", WorkDispatch.Operation);
        await WorkDispatch.EnqueueAsync(database, server.Services, "user-a", database.TenantA, "job", "1-a");
        await WorkDispatch.EnqueueAsync(database, server.Services, "user-b", database.TenantB, "job", "2-b");
        await WorkDispatch.EnqueueAsync(database, server.Services, "user-a", database.TenantA, "event", "3-a");
        var nestedContexts = new List<TenantContext?>();
        var runs = await WorkDispatch.DrainAsync(database, server.Services, async (item, work, _) =>
        {
            Assert.Equal(item.TenantId, work.Context.Tenant!.TenantId);
            // A scope created during the unit, without the context passed to it, has none.
            await using var nested = server.Services.CreateAsyncScope();
            nestedContexts.Add(nested.ServiceProvider.GetRequiredService<TenantExecution>().Context);
        }, Cancellation);
        Assert.All(runs, run => Assert.Equal(run.Item.TenantId, run.Context!.Tenant));
        Assert.Equal(3, nestedContexts.Count);
        Assert.All(nestedContexts, Assert.Null);
        Assert.Equal([(database.TenantA, "code-1"), (database.TenantA, "done-1-a"), (database.TenantA, "done-3-a"),
            (database.TenantB, "code-1"), (database.TenantB, "done-2-b")],
            (await database.InventoryAsync("Items", "Code")).OrderBy(row => row.Tenant == database.TenantA ? 0 : 1).ThenBy(row => row.Value));
        Assert.Equal([(database.TenantA, "completed"), (database.TenantA, "completed"), (database.TenantB, "completed")],
            (await database.InventoryAsync("WorkItems", "State")).OrderBy(row => row.Tenant == database.TenantA ? 0 : 1));
        Log($"Principal-scoped keys: {string.Join("; ", keys.Select(pair => pair.Key + " " + pair.Value))}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_R01_RecoveredWorkRevalidatesTenantAndActor_Isolation()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        var recovery = await RecoverAsync(database);
        // Every unit ran in a fresh scope reconstructed from its captured references, never under the other tenant.
        var completed = recovery.Restart.Where(run => run.Outcome.Disposition == TenantWorkDisposition.Completed).ToArray();
        Assert.Equal(["1-a-job", "3-a-event"], completed.Select(run => run.Item.Payload));
        Assert.All(completed, run =>
        {
            Assert.Equal(database.TenantA, run.Context!.Tenant);
            Assert.Equal("user-a", run.Context.Actor);
            Assert.Equal("Propagated", run.Context.Source);
            Assert.NotEqual(Producer(run.Item).ProducerUnitId, run.Context.Unit);
        });
        Assert.DoesNotContain(recovery.Restart, run => run.Context?.Tenant == database.TenantB);
        Assert.Equal([(database.TenantA, "done-1-a-job"), (database.TenantA, "done-3-a-event")],
            (await database.InventoryAsync("Items", "Code")).OrderBy(row => row.Value));
        Assert.Equal(2, completed.Select(run => run.Context!.Unit).Distinct().Count());
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_R01_RecoveredWorkRevalidatesTenantAndActor_Integration()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        var recovery = await RecoverAsync(database);
        // PostgreSQL holds each outcome: completed tenant-A work, denied tenant-B work with its reason, and no tenant-B effect.
        Assert.Equal([(database.TenantA, "completed"), (database.TenantA, "completed"), (database.TenantB, "denied:MTN007"), (database.TenantB, "denied:MTN007")],
            (await database.InventoryAsync("WorkItems", "State")).OrderBy(row => row.Tenant == database.TenantA ? 0 : 1));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Items") + " WHERE \"TenantId\" = @tenant",
            new NpgsqlParameter("tenant", database.TenantB)));
        Assert.Equal([(database.TenantA, "1"), (database.TenantA, "1"), (database.TenantB, "0"), (database.TenantB, "0")],
            (await database.InventoryAsync("WorkItems", "Attempts")).OrderBy(row => row.Tenant == database.TenantA ? 0 : 1));
        // Each dispatcher pass ran in its own audited host scope.
        Assert.Equal(3, database.Seams.Audit.Records.Count(record => record.OperationId == WorkDispatch.Operation));
        Assert.Equal(4, recovery.Restart.Count);
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_R01_RecoveredWorkRevalidatesTenantAndActor_Recovery()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        var recovery = await RecoverAsync(database);
        // The crash rolled back the interrupted unit's effect and completion together, so the restart ran it once.
        Assert.Equal("1-a-job", recovery.Interrupted);
        Assert.Equal(0, recovery.EffectsAfterCrash);
        Assert.Equal(4, recovery.QueuedAfterCrash);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Items") + " WHERE \"Code\" = 'done-1-a-job'"));
        // The work whose actor lost membership is refused at revalidation with a terminal disposition and is not retried.
        var denied = recovery.Restart.Where(run => run.Outcome.Disposition == TenantWorkDisposition.Denied).ToArray();
        Assert.Equal(["2-b-job", "4-b-event"], denied.Select(run => run.Item.Payload));
        Assert.All(denied, run =>
        {
            Assert.Equal(TenancyCodes.NotMember, run.Outcome.Code);
            Assert.Null(run.Outcome.Context);
        });
        Assert.Empty(recovery.Drained);
        foreach (var run in recovery.Restart) Log($"Restart {run.Item.Payload}: {run.Outcome.Disposition} {run.Outcome.Code} {run.Context}.");
    }

    private sealed record Recovery(string Interrupted, int EffectsAfterCrash, int QueuedAfterCrash, List<WorkDispatch.Run> Restart, List<WorkDispatch.Run> Drained);

    /// <summary>
    /// Both tenants enqueue a job and an event; user-b then loses membership. The first host stops during the first job's
    /// execution and is discarded; a restarted host drains everything; a further pass finds nothing left to run.
    /// </summary>
    private static async Task<Recovery> RecoverAsync(TenancyDatabase database)
    {
        await using (var producer = database.Services())
        {
            await database.SeedAsync(producer);
            await WorkDispatch.EnqueueAsync(database, producer, "user-a", database.TenantA, "job", "1-a-job");
            await WorkDispatch.EnqueueAsync(database, producer, "user-b", database.TenantB, "job", "2-b-job");
            await WorkDispatch.EnqueueAsync(database, producer, "user-a", database.TenantA, "event", "3-a-event");
            await WorkDispatch.EnqueueAsync(database, producer, "user-b", database.TenantB, "event", "4-b-event");
        }
        database.Seams.Membership.Remove("user-b", database.TenantB);
        database.Seams.Authority.Grant("dispatcher", WorkDispatch.Operation);

        string? interrupted = null;
        await using (var first = database.Services())
        {
            using var stopping = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkDispatch.DrainAsync(database, first, async (item, work, token) =>
            {
                // The host stops after the effect is flushed inside the unit's transaction and before it commits.
                await work.FlushAsync(token);
                interrupted = item.Payload;
                stopping.Cancel();
                token.ThrowIfCancellationRequested();
            }, stopping.Token));
        }
        var effects = (int)await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Items"));
        var queued = (int)await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("WorkItems") + " WHERE \"State\" = 'queued'");

        await using var restarted = database.Services();
        var restart = await WorkDispatch.DrainAsync(database, restarted, cancellationToken: Cancellation);
        var drained = await WorkDispatch.DrainAsync(database, restarted, cancellationToken: Cancellation);
        return new(interrupted!, effects, queued, restart, drained);
    }

    private static PropagatedTenantContext Producer(WorkItem item) =>
        PropagatedTenantContext.TryParse(item.Propagated, out var parsed) ? parsed! : throw new InvalidOperationException("Unreadable reference.");

    private static async Task<PropagatedTenantContext> CaptureAsync(IServiceProvider services, string subject, Guid tenant)
    {
        var (scope, context) = await Units.EnterAsync(services, subject, tenant);
        await using (scope) return PropagatedTenantContext.Capture(context);
    }

    private static async Task<PropagatedTenantContext> CaptureHostAsync(IServiceProvider services, string subject, string operation)
    {
        var (scope, context) = await Units.EnterHostAsync(services, subject, operation, service: true);
        await using (scope) return PropagatedTenantContext.Capture(context);
    }

    private static async Task AddItemAsync(TenancyDatabase database, IServiceProvider services, string subject, Guid tenant, string code)
    {
        var (scope, context) = await Units.EnterAsync(services, subject, tenant);
        await using (scope)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation);
            ((ItemsContext)work.Context).Items.Add(new Item { Id = Guid.NewGuid(), Code = code, Name = code });
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
    }

    private static async Task<object> TransactionIdAsync(EfCoreUnitOfWork work, CancellationToken cancellationToken)
    {
        await using var command = work.Context.Database.GetDbConnection().CreateCommand();
        command.Transaction = work.Context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT txid_current_if_assigned()";
        return (await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
