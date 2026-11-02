using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Events.Tests;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;
using static Modularyn.Framework.Audit.Tests.AuditFixture;

namespace Modularyn.Framework.Audit.Tests;

// TM-G02-051 and TM-G02-052 on PostgreSQL: a change and its record commit together or not at all, records are append-only
// on every write path, their times are whole microseconds from the host's clock, and none holds a secret form.
[Collection(DurablePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class AuditStoreTests
{
    [Fact]
    public async Task TM_G02_051_A_change_and_its_audit_record_commit_together_or_not_at_all()
    {
        await using var database = await DurableDatabase.CreateAsync();
        var clock = new DurableClock();
        await using var host = DurableHost.Create(database, clock);
        var trail = host.Get<IAuditTrail>();
        await using var unit = await host.EnterServiceAsync(await host.TenantAsync());
        AuditEntry Entry(string key) => AuditEntry.InContext("test.effect-changed", unit.Context) with { ResourceType = "effect", ResourceId = key };

        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            await DurableModel.EffectAsync(work, "committed", 1, Token);
            await trail.RecordAsync(work, Entry("committed"), Token);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        // A change rolled back, or abandoned, takes its record with it.
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            await DurableModel.EffectAsync(work, "rolled-back", 1, Token);
            await trail.RecordAsync(work, Entry("rolled-back"), Token);
            await work.RollbackAsync(Token);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            await DurableModel.EffectAsync(work, "abandoned", 1, Token);
            await trail.RecordAsync(work, Entry("abandoned"), Token);
        }
        // A change that fails at commit, here on its own key, commits no record either.
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            work.Context.Add(new DurableEffect { Key = "committed" });
            await trail.RecordAsync(work, Entry("duplicate"), Token);
            await Assert.ThrowsAsync<DbUpdateException>(() => work.CompleteAsync(Token));
        }
        // A record that cannot be formed rolls its change back before the refusal reaches the caller.
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            await DurableModel.EffectAsync(work, "malformed", 1, Token);
            Assert.Equal(AuditCodes.InvalidRecord, (await Assert.ThrowsAsync<AuditRecordException>(() =>
                trail.RecordAsync(work, Entry("malformed") with { ResourceType = "Not A Type" }, Token).AsTask())).Code);
            Assert.Equal(UnitOfWorkState.RolledBack, (await work.CompleteAsync(Token)).State);
        }
        // A record that cannot be written leaves its change unable to commit.
        await BreakAsync(database);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            await DurableModel.EffectAsync(work, "unrecorded", 1, Token);
            await Assert.ThrowsAnyAsync<Exception>(() => trail.RecordAsync(work, Entry("unrecorded"), Token).AsTask());
            Assert.Equal(UnitOfWorkState.Failed, (await work.CompleteAsync(Token)).State);
        }
        await RepairAsync(database);

        Assert.Equal(["committed"], (await host.RowsAsync<DurableEffect>(database.Producer, unit.Context)).Select(row => row.Key));
        var records = await database.AuditAsync("test.effect-changed");
        var record = Assert.Single(records);
        Assert.Equal("committed", record.Resource);
        Assert.Equal(unit.Context.TenantId, record.Tenant);
        Assert.Equal(unit.Context.UnitId, record.Unit);
        Assert.Equal(DurableHost.Service, record.Actor);
        Assert.Equal(clock.Now, record.At);
    }

    [Fact]
    public async Task TM_G02_052_Audit_records_are_append_only_on_every_write_path()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new DurableClock());
        var tenant = await host.TenantAsync();
        var original = Assert.Single(await database.AuditAsync(AuditActions.HostScopeGranted));

        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Audit, cancellationToken: Token))
        {
            var audit = (AuditContext)work.Context;
            var row = await audit.Records.SingleAsync(entry => entry.Id == original.Id, Token);
            row.Reason = "Rewritten";
            Assert.Equal("MPS044", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Token))).Code);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Audit, cancellationToken: Token))
        {
            var audit = (AuditContext)work.Context;
            audit.Remove(await audit.Records.SingleAsync(entry => entry.Id == original.Id, Token));
            Assert.Equal("MPS044", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Token))).Code);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Audit, cancellationToken: Token))
        {
            work.Context.Add(Forged());
            Assert.Equal("MPS044", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Token))).Code);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Audit, cancellationToken: Token))
        {
            var audit = (AuditContext)work.Context;
            Assert.Equal("MPS044", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.InsertAsync(Forged(), Token))).Code);
            Assert.Equal("MPS044", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                audit.Records.ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.Reason, "Rewritten"), Token))).Code);
            Assert.Equal("MPS044", (await Assert.ThrowsAsync<PersistenceContractException>(() => audit.Records.ExecuteDeleteAsync(Token))).Code);
        }
        // An append commits with a transaction, so a suppressed unit has none to append in.
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Audit, UnitOfWorkMode.Suppress, cancellationToken: Token))
            Assert.Equal("MPS045", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.AppendAsync(database.Audit, Forged(), Token))).Code);
        // A journal is a module of the unit's own database, mapping the record as an append-only entity.
        var elsewhere = AuditModule.Create(new PostgreSqlPersistenceOptions(database.Options.ConnectionString + ";Application Name=elsewhere"), database.AuditSchema);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, cancellationToken: Token))
        {
            Assert.Equal("MPS046", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.AppendAsync(elsewhere, Forged(), Token))).Code);
            Assert.Equal("MPS046", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.AppendAsync(database.Audit, new Unmapped(), Token))).Code);
        }

        var after = Assert.Single(await database.AuditAsync(AuditActions.HostScopeGranted), row => row.Id == original.Id);
        Assert.Equal(original, after);
        Assert.DoesNotContain(await database.AuditAsync(), row => row.Action == "test.forged");
        Assert.NotEqual(Guid.Empty, tenant);
    }

    [Fact]
    public async Task TM_G02_051_Audit_and_host_grant_times_are_whole_microseconds_from_the_host_clock()
    {
        await using var database = await DurableDatabase.CreateAsync();
        // The clock carries ticks below a microsecond, which PostgreSQL's timestamptz cannot keep and would round up.
        var clock = new DurableClock();
        clock.Advance(TimeSpan.FromTicks(7));
        Assert.NotEqual(0, clock.GetUtcNow().UtcTicks % TimeSpan.TicksPerMicrosecond);
        await using var host = DurableHost.Create(database, clock);
        var scope = await host.HostAsync();
        Assert.Equal(clock.Now, scope.HostGrant!.GrantedAt);
        Assert.Equal(0, scope.HostGrant.GrantedAt.UtcTicks % TimeSpan.TicksPerMicrosecond);
        var granted = Assert.Single(await database.AuditAsync(AuditActions.HostScopeGranted), row => row.Unit == scope.UnitId);
        Assert.Equal(scope.HostGrant.GrantedAt, granted.At);
        Assert.Equal(scope.HostGrant.OperationId, granted.Operation);
        Assert.Equal("source HostAuthority", granted.Detail);

        // A change's record and an access's record take the floored time of the host's clock too, not the database's rounding.
        var trail = host.Get<IAuditTrail>();
        await using (var unit = await host.EnterServiceAsync(await host.TenantAsync()))
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token))
        {
            await DurableModel.EffectAsync(work, "timed", 1, Token);
            await trail.RecordAsync(work, AuditEntry.InContext("test.timed-change", unit.Context), Token);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
        }
        await trail.RecordAccessAsync(new AuditEntry("test.timed-access", scope.Actor), Token);
        Assert.Equal(clock.Now, Assert.Single(await database.AuditAsync("test.timed-change")).At);
        Assert.Equal(clock.Now, Assert.Single(await database.AuditAsync("test.timed-access")).At);

        // A grant time that is not whole microseconds is refused rather than truncated by the database.
        var audit = host.Get<IHostScopeAudit>();
        await Assert.ThrowsAsync<AuditRecordException>(() => audit.RecordAsync(new(Guid.NewGuid(), scope.Actor, scope.HostGrant.OperationId, "Unfloored",
            TenantContextSource.HostAuthority, clock.GetUtcNow(), null), Token).AsTask());
        Assert.DoesNotContain(await database.AuditAsync(), row => row.Reason == "Unfloored");
    }

    [Fact]
    public async Task TM_G02_053_An_audit_record_never_holds_a_secret_form()
    {
        await using var database = await DurableDatabase.CreateAsync();
        await using var host = DurableHost.Create(database, new DurableClock());
        var trail = host.Get<IAuditTrail>();
        await using var unit = await host.EnterServiceAsync(await host.TenantAsync());
        var canaries = Canaries();
        foreach (var canary in canaries)
            foreach (var entry in new[]
            {
                AuditEntry.InContext("test.canary", unit.Context) with { Detail = "detail " + canary },
                AuditEntry.InContext("test.canary", unit.Context) with { Reason = "Reason " + canary }
            }.Concat(canary.Contains(' ', StringComparison.Ordinal) ? []
                : [AuditEntry.InContext("test.canary", unit.Context) with { ResourceType = "effect", ResourceId = canary }]))
            {
                await using var work = await EfCoreUnitOfWork.BeginAsync(database.Producer, unit.Context, cancellationToken: Token);
                await DurableModel.EffectAsync(work, "canary", 1, Token);
                await Assert.ThrowsAsync<AuditRecordException>(() => trail.RecordAsync(work, entry, Token).AsTask());
                Assert.NotEqual(UnitOfWorkState.Committed, (await work.CompleteAsync(Token)).State);
            }
        Assert.Empty(await host.RowsAsync<DurableEffect>(database.Producer, unit.Context));
        var everything = await EverythingAsync(database);
        foreach (var canary in canaries) Assert.DoesNotContain(canary, everything, StringComparison.Ordinal);
    }

    private static AuditRecordRow Forged() => new()
    {
        Id = Guid.NewGuid(), At = UtcMicroseconds.Normalize(DateTimeOffset.UtcNow), Action = "test.forged", Outcome = 1, ActorKind = 0, Actor = "forger"
    };

    private sealed class Unmapped : IAppendOnly;
}
