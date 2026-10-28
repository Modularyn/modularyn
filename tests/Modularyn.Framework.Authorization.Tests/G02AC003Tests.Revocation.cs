using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Modules.Identity.Application.Contracts;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// AC-003-B01: a revoked bounded permission stops at the declared freshness bound B on every entry point, in a process
/// that never heard of the change, and at once in the one that made it; a security-sensitive export rechecks the current
/// permission and session before each step and stops at the first check after either is revoked, leaving no result.
/// </summary>
public sealed partial class G02AC003Tests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_B01_RevocationMeetsFreshnessAndStopsSensitiveExports_Integration()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        // Two processes over one database: the reader's cache never hears of the revoker's changes.
        await using var reader = AuthorizationHost.Create(database, clock);
        await using var revoker = AuthorizationHost.Create(database, clock);
        var tenant = await revoker.TenantAsync("Tenant A");
        var admin = await revoker.UserAsync("admin-1", tenant);
        var user = await revoker.UserAsync("user-1", tenant);
        await revoker.OwnerAsync(admin, tenant);
        await using var owner = await revoker.EnterAsync(admin, tenant);
        var administration = revoker.Get<AuthorityAdministration>();
        await administration.GrantPermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Read, Cancellation);
        await administration.GrantPermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Export, Cancellation);
        await ItemWork.AddAsync(database.ItemsModule, owner.Context, user.Id.ToString("D"), "item-1", "item-2", "item-3", "item-4", "item-5");
        output.WriteLine($"Declared freshness bound B = {AuthorizationHost.Bound}; {Items.Export} is security-sensitive (current profile), {Items.Read} bounded.");

        await using var unit = await reader.EnterAsync(user, tenant);

        // The export rechecks before each chunk. Revoking item.export after the first chunk stops it at the next check.
        var revokedExport = await ItemWork.ExportAsync(reader.Services, database.ItemsModule, unit.Context, unit.Principal, async chunk =>
        {
            if (chunk == 0) await administration.RevokePermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Export, Cancellation);
        }, Cancellation);
        Assert.Equal(("denied", AuthorizationCodes.NotHeld), (revokedExport.State, revokedExport.Disposition));
        Assert.Equal(0L, await ExportsAsync(database, revokedExport.Id));

        // Repeated with the user's sessions revoked instead: the recheck of the session stops it, again with nothing to download.
        await administration.GrantPermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Export, Cancellation);
        var sessions = revoker.Get<ISessionService>();
        var endedExport = await ItemWork.ExportAsync(reader.Services, database.ItemsModule, unit.Context, unit.Principal, async chunk =>
        {
            if (chunk == 0) Assert.True(await sessions.RevokeAllAsync(IdentityAuthority.Host(await revoker.HostAsync(IdentityHostOperations.SessionsId)), user.Id, Cancellation) >= 1);
        }, Cancellation);
        Assert.Equal(("denied", AuthorizationCodes.PrincipalNotCurrent), (endedExport.State, endedExport.Disposition));
        Assert.Equal(0L, await ExportsAsync(database, endedExport.Id));

        // With both current, an export completes and only then can be downloaded.
        await using var fresh = await reader.EnterAsync(user, tenant);
        var completed = await ItemWork.ExportAsync(reader.Services, database.ItemsModule, fresh.Context, fresh.Principal, null, Cancellation);
        Assert.Equal("completed", completed.State);
        Assert.Equal(1L, await ExportsAsync(database, completed.Id));
        output.WriteLine($"Exports: after the permission's revocation {revokedExport.State} {revokedExport.Disposition}; after the session's "
            + $"{endedExport.State} {endedExport.Disposition}; neither left a result; with both current {completed.State}.");

        // A warm cache entry in the reader, built at t0, once every earlier entry has expired.
        clock.Advance(AuthorizationHost.Bound + TimeSpan.FromSeconds(1));
        var key = reader.Get<TenantCache>().KeyFor(fresh.Context, TenantAuthorityCache.Entry(user.Actor, AuthorizationHost.Bound));
        var t0 = clock.Now;
        Assert.Equal((true, AuthorizationSource.Store), Outcome(await reader.AuthorizeAsync(fresh, Items.Read)));
        Assert.Equal((true, AuthorizationSource.Cache), Outcome(await reader.AuthorizeAsync(fresh, Items.Read)));
        // The revoking process holds a warm entry of its own.
        await using var revokerUnit = await revoker.EnterAsync(user, tenant);
        Assert.Equal((true, AuthorizationSource.Store), Outcome(await revoker.AuthorizeAsync(revokerUnit, Items.Read)));
        Assert.Equal((true, AuthorizationSource.Cache), Outcome(await revoker.AuthorizeAsync(revokerUnit, Items.Read)));

        // The revocation is authoritative when it commits, at t0; the revoking process invalidates its entry and refuses at once.
        await administration.RevokePermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Read, Cancellation);
        var version = await database.ScalarAsync<Guid>("SELECT \"Version\" FROM " + database.Table(database.AuthorizationSchema, "TenantAuthority")
            + " WHERE \"TenantId\" = @tenant", new NpgsqlParameter("tenant", tenant));
        Assert.Equal((false, AuthorizationCodes.NotHeld, AuthorizationSource.Store), Refusal(await revoker.AuthorizeAsync(revokerUnit, Items.Read)));
        output.WriteLine($"Revocation of {Items.Read} committed at t0 = {t0:O}, authority version {version:D}; cache entry {key}.");

        // At B minus one second the reader may still permit from its entry, read at t0; at B plus one second it reads the store and refuses.
        clock.Set(t0 + AuthorizationHost.Bound - TimeSpan.FromSeconds(1));
        var early = await reader.AuthorizeAsync(fresh, Items.Read);
        Assert.Equal((true, AuthorizationSource.Cache, t0), (early.Permitted, early.Source, early.AuthorityReadAt!.Value));
        clock.Set(t0 + AuthorizationHost.Bound + TimeSpan.FromSeconds(1));
        var late = await reader.AuthorizeAsync(fresh, Items.Read);
        Assert.Equal((false, AuthorizationCodes.NotHeld, AuthorizationSource.Store, version), (late.Permitted, late.Code, late.Source, late.AuthorityVersion!.Value));
        Assert.True(late.DecidedAt - late.AuthorityReadAt!.Value < AuthorizationHost.Bound);
        output.WriteLine($"B-1s: permitted from the entry read at {early.AuthorityReadAt:O}; B+1s: refused {late.Code} from the store at version {late.AuthorityVersion:D}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_B01_RevocationMeetsFreshnessAndStopsSensitiveExports_Isolation()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var revoker = AuthorizationHost.Create(database, clock);
        var tenantA = await revoker.TenantAsync("Tenant A");
        var tenantB = await revoker.TenantAsync("Tenant B");
        var admin = await revoker.UserAsync("admin-1", tenantA, tenantB);
        var user = await revoker.UserAsync("user-1", tenantA, tenantB);
        var administration = revoker.Get<AuthorityAdministration>();
        var owners = new Dictionary<Guid, Unit>();
        foreach (var tenant in new[] { tenantA, tenantB })
        {
            await revoker.OwnerAsync(admin, tenant);
            owners[tenant] = await revoker.EnterAsync(admin, tenant);
            await administration.GrantPermissionAsync(owners[tenant].Context, owners[tenant].Principal, user.Actor, Items.Read, Cancellation);
            await ItemWork.AddAsync(database.ItemsModule, owners[tenant].Context, user.Id.ToString("D"), "item-1");
        }

        await using var server = await AuthorizationServer.StartAsync(database, clock);
        var browser = server.Browser();
        await browser.SignInAsync(user);
        string Item(Guid tenant) => "/t/" + TenantIdentifier.Format(tenant) + "/items/item-1";
        var t0 = clock.Now;
        Assert.Equal(HttpStatusCode.OK, (await browser.CallAsync(HttpMethod.Get, Item(tenantA))).Status);
        Assert.Equal(HttpStatusCode.OK, (await browser.CallAsync(HttpMethod.Get, Item(tenantB))).Status);

        // Revoked in tenant A by another process at t0: the server's entry may serve until B, and no longer.
        await administration.RevokePermissionAsync(owners[tenantA].Context, owners[tenantA].Principal, user.Actor, Items.Read, Cancellation);
        clock.Set(t0 + AuthorizationHost.Bound - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await browser.CallAsync(HttpMethod.Get, Item(tenantA))).Status);
        clock.Set(t0 + AuthorizationHost.Bound + TimeSpan.FromSeconds(1));
        Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.NotHeld), Pair(await browser.CallAsync(HttpMethod.Get, Item(tenantA))));
        Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.NotHeld), Pair(await browser.CallAsync(HttpMethod.Get, "/t/" + TenantIdentifier.Format(tenantA) + "/items")));
        // Tenant B's grant is its own: neither the revocation nor tenant A's entry touches it.
        Assert.Equal(HttpStatusCode.OK, (await browser.CallAsync(HttpMethod.Get, Item(tenantB))).Status);
        output.WriteLine("Over HTTP: tenant A's read permitted at B-1s and refused 403 MAZ005 at B+1s; tenant B's read permitted throughout.");

        // The export over HTTP needs the current permission, which tenant A's member does not hold.
        Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.NotHeld), Pair(await browser.CallAsync(HttpMethod.Post, "/t/" + TenantIdentifier.Format(tenantB) + "/exports")));
        await administration.GrantPermissionAsync(owners[tenantB].Context, owners[tenantB].Principal, user.Actor, Items.Export, Cancellation);
        // A current-profile permission is read from the store, so another process's grant applies at once.
        var exported = await browser.CallAsync(HttpMethod.Post, "/t/" + TenantIdentifier.Format(tenantB) + "/exports");
        Assert.Equal(HttpStatusCode.OK, exported.Status);
        Assert.Equal("completed", exported.Body!.Value.GetProperty("state").GetString());
        foreach (var held in owners.Values) await held.DisposeAsync();
    }

    private static (bool, AuthorizationSource) Outcome(AuthorizationDecision decision) => (decision.Permitted, decision.Source);

    private static (bool, string?, AuthorizationSource) Refusal(AuthorizationDecision decision) => (decision.Permitted, decision.Code, decision.Source);

    private static Task<long> ExportsAsync(AuthorizationDatabase database, Guid operation) => database.ScalarAsync<long>(
        "SELECT count(*) FROM " + database.Table(database.ItemsSchema, "ItemExports") + " WHERE \"Id\" = @id", new NpgsqlParameter("id", operation));
}
