using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;
using static Modularyn.Framework.Audit.Tests.AuditFixture;

namespace Modularyn.Framework.Audit.Tests;

// TM-G02-039: durable payloads carry references. The rule refuses a payload that is empty or unbounded, holds a control
// character, a recognized secret form or a propagated context, and an ordering key that holds a secret form.
public sealed class PayloadRuleTests
{
    [Fact]
    public void TM_G02_039_The_payload_rule_refuses_secret_forms_propagated_context_and_unbounded_payloads()
    {
        foreach (var canary in Canaries())
        {
            Assert.Throws<ArgumentException>(() => DurablePayloads.Require("order 42 " + canary, "payload"));
            Assert.Throws<ArgumentException>(() => DurablePayloads.RequireReference("order " + canary, "key"));
        }
        Assert.Throws<ArgumentException>(() => DurablePayloads.Require(DurablePayloads.Redacted, "payload"));
        Assert.Throws<ArgumentException>(() => DurablePayloads.Require(new string('a', DurablePayloads.MaxLength + 1), "payload"));
        Assert.Throws<ArgumentException>(() => DurablePayloads.Require("order\u000042", "payload"));
        Assert.Throws<ArgumentException>(() => DurablePayloads.Require("{\"format\":\"" + PropagatedTenantContext.FormatName + "\"}", "payload"));
        // References, bounded text with tabs and line breaks, and the longest payload are kept.
        DurablePayloads.Require("{\"order\":\"" + Guid.NewGuid().ToString("D") + "\",\"lines\":3}", "payload");
        DurablePayloads.Require("order 42\n\tline 1\r\n", "payload");
        DurablePayloads.Require(new string('a', DurablePayloads.MaxLength), "payload");
        DurablePayloads.RequireReference("order-42", "key");
        Assert.Equal(new PayloadSummary(true, 0), PayloadSummary.Of(DurablePayloads.Redacted));
        Assert.Equal("12 characters", PayloadSummary.Of("order 42 a b").ToString());
    }
}

// TM-G02-039 and TM-G02-054 on PostgreSQL: the stores refuse payloads outside the rule; a payload is redacted in the
// transaction that ends its row, and a quarantined one after its retention, after which it cannot be replayed; and the
// quarantine and job history views show only payload summaries, under the diagnostics permission alone.
[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class DurablePayloadTests
{
    [Fact]
    public async Task TM_G02_039_The_stores_refuse_payloads_outside_the_rule()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var source = await host.EnterServiceAsync(tenant);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, cancellationToken: Token))
        {
            var propagated = PropagatedTenantContext.Capture(work.Context.Tenant!, clock).ToJson(host.JobStore.JobBinding(Guid.NewGuid()));
            foreach (var payload in Canaries().Select(canary => "order 42 " + canary).Append(propagated).Append(""))
            {
                Assert.Throws<ArgumentException>(() => host.Outbox.Append(work, new(DurableModel.Consumer, "item.changed", 1, "order-42", 1, payload)));
                Assert.Throws<ArgumentException>(() => host.JobStore.Enqueue(work, new("durable.counter", payload, clock.GetUtcNow())));
                Assert.Throws<ArgumentException>(() => host.JobStore.Schedule(work, "durable.counter", payload, new("Etc/UTC", new(9, 0)), new(2026, 1, 2)));
            }
            foreach (var canary in Canaries())
                Assert.Throws<ArgumentException>(() => host.Outbox.Append(work, new(DurableModel.Consumer, "item.changed", 1, "order " + canary, 1, "order 42")));
            await work.CompleteAsync(Token);
        }
        Assert.Empty(await host.RowsAsync<OutboxEvent>(database.Producer, source.Context));
        Assert.Empty(await host.RowsAsync<JobRecord>(database.Producer, source.Context));
        Assert.Empty(await host.RowsAsync<JobSchedule>(database.Producer, source.Context));
    }

    [Fact]
    public async Task TM_G02_039_A_payload_is_redacted_once_its_row_can_no_longer_run()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var tenant = await host.TenantAsync();
        await using var source = await host.EnterServiceAsync(tenant);
        var evaluator = host.Get<IPermissionEvaluator>();
        var delivered = await host.ProduceAsync(source, "delivered");
        var quarantined = await host.ProduceAsync(source, "quarantined", schema: 2);
        var dispatch = await host.HostAsync();
        var principal = host.WorkerPrincipal();
        await host.Pump().DispatchAsync(dispatch, principal, cancellationToken: Token);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await host.Pump().ConsumeAsync(dispatch, principal, cancellationToken: Token);
            clock.Advance(TimeSpan.FromSeconds(1 << attempt));
        }

        // Delivered outbox rows and a consumed receipt keep no payload; the quarantined receipt keeps its own for replay.
        Assert.All(await host.RowsAsync<OutboxEvent>(database.Producer, source.Context), row =>
        {
            Assert.Equal(EventState.Completed, row.State);
            Assert.Equal(DurablePayloads.Redacted, row.Payload);
        });
        var receipts = await host.RowsAsync<InboxEvent>(database.Consumer, source.Context);
        Assert.Equal(DurablePayloads.Redacted, Assert.Single(receipts, row => row.Id == delivered && row.State == EventState.Completed).Payload);
        Assert.Equal("value-1", Assert.Single(receipts, row => row.Id == quarantined && row.State == EventState.Poison).Payload);

        // A completed job, a cancelled one and a stopped schedule keep no payload; a queued job keeps its own.
        var completed = await host.QueueAsync(source, "completed");
        var cancelled = await host.QueueAsync(source, "cancelled", clock.Now.AddHours(1));
        var queued = await host.QueueAsync(source, "queued", clock.Now.AddHours(1));
        Assert.Equal(1, await host.Worker().RunDueAsync(await host.HostAsync(), principal, cancellationToken: Token));
        Assert.True(await host.JobStore.CancelAsync(source.Context, source.Principal, evaluator, cancelled, Token));
        Guid schedule;
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, source.Context, cancellationToken: Token))
        {
            schedule = host.JobStore.Schedule(work, "durable.counter", "scheduled", new("Etc/UTC", new(9, 0)), new(2026, 1, 2));
            await work.CompleteAsync(Token);
        }
        Assert.True(await host.JobStore.StopScheduleAsync(source.Context, source.Principal, evaluator, schedule, Token));
        var jobs = await host.RowsAsync<JobRecord>(database.Producer, source.Context);
        Assert.Equal(DurablePayloads.Redacted, Assert.Single(jobs, row => row.Id == completed && row.State == JobState.Completed).Payload);
        Assert.Equal(DurablePayloads.Redacted, Assert.Single(jobs, row => row.Id == cancelled && row.State == JobState.CancelledBeforeRun).Payload);
        Assert.Equal("queued", Assert.Single(jobs, row => row.Id == queued).Payload);
        Assert.Equal(DurablePayloads.Redacted, Assert.Single(await host.RowsAsync<JobSchedule>(database.Producer, source.Context)).Payload);

        // The views show what each row is and only a summary of its payload.
        var quarantine = Assert.Single(await host.Inbox.QuarantineAsync(source.Context, source.Principal, evaluator, cancellationToken: Token));
        Assert.Equal((EventSide.Inbox, quarantined, EventState.Poison, "UnsupportedVersion", new PayloadSummary(false, "value-1".Length)),
            (quarantine.Side, quarantine.Id, quarantine.State, quarantine.Code, quarantine.Payload));
        var history = await host.JobStore.HistoryAsync(source.Context, source.Principal, evaluator, cancellationToken: Token);
        Assert.Equal(3, history.Count);
        Assert.Equal(new PayloadSummary(false, "queued".Length), Assert.Single(history, row => row.Id == queued).Payload);
        Assert.All(history.Where(row => row.Id != queued), row => Assert.True(row.Payload.Redacted));
        var shown = string.Join("\n", history.Select(row => row.ToString()).Append(quarantine.ToString()));
        foreach (var payload in new[] { "value-1", "queued", "completed", "cancelled" }) Assert.DoesNotContain(payload, shown, StringComparison.Ordinal);

        // A quarantined payload is kept for its retention, then redacted, and a redacted row can no longer be replayed.
        Assert.Equal(0, await RedactAsync(host, source));
        clock.Advance(EventStore.QuarantineRetention + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await RedactAsync(host, source));
        Assert.Equal(0, await RedactAsync(host, source));
        Assert.True(Assert.Single(await host.Inbox.QuarantineAsync(source.Context, source.Principal, evaluator, cancellationToken: Token)).Payload.Redacted);
        Assert.False(await host.Inbox.ReplayAsync(source.Context, source.Principal, evaluator, quarantined, Token));
        Assert.Empty(await database.AuditAsync(EventStore.ReplayedAction));
        Assert.Equal(EventState.Poison, Assert.Single(await host.RowsAsync<InboxEvent>(database.Consumer, source.Context), row => row.Id == quarantined).State);
    }

    [Fact]
    public async Task TM_G02_054_Durable_diagnostics_need_the_diagnostics_permission_alone()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new DurableClock());
        var tenant = await host.TenantAsync();
        var owner = await host.UserAsync(tenant, owner: true);
        var auditor = await host.UserAsync(tenant);
        var operatorUser = await host.UserAsync(tenant);
        await using (var admin = await host.EnterAsync(owner, tenant))
        {
            await host.GrantAsync(admin, auditor, AuditModule.ReadPermission, EventStore.ReplayPermission, JobStore.CancelPermission);
            await host.GrantAsync(admin, operatorUser, AuditModule.DiagnosticsPermission);
        }
        var evaluator = host.Get<IPermissionEvaluator>();

        // Reading audit, replaying and cancelling are other authorities: none of them reads diagnostics.
        await using (var unit = await host.EnterAsync(auditor, tenant))
        {
            Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<DiagnosticsAccessException>(() =>
                host.Inbox.QuarantineAsync(unit.Context, unit.Principal, evaluator, cancellationToken: Token))).Code);
            Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<DiagnosticsAccessException>(() =>
                host.JobStore.HistoryAsync(unit.Context, unit.Principal, evaluator, cancellationToken: Token))).Code);
            Assert.Equal(AuthorizationCodes.Unavailable, (await Assert.ThrowsAsync<DiagnosticsAccessException>(() =>
                host.JobStore.HistoryAsync(unit.Context, unit.Principal, null, cancellationToken: Token))).Code);
        }
        // The diagnostics permission reads the views, and reads no audit.
        await using (var unit = await host.EnterAsync(operatorUser, tenant))
        {
            Assert.Empty(await host.Inbox.QuarantineAsync(unit.Context, unit.Principal, evaluator, cancellationToken: Token));
            Assert.Empty(await host.JobStore.HistoryAsync(unit.Context, unit.Principal, evaluator, cancellationToken: Token));
            Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<AuditAccessException>(() =>
                host.Get<AuditReader>().ReadAsync(unit.Context, unit.Principal, new(), Token))).Code);
        }
        // A host scope reads no tenant's durable diagnostics.
        var diagnosticsHost = await host.HostAsync(AuditModule.DiagnosticsHostReadOperation.Id);
        Assert.Equal(AuthorizationCodes.NoTenantContext, (await Assert.ThrowsAsync<DiagnosticsAccessException>(() =>
            host.JobStore.HistoryAsync(diagnosticsHost, null, evaluator, cancellationToken: Token))).Code);
    }

    private static async Task<int> RedactAsync(DurableHost host, DurableUnit source)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(host.Database.Consumer, source.Context, cancellationToken: Token);
        var redacted = await host.Inbox.RedactQuarantinedAsync(work, cancellationToken: Token);
        await work.CompleteAsync(Token);
        return redacted;
    }
}
