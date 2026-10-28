using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// AC-003-R01: a bulk update over 100 items, committed in batches of 10, races a revocation of item.bulk-update. The
/// running operation rechecks before each batch and stops; the job queued for the remainder revalidates the producer's
/// current authority when it runs, as its own service identity, and refuses rather than replaying captured authority.
/// </summary>
public sealed partial class G02AC003Tests
{
    private static readonly string[] Hundred = Enumerable.Range(1, 100).Select(index => "item-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_R01_RevokedWorkStopsAtTheNextAuthorityCheck_Integration()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = AuthorizationHost.Create(database, clock);
        var (tenant, admin, user, owner) = await BulkFixtureAsync(host);
        var other = await host.UserAsync("user-2", tenant);
        await host.Get<AuthorityAdministration>().GrantPermissionAsync(owner.Context, owner.Principal, other.Actor, Items.BulkUpdate, Cancellation);

        // user-1 starts the bulk update and queues the item job for the rest; after the fourth batch the grant is revoked.
        await using var unit = await host.EnterAsync(user, tenant);
        var started = await ItemWork.StartAsync(database.ItemsModule, unit.Context, "bulk-update", Cancellation);
        var job = await ItemWork.EnqueueAsync(database.ItemsModule, unit.Context, started.Id, clock, Cancellation);
        var operation = await ItemWork.BulkUpdateAsync(host.Services, database.ItemsModule, unit.Context, unit.Principal, Hundred, 10, async batch =>
        {
            if (batch == 4) await host.Get<AuthorityAdministration>().RevokePermissionAsync(owner.Context, owner.Principal, user.Actor, Items.BulkUpdate, Cancellation);
        }, Cancellation, started.Id);
        Assert.Equal(("partially-applied", AuthorizationCodes.NotHeld, 4), (operation.State, operation.Disposition, await BatchesAsync(database, started.Id)));
        Assert.Equal((40L, 60L), await RevisionsAsync(database, tenant));
        output.WriteLine($"The operation committed {await BatchesAsync(database, started.Id)} batches, then its recheck refused with {operation.Disposition}.");

        // The job revalidates the producer now: user-1 no longer holds the permission, so the remainder is refused, not replayed.
        var dispositions = await DispatchAsync(host, database);
        Assert.Equal((TenantWorkDisposition.Denied, AuthorizationCodes.NotHeld), dispositions[job]);
        Assert.Equal((40L, 60L), await RevisionsAsync(database, tenant));
        Assert.Equal(("denied", AuthorizationCodes.NotHeld), await JobAsync(database, job));

        // The same job for a producer who still holds the permission runs, as the job's own service identity, for that producer.
        await using var otherUnit = await host.EnterAsync(other, tenant);
        var otherOperation = await ItemWork.StartAsync(database.ItemsModule, otherUnit.Context, "bulk-update", Cancellation);
        var otherJob = await ItemWork.EnqueueAsync(database.ItemsModule, otherUnit.Context, otherOperation.Id, clock, Cancellation);
        ExecutionPrincipal? ranAs = null;
        ActorReference? ranFor = null;
        dispositions = await DispatchAsync(host, database, (unitOfWork, decision, principal) =>
        {
            (ranAs, ranFor) = (principal, unitOfWork.Context.Actor);
            Assert.Equal(AuthorizationSource.Store, decision.Source);
            return Task.CompletedTask;
        });
        Assert.Equal((TenantWorkDisposition.Completed, (string?)null), dispositions[otherJob]);
        Assert.Equal((Items.Service, IdentityKind.Service), (ranAs!.Actor.Id, ranAs.Actor.Kind));
        Assert.Equal(other.Actor, ranFor);
        Assert.Equal(["completed", "denied"], await DispositionsAsync(database));
        output.WriteLine($"The job for user-1 was denied {dispositions.GetValueOrDefault(job).Code}; the job for user-2 ran as {ranAs} for {ranFor!.Subject}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_R01_RevokedWorkStopsAtTheNextAuthorityCheck_Isolation()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = AuthorizationHost.Create(database, clock);
        var (tenantA, admin, user, ownerA) = await BulkFixtureAsync(host);
        var tenantB = await host.TenantAsync("Tenant B");
        await host.AddMemberAsync(admin, tenantB);
        await host.AddMemberAsync(user, tenantB);
        await host.OwnerAsync(admin, tenantB);
        await using var ownerB = await host.EnterAsync(admin, tenantB);
        await ItemWork.AddAsync(database.ItemsModule, ownerB.Context, user.Id.ToString("D"), Hundred);
        await host.Get<AuthorityAdministration>().GrantPermissionAsync(ownerB.Context, ownerB.Principal, user.Actor, Items.BulkUpdate, Cancellation);

        // The same member runs both tenants' operations batch by batch, interleaved; the revocation is tenant A's alone.
        await using var unitA = await host.EnterAsync(user, tenantA);
        await using var unitB = await host.EnterAsync(user, tenantB, unitA.Principal);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = ItemWork.BulkUpdateAsync(host.Services, database.ItemsModule, unitA.Context, unitA.Principal, Hundred, 10, async batch =>
        {
            if (batch != 4) return;
            paused.SetResult();
            await revoked.Task.WaitAsync(Cancellation);
        }, Cancellation);
        await paused.Task.WaitAsync(Cancellation);
        // While A is paused, B runs to completion, and A's revocation then lands in A only.
        var b = await ItemWork.BulkUpdateAsync(host.Services, database.ItemsModule, unitB.Context, unitB.Principal, Hundred, 10, null, Cancellation);
        await host.Get<AuthorityAdministration>().RevokePermissionAsync(ownerA.Context, ownerA.Principal, user.Actor, Items.BulkUpdate, Cancellation);
        revoked.SetResult();
        var resultA = await a;
        Assert.Equal(("partially-applied", AuthorizationCodes.NotHeld), (resultA.State, resultA.Disposition));
        Assert.Equal(("completed", ""), (b.State, b.Disposition));
        Assert.Equal((40L, 60L), await RevisionsAsync(database, tenantA));
        Assert.Equal((100L, 0L), await RevisionsAsync(database, tenantB));

        // Tenant B's grant does not reach tenant A, now or for running work.
        Assert.Equal(AuthorizationCodes.NotHeld, (await host.AuthorizeAsync(unitA, Items.BulkUpdate, current: true)).Code);
        Assert.True((await host.AuthorizeAsync(unitB, Items.BulkUpdate, current: true)).Permitted);
        output.WriteLine("Tenant A's operation stopped after 4 batches; tenant B's, by the same member, completed all 10.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_R01_RevokedWorkStopsAtTheNextAuthorityCheck_Recovery()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        Guid tenant, job, operation;
        await using (var before = AuthorizationHost.Create(database, clock))
        {
            var (fixtureTenant, _, user, owner) = await BulkFixtureAsync(before);
            tenant = fixtureTenant;
            await using var unit = await before.EnterAsync(user, tenant);
            var started = await ItemWork.StartAsync(database.ItemsModule, unit.Context, "bulk-update", Cancellation);
            operation = started.Id;
            job = await ItemWork.EnqueueAsync(database.ItemsModule, unit.Context, started.Id, clock, Cancellation);
            var stopped = await ItemWork.BulkUpdateAsync(before.Services, database.ItemsModule, unit.Context, unit.Principal, Hundred, 10, async batch =>
            {
                if (batch == 4) await before.Get<AuthorityAdministration>().RevokePermissionAsync(owner.Context, owner.Principal, user.Actor, Items.BulkUpdate, Cancellation);
            }, Cancellation, started.Id);
            Assert.Equal("partially-applied", stopped.State);
            await owner.DisposeAsync();
        }
        // The host restarts before the job runs. Nothing of the first process survives but the database.
        output.WriteLine("Restarted the host with the item job still queued.");
        await using var after = AuthorizationHost.Create(database, clock);
        ExecutionPrincipal? ranAs = null;
        var dispositions = await DispatchAsync(after, database, (_, _, principal) =>
        {
            ranAs = principal;
            return Task.CompletedTask;
        });
        // The job runs as svc-items, revalidates user-1's current authority from the store, and refuses the remainder.
        Assert.Equal((TenantWorkDisposition.Denied, AuthorizationCodes.NotHeld), dispositions[job]);
        Assert.Null(ranAs);
        Assert.Equal(("denied", AuthorizationCodes.NotHeld), await JobAsync(database, job));
        Assert.Equal((40L, 60L), await RevisionsAsync(database, tenant));
        Assert.Equal(4, await BatchesAsync(database, operation));
        // The operation ends partially applied and denied, with no orphaned in-progress marker or queued work left.
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table(database.ItemsSchema, "ItemOperations") + " WHERE \"State\" = 'running'"));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table(database.ItemsSchema, "ItemJobs") + " WHERE \"State\" = 'queued'"));
        Assert.Equal("partially-applied", await database.ScalarAsync<string>("SELECT \"State\" FROM " + database.Table(database.ItemsSchema, "ItemOperations")
            + " WHERE \"Id\" = @id", new NpgsqlParameter("id", operation)));
        output.WriteLine("After the restart the job was denied MAZ005 from the current grants; 4 batches stayed committed and nothing was left running.");
    }

    /// <summary>Tenant A with its owner and user-1, who holds item.bulk-update there, and 100 items of user-1's.</summary>
    private static async Task<(Guid Tenant, User Admin, User User, Unit Owner)> BulkFixtureAsync(AuthorizationHost host)
    {
        var tenant = await host.TenantAsync("Tenant A");
        var admin = await host.UserAsync("admin-1", tenant);
        var user = await host.UserAsync("user-1", tenant);
        await host.OwnerAsync(admin, tenant);
        var owner = await host.EnterAsync(admin, tenant);
        await ItemWork.AddAsync(host.Database.ItemsModule, owner.Context, user.Id.ToString("D"), Hundred);
        await host.Get<AuthorityAdministration>().GrantPermissionAsync(owner.Context, owner.Principal, user.Actor, Items.BulkUpdate, Cancellation);
        return (tenant, admin, user, owner);
    }

    /// <summary>
    /// The item job's dispatcher: in an audited host scope that reaches only the job table, it lists queued jobs across
    /// tenants and runs each through the worker entry point as svc-items, then records a refusal on the job's row.
    /// </summary>
    private static async Task<Dictionary<Guid, (TenantWorkDisposition Disposition, string? Code)>> DispatchAsync(AuthorizationHost host, AuthorizationDatabase database,
        Func<TenantWorkUnit, AuthorizationDecision, ExecutionPrincipal, Task>? work = null)
    {
        var dispatch = await host.HostAsync(Items.Dispatch.Id);
        List<ItemJob> queued;
        await using (var read = await EfCoreUnitOfWork.BeginAsync(database.ItemsModule, dispatch, cancellationToken: Cancellation))
            queued = await read.Context.AcrossTenants<ItemJob>().Query.AsNoTracking().Where(row => row.State == "queued").ToListAsync(Cancellation);
        var results = new Dictionary<Guid, (TenantWorkDisposition, string?)>();
        foreach (var job in queued)
        {
            await using var service = host.Get<IServiceIdentityScopes>().Begin(Items.Service);
            var outcome = await host.Get<AuthorizedWorkRunner>().RunAsync(service.Principal, job.Propagated, Items.BulkUpdate, async (unit, decision, token) =>
            {
                if (work is not null) await work(unit, decision, service.Principal);
                await using var complete = await EfCoreUnitOfWork.BeginAsync(database.ItemsModule, unit.Context, cancellationToken: token);
                (await ((ItemsContext)complete.Context).Jobs.SingleAsync(row => row.Id == job.Id, token)).State = "completed";
                await complete.CompleteAsync(token);
            }, Cancellation);
            results[job.Id] = (outcome.Disposition, outcome.Code);
            if (outcome.Disposition != TenantWorkDisposition.Denied) continue;
            await using var record = await EfCoreUnitOfWork.BeginAsync(database.ItemsModule, dispatch, cancellationToken: Cancellation);
            var state = await record.Sql.UpdateForTenantAsync<ItemJob, Guid, string>(job.TenantId, row => row.Id, job.Id, job.Version, row => row.State, "denied",
                Cancellation);
            Assert.True((await record.Sql.UpdateForTenantAsync<ItemJob, Guid, string>(job.TenantId, row => row.Id, job.Id, state.Version!.Value,
                row => row.Disposition, outcome.Code!, Cancellation)).Succeeded);
            await record.CompleteAsync(Cancellation);
        }
        return results;
    }

    private static Task<int> BatchesAsync(AuthorizationDatabase database, Guid operation) => database.ScalarAsync<int>(
        "SELECT \"Batches\" FROM " + database.Table(database.ItemsSchema, "ItemOperations") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", operation));

    private static async Task<(long Updated, long Untouched)> RevisionsAsync(AuthorizationDatabase database, Guid tenant)
    {
        var table = database.Table(database.ItemsSchema, "Items");
        return (await database.ScalarAsync<long>("SELECT count(*) FROM " + table + " WHERE \"TenantId\" = @tenant AND \"Revision\" = 1", new NpgsqlParameter("tenant", tenant)),
            await database.ScalarAsync<long>("SELECT count(*) FROM " + table + " WHERE \"TenantId\" = @tenant AND \"Revision\" = 0", new NpgsqlParameter("tenant", tenant)));
    }

    private static async Task<(string State, string Disposition)> JobAsync(AuthorizationDatabase database, Guid job)
    {
        var table = database.Table(database.ItemsSchema, "ItemJobs");
        return (await database.ScalarAsync<string>("SELECT \"State\" FROM " + table + " WHERE \"Id\" = @id", new NpgsqlParameter("id", job)),
            await database.ScalarAsync<string>("SELECT \"Disposition\" FROM " + table + " WHERE \"Id\" = @id", new NpgsqlParameter("id", job)));
    }

    private static async Task<string[]> DispositionsAsync(AuthorizationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT \"State\" FROM " + database.Table(database.ItemsSchema, "ItemJobs") + " ORDER BY \"State\"", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        var states = new List<string>();
        while (await reader.ReadAsync(Cancellation)) states.Add(reader.GetString(0));
        return [.. states];
    }
}
