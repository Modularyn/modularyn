using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-027 and TM-G02-041: events, jobs and file steps carry the validated tenant and actor as references, never
// credentials, and the receiver revalidates them, and the producer's membership or host authority, before any work.
// A refusal is terminal or transient, so a dispatcher records a denial instead of retrying it without bound.
public sealed class PropagationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private readonly MemoryDirectory directory = new();
    private readonly TestSeams seams = new();
    private readonly Guid tenantA;

    public PropagationTests()
    {
        tenantA = directory.Add("Tenant A");
        seams.Membership.Add("user-a", tenantA);
        seams.Authority.Grant("maintenance", "test.maintenance");
    }

    private async Task<PropagatedTenantContext> CaptureAsync(IServiceProvider services, bool host = false)
    {
        var (scope, context) = host
            ? await Units.EnterHostAsync(services, "maintenance", "test.maintenance", service: true)
            : await Units.EnterAsync(services, "user-a", tenantA);
        await using (scope) return PropagatedTenantContext.Capture(context);
    }

    [Fact]
    public async Task TM_G02_041_The_propagated_form_holds_only_references_and_round_trips_exactly()
    {
        await using var services = UnitServices.Build(directory, seams);
        var (scope, context) = await Units.EnterAsync(services, "user-a", tenantA);
        PropagatedTenantContext tenant;
        await using (scope) tenant = PropagatedTenantContext.Capture(context);
        Assert.Throws<InvalidOperationException>(() => PropagatedTenantContext.Capture(context));
        var json = tenant.ToJson();
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(["format", "formatVersion", "scope", "tenant", "actor", "producerUnit", "capturedAt"],
                document.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(["kind", "subject"], document.RootElement.GetProperty("actor").EnumerateObject().Select(property => property.Name));
        }
        Assert.True(PropagatedTenantContext.TryParse(json, out var parsed));
        Assert.Equal((TenantScope.Tenant, tenantA, ActorKind.User, "user-a", context.UnitId, tenant.CapturedAt),
            (parsed!.Scope, parsed.TenantId!.Value, parsed.Actor.Kind, parsed.Actor.Subject, parsed.ProducerUnitId, parsed.CapturedAt));
        Assert.Equal(json, parsed.ToJson());

        var host = await CaptureAsync(services, host: true);
        Assert.True(PropagatedTenantContext.TryParse(host.ToJson(), out var hostParsed));
        Assert.Equal((TenantScope.Host, (Guid?)null, "test.maintenance", ActorKind.Service), (hostParsed!.Scope, hostParsed.TenantId, hostParsed.OperationId, hostParsed.Actor.Kind));
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        TestContext.Current.TestOutputHelper?.WriteLine(host.ToJson());
    }

    public static TheoryData<string> Malformed()
    {
        const string tenant = "\"tenant\":\"0b9a5d4e-4e7f-4c3a-9a51-2f6c1d0e8b77\"";
        const string tail = "\"actor\":{\"kind\":\"user\",\"subject\":\"user-a\"},\"producerUnit\":\"5f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b\",\"capturedAt\":\"2026-10-26T12:00:00.000000Z\"";
        const string head = "\"format\":\"modularyn.tenant-context\",\"formatVersion\":1,\"scope\":\"tenant\",";
        return new()
        {
            "{" + head + tenant + "," + tail + ",\"token\":\"secret\"}",
            "{" + head + tenant + "," + tenant + "," + tail + "}",
            "{" + head.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal) + tenant + "," + tail + "}",
            "{" + head.Replace("\"formatVersion\":1", "\"formatVersion\":1.0", StringComparison.Ordinal) + tenant + "," + tail + "}",
            "{" + head.Replace("modularyn.tenant-context", "tenant-context", StringComparison.Ordinal) + tenant + "," + tail + "}",
            "{" + head + tenant.ToUpperInvariant().Replace("\"TENANT\"", "\"tenant\"", StringComparison.Ordinal) + "," + tail + "}",
            "{" + head + "\"tenant\":\"00000000-0000-0000-0000-000000000000\"," + tail + "}",
            "{" + head + tail + "}",
            "{" + head + tenant + "," + tail.Replace("\"subject\":\"user-a\"", "\"subject\":\" user-a\"", StringComparison.Ordinal) + "}",
            "{" + head + tenant + "," + tail.Replace("\"kind\":\"user\"", "\"kind\":\"admin\"", StringComparison.Ordinal) + "}",
            "{" + head + tenant + "," + tail.Replace("\"subject\":\"user-a\"}", "\"subject\":\"user-a\",\"session\":\"s\"}", StringComparison.Ordinal) + "}",
            "{" + head + tenant + "," + tail.Replace("12:00:00.000000Z", "12:00:00+01:00", StringComparison.Ordinal) + "}",
            "{" + head + tenant + ",\"operation\":\"test.maintenance\"," + tail + "}",
            "{" + head.Replace("\"tenant\",", "\"host\",", StringComparison.Ordinal) + tenant + "," + tail + "}",
            "[" + "{" + head + tenant + "," + tail + "}" + "]",
            "{" + head + tenant + "," + tail + "," + "\"padding\":\"" + new string('x', PropagatedTenantContext.MaxJsonLength) + "\"}",
            "not json"
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task TM_G02_027_A_malformed_or_unknown_reference_is_denied_without_running_any_work(string propagated)
    {
        Assert.False(PropagatedTenantContext.TryParse(propagated, out _));
        await using var services = UnitServices.Build(directory, seams);
        var ran = false;
        var outcome = await services.GetRequiredService<TenantWorkRunner>().RunAsync(propagated, (_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        }, Cancellation);
        Assert.Equal((TenantWorkDisposition.Denied, TenancyCodes.Malformed), (outcome.Disposition, outcome.Code));
        Assert.False(ran);
    }

    [Fact]
    public async Task TM_G02_027_The_receiver_revalidates_tenant_and_actor_before_acting()
    {
        await using var services = UnitServices.Build(directory, seams);
        var runner = services.GetRequiredService<TenantWorkRunner>();
        var reference = await CaptureAsync(services);
        var hostReference = await CaptureHostAsync(services);
        var runs = 0;
        Task Work(TenantWorkUnit unit, CancellationToken token)
        {
            runs++;
            return Task.CompletedTask;
        }

        // Accepted: a new unit, its own identity, the captured tenant and actor.
        var accepted = await runner.RunAsync(reference, Work, Cancellation);
        Assert.Equal(TenantWorkDisposition.Completed, accepted.Disposition);
        Assert.Equal((tenantA, "user-a", TenantContextSource.Propagated), (accepted.Context!.TenantId!.Value, accepted.Context.Actor.Subject, accepted.Context.Source));
        Assert.NotEqual(reference.ProducerUnitId, accepted.Context.UnitId);

        // Transient: an unavailable seam or directory defers the work.
        seams.Membership.Failing = true;
        Assert.Equal((TenantWorkDisposition.Deferred, TenancyCodes.SeamUnavailable), Disposition(await runner.RunAsync(reference, Work, Cancellation)));
        seams.Membership.Failing = false;
        directory.Unavailable = true;
        Assert.Equal((TenantWorkDisposition.Deferred, TenancyCodes.DirectoryUnavailable), Disposition(await runner.RunAsync(reference, Work, Cancellation)));
        directory.Unavailable = false;
        seams.Audit.Failing = true;
        Assert.Equal((TenantWorkDisposition.Deferred, TenancyCodes.AuditUnavailable), Disposition(await runner.RunAsync(hostReference, Work, Cancellation)));
        seams.Audit.Failing = false;

        // Terminal: the producer's authority no longer holds, so the reference alone authorizes nothing.
        seams.Authority.Revoke("maintenance", "test.maintenance");
        Assert.Equal((TenantWorkDisposition.Denied, TenancyCodes.HostAuthorityRefused), Disposition(await runner.RunAsync(hostReference, Work, Cancellation)));
        seams.Membership.Remove("user-a", tenantA);
        Assert.Equal((TenantWorkDisposition.Denied, TenancyCodes.NotMember), Disposition(await runner.RunAsync(reference, Work, Cancellation)));
        seams.Membership.Add("user-a", tenantA);
        directory.Suspend(tenantA);
        Assert.Equal((TenantWorkDisposition.Denied, TenancyCodes.UnknownTenant), Disposition(await runner.RunAsync(reference, Work, Cancellation)));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task TM_G02_027_A_revalidated_host_reference_is_audited_again_as_propagated()
    {
        await using var services = UnitServices.Build(directory, seams);
        var reference = await CaptureHostAsync(services);
        var before = seams.Audit.Records.Count;
        var outcome = await services.GetRequiredService<TenantWorkRunner>().RunAsync(reference, (unit, _) =>
        {
            Assert.Equal(TenantScope.Host, unit.Context.Scope);
            return Task.CompletedTask;
        }, Cancellation);
        Assert.Equal(TenantWorkDisposition.Completed, outcome.Disposition);
        var record = seams.Audit.Records.Last();
        Assert.Equal(before + 1, seams.Audit.Records.Count);
        Assert.Equal((outcome.Context!.UnitId, TenantContextSource.Propagated, "maintenance", "test.maintenance"),
            (record.UnitId, record.Source, record.Actor.Subject, record.OperationId));
    }

    private async Task<PropagatedTenantContext> CaptureHostAsync(IServiceProvider services) => await CaptureAsync(services, host: true);

    private static (TenantWorkDisposition, string?) Disposition(TenantWorkOutcome outcome) => (outcome.Disposition, outcome.Code);
}

[Collection(TenancyPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class PropagationDatabaseTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TM_G02_027_Work_runs_only_in_the_propagated_tenant_whatever_its_payload_names()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        database.Seams.Authority.Grant("dispatcher", WorkDispatch.Operation);
        // The payload names tenant B; the work still runs in, and writes to, the tenant whose references it carries.
        await WorkDispatch.EnqueueAsync(database, services, "user-a", database.TenantA, "job", TenantIdentifier.Format(database.TenantB));
        var run = Assert.Single(await WorkDispatch.DrainAsync(database, services, cancellationToken: Cancellation));
        Assert.Equal(TenantWorkDisposition.Completed, run.Outcome.Disposition);
        Assert.Equal(database.TenantA, run.Context!.Tenant);
        Assert.Equal([(database.TenantA, "done-" + TenantIdentifier.Format(database.TenantB))], await database.InventoryAsync("Items", "Code"));

        // Suspending the tenant in the PostgreSQL directory denies its queued work at revalidation.
        await WorkDispatch.EnqueueAsync(database, services, "user-a", database.TenantA, "job", "after-suspension");
        var (scope, host) = await Units.EnterHostAsync(services, "host-1", TenantDirectoryAdministration.OperationId);
        await using (scope)
            await services.GetRequiredService<TenantDirectoryAdministration>().SetStateAsync(host, database.TenantA, TenantState.Suspended, Cancellation);
        var denied = Assert.Single(await WorkDispatch.DrainAsync(database, services, cancellationToken: Cancellation));
        Assert.Equal((TenantWorkDisposition.Denied, TenancyCodes.UnknownTenant), (denied.Outcome.Disposition, denied.Outcome.Code));
        Assert.Contains((database.TenantA, "denied:" + TenancyCodes.UnknownTenant), await database.InventoryAsync("WorkItems", "State"));
        Assert.Empty(await WorkDispatch.DrainAsync(database, services, cancellationToken: Cancellation));
    }

    [Fact]
    public async Task TM_G02_027_A_host_scope_records_a_denial_only_on_the_named_tenant_s_row()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        database.Seams.Authority.Grant("dispatcher", WorkDispatch.Operation);
        await WorkDispatch.EnqueueAsync(database, services, "user-a", database.TenantA, "job", "a");
        var (scope, host) = await Units.EnterHostAsync(services, "dispatcher", WorkDispatch.Operation, service: true);
        await using (scope)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, host, cancellationToken: Cancellation);
            var item = await work.Context.AcrossTenants<WorkItem>().Query.SingleAsync(Cancellation);
            // The tenant column cannot be the value written, and naming another tenant matches no row.
            Assert.Equal("MPS036", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.UpdateForTenantAsync<WorkItem, Guid, Guid>(
                database.TenantA, row => row.Id, item.Id, item.Version, row => row.TenantId, database.TenantB, Cancellation))).Code);
            Assert.False((await work.Sql.UpdateForTenantAsync<WorkItem, Guid, string>(database.TenantB, row => row.Id, item.Id, item.Version,
                row => row.State, "denied:x", Cancellation)).Succeeded);
            Assert.Equal(UnitOfWorkState.Failed, (await work.CompleteAsync(Cancellation)).State);
        }
        Assert.Equal([(database.TenantA, "queued")], await database.InventoryAsync("WorkItems", "State"));
    }
}
