using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Framework.Events.Tests;

[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class DurableAuthorizationTests
{
    private static CancellationToken Token => DurableDatabase.Token;

    [Fact]
    public async Task Revoked_producer_permission_denies_events_and_jobs_after_restart()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        Guid tenant;
        await using (var host = DurableHost.Create(database, clock))
        {
            tenant = await host.TenantAsync();
            var owner = await host.UserAsync(tenant, owner: true);
            var producer = await host.UserAsync(tenant);
            await using var admin = await host.EnterAsync(owner, tenant);
            await host.GrantAsync(admin, producer, DurableModel.Apply);
            await using var unit = await host.EnterAsync(producer, tenant);
            await host.ProduceAsync(unit, "denied");
            await host.QueueAsync(unit, "denied");
            Assert.True(await host.Get<AuthorityAdministration>().RevokePermissionAsync(admin.Context, admin.Principal, producer.Actor, DurableModel.Apply, Token));
        }
        await using var recovered = DurableHost.Create(database, clock);
        var dispatch = await recovered.HostAsync();
        var principal = recovered.WorkerPrincipal();
        Assert.Equal(0, await recovered.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await recovered.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
        await using var read = await recovered.EnterServiceAsync(tenant);
        Assert.Equal(EventState.Denied, Assert.Single(await recovered.RowsAsync<OutboxEvent>(database.Producer, read.Context)).State);
        Assert.Equal(JobState.Denied, Assert.Single(await recovered.RowsAsync<JobRecord>(database.Producer, read.Context)).State);
        Assert.Empty(await recovered.RowsAsync<InboxEvent>(database.Consumer, read.Context));
        Assert.Empty(await recovered.RowsAsync<JobAttempt>(database.Producer, read.Context));
        Assert.Empty(await recovered.RowsAsync<DurableEffect>(database.Consumer, read.Context));
    }

    [Fact]
    public async Task Revocation_after_receipt_is_rechecked_before_the_event_effect_commits()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var producer = await host.UserAsync(tenant);
        await using var admin = await host.EnterAsync(owner, tenant);
        await using var unit = await host.EnterAsync(producer, tenant);
        await host.GrantAsync(admin, producer, DurableModel.Apply);
        await host.ProduceAsync(unit, "revoked-during");
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
        var observer = new BeforeCommit(async () => Assert.True(await host.Get<AuthorityAdministration>().RevokePermissionAsync(
            admin.Context, admin.Principal, producer.Actor, DurableModel.Apply, Token)));
        Assert.Equal(0, await host.Pump(observer).ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Consumer, unit.Context));
        var receipt = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, unit.Context));
        Assert.Equal(EventState.Denied, receipt.State);
        Assert.Equal(AuthorizationCodes.NotHeld, receipt.Code);
    }

    [Fact]
    public async Task Job_steps_pass_the_running_principal_and_revoke_before_commit()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var producer = await host.UserAsync(tenant);
        await using var admin = await host.EnterAsync(owner, tenant);
        await using var unit = await host.EnterAsync(producer, tenant);
        await host.GrantAsync(admin, producer, DurableModel.Apply);
        await host.QueueAsync(unit, "revoked-during");
        var worker = host.Worker(async (execution, token) =>
        {
            Assert.NotNull(execution.Principal.ServiceScope);
            Assert.Equal(producer.Actor, execution.Work.Context.Tenant!.Actor);
            var permitted = await host.Get<IPermissionEvaluator>().AuthorizeAsync(new(execution.Work.Context.Tenant!, execution.Principal, DurableModel.Apply)
                { RequireCurrent = true }, token);
            Assert.True(permitted.Permitted, permitted.Code);
            await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
            Assert.True(await host.Get<AuthorityAdministration>().RevokePermissionAsync(admin.Context, admin.Principal, producer.Actor, DurableModel.Apply, token));
        });
        Assert.Equal(0, await worker.RunDueAsync(await host.HostAsync(), host.WorkerPrincipal(), cancellationToken: Token));
        var job = Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, unit.Context));
        Assert.Equal(JobState.Denied, job.State);
        Assert.Equal(AuthorizationCodes.NotHeld, job.Code);
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Producer, unit.Context));
    }

    [Fact]
    public async Task Operator_replay_requires_current_permission_and_preserves_the_original_producer()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var source = await host.EnterServiceAsync(tenant);
        var id = await host.ProduceAsync(source, "unsupported", schema: 2);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token);
            clock.Advance(TimeSpan.FromSeconds(1 << attempt));
        }
        var before = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context));
        var owner = await host.UserAsync(tenant, owner: true);
        var member = await host.UserAsync(tenant);
        await using var admin = await host.EnterAsync(owner, tenant);
        await using var requester = await host.EnterAsync(member, tenant);
        Assert.False(await host.Inbox.ReplayAsync(requester.Context, requester.Principal, host.Get<IPermissionEvaluator>(), id, Token));
        await host.GrantAsync(admin, member, EventStore.ReplayPermission);
        Assert.True(await host.Inbox.ReplayAsync(requester.Context, requester.Principal, host.Get<IPermissionEvaluator>(), id, Token));
        var after = Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context));
        Assert.Equal(before.Propagated, after.Propagated);
        Assert.Equal(EventState.Pending, after.State);
        var repaired = new EventPump(host.Outbox, host.Inbox, new(DurableModel.Consumer, DurableModel.Apply,
            [new("item.changed", 2, (execution, token) => DurableModel.EffectAsync(execution.Work, execution.Event.OrderingKey, execution.Event.Position, token))]),
            host.Get<AuthorizedWorkRunner>(), clock, new EventRetryPolicy());
        Assert.Equal(1, await repaired.ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.False(await host.Inbox.ReplayAsync(admin.Context, admin.Principal, host.Get<IPermissionEvaluator>(), id, Token));
        Assert.Equal(1, Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, source.Context)).Count);
    }

    [Fact]
    public async Task Service_route_needs_a_current_server_identity_and_store_context_cannot_come_from_payload()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var forged = ExecutionPrincipal.ForService(new(DurableHost.Service, IdentityKind.Service), ["not.declared"]);
        var refusal = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishServiceAsync(forged, tenant, Token);
        Assert.False(refusal.Succeeded);
        await using var valid = await host.EnterServiceAsync(tenant);
        Assert.False(await host.Get<ITenantMembership>().IsMemberAsync(valid.Context.Actor, tenant, Token));
        await using var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, valid.Context, cancellationToken: Token);
        var id = host.Outbox.Append(work, new(DurableModel.Consumer, "item.changed", 1, "opaque", 1,
            "{\"tenant\":\"untrusted\",\"actor\":\"untrusted\",\"propagated\":\"untrusted\"}"));
        await work.CompleteAsync(Token);
        var row = Assert.Single(await host.RowsAsync<OutboxEvent>(database.Producer, valid.Context));
        Assert.Equal(id, row.Id);
        Assert.True(PropagatedTenantContext.TryParse(row.Propagated, out var captured));
        Assert.Equal(valid.Context.Actor, captured!.Actor);
        Assert.Equal(tenant, captured.TenantId);
    }

    private sealed class BeforeCommit(Func<Task> action) : IEventObserver
    {
        public Task ObserveAsync(EventCheckpoint checkpoint, Guid eventId, CancellationToken cancellationToken) =>
            checkpoint == EventCheckpoint.EffectBeforeCommit ? action() : Task.CompletedTask;
    }
}
