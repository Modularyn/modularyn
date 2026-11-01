using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Modules.BackgroundJobs.Domain;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Domain;
using Xunit;

namespace Modularyn.Modules.BackgroundJobs.Application.Tests;

[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class ProviderLifecycleTests
{
    private static CancellationToken Token => DurableDatabase.Token;

    [Fact]
    public async Task Lease_renewal_extends_the_same_attempt_without_invalidating_its_commit()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var unit = await host.EnterServiceAsync(tenant);
        await host.QueueAsync(unit, "renewed");
        var worker = host.Worker(async (execution, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.True(await execution.RenewLeaseAsync(token));
            clock.Advance(TimeSpan.FromSeconds(31));
            await execution.CheckpointAsync(token);
            await DurableModel.EffectAsync(execution.Work, execution.Payload, 1, token);
        });
        Assert.Equal(1, await worker.RunDueAsync(await host.HostAsync(), host.WorkerPrincipal(), cancellationToken: Token));
        Assert.Equal(1, Assert.Single(await host.RowsAsync<JobRecord>(database.Producer, unit.Context)).Attempts);
        Assert.Equal(JobAttemptState.Completed, Assert.Single(await host.RowsAsync<JobAttempt>(database.Producer, unit.Context)).State);
        Assert.Equal(1, Assert.Single(await host.RowsAsync<DurableEffect>(database.Producer, unit.Context)).Count);
    }

    [Fact]
    public async Task Background_jobs_module_migrates_and_commits_a_followup_intent_with_completion()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var unit = await host.EnterServiceAsync(tenant);
        var store = new JobStore(database.Jobs, clock, new JobPolicy());
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Jobs, unit.Context, cancellationToken: Token))
        {
            store.Enqueue(work, new("durable.parent", "intent", clock.Now));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        var worker = new JobWorker(store, host.Get<AuthorizedWorkRunner>(), host.Get<IPermissionEvaluator>(),
            [new("durable.parent", JobWorker.ExecutePermission, (execution, _) =>
            {
                store.Enqueue(execution.Work, new("durable.followup", "intent", clock.Now.AddDays(1)));
                return Task.CompletedTask;
            })]);
        Assert.Equal(1, await worker.RunDueAsync(await host.HostAsync(BackgroundJobsDefinition.Dispatch.Id), host.WorkerPrincipal(), cancellationToken: Token));
        Assert.Equal(0, await worker.RunDueAsync(await host.HostAsync(BackgroundJobsDefinition.Dispatch.Id), host.WorkerPrincipal(), cancellationToken: Token));
        var rows = await host.RowsAsync<JobRecord>(database.Jobs, unit.Context);
        Assert.Equal(JobState.Completed, Assert.Single(rows, row => row.Kind == "durable.parent").State);
        Assert.Equal(JobState.Queued, Assert.Single(rows, row => row.Kind == "durable.followup").State);
    }

    [Fact]
    public async Task Identity_retention_job_removes_expired_counters_and_sessions_and_keeps_live_state()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        var user = await host.UserAsync(tenant);
        await using var unit = await host.EnterServiceAsync(tenant);
        var old = clock.Now - TimeSpan.FromDays(61);
        var source = ThrottleKey.ForSource("203.0.113.7");
        var account = ThrottleKey.ForUserName(ThrottleKind.Password, user.Name);
        var pair = ThrottleKey.ForSourceOnSubject(source, account);
        var live = ThrottleKey.ForSource("203.0.113.8");
        var ledger = host.Get<IVerificationLedger>();
        await using (var entry = await ledger.AcquireAsync([source, account, pair], old, Token))
        {
            foreach (var key in new[] { source, account, pair }) entry.Set(key, new(3, old, old, old.AddHours(1)));
            await entry.CommitAsync(Token);
        }
        await using (var entry = await ledger.AcquireAsync([live], clock.Now, Token))
        {
            entry.Set(live, new(3, clock.Now, clock.Now, clock.Now.AddHours(1)));
            await entry.CommitAsync(Token);
        }
        var sessions = host.Get<ISessionStore>();
        var expired = Guid.NewGuid();
        var active = Guid.NewGuid();
        var recent = Guid.NewGuid();
        await sessions.CreateAsync(Session(expired, user.Id, old), Token);
        await sessions.EndAsync(expired, SessionEndReason.SignedOut, old.AddMinutes(1), null, Token);
        await sessions.CreateAsync(Session(active, user.Id, clock.Now), Token);
        await sessions.CreateAsync(Session(recent, user.Id, clock.Now.AddHours(-1)), Token);
        await sessions.EndAsync(recent, SessionEndReason.SignedOut, clock.Now, null, Token);
        var retention = host.Get<IIdentityRetentionStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => retention.PruneAsync(unit.Context, 100, Token));
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            host.JobStore.Enqueue(work, new(IdentityRetentionJob.Kind, "", clock.Now));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        var worker = new JobWorker(host.JobStore, host.Get<AuthorizedWorkRunner>(), host.Get<IPermissionEvaluator>(),
            [IdentityRetentionJob.Create(retention, host.Get<IServiceScopeFactory>())]);
        Assert.Equal(1, await worker.RunDueAsync(await host.HostAsync(), host.WorkerPrincipal(), cancellationToken: Token));
        Assert.Null(await sessions.FindAsync(expired, Token));
        Assert.NotNull(await sessions.FindAsync(active, Token));
        Assert.NotNull(await sessions.FindAsync(recent, Token));
        await using (var entry = await ledger.AcquireAsync([source, account, pair, live], clock.Now, Token))
        {
            foreach (var key in new[] { source, account, pair }) Assert.Equal(0, entry.Get(key).Failures);
            Assert.Equal(3, entry.Get(live).Failures);
            await entry.CommitAsync(Token);
        }
        var again = await retention.PruneAsync(await host.HostAsync(IdentityRetentionPolicy.OperationId), 100, Token);
        Assert.True(again.Committed);
        Assert.Equal(0, again.ThrottleRows);
        Assert.Equal(0, again.Sessions);
    }

    private static SessionRecord Session(Guid id, Guid account, DateTimeOffset at) => new(id, SHA256.HashData(id.ToByteArray()), account,
        at, at, at.AddHours(12), "session-stamp", null, null);
}
