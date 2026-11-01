using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Jobs;
using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Framework.Events.Tests;

[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class DurableAuthorityRecoveryTests
{
    private static CancellationToken Token => DurableDatabase.Token;

    [Fact]
    public async Task Membership_revoked_after_interruption_closes_the_expired_attempt_without_effects()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var producer = await host.UserAsync(tenant);
        await using var admin = await host.EnterAsync(owner, tenant);
        await host.GrantAsync(admin, producer, DurableModel.Apply);
        await using var source = await host.EnterAsync(producer, tenant);
        await host.QueueAsync(source, "interrupted");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = host.Worker(async (execution, token) =>
        {
            await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
            await execution.Work.FlushAsync(token);
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }).RunDueAsync(await host.HostAsync(), host.WorkerPrincipal(), cancellationToken: shutdown.Token);
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(20), Token); }
        finally { shutdown.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(await host.Get<ITenantMemberships>().RemoveMemberAsync(
            IdentityAuthority.Host(await host.HostAsync(IdentityHostOperations.MembershipsId)), producer.Id, tenant, Token));
        clock.Advance(host.JobStore.Policy.Lease);
        await using var restarted = DurableHost.Create(database, clock);
        Assert.Equal(0, await restarted.Worker().RunDueAsync(await restarted.HostAsync(), restarted.WorkerPrincipal(), cancellationToken: Token));
        await using var read = await restarted.EnterServiceAsync(tenant);
        var job = Assert.Single(await restarted.RowsAsync<JobRecord>(database.Producer, read.Context));
        Assert.Equal(JobState.Denied, job.State);
        Assert.Null(job.Lease);
        var attempt = Assert.Single(await restarted.RowsAsync<JobAttempt>(database.Producer, read.Context));
        Assert.Equal(JobAttemptState.Denied, attempt.State);
        Assert.Equal(clock.Now, attempt.EndedAt);
        Assert.Empty(await restarted.RowsAsync<DurableEffect>(database.Producer, read.Context));
    }

    [Fact]
    public async Task A_real_authority_table_outage_has_bounded_deferrals_and_authorized_dispatch_replay()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var producer = await host.UserAsync(tenant);
        await using var admin = await host.EnterAsync(owner, tenant);
        await host.GrantAsync(admin, producer, DurableModel.Apply);
        await using var source = await host.EnterAsync(producer, tenant);
        var id = await host.ProduceAsync(source, "outage");
        await host.QueueAsync(source, "outage");
        var original = Assert.Single(await host.RowsAsync<OutboxEvent>(database.Producer, source.Context)).Propagated;
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        var table = "\"" + database.Authority.Schema + "\".";
        await database.ExecuteAsync("ALTER TABLE " + table + "\"PermissionGrants\" RENAME TO \"UnavailablePermissionGrants\"");
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Assert.Equal(0, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
                Assert.Equal(0, await host.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
                var outbox = Assert.Single(await host.RowsAsync<OutboxEvent>(database.Producer, source.Context));
                var job = Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, source.Context));
                Assert.Equal(AuthorizationCodes.Unavailable, outbox.Code);
                Assert.Equal(attempt, outbox.Attempts);
                Assert.Equal(attempt == 3 ? EventState.Poison : EventState.Pending, outbox.State);
                Assert.Equal(AuthorizationCodes.Unavailable, job.Code);
                Assert.Equal(attempt, job.Deferrals);
                Assert.Equal(0, job.Attempts);
                Assert.Equal(attempt == 3 ? JobState.Failed : JobState.Retry, job.State);
                Assert.Empty(await host.RowsAsync<JobAttempt>(database.Producer, source.Context));
                Assert.Empty(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context));
                clock.Advance(TimeSpan.FromSeconds(1 << (attempt - 1)));
            }
        }
        finally
        {
            await database.ExecuteAsync("ALTER TABLE " + table + "\"UnavailablePermissionGrants\" RENAME TO \"PermissionGrants\"");
        }
        await using var operation = await host.EnterServiceAsync(tenant);
        Assert.True(await host.Outbox.ReplayDispatchAsync(operation.Context, operation.Principal, host.Get<IPermissionEvaluator>(), id, Token));
        Assert.Equal(original, Assert.Single(await host.RowsAsync<OutboxEvent>(database.Producer, source.Context)).Propagated);
        Assert.Equal(1, await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(1, Assert.Single(await host.RowsAsync<DurableEffect>(database.Consumer, source.Context)).Count);
    }

    [Fact]
    public async Task Service_originated_work_uses_its_current_scope_after_restart()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        Guid tenant;
        await using (var host = DurableHost.Create(database, clock))
        {
            tenant = await host.TenantAsync();
            await using var source = await host.EnterServiceAsync(tenant);
            await host.ProduceAsync(source, "service");
            await host.QueueAsync(source, "service");
            await host.Pump().DispatchAsync(await host.HostAsync(), host.WorkerPrincipal(), cancellationToken: Token);
        }
        await using var restarted = DurableHost.Create(database, clock, [JobWorker.ExecutePermission]);
        var dispatch = await restarted.HostAsync();
        var principal = restarted.WorkerPrincipal();
        Assert.Equal(0, await restarted.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token));
        Assert.Equal(0, await restarted.Worker().RunDueAsync(dispatch, principal, cancellationToken: Token));
        await using var read = await restarted.EnterServiceAsync(tenant);
        Assert.Equal(EventState.Denied, Assert.Single(await restarted.RowsAsync<InboxEvent>(database.Consumer, read.Context)).State);
        Assert.Equal(JobState.Denied, Assert.Single(await restarted.RowsAsync<JobRecord>(database.Producer, read.Context)).State);
        Assert.Empty(await restarted.RowsAsync<DurableEffect>(database.Consumer, read.Context));
        Assert.Equal(1, Assert.Single(await restarted.RowsAsync<DurableEffect>(database.Producer, read.Context)).Count);
        Assert.Empty(await restarted.RowsAsync<JobAttempt>(database.Producer, read.Context));
    }
}
