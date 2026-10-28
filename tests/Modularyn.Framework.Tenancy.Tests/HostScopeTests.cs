using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-028: host scope needs separately granted host authority for the operation, is recorded in audit before it
// exists, is never derived from or combined with a tenant the request names, and reaches tenant data only through the
// explicit cross-tenant read. Tenant membership or administration never implies it.
public sealed class HostScopeTests
{
    private const string Operation = "test.host.report";
    private readonly MemoryDirectory directory = new();
    private readonly TestSeams seams = new();
    private readonly Guid tenantA;

    public HostScopeTests()
    {
        tenantA = directory.Add("Tenant A", domain: "tenant-a.example.test");
        seams.Membership.Add("ta-admin", tenantA);
        seams.Authority.Grant("host-1", Operation);
    }

    private async Task<TenantDecision> HostAsync(ClaimsPrincipal principal, string operation = Operation, params TenantCandidate[] candidates)
    {
        await using var services = UnitServices.Build(directory, seams);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
            TenantResolutionRequest.ForHostOperation(principal, operation, "Report across tenants", candidates), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TM_G02_028_Host_scope_needs_host_authority_and_an_audit_record_written_first()
    {
        var granted = await HostAsync(TestIdentity.User("host-1"));
        Assert.True(granted.Succeeded, granted.Code);
        var context = granted.Context!;
        Assert.Equal((TenantScope.Host, (Guid?)null, TenantContextSource.HostAuthority), (context.Scope, context.TenantId, context.Source));
        Assert.Equal((Operation, "Report across tenants"), (context.HostGrant!.OperationId, context.HostGrant.Reason));
        var record = Assert.Single(seams.Audit.Records);
        Assert.Equal((context.UnitId, "host-1", Operation, "Report across tenants", TenantContextSource.HostAuthority, context.HostGrant.GrantedAt),
            (record.UnitId, record.Actor.Subject, record.OperationId, record.Reason, record.Source, record.At));

        // A tenant administrator, an authority granted for another operation or a principal claiming a tenant gains nothing.
        Assert.Equal(TenancyCodes.HostAuthorityRefused, (await HostAsync(TestIdentity.User("ta-admin"))).Code);
        Assert.Equal(TenancyCodes.HostAuthorityRefused, (await HostAsync(TestIdentity.User("host-1"), "test.other")).Code);
        Assert.Equal(TenancyCodes.HostAuthorityRefused, (await HostAsync(TestIdentity.User("ta-admin", TenantIdentifier.Format(tenantA)))).Code);
        Assert.Equal(TenancyCodes.Unauthenticated, (await HostAsync(TestIdentity.Anonymous())).Code);
        Assert.Single(seams.Audit.Records);

        // Without an audit record there is no scope.
        seams.Audit.Failing = true;
        var unaudited = await HostAsync(TestIdentity.User("host-1"));
        Assert.Equal(TenancyCodes.AuditUnavailable, unaudited.Code);
        Assert.Null(unaudited.Context);
        seams.Audit.Failing = false;
        var noSeams = new ServiceCollection();
        noSeams.AddSingleton<ITenantDirectory>(directory);
        noSeams.AddSingleton(TimeProvider.System);
        noSeams.AddScoped<TenantExecution>();
        seams.Register(noSeams, audit: false);
        await using (var provider = noSeams.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
            Assert.Equal(TenancyCodes.AuditUnavailable, (await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
                TenantResolutionRequest.ForHostOperation(TestIdentity.User("host-1"), Operation, "Report across tenants"), TestContext.Current.CancellationToken)).Code);
        var noAuthority = new ServiceCollection();
        noAuthority.AddSingleton<ITenantDirectory>(directory);
        noAuthority.AddSingleton(TimeProvider.System);
        noAuthority.AddScoped<TenantExecution>();
        seams.Register(noAuthority, authority: false);
        await using (var provider = noAuthority.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
            Assert.Equal(TenancyCodes.SeamUnavailable, (await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
                TenantResolutionRequest.ForHostOperation(TestIdentity.User("host-1"), Operation, "Report across tenants"), TestContext.Current.CancellationToken)).Code);
    }

    [Fact]
    public async Task TM_G02_028_A_request_that_names_a_tenant_never_gets_host_scope()
    {
        var a = TenantIdentifier.Format(tenantA);
        Assert.Equal(TenancyCodes.HostCandidates, (await HostAsync(TestIdentity.User("host-1"), Operation, Candidates.Header(a))).Code);
        Assert.Equal(TenancyCodes.HostCandidates, (await HostAsync(TestIdentity.User("host-1"), Operation, Candidates.Route(a))).Code);
        Assert.Equal(TenancyCodes.HostCandidates, (await HostAsync(TestIdentity.User("host-1"), Operation, Candidates.Host("tenant-a.example.test"))).Code);
        Assert.Equal(TenancyCodes.HostCandidates, (await HostAsync(TestIdentity.User("ta-admin"), Operation, Candidates.Header(a))).Code);
        // The shared host origin, which names no tenant, is where host operations are served.
        Assert.True((await HostAsync(TestIdentity.User("host-1"), Operation, Candidates.Host("app.example.test"))).Succeeded);
        Assert.DoesNotContain(seams.Audit.Records, record => record.Actor.Subject == "ta-admin");
    }

    [Fact]
    public async Task TM_G02_028_A_host_grant_carries_only_the_reach_of_the_operation_asked_for()
    {
        var reach = new HostOperation(Operation, [new HostReach("Items", "Items")]);
        seams.Authority.Grant("host-1", reach);
        var granted = await HostAsync(TestIdentity.User("host-1"));
        Assert.Equal(reach.Reach, granted.Context!.HostGrant!.Reach);
        Assert.True(granted.Context.HostGrant.Reaches("Items", "Items"));
        Assert.False(granted.Context.HostGrant.Reaches("Items", "WorkItems"));
        Assert.False(granted.Context.HostGrant.Reaches("Other", "Items"));
        // A seam that answers with another operation, or its reach, grants nothing.
        await using var services = UnitServices.Build(directory, seams, collection =>
            collection.AddSingleton<IHostAuthority>(new MismatchedAuthority(new HostOperation("test.other", [new HostReach("Items", "Items")]))));
        await using var scope = services.CreateAsyncScope();
        var refused = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
            TenantResolutionRequest.ForHostOperation(TestIdentity.User("host-1"), Operation, "Report across tenants"), TestContext.Current.CancellationToken);
        Assert.Equal(TenancyCodes.HostAuthorityRefused, refused.Code);
    }

    private sealed class MismatchedAuthority(HostOperation answer) : IHostAuthority
    {
        public ValueTask<HostOperation?> AuthorizeAsync(ActorReference actor, string operationId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<HostOperation?>(answer);
    }
}

[Collection(TenancyPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class HostScopeDatabaseTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TM_G02_028_Host_scope_reads_tenant_data_only_through_the_explicit_bypass_and_cannot_change_it()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        var aItem = await AddAsync(database, services, "user-a", database.TenantA, "a-1");
        var bItem = await AddAsync(database, services, "user-b", database.TenantB, "b-1");
        var deleted = await AddAsync(database, services, "user-b", database.TenantB, "b-deleted");
        var (deleting, tenantB) = await Units.EnterAsync(services, "user-b", database.TenantB);
        await using (deleting)
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, tenantB, cancellationToken: Cancellation))
        {
            work.Context.Remove(await ((ItemsContext)work.Context).Items.SingleAsync(row => row.Id == deleted, Cancellation));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        database.Seams.Authority.Grant("host-1", TenancyServer.HostReport);

        TenantContext ended;
        var (scope, host) = await Units.EnterHostAsync(services, "host-1", TenancyServer.HostReport);
        await using (scope)
        {
            ended = host;
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, host, cancellationToken: Cancellation);
            var items = (ItemsContext)work.Context;
            Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() => items.Items.CountAsync(Cancellation))).Code);
            Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.Query<Item>().CountAsync(Cancellation))).Code);
            var across = work.Context.AcrossTenants<Item>();
            Assert.Same(host.HostGrant, across.Grant);
            // Only the tenant predicate is lifted: soft delete still applies, on both paths.
            Assert.Equal(["a-1", "b-1"], await across.Query.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(Cancellation));
            Assert.Equal(["a-1", "b-1"], await work.Sql.AcrossTenants<Item>().Query.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(Cancellation));
            // The bypass cannot carry a bulk write, and tracked cross-tenant rows cannot be saved.
            Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() => across.Query.ExecuteDeleteAsync(Cancellation))).Code);
            Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() => across.Query
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "host"), Cancellation))).Code);
            var tracked = await across.Query.AsTracking().SingleAsync(row => row.Id == bItem, Cancellation);
            tracked.Name = "changed by host";
            Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.CompleteAsync(Cancellation))).Code);
        }
        // Once the host scope has ended, a cross-tenant query is refused at execution, not just at construction.
        await using (var leaked = await EfCoreUnitOfWork.BeginAsync(database.Items, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
            Assert.Equal("MPS038", Assert.Throws<PersistenceContractException>(() => leaked.Context.AcrossTenants<Item>()).Code);
        Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
            EfCoreUnitOfWork.BeginAsync(database.Items, ended, cancellationToken: Cancellation))).Code);
        Assert.Equal(["a-1", "b-1", "b-deleted"], (await database.InventoryAsync("Items", "Code")).Select(row => row.Value).Order());
        Assert.DoesNotContain(await database.InventoryAsync("Items", "Name"), row => row.Value == "changed by host");

        // A tenant unit cannot use the bypass.
        var (tenantScope, tenantA) = await Units.EnterAsync(services, "ta-admin", database.TenantA);
        await using (tenantScope)
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, tenantA, cancellationToken: Cancellation))
        {
            Assert.Equal("MPS038", Assert.Throws<PersistenceContractException>(() => work.Context.AcrossTenants<Item>()).Code);
            Assert.Equal("MPS038", Assert.Throws<PersistenceContractException>(() => work.Sql.AcrossTenants<Item>()).Code);
            Assert.Equal("MPS038", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.UpdateForTenantAsync<Item, Guid, string>(
                database.TenantB, row => row.Id, bItem, Guid.NewGuid(), row => row.Name, "x", Cancellation))).Code);
            Assert.Equal(["a-1"], await ((ItemsContext)work.Context).Items.Select(row => row.Code).ToArrayAsync(Cancellation));
        }
        Assert.NotEqual(Guid.Empty, aItem);
    }

    [Fact]
    public async Task TM_G02_028_A_cross_tenant_bypass_query_is_refused_after_its_host_scope_ends()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        await AddAsync(database, services, "user-a", database.TenantA, "a-1");
        database.Seams.Authority.Grant("host-1", TenancyServer.HostReport);
        var (scope, host) = await Units.EnterHostAsync(services, "host-1", TenancyServer.HostReport);
        await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, host, UnitOfWorkMode.Suppress, cancellationToken: Cancellation);
        var query = work.Context.AcrossTenants<Item>().Query;
        Assert.Equal(1, await query.CountAsync(Cancellation));
        await scope.DisposeAsync();
        Assert.False(host.IsActive);
        Assert.Equal("MPS035", (await Assert.ThrowsAsync<PersistenceContractException>(() => query.CountAsync(Cancellation))).Code);
    }

    [Fact]
    public async Task TM_G02_028_Directory_changes_need_host_scope_granted_for_directory_administration()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        var administration = services.GetRequiredService<TenantDirectoryAdministration>();
        var (tenantScope, tenant) = await Units.EnterAsync(services, "ta-admin", database.TenantA);
        await using (tenantScope)
            Assert.Equal("MTN009", (await Assert.ThrowsAsync<TenancyException>(() => administration.CreateTenantAsync(tenant, "Tenant C", Cancellation))).Code);
        database.Seams.Authority.Grant("host-1", TenancyServer.HostReport);
        var (reportScope, report) = await Units.EnterHostAsync(services, "host-1", TenancyServer.HostReport);
        await using (reportScope)
            Assert.Equal("MTN009", (await Assert.ThrowsAsync<TenancyException>(() => administration.SetStateAsync(report, database.TenantA, TenantState.Suspended, Cancellation))).Code);
        var (adminScope, admin) = await Units.EnterHostAsync(services, "host-1", TenantDirectoryAdministration.OperationId);
        await using (adminScope)
        {
            Assert.Equal("MTN020", (await Assert.ThrowsAsync<TenancyException>(() => administration.AddDomainAsync(admin, database.TenantA, "unlisted.example.test", Cancellation))).Code);
            Assert.Equal("MTN020", (await Assert.ThrowsAsync<TenancyException>(() => administration.AddDomainAsync(admin, database.TenantA, TenancyDatabase.HostB, Cancellation))).Code);
            await Assert.ThrowsAsync<ArgumentException>(() => administration.AddDomainAsync(admin, database.TenantA, "Tenant-C.example.test", Cancellation));
        }
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM \"" + database.DirectorySchema + "\".\"Tenants\""));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT \"State\" FROM \"" + database.DirectorySchema + "\".\"Tenants\" WHERE \"Id\" = @id",
            new Npgsql.NpgsqlParameter("id", database.TenantA)));
    }

    [Fact]
    public async Task TM_G02_028_Host_endpoints_refuse_tenant_administrators_and_tenant_markers_and_audit_the_grant()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using (var seeding = database.Services()) await database.SeedAsync(seeding);
        await using var server = await TenancyServer.StartAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/items?code=a-1&name=A", TenancyDatabase.HostA, "user-a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Post, "/items?code=b-1&name=B", TenancyDatabase.HostB, "user-b")).StatusCode);
        database.Seams.Authority.Grant("host-1", TenancyServer.HostReport);
        var audited = database.Seams.Audit.Records.Count;
        var handled = server.Handled;

        Assert.Equal(HttpStatusCode.Forbidden, (await server.SendAsync(HttpMethod.Get, "/host/report", TenancyDatabase.AppHost, "ta-admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.SendAsync(HttpMethod.Get, "/host/report", TenancyDatabase.AppHost, "ta-admin",
            request =>
            {
                request.Headers.Add(TenancyHttp.HeaderName, TenantIdentifier.Format(database.TenantB));
                request.Headers.Add("X-Modularyn-Host-Scope", "true");
            })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.SendAsync(HttpMethod.Get, "/host/report", TenancyDatabase.AppHost, "host-1",
            request => request.Headers.Add(TenancyHttp.HeaderName, TenantIdentifier.Format(database.TenantA)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.SendAsync(HttpMethod.Get, "/host/report", TenancyDatabase.HostA, "host-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.SendAsync(HttpMethod.Get, "/host/report", TenancyDatabase.AppHost)).StatusCode);
        Assert.Equal(handled, server.Handled);
        Assert.Equal(audited, database.Seams.Audit.Records.Count);

        var counts = await TenancyServer.ReadAsync<TenantCount[]>(await server.SendAsync(HttpMethod.Get, "/host/report", TenancyDatabase.AppHost, "host-1"));
        Assert.Equal([new TenantCount(database.TenantA, 1), new TenantCount(database.TenantB, 1)], counts.OrderBy(count => count.Tenant == database.TenantA ? 0 : 1));
        var record = database.Seams.Audit.Records.Last();
        Assert.Equal(("host-1", TenancyServer.HostReport, "Count items for every tenant"), (record.Actor.Subject, record.OperationId, record.Reason));
        Assert.Equal(record.UnitId, server.Observed.Last().Unit);
        Assert.Null(server.Observed.Last().Tenant);
    }

    [Fact]
    public async Task TM_G02_028_A_host_grant_reaches_only_the_entities_its_operation_declares()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using var services = database.Services();
        await database.SeedAsync(services);
        var item = await AddAsync(database, services, "user-a", database.TenantA, "a-1");
        // The work operation reaches the work table only; the lookups operation reaches only the host-scoped lookups.
        database.Seams.Authority.Grant("host-1", new HostOperation("test.reach.work", [new HostReach("Items", "WorkItems")]));
        database.Seams.Authority.Grant("host-1", new HostOperation("test.reach.lookups", [new HostReach("Items", "Lookups")]));
        database.Seams.Authority.Grant("host-1", "test.reach.nothing");

        var (workScope, work) = await Units.EnterHostAsync(services, "host-1", "test.reach.work");
        await using (workScope)
        await using (var unit = await EfCoreUnitOfWork.BeginAsync(database.Items, work, cancellationToken: Cancellation))
        {
            Assert.Equal(new HostReach("Items", "WorkItems"), Assert.Single(work.HostGrant!.Reach));
            Assert.Equal(0, await unit.Context.AcrossTenants<WorkItem>().Query.CountAsync(Cancellation));
            Assert.Equal(0, await unit.Sql.AcrossTenants<WorkItem>().Query.CountAsync(Cancellation));
            // Reading tenant data, recording a named tenant's row and changing host data outside the reach are each refused.
            Assert.Equal("MPS043", Assert.Throws<PersistenceContractException>(() => unit.Context.AcrossTenants<Item>()).Code);
            Assert.Equal("MPS043", Assert.Throws<PersistenceContractException>(() => unit.Sql.AcrossTenants<Item>()).Code);
            Assert.Equal("MPS043", (await Assert.ThrowsAsync<PersistenceContractException>(() => unit.Sql.UpdateForTenantAsync<Item, Guid, string>(
                database.TenantA, row => row.Id, item, Guid.NewGuid(), row => row.Name, "x", Cancellation))).Code);
            Assert.Equal("MPS043", (await Assert.ThrowsAsync<PersistenceContractException>(() => unit.Sql.InsertAsync(new Lookup { Id = "direct", Name = "x" },
                Cancellation))).Code);
        }
        var (lookupScope, lookups) = await Units.EnterHostAsync(services, "host-1", "test.reach.lookups");
        await using (lookupScope)
        {
            await using (var unit = await EfCoreUnitOfWork.BeginAsync(database.Items, lookups, cancellationToken: Cancellation))
            {
                unit.Context.Add(new Lookup { Id = "reached", Name = "within the reach" });
                Assert.Equal(UnitOfWorkState.Committed, (await unit.CompleteAsync(Cancellation)).State);
            }
            await using (var unit = await EfCoreUnitOfWork.BeginAsync(database.Items, lookups, cancellationToken: Cancellation))
                Assert.Equal("MPS043", Assert.Throws<PersistenceContractException>(() => unit.Context.AcrossTenants<WorkItem>()).Code);
        }
        // An operation whose module declared no reach reaches nothing at all, though its scope exists and was audited.
        var (nothingScope, nothing) = await Units.EnterHostAsync(services, "host-1", "test.reach.nothing");
        await using (nothingScope)
        await using (var unit = await EfCoreUnitOfWork.BeginAsync(database.Items, nothing, cancellationToken: Cancellation))
        {
            Assert.Empty(nothing.HostGrant!.Reach);
            Assert.Equal("MPS043", Assert.Throws<PersistenceContractException>(() => unit.Context.AcrossTenants<WorkItem>()).Code);
            unit.Context.Add(new Lookup { Id = "unreached", Name = "outside the reach" });
            Assert.Equal("MPS043", (await Assert.ThrowsAsync<PersistenceContractException>(() => unit.CompleteAsync(Cancellation))).Code);
        }
        Assert.Equal("reached", await database.ScalarAsync<string>("SELECT string_agg(\"Id\", ',' ORDER BY \"Id\") FROM \"" + database.ItemsSchema
            + "\".\"Lookups\" WHERE \"Id\" <> 'core'"));
    }

    private static async Task<Guid> AddAsync(TenancyDatabase database, IServiceProvider services, string subject, Guid tenant, string code)
    {
        var (scope, context) = await Units.EnterAsync(services, subject, tenant);
        await using (scope)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation);
            var item = new Item { Id = Guid.NewGuid(), Code = code, Name = code };
            work.Context.Add(item);
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
            return item.Id;
        }
    }
}
