using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

// Permission administration, resources, lists and host authority on PostgreSQL, through the real stores and the real
// tenancy runtime; only authentication and SEC-07's audit are stand-ins.
[Trait("Requires", "PostgreSQL")]
[Collection(AuthorizationPostgreSqlCollection.Name)]
public sealed class AuthorityTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private sealed record Tenant(Guid Id, User Owner, Unit OwnerUnit);

    private static async Task<Tenant> TenantAsync(AuthorizationHost host, string name = "Tenant A")
    {
        var id = await host.TenantAsync(name);
        var owner = await host.UserAsync("owner-" + id.ToString("N")[..8], id);
        await host.OwnerAsync(owner, id);
        return new(id, owner, await host.EnterAsync(owner, id));
    }

    private static async Task<string> RefusedAsync(Func<Task> change) => (await Assert.ThrowsAsync<AuthorityChangeException>(change)).Code;

    [Fact]
    public async Task TM_G02_022_No_grant_reaches_beyond_the_grantor_s_ceiling_or_what_it_holds()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        await using var host = AuthorizationHost.Create(database, new TestClock());
        var tenant = await TenantAsync(host);
        var adminA = await host.UserAsync("admin-a", tenant.Id);
        var user = await host.UserAsync("user-1", tenant.Id);
        var outsider = await host.UserAsync("outsider-1");
        var administration = host.Get<AuthorityAdministration>();
        // The owner defines a role that holds item.delete but may assign only item.read and item.export.
        Assert.True(await administration.DefineRoleAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, "item-admins",
            [AuthorizationModule.ManagePermission, Items.Read, Items.Export, Items.Delete], [Items.Read, Items.Export], Cancellation));
        Assert.True(await administration.AssignRoleAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, adminA.Actor, "item-admins", Cancellation));
        await using var admin = await host.EnterAsync(adminA, tenant.Id);

        Assert.True(await administration.GrantPermissionAsync(admin.Context, admin.Principal, user.Actor, Items.Export, Cancellation));
        Assert.False(await administration.GrantPermissionAsync(admin.Context, admin.Principal, user.Actor, Items.Export, Cancellation));
        // Held but outside the ceiling; in neither; declared by no composed module.
        Assert.Equal(AuthorizationCodes.BeyondCeiling, await RefusedAsync(() => administration.GrantPermissionAsync(admin.Context, admin.Principal, user.Actor, Items.Delete, Cancellation)));
        Assert.Equal(AuthorizationCodes.BeyondCeiling, await RefusedAsync(() => administration.GrantPermissionAsync(admin.Context, admin.Principal, user.Actor, Items.Update, Cancellation)));
        Assert.Equal(AuthorizationCodes.ChangeUndefined, await RefusedAsync(() => administration.GrantPermissionAsync(admin.Context, admin.Principal, user.Actor, "absent.read", Cancellation)));
        // A role carrying more than the ceiling can be neither defined, assigned nor removed by the actor.
        Assert.Equal(AuthorizationCodes.BeyondCeiling, await RefusedAsync(() => administration.DefineRoleAsync(admin.Context, admin.Principal, "deleters",
            [Items.Delete], [], Cancellation)));
        Assert.Equal(AuthorizationCodes.BeyondCeiling, await RefusedAsync(() => administration.AssignRoleAsync(admin.Context, admin.Principal, user.Actor,
            "item-admins", Cancellation)));
        Assert.Equal(AuthorizationCodes.BeyondCeiling, await RefusedAsync(() => administration.RemoveRoleAsync(admin.Context, admin.Principal, "item-admins", Cancellation)));
        // A role within the ceiling can be defined and assigned by the actor.
        Assert.True(await administration.DefineRoleAsync(admin.Context, admin.Principal, "readers", [Items.Read], [], Cancellation));
        Assert.True(await administration.AssignRoleAsync(admin.Context, admin.Principal, user.Actor, "readers", Cancellation));
        // Only current members of the tenant, and never a service identity, receive grants.
        Assert.Equal(AuthorizationCodes.ChangeInvalid, await RefusedAsync(() => administration.GrantPermissionAsync(admin.Context, admin.Principal, outsider.Actor,
            Items.Read, Cancellation)));
        Assert.Equal(AuthorizationCodes.ChangeInvalid, await RefusedAsync(() => administration.GrantPermissionAsync(admin.Context, admin.Principal,
            new ActorReference(ActorKind.Service, Items.Service), Items.Read, Cancellation)));

        // Without the administration permission nothing can be granted, and authority is never changed under a delegation.
        await using (var member = await host.EnterAsync(user, tenant.Id))
            Assert.Equal(AuthorizationCodes.ChangeNotPermitted, await RefusedAsync(() => administration.GrantPermissionAsync(member.Context, member.Principal,
                adminA.Actor, Items.Read, Cancellation)));
        var delegation = ExecutionPrincipal.ForDelegation(new PrincipalReference(user.Id.ToString("D"), IdentityKind.User), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new PrincipalReference(adminA.Id.ToString("D"), IdentityKind.User),
            new DelegatedAuthority(Guid.NewGuid(), [AuthorizationModule.ManagePermission], "Administer for admin-a", tenant.Id, DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddMinutes(10)));
        await using (var delegated = await host.EnterAsync(user, tenant.Id, delegation))
            Assert.Equal(AuthorizationCodes.ChangeNotPermitted, await RefusedAsync(() => administration.GrantPermissionAsync(delegated.Context, delegated.Principal,
                user.Actor, Items.Read, Cancellation)));

        // A change that cannot be recorded in audit is not made.
        host.Audit.Failing = true;
        Assert.Equal(AuthorizationCodes.AuditUnavailable, await RefusedAsync(() => administration.RevokePermissionAsync(admin.Context, admin.Principal, user.Actor,
            Items.Export, Cancellation)));
        host.Audit.Failing = false;
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table(database.AuthorizationSchema, "PermissionGrants")
            + " WHERE \"Member\" = @member", new NpgsqlParameter("member", user.Id.ToString("D"))));
        Assert.True((await host.AuthorizeAsync(await host.EnterAsync(user, tenant.Id), Items.Export)).Permitted);

        // Every committed change was recorded before it committed, naming the actor, tenant, member and permissions.
        var grant = host.Audit.Records.Single(record => record.Kind == AuthorityChangeKind.PermissionGranted && record.Member == user.Actor);
        Assert.Equal((adminA.Actor, tenant.Id), (grant.Actor, grant.TenantId!.Value));
        Assert.Equal([Items.Export], grant.Permissions);
        output.WriteLine($"Audit received {host.Audit.Records.Count} authority changes; refused changes beyond the ceiling recorded none.");
    }

    [Fact]
    public async Task TM_G02_022_A_grant_and_a_concurrent_ceiling_reduction_serialize_on_the_tenant_s_authority()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        await using var host = AuthorizationHost.Create(database, new TestClock());
        var tenant = await TenantAsync(host);
        var adminA = await host.UserAsync("admin-a", tenant.Id);
        var user = await host.UserAsync("user-1", tenant.Id);
        var administration = host.Get<AuthorityAdministration>();
        Task<bool> Ceiling(params string[] ceiling) => administration.DefineRoleAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, "item-admins",
            [AuthorizationModule.ManagePermission, Items.Export], ceiling, Cancellation);
        await Ceiling(Items.Export);
        await administration.AssignRoleAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, adminA.Actor, "item-admins", Cancellation);
        await using var admin = await host.EnterAsync(adminA, tenant.Id);
        Task<bool> Grant() => administration.GrantPermissionAsync(admin.Context, admin.Principal, user.Actor, Items.Export, Cancellation);
        Task<bool> Revoke() => administration.RevokePermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, Items.Export, Cancellation);

        // 1. The grant holds the tenant's authority, decided and not yet committed; the reduction must wait for it.
        var grantHeld = Hold(host, AuthorityChangeKind.PermissionGranted, out var releaseGrant);
        var grant = Grant();
        await grantHeld.WaitAsync(Cancellation);
        var reduction = Ceiling();
        Assert.NotSame(reduction, await Task.WhenAny(reduction, Task.Delay(TimeSpan.FromMilliseconds(500), Cancellation)));
        releaseGrant();
        Assert.True(await grant);
        Assert.True(await reduction);
        // The grant committed under the earlier ceiling; the reduction, after it, cannot recall it.
        Assert.True((await host.AuthorizeAsync(await host.EnterAsync(user, tenant.Id), Items.Export, current: true)).Permitted);
        output.WriteLine("A grant decided first committed under the earlier ceiling; the reduction waited for it and committed after.");

        // 2. Reverse: the reduction holds the tenant's authority; the grant waits, then reads the reduced ceiling and is refused.
        await Revoke();
        await Ceiling(Items.Export);
        var reductionHeld = Hold(host, AuthorityChangeKind.RoleDefined, out var releaseReduction);
        reduction = Ceiling();
        await reductionHeld.WaitAsync(Cancellation);
        grant = Grant();
        Assert.NotSame(grant, await Task.WhenAny(grant, Task.Delay(TimeSpan.FromMilliseconds(500), Cancellation)));
        releaseReduction();
        Assert.True(await reduction);
        Assert.Equal(AuthorizationCodes.BeyondCeiling, (await Assert.ThrowsAsync<AuthorityChangeException>(() => grant)).Code);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table(database.AuthorizationSchema, "PermissionGrants")
            + " WHERE \"Member\" = @member", new NpgsqlParameter("member", user.Id.ToString("D"))));
        output.WriteLine("A grant that waited for a reduction read the reduced ceiling and was refused; no grant committed under it.");

        // 3. Many unordered pairs: every grant either committed before the reduction it raced or was refused.
        for (var round = 0; round < 10; round++)
        {
            await Ceiling(Items.Export);
            var pair = new[] { Grant(), Ceiling() };
            try { await Task.WhenAll(pair); }
            catch (AuthorityChangeException exception) { Assert.Contains(exception.Code, new[] { AuthorizationCodes.BeyondCeiling, AuthorizationCodes.ChangeConflict }); }
            var granted = await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table(database.AuthorizationSchema, "PermissionGrants")
                + " WHERE \"Member\" = @member", new NpgsqlParameter("member", user.Id.ToString("D")));
            Assert.Equal(granted == 1, pair[0].IsCompletedSuccessfully && await pair[0]);
            if (granted == 1) await Revoke();
        }
    }

    private static Task Hold(AuthorizationHost host, AuthorityChangeKind kind, out Action release)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = () =>
        {
            host.Audit.Holding = null;
            gate.SetResult();
        };
        host.Audit.Holding = async record =>
        {
            if (record.Kind != kind) return;
            reached.TrySetResult();
            await gate.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        return reached.Task;
    }

    [Fact]
    public async Task TM_G02_020_Resource_grants_are_read_at_every_decision_and_revoked_at_once()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = AuthorizationHost.Create(database, clock);
        var tenant = await TenantAsync(host);
        var other = await TenantAsync(host, "Tenant B");
        var user1 = await host.UserAsync("user-1", tenant.Id, other.Id);
        var user2 = await host.UserAsync("user-2", tenant.Id);
        var administration = host.Get<AuthorityAdministration>();
        foreach (var user in new[] { user1, user2 })
            await administration.GrantPermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, Items.Read, Cancellation);
        await administration.GrantPermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user1.Actor, AuthorizationModule.SharePermission, Cancellation);
        await ItemWork.AddAsync(database.ItemsModule, tenant.OwnerUnit.Context, user1.Id.ToString("D"), "item-1");
        await ItemWork.AddAsync(database.ItemsModule, other.OwnerUnit.Context, user1.Id.ToString("D"), "item-1");
        var item = (await ItemWork.FindAsync(database.ItemsModule, tenant.OwnerUnit.Context, "item-1", Cancellation))!;
        await using var owner = await host.EnterAsync(user1, tenant.Id);
        await using var reader = await host.EnterAsync(user2, tenant.Id);
        Assert.Equal(AuthorizationCodes.ResourceNotFound, (await host.AuthorizeAsync(reader, Items.Read, Items.Reference(item))).Code);

        // The owner, with the share permission and the permission it shares, grants it until an expiry, floored to whole microseconds.
        var expiry = clock.Now.AddMinutes(10).AddTicks(7);
        Assert.True(await administration.GrantResourceAsync(owner.Context, owner.Principal, user2.Actor, Items.Reference(item), Items.Read, expiry, Cancellation));
        Assert.Equal(AuthorizationClock.Floor(expiry).UtcDateTime, await database.ScalarAsync<DateTime>("SELECT \"ExpiresAt\" FROM "
            + database.Table(database.AuthorizationSchema, "ResourceGrants") + " WHERE \"ResourceId\" = 'item-1'"));
        Assert.True((await host.AuthorizeAsync(reader, Items.Read, Items.Reference(item))).Permitted);
        // The grant is in its tenant only: the same code in tenant B is another resource.
        var otherItem = (await ItemWork.FindAsync(database.ItemsModule, other.OwnerUnit.Context, "item-1", Cancellation))!;
        Assert.Equal(AuthorizationCodes.ResourceNotFound, (await host.AuthorizeAsync(reader, Items.Read, Items.Reference(otherItem))).Code);
        // Neither another member nor one without the share permission may share someone else's item; the owner may revoke.
        Assert.Equal(AuthorizationCodes.BeyondCeiling, await RefusedAsync(() => administration.GrantResourceAsync(reader.Context, reader.Principal, user2.Actor,
            Items.Reference(item), Items.Read, null, Cancellation)));
        Assert.True(await administration.RevokeResourceAsync(owner.Context, owner.Principal, user2.Actor, Items.Reference(item), Items.Read, Cancellation));
        // Revocation needs no freshness bound: the next decision reads the grant from the store.
        Assert.Equal(AuthorizationCodes.ResourceNotFound, (await host.AuthorizeAsync(reader, Items.Read, Items.Reference(item))).Code);

        // A grant ends at its expiry, and a grant whose expiry has passed cannot be made.
        await administration.GrantResourceAsync(owner.Context, owner.Principal, user2.Actor, Items.Reference(item), Items.Read, clock.Now.AddSeconds(5), Cancellation);
        Assert.True((await host.AuthorizeAsync(reader, Items.Read, Items.Reference(item))).Permitted);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(AuthorizationCodes.ResourceNotFound, (await host.AuthorizeAsync(reader, Items.Read, Items.Reference(item))).Code);
        Assert.Equal(AuthorizationCodes.ChangeInvalid, await RefusedAsync(() => administration.GrantResourceAsync(owner.Context, owner.Principal, user2.Actor,
            Items.Reference(item), Items.Read, clock.Now, Cancellation)));
    }

    [Fact]
    public async Task TM_G02_021_Lists_counts_and_exports_contain_only_the_resources_the_caller_may_read()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        await using var host = AuthorizationHost.Create(database, new TestClock());
        var tenant = await TenantAsync(host);
        var user1 = await host.UserAsync("user-1", tenant.Id);
        var user2 = await host.UserAsync("user-2", tenant.Id);
        var administration = host.Get<AuthorityAdministration>();
        foreach (var permission in new[] { Items.Read, Items.Export, AuthorizationModule.SharePermission })
        foreach (var user in new[] { user1, user2 })
            await administration.GrantPermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, permission, Cancellation);
        await ItemWork.AddAsync(database.ItemsModule, tenant.OwnerUnit.Context, user1.Id.ToString("D"), "item-1", "item-2", "item-4");
        await ItemWork.AddAsync(database.ItemsModule, tenant.OwnerUnit.Context, user2.Id.ToString("D"), "item-3");
        await using var owner = await host.EnterAsync(user1, tenant.Id);
        var item4 = (await ItemWork.FindAsync(database.ItemsModule, owner.Context, "item-4", Cancellation))!;
        await administration.GrantResourceAsync(owner.Context, owner.Principal, user2.Actor, Items.Reference(item4), Items.Read, null, Cancellation);

        // As user-2: the list, its total and the export hold only item-3, its own, and item-4, shared with it.
        await using var unit = await host.EnterAsync(user2, tenant.Id);
        var (codes, count) = await ItemWork.ListAsync(host.Get<IPermissionEvaluator>(), database.ItemsModule, unit.Context, unit.Principal, Cancellation);
        Assert.Equal(["item-3", "item-4"], codes);
        Assert.Equal(2, count);
        var export = await ItemWork.ExportAsync(host.Services, database.ItemsModule, unit.Context, unit.Principal, null, Cancellation);
        Assert.Equal("item-3\nitem-4", await database.ScalarAsync<string>("SELECT \"Content\" FROM " + database.Table(database.ItemsSchema, "ItemExports")
            + " WHERE \"Id\" = @id", new NpgsqlParameter("id", export.Id)));
        // Without item.read nothing is visible at all, whatever is owned or shared.
        await administration.RevokePermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user2.Actor, Items.Read, Cancellation);
        var hidden = await ItemWork.ListAsync(host.Get<IPermissionEvaluator>(), database.ItemsModule, unit.Context, unit.Principal, Cancellation);
        Assert.Empty(hidden.Codes);
        Assert.Equal(0, hidden.Count);
        output.WriteLine($"user-2 listed {string.Join(", ", codes)} with total {count}; the export held the same two items.");
    }

    [Fact]
    public async Task TM_G02_028_Host_authority_comes_only_from_the_elevated_policy_and_reaches_only_declared_entities()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        await using var host = AuthorizationHost.Create(database, new TestClock());
        var tenant = await TenantAsync(host);
        var member = await host.UserAsync("member-1", tenant.Id);
        await ItemWork.AddAsync(database.ItemsModule, tenant.OwnerUnit.Context, member.Id.ToString("D"), "item-1");
        async Task<TenantDecision> Establish(string subject, string operation)
        {
            await using var scope = host.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
                TenantResolutionRequest.ForHostOperation(TestIdentity.User(subject), operation, "Host authority test"), Cancellation);
        }

        // The standing operator holds every declared operation; nobody else holds any, a tenant owner included.
        Assert.True((await Establish(AuthorizationHost.Operator, Items.Report.Id)).Succeeded);
        Assert.Equal(TenancyCodes.HostAuthorityRefused, (await Establish(member.Id.ToString("D"), Items.Report.Id)).Code);
        Assert.Equal(TenancyCodes.HostAuthorityRefused, (await Establish(tenant.Owner.Id.ToString("D"), Items.Report.Id)).Code);
        Assert.Equal(TenancyCodes.HostAuthorityRefused, (await Establish(AuthorizationHost.Operator, "test.undeclared")).Code);

        // A stored grant, made in an audited host scope for host authority, by an operator who holds the operation itself.
        var administration = host.Get<HostAuthorityAdministration>();
        var grants = await host.HostAsync(AuthorizationModule.HostAuthorityOperation.Id);
        Assert.True(await administration.GrantAsync(grants, member.Actor, Items.Report.Id, Cancellation));
        Assert.True((await Establish(member.Id.ToString("D"), Items.Report.Id)).Succeeded);
        // No grant is made outside the host-authority scope, and one who may grant host authority cannot pass on an operation it does not hold.
        await using (var reporting = host.Services.CreateAsyncScope())
        {
            var decision = await reporting.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(
                TenantResolutionRequest.ForHostOperation(TestIdentity.User(member.Id.ToString("D")), Items.Report.Id, "Host authority test"), Cancellation);
            Assert.Equal(AuthorizationCodes.ChangeNotPermitted, await RefusedAsync(() => administration.GrantAsync(decision.Context!, tenant.Owner.Actor,
                Items.Report.Id, Cancellation)));
        }
        Assert.True(await administration.GrantAsync(grants, member.Actor, AuthorizationModule.HostAuthorityOperation.Id, Cancellation));
        await using (var delegating = host.Services.CreateAsyncScope())
        {
            var decision = await delegating.ServiceProvider.GetRequiredService<TenantExecution>().EstablishAsync(TenantResolutionRequest.ForHostOperation(
                TestIdentity.User(member.Id.ToString("D")), AuthorizationModule.HostAuthorityOperation.Id, "Host authority test"), Cancellation);
            Assert.True(decision.Succeeded, decision.Code);
            Assert.Equal(AuthorizationCodes.BeyondCeiling, await RefusedAsync(() => administration.GrantAsync(decision.Context!, tenant.Owner.Actor,
                Items.Dispatch.Id, Cancellation)));
            Assert.True(await administration.GrantAsync(decision.Context!, tenant.Owner.Actor, Items.Report.Id, Cancellation));
        }
        Assert.Equal(AuthorizationCodes.ChangeNotPermitted, await RefusedAsync(() => administration.GrantAsync(tenant.OwnerUnit.Context, member.Actor,
            Items.Dispatch.Id, Cancellation)));
        Assert.Equal(AuthorizationCodes.ChangeUndefined, await RefusedAsync(() => administration.GrantAsync(grants, member.Actor, "test.undeclared", Cancellation)));
        // Revocation applies to the next host scope.
        Assert.True(await administration.RevokeAsync(grants, member.Actor, Items.Report.Id, Cancellation));
        Assert.Equal(TenancyCodes.HostAuthorityRefused, (await Establish(member.Id.ToString("D"), Items.Report.Id)).Code);
        Assert.Contains(host.Audit.Records, record => record.Kind == AuthorityChangeKind.HostAuthorityRevoked && record.Member == member.Actor);

        // A host scope reaches only the entities its operation declares: the report reads items across tenants, never jobs,
        // and the dispatcher records a job's disposition, never an item's.
        var report = await host.HostAsync(Items.Report.Id);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.ItemsModule, report, cancellationToken: Cancellation))
        {
            Assert.Equal(1, await work.Context.AcrossTenants<Item>().Query.CountAsync(Cancellation));
            Assert.Equal("MPS043", Assert.Throws<PersistenceContractException>(() => work.Context.AcrossTenants<ItemJob>()).Code);
            Assert.Equal("MPS043", Assert.Throws<PersistenceContractException>(() => work.Sql.AcrossTenants<ItemJob>()).Code);
            Assert.Equal("MPS043", (await Assert.ThrowsAsync<PersistenceContractException>(() => work.Sql.UpdateForTenantAsync<ItemJob, Guid, string>(tenant.Id,
                row => row.Id, Guid.NewGuid(), Guid.NewGuid(), row => row.State, "denied", Cancellation))).Code);
        }
        // The tenant-owners operation writes owners, and the host-authority operation cannot.
        Assert.Equal(AuthorizationCodes.ChangeNotPermitted, await RefusedAsync(() => administration.AddTenantOwnerAsync(grants, tenant.Id, member.Actor, Cancellation)));
        Assert.Equal("MPS043", (await Assert.ThrowsAsync<PersistenceContractException>(async () =>
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Authorization, report, cancellationToken: Cancellation);
            work.Context.Add(new HostAuthorityGrantRow { Actor = member.Id.ToString("D"), OperationId = Items.Report.Id, GrantedBy = "test" });
            await work.CompleteAsync(Cancellation);
        })).Code);
        output.WriteLine("Host scope came only from the standing operator or a stored grant, ended at revocation, and reached only its declared entities.");
    }

    [Fact]
    public async Task TM_G02_028_A_tenant_owner_is_named_only_in_an_audited_host_scope_and_holds_every_declared_permission()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        await using var host = AuthorizationHost.Create(database, new TestClock());
        var tenant = await TenantAsync(host);
        var member = await host.UserAsync("member-1", tenant.Id);
        var administration = host.Get<HostAuthorityAdministration>();
        Assert.Equal(AuthorizationCodes.ChangeNotPermitted, await RefusedAsync(() => administration.AddTenantOwnerAsync(tenant.OwnerUnit.Context, tenant.Id,
            member.Actor, Cancellation)));
        var owners = await host.HostAsync(AuthorizationModule.TenantOwnersOperation.Id);
        Assert.Equal(AuthorizationCodes.ChangeInvalid, await RefusedAsync(() => administration.AddTenantOwnerAsync(owners, Guid.NewGuid(), member.Actor, Cancellation)));
        Assert.Equal(AuthorizationCodes.ChangeInvalid, await RefusedAsync(() => administration.AddTenantOwnerAsync(owners, tenant.Id,
            new ActorReference(ActorKind.Service, Items.Service), Cancellation)));
        var described = await host.Get<IPermissionEvaluator>().DescribeAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, Cancellation);
        Assert.Equal(database.Registry.Permissions.Select(definition => definition.Name).Order(StringComparer.Ordinal), described!.Permissions.Order(StringComparer.Ordinal));
        Assert.True(await administration.RemoveTenantOwnerAsync(owners, tenant.Id, tenant.Owner.Actor, Cancellation));
        // In this process the change invalidates the tenant's cached authority at once.
        Assert.Equal(AuthorizationCodes.NotHeld, (await host.AuthorizeAsync(tenant.OwnerUnit, Items.Read)).Code);
        Assert.Contains(host.Audit.Records, record => record.Kind == AuthorityChangeKind.TenantOwnerRemoved && record.TenantId == tenant.Id);
    }

    [Fact]
    public async Task TM_G02_024_Features_and_excluded_modules_confer_no_authority()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        await using var host = AuthorizationHost.Create(database, new TestClock(), services =>
            services.AddSingleton<Features.IFeatureGate>(new AllowingGate()));
        var tenant = await TenantAsync(host);
        var user = await host.UserAsync("user-1", tenant.Id);
        await using var unit = await host.EnterAsync(user, tenant.Id);

        // The export feature is on and its gate allows it, yet the export is refused: an entitlement is not a permission.
        await using var scope = host.Services.CreateAsyncScope();
        var features = scope.ServiceProvider.GetRequiredService<Features.FeatureService>();
        await features.SetAsync(await host.HostAsync(Settings.SettingsService.GlobalOperationId), null, Items.ExportEnabled, Settings.Abstractions.ValueSource.Global,
            true, cancellationToken: Cancellation);
        Assert.True(await features.IsEnabledAsync(unit.Context, Items.ExportEnabled, Cancellation));
        Assert.Equal(AuthorizationCodes.NotHeld, (await host.AuthorizeAsync(unit, Items.Export)).Code);

        // A permission of a module the composition excludes can be neither granted nor exercised, even where a row of it survives.
        Assert.Equal(AuthorizationCodes.ChangeUndefined, await RefusedAsync(() => host.Get<AuthorityAdministration>().GrantPermissionAsync(tenant.OwnerUnit.Context,
            tenant.OwnerUnit.Principal, user.Actor, "excluded.export", Cancellation)));
        await database.ExecuteAsync("INSERT INTO " + database.Table(database.AuthorizationSchema, "PermissionGrants")
            + " VALUES (@tenant, 0, @member, 'excluded.export', @at, 0, 'migration')", new NpgsqlParameter("tenant", tenant.Id),
            new NpgsqlParameter("member", user.Id.ToString("D")), new NpgsqlParameter("at", AuthorizationClock.Floor(DateTimeOffset.UtcNow)));
        Assert.Equal(AuthorizationCodes.UndefinedPermission, (await host.AuthorizeAsync(unit, "excluded.export", current: true)).Code);
        Assert.DoesNotContain("excluded.export", (await host.Get<IPermissionEvaluator>().DescribeAsync(unit.Context, unit.Principal, Cancellation))!.Permissions);
    }

    [Fact]
    public async Task TM_G02_024_Settings_and_feature_management_need_their_permission_for_the_caller()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        await using var host = AuthorizationHost.Create(database, new TestClock());
        var tenant = await TenantAsync(host);
        var user = await host.UserAsync("user-1", tenant.Id);
        var support = await host.UserAsync("support-1", tenant.Id);
        var settings = host.Get<Settings.SettingsService>();
        var administration = host.Get<AuthorityAdministration>();
        await using var unit = await host.EnterAsync(user, tenant.Id);
        await using var scope = host.Services.CreateAsyncScope();
        var features = scope.ServiceProvider.GetRequiredService<Features.FeatureService>();
        async Task<string> Denied(Func<Task> write) => (await Assert.ThrowsAsync<Settings.ValueAccessDeniedException>(write)).Code;
        const Settings.Abstractions.ValueSource TenantScope = Settings.Abstractions.ValueSource.Tenant;
        const Settings.Abstractions.ValueSource UserScope = Settings.Abstractions.ValueSource.User;

        // A member reads effective values freely, but changes no override without the permission for that scope.
        Assert.Equal(20L, (await settings.ResolveAsync(unit.Context, Items.PageSize, Cancellation)).Get<long>());
        Assert.Equal(AuthorizationCodes.NotHeld, await Denied(() => settings.SetAsync(unit.Context, unit.Principal, Items.PageSize, TenantScope, "30", null, Cancellation)));
        Assert.Equal(AuthorizationCodes.NotHeld, await Denied(() => settings.SetAsync(unit.Context, unit.Principal, Items.PageSize, UserScope, "30", null, Cancellation)));
        Assert.Equal(AuthorizationCodes.NotHeld, await Denied(() => features.SetAsync(unit.Context, unit.Principal, Items.ExportEnabled, TenantScope, true, null, Cancellation)));
        Assert.Equal(AuthorizationCodes.Unauthenticated, await Denied(() => settings.SetAsync(unit.Context, null, Items.PageSize, TenantScope, "30", null, Cancellation)));

        await administration.GrantPermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, Settings.SettingsModule.TenantOverridesPermission, Cancellation);
        var version = await settings.SetAsync(unit.Context, unit.Principal, Items.PageSize, TenantScope, "30", null, Cancellation);
        await features.SetAsync(unit.Context, unit.Principal, Items.ExportEnabled, TenantScope, true, null, Cancellation);
        Assert.Equal(AuthorizationCodes.NotHeld, await Denied(() => settings.SetAsync(unit.Context, unit.Principal, Items.PageSize, UserScope, "40", null, Cancellation)));
        await administration.GrantPermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, Settings.SettingsModule.UserOverridesPermission, Cancellation);
        await settings.SetAsync(unit.Context, unit.Principal, Items.PageSize, UserScope, "40", null, Cancellation);
        Assert.Equal(40L, (await settings.ResolveAsync(unit.Context, Items.PageSize, Cancellation)).Get<long>());

        // Management is decided now, at the current profile: a revoked permission refuses the next reset at once.
        await administration.RevokePermissionAsync(tenant.OwnerUnit.Context, tenant.OwnerUnit.Principal, user.Actor, Settings.SettingsModule.TenantOverridesPermission, Cancellation);
        Assert.Equal(AuthorizationCodes.NotHeld, await Denied(() => settings.ResetAsync(unit.Context, unit.Principal, Items.PageSize, TenantScope, version, Cancellation)));

        // A user override is the acting user's own, so none is written under a delegation, whatever its scope.
        var delegation = ExecutionPrincipal.ForDelegation(new PrincipalReference(support.Id.ToString("D"), IdentityKind.User), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new PrincipalReference(user.Id.ToString("D"), IdentityKind.User), new DelegatedAuthority(Guid.NewGuid(), [Settings.SettingsModule.UserOverridesPermission],
                "Adjust the member's preferences", tenant.Id, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10)));
        await using var delegated = await host.EnterAsync(support, tenant.Id, delegation);
        Assert.Equal(AuthorizationCodes.OutsideCeiling, await Denied(() => settings.SetAsync(delegated.Context, delegated.Principal, Items.PageSize, UserScope, "50", null,
            Cancellation)));
    }

    private sealed class AllowingGate : Features.IFeatureGate
    {
        public ValueTask<bool> AllowsAsync(TenantContext context, Features.Abstractions.FeatureDefinition feature, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }
}
