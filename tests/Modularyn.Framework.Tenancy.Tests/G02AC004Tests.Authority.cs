using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.HttpApi;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// AC-004-N03, SEC-08's: ta-admin administers tenant-A without host authority, and host-1 holds host authority under the
/// elevated policy. Host scope is never applied implicitly or by a request's markers; work produced in tenant-A whose payload
/// names tenant-B runs in tenant-A alone and is rejected there; forged, copied and replayed references, and a service identity
/// reaching past its scope or acting for a user outside propagated work, are refused; host-1's operation is audited.
/// </summary>
public sealed partial class G02AC004Tests
{
    private const string ReportReason = "Count every tenant's items";

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_N03_HostAndWorkerAuthorityCannotComeFromPayloads_Isolation()
    {
        await using var database = await AdversarialDatabase.CreateAsync();
        await using var host = AdversarialHost.Create(database);
        var seed = await AdversarialTenants.SeedAsync(host);
        var report = AdversarialItems.Report.Id;
        async Task<string> AttemptAsync(Account account, Func<ClaimsPrincipal, TenantResolutionRequest> request)
        {
            var (decision, unit) = await host.TryEnterAsync(account, request);
            if (unit is not null) await unit.DisposeAsync();
            return decision.Succeeded ? "granted" : decision.Code!;
        }

        // 1. ta-admin calls the cross-tenant report directly, then with tenant-B's identifier as a route value or header, beside
        //    tenant-A's domain, and with a tenant claim: host scope comes only from separately held host authority.
        Assert.Equal(TenancyCodes.HostAuthorityRefused, await AttemptAsync(seed.TaAdmin, claims => TenantResolutionRequest.ForHostOperation(claims, report, ReportReason)));
        foreach (var marker in new TenantCandidate[]
        {
            new(TenantCandidateSource.Route, seed.TenantB.ToString("D")), new(TenantCandidateSource.Header, seed.TenantB.ToString("D")),
            new(TenantCandidateSource.Host, AdversarialHost.HostA)
        })
            Assert.Equal(TenancyCodes.HostCandidates, await AttemptAsync(seed.TaAdmin, claims => TenantResolutionRequest.ForHostOperation(claims, report, ReportReason, [marker])));
        Assert.Equal(TenancyCodes.HostAuthorityRefused, await AttemptAsync(seed.TaAdmin, claims =>
        {
            ((ClaimsIdentity)claims.Identity!).AddClaim(new Claim(TenantClaimTypes.Tenant, seed.TenantB.ToString("D")));
            return TenantResolutionRequest.ForHostOperation(claims, report, ReportReason);
        }));
        // Owning tenant-A confers no host scope: ta-admin's unit is tenant-A's, it reads no other tenant's rows, and it can grant
        // itself the report neither as a permission nor as host authority.
        await using (var admin = await host.EnterAsync(seed.TaAdmin, seed.TenantA))
        {
            Assert.Equal((TenantScope.Tenant, (Guid?)seed.TenantA), (admin.Context.Scope, admin.Context.TenantId));
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, admin.Context, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
                Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Context.AcrossTenantsAsync<Item>(Cancellation))).Code);
            Assert.Equal(AuthorizationCodes.ChangeUndefined, (await Assert.ThrowsAsync<AuthorityChangeException>(() => host.Get<AuthorityAdministration>()
                .GrantPermissionAsync(admin.Context, admin.Principal, seed.TaAdmin.Actor, report, Cancellation))).Code);
            Assert.Equal(AuthorizationCodes.ChangeNotPermitted, (await Assert.ThrowsAsync<AuthorityChangeException>(() => host.Get<HostAuthorityAdministration>()
                .GrantAsync(admin.Context, seed.TaAdmin.Actor, report, Cancellation))).Code);
        }

        // 2. From tenant-A's context, work whose payload names tenant-B's item-b1, and work whose payload names tenant-B as its
        //    target. The dispatcher runs each in its own revalidated unit as the worker's service identity; neither unit can be
        //    tenant-B's, a lookup by the payload's identifier finds nothing, a write for tenant-B is refused, and each is rejected.
        static PropagationBinding Binding(Guid id) => PropagationBinding.ForRow("tests.adversarial-work", id);
        var userA2 = await host.AccountAsync("user-a2", seed.TenantA);
        await using (var admin = await host.EnterAsync(seed.TaAdmin, seed.TenantA)) await host.GrantAsync(admin, userA2, AdversarialItems.Read);
        var produced = new Dictionary<Guid, string>();
        foreach (var (producer, kind, payload) in new[]
        {
            (seed.UserA, "lookup", "item=" + seed.ItemB1.Id.ToString("D")),
            (seed.UserA, "target", "tenant=" + seed.TenantB.ToString("D") + ";code=payload-made"),
            (userA2, "lookup", "item=" + seed.ItemA1.Id.ToString("D"))
        })
        {
            await using var unit = await host.EnterAsync(producer, seed.TenantA);
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: Cancellation);
            var id = Guid.NewGuid();
            work.Context.Add(new WorkItem { Id = id, Kind = kind, Payload = payload, Propagated = PropagatedTenantContext.Capture(unit.Context).ToJson(Binding(id)) });
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
            produced[id] = producer.Name + "/" + kind;
        }
        var before = await database.TenantInventoryAsync(seed.TenantB);
        var dispatch = await host.HostAsync(AdversarialItems.Dispatch.Id);
        await using var worker = host.Get<IServiceIdentityScopes>().Begin(AdversarialHost.Service);
        var runner = host.Get<AuthorizedWorkRunner>();
        List<WorkItem> queued;
        await using (var read = await EfCoreUnitOfWork.BeginAsync(database.Items, dispatch, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
            queued = await (await read.Context.AcrossTenantsAsync<WorkItem>(Cancellation)).Query.AsNoTracking().Where(item => item.State == "queued").ToListAsync(Cancellation);
        Assert.Equal(3, queued.Count);
        var dispositions = new Dictionary<string, string>();
        foreach (var item in queued)
        {
            ContextSnapshot? ran = null;
            string disposition;
            try
            {
                var outcome = await runner.RunAsync(worker.Principal, item.Propagated, Binding(item.Id), AdversarialItems.Write, async (unit, _, token) =>
                {
                    ran = unit.Context.Snapshot();
                    await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, unit.Context, cancellationToken: token);
                    var items = (ItemsContext)work.Context;
                    var fields = item.Payload.Split(';').Select(field => field.Split('=')).ToDictionary(pair => pair[0], pair => pair[1]);
                    // A naive handler that trusts its payload: it looks the item up by the identifier it names, or writes for the
                    // tenant it names. Only the unit's own tenant can answer either.
                    if (fields.TryGetValue("item", out var named))
                    {
                        var target = await items.Items.SingleOrDefaultAsync(row => row.Id == Guid.Parse(named), token)
                            ?? throw new KeyNotFoundException("The payload's item is not in the unit's tenant.");
                        target.Name = "changed by its payload";
                    }
                    else items.Items.Add(new Item { TenantId = Guid.Parse(fields["tenant"]), Id = Guid.NewGuid(), Code = fields["code"], Name = "for the payload's tenant" });
                    await work.FlushAsync(token);
                    Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(token)).State);
                }, Cancellation);
                disposition = outcome.Disposition == TenantWorkDisposition.Completed ? "completed" : "denied:" + outcome.Code;
            }
            catch (KeyNotFoundException) { disposition = "rejected:not-found"; }
            catch (PersistenceContractException exception) { disposition = "rejected:" + exception.Code; }
            // The dispatcher records the disposition on the work item, in its own tenant, from its audited host scope.
            await using (var write = await EfCoreUnitOfWork.BeginAsync(database.Items, dispatch, cancellationToken: Cancellation))
            {
                Assert.True((await write.Sql.UpdateForTenantAsync<WorkItem, Guid, string>(item.TenantId, row => row.Id, item.Id, item.Version, row => row.State,
                    disposition, Cancellation)).Succeeded);
                Assert.Equal(UnitOfWorkState.Committed, (await write.CompleteAsync(Cancellation)).State);
            }
            if (ran is not null) Assert.Equal((seed.TenantA, "Propagated"), (ran.Tenant!.Value, ran.Source));
            dispositions[produced[item.Id]] = disposition + (ran is null ? "" : " as " + ran.Actor);
            Log($"Work {produced[item.Id]} with payload {item.Payload}: {disposition}, unit {ran}.");
        }
        Assert.Equal(new Dictionary<string, string>
        {
            ["user-a/lookup"] = "rejected:not-found as " + seed.UserA.Id.ToString("D"),
            ["user-a/target"] = "rejected:MPS036 as " + seed.UserA.Id.ToString("D"),
            // The worker's scope holds items.write, but the producer does not: a service identity adds no authority of its own.
            ["user-a2/lookup"] = "denied:" + AuthorizationCodes.NotHeld
        }, dispositions);
        Assert.Equal(before, await database.TenantInventoryAsync(seed.TenantB));
        Assert.Equal(["denied:" + AuthorizationCodes.NotHeld, "rejected:MPS036", "rejected:not-found"],
            (await database.RowsAsync("SELECT \"State\" FROM " + database.Table("items", "WorkItems") + " WHERE \"TenantId\" = @tenant ORDER BY 1",
                new NpgsqlParameter("tenant", seed.TenantA))).Select(row => row[0]));

        // A service identity acts for a user only in that user's propagated work. In a member context that names user-a but was
        // resolved from claims a worker built, not from a reference user-a's unit sealed, the service has no subject at all.
        await using (var service = host.Get<IServiceIdentityScopes>().Begin(AdversarialHost.Service))
        {
            var forged = await service.Services.GetRequiredService<TenantExecution>().EstablishAsync(TenantResolutionRequest.ForMember(AdversarialHost.Claims(seed.UserA),
                [new TenantCandidate(TenantCandidateSource.Header, seed.TenantA.ToString("D"))]), Cancellation);
            Assert.True(forged.Succeeded, forged.Code);
            Assert.Equal((TenantContextSource.Request, seed.UserA.Id.ToString("D")), (forged.Context!.Source, forged.Context.Actor.Subject));
            var decision = await host.Get<IPermissionEvaluator>().AuthorizeAsync(new AuthorizationRequest(forged.Context, service.Principal, AdversarialItems.Write),
                Cancellation);
            Assert.Equal(AuthorizationCodes.PrincipalMismatch, decision.Code);
            Assert.Null(await host.Get<IPermissionEvaluator>().DescribeAsync(forged.Context, service.Principal, Cancellation));
        }
        // A user's own session cannot be presented for another user named by claims either.
        var (session, userAPrincipal) = await host.SignInAsync(seed.UserA);
        Assert.NotNull(session);
        await using (var unit = await host.EnterAsync(seed.TaAdmin, seed.TenantA))
            Assert.Equal(AuthorizationCodes.PrincipalMismatch, (await host.Get<IPermissionEvaluator>().AuthorizeAsync(
                new AuthorizationRequest(unit.Context, userAPrincipal, AdversarialItems.Write), Cancellation)).Code);
        // A worker whose declared scope lacks the permission cannot run the producer's work, though the producer holds it.
        await using (var narrow = AdversarialHost.Create(database, [AdversarialItems.Read]))
        await using (var unit = await narrow.EnterAsync(seed.UserA, seed.TenantA))
        await using (var narrowed = narrow.Get<IServiceIdentityScopes>().Begin(AdversarialHost.Service))
        {
            var id = Guid.NewGuid();
            var reference = PropagatedTenantContext.Capture(unit.Context).ToJson(Binding(id));
            var ran = false;
            var outcome = await narrow.Get<AuthorizedWorkRunner>().RunAsync(narrowed.Principal, reference, Binding(id), AdversarialItems.Write, (_, _, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            }, Cancellation);
            Assert.Equal((TenantWorkDisposition.Denied, AuthorizationCodes.OutsideCeiling, false), (outcome.Disposition, outcome.Code, ran));
        }

        // 3. host-1 runs the report under the elevated policy: it reaches both tenants' items, and audit holds its host scope,
        //    its cross-tenant read and the tenants the report reached, each with host-1 as the actor. ta-admin's attempts left none.
        var (granted, reporting) = await host.TryEnterAsync(seed.Host1, claims => TenantResolutionRequest.ForHostOperation(claims, report, ReportReason));
        Assert.True(granted.Succeeded, granted.Code);
        TenantCount[] counts;
        await using (reporting)
        {
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, reporting!.Context, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
                counts = await (await work.Context.AcrossTenantsAsync<Item>(Cancellation)).Query.GroupBy(row => row.TenantId)
                    .Select(group => new TenantCount(group.Key, group.Count())).ToArrayAsync(Cancellation);
            foreach (var count in counts)
                await host.Get<IAuditTrail>().RecordAccessAsync(new AuditEntry(AdversarialEndpoints.ReportAction, reporting.Context.Actor)
                {
                    TenantId = count.Tenant, UnitId = reporting.Context.UnitId, OperationId = report, Reason = ReportReason
                }, Cancellation);
        }
        Assert.Equal(new[] { seed.TenantA, seed.TenantB }.Order(), counts.Select(count => count.Tenant).Order());
        // host-1's host authority is no membership: it cannot enter either tenant as a member.
        Assert.Equal(TenancyCodes.NotMember, await AttemptAsync(seed.Host1, claims => TenantResolutionRequest.ForMember(claims,
            [new TenantCandidate(TenantCandidateSource.Header, seed.TenantA.ToString("D"))])));
        var audited = await database.AuditAsync();
        var host1 = seed.Host1.Id.ToString("D");
        Assert.Contains(audited, row => row.Action == AuditActions.HostScopeGranted && row.Actor == host1 && row.Operation == report && row.Unit == granted.Context!.UnitId);
        Assert.Contains(audited, row => row.Action == AuditActions.CrossTenantRead && row.Actor == host1 && row.Operation == report);
        Assert.Equal(new[] { seed.TenantA, seed.TenantB }.Order(),
            audited.Where(row => row.Action == AdversarialEndpoints.ReportAction && row.Actor == host1).Select(row => row.Tenant!.Value).Order());
        Assert.DoesNotContain(audited, row => row.Actor == seed.TaAdmin.Id.ToString("D") && row.Action is AuditActions.HostScopeGranted or AuditActions.CrossTenantRead);
        foreach (var row in audited.Where(row => row.Actor == host1)) Log($"Audit: {row.Action} by host-1, operation {row.Operation}, tenant {row.Tenant}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_N03_HostAndWorkerAuthorityCannotComeFromPayloads_Integration()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenantA = await host.TenantAsync();
        var tenantB = await host.TenantAsync();
        var taAdmin = await host.UserAsync(tenantA, owner: true);
        var userA = await host.UserAsync(tenantA);
        var userA2 = await host.UserAsync(tenantA);
        var userB = await host.UserAsync(tenantB, owner: true);
        var host1 = await DurableAccountAsync(host, "host-1");
        await using (var admin = await host.EnterAsync(taAdmin, tenantA)) await host.GrantAsync(admin, userA, DurableModel.Apply);
        await using (var b = await host.EnterAsync(userB, tenantB))
        {
            await NotesAsync(host, database.Producer, b, "item-b1");
            await NotesAsync(host, database.Consumer, b, "item-b1");
        }
        await using (var a = await host.EnterAsync(userA, tenantA)) await NotesAsync(host, database.Producer, a, "item-a1");

        // 1. ta-admin, owner of tenant-A, asks for the dispatch host operation, plainly and with tenant-B named beside it.
        var dispatchId = DurableModel.Dispatch.Id;
        Assert.Equal(TenancyCodes.HostAuthorityRefused, (await DurableHostScopeAsync(host, taAdmin, dispatchId)).Code);
        Assert.Equal(TenancyCodes.HostCandidates, (await DurableHostScopeAsync(host, taAdmin, dispatchId,
            new TenantCandidate(TenantCandidateSource.Header, tenantB.ToString("D")))).Code);

        // 2. From tenant-A's context, an event whose payload names tenant-B as its target, and a job whose arguments name
        //    tenant-B's item-b1. Each runs in tenant-A's revalidated unit, finds no item-b1, cannot write for tenant-B, and is
        //    quarantined or failed with its reason.
        var target = tenantB.ToString("D") + "/item-b1";
        var jobs = new JobStore(database.Producer, clock, new JobPolicy(maxAttempts: 1), host.Get<IAuditTrail>());
        Guid eventId, jobId;
        await using (var a = await host.EnterAsync(userA, tenantA))
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, a.Context, cancellationToken: Cancellation);
            eventId = host.Outbox.Append(work, new EventIntent(DurableModel.Consumer, "item.target", 1, "item-b1", 1, target));
            jobId = jobs.Enqueue(work, new JobRequest("item.target", target, clock.GetUtcNow()));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        var beforeB = await DurableTenantRowsAsync(host, tenantB);
        static async Task ActForPayloadAsync(EfCoreUnitOfWork work, string payload, CancellationToken token)
        {
            var parts = payload.Split('/');
            // A naive handler: it looks the item up by the payload's key, then writes for the tenant the payload names.
            Assert.Null(await work.Context.Set<DurableNote>().SingleOrDefaultAsync(row => row.Key == parts[1], token));
            work.Context.Add(new DurableEffect { TenantId = Guid.Parse(parts[0]), Key = parts[1], Count = 1 });
            await work.FlushAsync(token);
        }
        var pump = new EventPump(host.Outbox, host.Inbox, new EventConsumer(DurableModel.Consumer, DurableModel.Apply,
            [new Events.EventHandler("item.target", 1, (execution, token) => ActForPayloadAsync(execution.Work, execution.Event.Payload, token))]),
            host.Get<AuthorizedWorkRunner>(), clock, new EventRetryPolicy(attempts: 1));
        var worker = new JobWorker(jobs, host.Get<AuthorizedWorkRunner>(), host.Get<IPermissionEvaluator>(),
            [new JobDefinition("item.target", DurableModel.Apply, (execution, token) => ActForPayloadAsync(execution.Work, execution.Payload, token))]);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        Assert.Equal(1, await pump.DispatchAsync(dispatch, principal, cancellationToken: Cancellation));
        Assert.Equal(0, await pump.ConsumeAsync(dispatch, principal, cancellationToken: Cancellation));
        Assert.Equal(0, await worker.RunDueAsync(dispatch, principal, cancellationToken: Cancellation));
        await using (var read = await host.EnterServiceAsync(tenantA))
        {
            var receipt = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, read.Context), row => row.Id == eventId);
            Assert.Equal((EventState.Poison, "HandlerFailed", 1), (receipt.State, receipt.Code, receipt.Attempts));
            var job = Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, read.Context), row => row.Id == jobId);
            Assert.Equal(JobState.Failed, job.State);
            var attempt = Assert.Single(await host.RowsAsync<JobAttempt>(database.Producer, read.Context), row => row.JobId == jobId);
            Assert.Equal(JobAttemptState.Failed, attempt.State);
            Log($"Event naming tenant-B: {receipt.State} {receipt.Code}; job naming item-b1: {job.State} {job.Code}, attempt {attempt.State} {attempt.Code}.");
            Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, read.Context));
        }
        Assert.Equal(beforeB, await DurableTenantRowsAsync(host, tenantB));

        // Forged and copied references on stored jobs: a seal replaced by another value or by another key ring's, an actor
        // rewritten to another user, and a reference copied from another row of the same tenant are each denied unopened; the
        // untouched row runs.
        var forged = new Guid[5];
        await using (var a = await host.EnterAsync(userA, tenantA))
            for (var index = 0; index < forged.Length; index++) forged[index] = await host.QueueAsync(a, "forged-" + index);
        var jobsTable = "\"" + database.ProducerSchema + "\".\"Jobs\"";
        async Task<string> PropagatedOfAsync(Guid id) => await database.ScalarAsync<string>("SELECT \"Propagated\" FROM " + jobsTable + " WHERE \"Id\" = @id",
            new NpgsqlParameter("id", id));
        async Task SetAsync(Guid id, string column, object value) => await database.ExecuteAsync("UPDATE " + jobsTable + " SET \"" + column + "\" = @value WHERE \"Id\" = @id",
            new NpgsqlParameter("value", value), new NpgsqlParameter("id", id));
        var original = await PropagatedOfAsync(forged[0]);
        var seal = JsonDocument.Parse(original).RootElement.GetProperty("seal").GetString()!;
        await SetAsync(forged[0], "Propagated", original.Replace(seal, "CfDJ8" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(96))
            .Replace('+', '-').Replace('/', '_').TrimEnd('='), StringComparison.Ordinal));
        var foreignSeal = new PropagationSeal(new EphemeralDataProtectionProvider()).Seal(System.Text.Encoding.UTF8.GetBytes(await PropagatedOfAsync(forged[1])));
        await SetAsync(forged[1], "Propagated", (await PropagatedOfAsync(forged[1])).Replace(JsonDocument.Parse(await PropagatedOfAsync(forged[1])).RootElement
            .GetProperty("seal").GetString()!, foreignSeal, StringComparison.Ordinal));
        await SetAsync(forged[2], "Propagated", (await PropagatedOfAsync(forged[2])).Replace(userA.Id.ToString("D"), taAdmin.Id.ToString("D"), StringComparison.Ordinal));
        await SetAsync(forged[3], "Propagated", await PropagatedOfAsync(forged[4]));
        Assert.Equal(1, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Cancellation));
        await using (var read = await host.EnterServiceAsync(tenantA))
        {
            var rows = await host.RowsAsync<JobRecord>(database.Producer, read.Context);
            foreach (var id in forged[..4])
            {
                var row = rows.Single(job => job.Id == id);
                Assert.Equal((JobState.Denied, TenancyCodes.Unsealed, DurablePayloads.Redacted), (row.State, row.Code, row.Payload));
            }
            Assert.Equal(JobState.Completed, rows.Single(job => job.Id == forged[4]).State);
            Assert.Equal(1, Assert.Single(await host.RowsAsync<DurableEffect>(database.Producer, read.Context), effect => effect.Key == "forged-4").Count);
        }

        // Replay and rewrite of the untouched row, as a database writer could: the seal binds the row's identity and purpose,
        // not its state, payload or kind, so the row runs again, and with a rewritten payload, under the same revalidated
        // context. That is the direct-database residual: it never runs in another tenant or beyond its producer's current
        // authority, which a revocation shows.
        async Task RequeueAsync(Guid id)
        {
            await database.ExecuteAsync("DELETE FROM \"" + database.ProducerSchema + "\".\"JobAttempts\" WHERE \"JobId\" = @id", new NpgsqlParameter("id", id));
            await database.ExecuteAsync("UPDATE " + jobsTable + " SET \"State\" = 0, \"Lease\" = NULL, \"LeaseUntil\" = NULL, \"AvailableAt\" = @now, \"Attempts\" = 0 "
                + "WHERE \"Id\" = @id", new NpgsqlParameter("now", clock.GetUtcNow()), new NpgsqlParameter("id", id));
        }
        // A completed row's payload is redacted, so the writer puts it back before requeueing the row.
        await SetAsync(forged[4], "Payload", "forged-4");
        await RequeueAsync(forged[4]);
        Assert.Equal(1, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Cancellation));
        await SetAsync(forged[4], "Payload", "rewritten");
        await RequeueAsync(forged[4]);
        Assert.Equal(1, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Cancellation));
        await using (var read = await host.EnterServiceAsync(tenantA))
        {
            var effects = await host.RowsAsync<DurableEffect>(database.Producer, read.Context);
            Assert.Equal((2, 1), (effects.Single(effect => effect.Key == "forged-4").Count, effects.Single(effect => effect.Key == "rewritten").Count));
        }
        await SetAsync(forged[4], "Kind", "item.target");
        await SetAsync(forged[4], "Payload", target);
        await RequeueAsync(forged[4]);
        // A rewritten kind selects that kind's handler and permission, still in tenant-A's unit: it cannot write for tenant-B.
        Assert.Equal(0, await worker.RunDueAsync(dispatch, principal, cancellationToken: Cancellation));
        await using (var admin = await host.EnterAsync(taAdmin, tenantA))
            Assert.True(await host.Get<AuthorityAdministration>().RevokePermissionAsync(admin.Context, admin.Principal, userA.Actor, DurableModel.Apply, Cancellation));
        await SetAsync(forged[4], "Kind", "durable.counter");
        await SetAsync(forged[4], "Payload", "after-revocation");
        await RequeueAsync(forged[4]);
        Assert.Equal(0, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Cancellation));
        await using (var read = await host.EnterServiceAsync(tenantA))
        {
            var row = (await host.RowsAsync<JobRecord>(database.Producer, read.Context)).Single(job => job.Id == forged[4]);
            Assert.Equal((JobState.Denied, AuthorizationCodes.NotHeld), (row.State, row.Code));
            Assert.DoesNotContain(await host.RowsAsync<DurableEffect>(database.Producer, read.Context), effect => effect.Key == "after-revocation");
        }
        Assert.Equal(beforeB, await DurableTenantRowsAsync(host, tenantB));
        Log("Replayed and rewritten rows ran again under the producer's revalidated tenant-A context, and were denied once its grant was revoked.");

        // Service identities acting for users: the worker runs a user's work only with that user's current authority and within
        // its own declared scope. A worker scoped without the permission cannot run it, and a user without it is refused.
        Guid narrowJob, unheldJob;
        await using (var a = await host.EnterAsync(taAdmin, tenantA)) narrowJob = await host.QueueAsync(a, "narrow");
        await using (var narrow = DurableHost.Create(database, clock, database.Registry.Permissions.Select(permission => permission.Name)
            .Where(name => name != DurableModel.Apply)))
            Assert.Equal(0, await narrow.Worker().RunDueAsync(await narrow.HostAsync(), narrow.WorkerPrincipal(), cancellationToken: Cancellation));
        await using (var a = await host.EnterAsync(userA2, tenantA)) unheldJob = await host.QueueAsync(a, "unheld");
        Assert.Equal(0, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Cancellation));
        await using (var read = await host.EnterServiceAsync(tenantA))
        {
            var rows = await host.RowsAsync<JobRecord>(database.Producer, read.Context);
            Assert.Equal((JobState.Denied, AuthorizationCodes.OutsideCeiling), (rows.Single(job => job.Id == narrowJob).State, rows.Single(job => job.Id == narrowJob).Code));
            Assert.Equal((JobState.Denied, AuthorizationCodes.NotHeld), (rows.Single(job => job.Id == unheldJob).State, rows.Single(job => job.Id == unheldJob).Code));
        }

        // 3. host-1 holds the dispatch operation under a stored grant and runs it: its host scope and its cross-tenant reads
        //    are audited with host-1 as the actor; none of ta-admin's attempts produced a host scope.
        Assert.True(await host.Get<HostAuthorityAdministration>().GrantAsync(await host.HostAsync(AuthorizationModule.HostAuthorityOperation.Id), host1.Actor,
            dispatchId, Cancellation));
        var granted = await DurableHostScopeAsync(host, host1, dispatchId);
        Assert.True(granted.Succeeded, granted.Code);
        await using (var a = await host.EnterAsync(userA2, tenantA)) await host.ProduceAsync(a, "after-grant");
        Assert.Equal(0, await host.Pump().DispatchAsync(granted.Context!, principal, cancellationToken: Cancellation));
        var notes = await host.RowsAcrossAsync(database.Producer, granted.Context!);
        Assert.Equal(new[] { tenantA, tenantB }.Order(), notes.Select(note => note.TenantId).Distinct().Order());
        var hostScopes = await database.AuditAsync(AuditActions.HostScopeGranted);
        var reads = await database.AuditAsync(AuditActions.CrossTenantRead);
        Assert.Contains(hostScopes, row => row.Actor == host1.Id.ToString("D") && row.Operation == dispatchId && row.Unit == granted.Context!.UnitId);
        Assert.Contains(reads, row => row.Actor == host1.Id.ToString("D") && row.Operation == dispatchId);
        Assert.DoesNotContain(hostScopes, row => row.Actor == taAdmin.Id.ToString("D"));
        Log($"host-1's dispatch: host scope {granted.Context!.UnitId} audited; {reads.Count(row => row.Actor == host1.Id.ToString("D"))} cross-tenant reads audited.");
    }

    private static async Task<DurableUser> DurableAccountAsync(DurableHost host, string name)
    {
        var password = "pw-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var created = await host.Get<IAccountService>().CreateLocalAccountAsync(IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.AccountsId)), name,
            new SecretText(password), Cancellation);
        Assert.Equal(AccountCreationStatus.Created, created.Status);
        return new(created.AccountId!.Value, name, password);
    }

    /// <summary>A host scope requested by the user through its real session, as a request would, with any candidates beside it.</summary>
    private static async Task<TenantDecision> DurableHostScopeAsync(DurableHost host, DurableUser user, string operationId, params TenantCandidate[] candidates)
    {
        var signedIn = await host.Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(user.Name, new SecretText(user.Password), "203.0.113.5"),
            Cancellation);
        Assert.Equal(SignInStatus.Succeeded, signedIn.Status);
        var session = await host.Get<ISessionService>().ValidateAsync(signedIn.Session!.Token, Cancellation);
        var scope = host.Services.CreateAsyncScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(session!.ToPrincipal()));
        var claims = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D"))],
            IdentityHttpServiceCollectionExtensions.AuthenticationScheme));
        return await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(TenantResolutionRequest.ForHostOperation(claims, operationId,
            "Adversarial host operation", candidates), Cancellation);
    }

    private static async Task NotesAsync(DurableHost host, PostgreSqlModule module, DurableUnit unit, string key)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, unit.Context, cancellationToken: Cancellation);
        work.Context.Add(new DurableNote { Key = key });
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
    }

    /// <summary>Tenant-B's rows in both durable modules, read directly, as an inventory before and after.</summary>
    private static async Task<List<string>> DurableTenantRowsAsync(DurableHost host, Guid tenant)
    {
        var rows = new List<string>();
        await using var connection = new NpgsqlConnection(host.Database.Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        foreach (var schema in new[] { host.Database.ProducerSchema, host.Database.Consumer.Schema })
            foreach (var table in new[] { "Notes", "Effects", "OutboxEvents", "InboxEvents", "Jobs", "JobAttempts" })
            {
                await using var command = new NpgsqlCommand("SELECT row_to_json(t)::text FROM \"" + schema + "\".\"" + table + "\" t WHERE \"TenantId\" = @tenant ORDER BY 1",
                    connection);
                command.Parameters.AddWithValue("tenant", tenant);
                await using var reader = await command.ExecuteReaderAsync(Cancellation);
                while (await reader.ReadAsync(Cancellation)) rows.Add(table + ":" + reader.GetString(0));
            }
        return rows;
    }
}

internal static class DurableHostExtensions
{
    /// <summary>Every tenant's notes of the module, read across tenants in the host scope, as its audited cross-tenant read.</summary>
    internal static async Task<DurableNote[]> RowsAcrossAsync(this DurableHost host, PostgreSqlModule module, TenantContext scope)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, scope, UnitOfWorkMode.Suppress, cancellationToken: TestContext.Current.CancellationToken);
        return await (await work.Context.AcrossTenantsAsync<DurableNote>(TestContext.Current.CancellationToken)).Query.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }
}
