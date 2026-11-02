using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Features;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// AC-004-B02, SEC-08's: cache entries built in pairs that differ only in tenant, principal, module or schema version are
/// never served across the pair; business key code-1, created in tenant-A, again in tenant-A and once in tenant-B, conflicts
/// only within tenant-A and reveals nothing of tenant-B; and bootstrap configuration, which holds a value client-visible to
/// tenant-A's administrator alone, gives each principal only what is approved for it and its tenant.
/// </summary>
public sealed partial class G02AC004Tests
{
    private const string AdministratorBanner = "Only for ta-admin";

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_B02_CacheAndUniqueKeysKeepEveryScopeDimension_Isolation()
    {
        await using var database = await AdversarialDatabase.CreateAsync();
        await using var host = AdversarialHost.Create(database);
        var seed = await AdversarialTenants.SeedAsync(host);
        await ValuesAsync(host, seed);
        var userA2 = await host.AccountAsync("user-a2", seed.TenantA);
        await using var a = await host.EnterAsync(seed.UserA, seed.TenantA);
        await using var a2 = await host.EnterAsync(userA2, seed.TenantA);
        await using var b = await host.EnterAsync(seed.UserB, seed.TenantB);
        var cache = host.Get<TenantCache>();

        // 1. Each pair differs in one dimension; the second of each pair is built, never served the first one's entry.
        var entry = new TenantCacheEntry(AdversarialItems.ModuleId, "1", "item-codes", TimeSpan.FromMinutes(5), principalScoped: true);
        var pairs = new (string Dimension, AdversarialUnit First, TenantCacheEntry FirstEntry, AdversarialUnit Second, TenantCacheEntry SecondEntry)[]
        {
            ("tenant", a, entry, b, entry),
            ("principal", a, entry, a2, entry),
            ("module", a, entry, a, new(AdversarialItems.ModuleId + ".Reports", "1", "item-codes", TimeSpan.FromMinutes(5), principalScoped: true)),
            ("schema", a, entry, a, new(AdversarialItems.ModuleId, "2", "item-codes", TimeSpan.FromMinutes(5), principalScoped: true))
        };
        foreach (var (dimension, first, firstEntry, second, secondEntry) in pairs)
        {
            var firstKey = cache.KeyFor(first.Context, firstEntry);
            var built = await cache.GetOrCreateAsync(first.Context, firstEntry, _ => Task.FromResult(dimension + ":first"), Cancellation);
            var secondKey = cache.KeyFor(second.Context, secondEntry);
            var miss = false;
            var served = await cache.GetOrCreateAsync(second.Context, secondEntry, _ =>
            {
                miss = true;
                return Task.FromResult(dimension + ":second");
            }, Cancellation);
            Assert.NotEqual(firstKey, secondKey);
            Assert.True(miss, dimension);
            Assert.Equal(dimension + ":second", served);
            // The first entry is still its own context's, and a hit there.
            Assert.Equal(built, await cache.GetOrCreateAsync(first.Context, firstEntry, _ => Task.FromResult("rebuilt"), Cancellation));
            Log($"Pair by {dimension}: {firstKey} then {secondKey}: miss, built its own.");
        }

        // The settings cache is keyed the same way: each principal resolves its own value after warming the others'.
        var settings = host.Get<SettingsService>();
        await using var admin = await host.EnterAsync(seed.TaAdmin, seed.TenantA);
        var resolved = new List<string>();
        foreach (var unit in new[] { admin, a, b, admin, a, b })
            resolved.Add((await settings.ResolveAsync(unit.Context, AdversarialItems.Banner, Cancellation)).Get<string>());
        Assert.Equal([AdministratorBanner, "Banner of tenant A", "Banner of tenant B", AdministratorBanner, "Banner of tenant A", "Banner of tenant B"], resolved);

        // 2. code-1 exists in neither tenant; created in tenant-A it is unique there, created again in tenant-A it conflicts,
        //    naming tenant-A's key alone, and created in tenant-B it succeeds.
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("items", "Items") + " WHERE \"Code\" = 'code-1'"));
        await AdversarialTenants.AddItemAsync(host, a, "code-1", "Tenant A's code-1");
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation))
        {
            work.Context.Add(new Item { Id = Guid.NewGuid(), Code = "code-1", Name = "Tenant A's second code-1" });
            var conflict = Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() => work.FlushAsync(Cancellation))).InnerException);
            Assert.Equal((PostgresErrorCodes.UniqueViolation, "IX_Items_TenantId_Code"), (conflict.SqlState, conflict.ConstraintName));
            Assert.DoesNotContain(seed.TenantB.ToString("D"), conflict.Message + conflict.Detail, StringComparison.OrdinalIgnoreCase);
        }
        await AdversarialTenants.AddItemAsync(host, b, "code-1", "Tenant B's code-1");
        Assert.Equal([(seed.TenantA, "code-1"), (seed.TenantB, "code-1")], (await database.RowsAsync("SELECT \"TenantId\", \"Code\" FROM "
            + database.Table("items", "Items") + " WHERE \"Code\" = 'code-1' ORDER BY 1")).Select(row => (Guid.Parse(row[0]), row[1])).OrderBy(row => row.Item1 == seed.TenantA ? 0 : 1));
        // Every key, unique constraint and index of a tenant table leads with the tenant, so no business key spans tenants.
        Assert.All(await database.RowsAsync("SELECT indexdef FROM pg_indexes WHERE schemaname = @schema AND left(tablename, 2) <> '__' AND tablename <> 'Lookups'",
            new NpgsqlParameter("schema", database.Schemas["items"])), index => Assert.Contains("\"TenantId\"", index[0], StringComparison.Ordinal));

        // 3. Bootstrap configuration as tenant-A's administrator, user-a and user-b, each after warming the others' entries:
        //    the administrator's own value, the tenant's, and nothing hidden or of the other tenant.
        var bootstrap = new List<string>();
        foreach (var unit in new[] { b, a, admin, admin, a, b })
        {
            var values = await new BootstrapReader(host.Get<ValueDefinitionRegistry>(), settings, new FeatureService(settings, new EntitledGate()))
                .ReadAsync(unit.Context, Cancellation);
            bootstrap.Add(JsonSerializer.Serialize(values, JsonSerializerOptions.Web));
        }
        Assert.Equal(Enumerable.Reverse(bootstrap[..3]), bootstrap[3..]);
        Assert.Equal(Bootstrap(AdministratorBanner, true), bootstrap[2]);
        Assert.Equal(Bootstrap("Banner of tenant A", true), bootstrap[1]);
        Assert.Equal(Bootstrap("Banner of tenant B", false), bootstrap[0]);
        Assert.All(bootstrap, payload => Assert.DoesNotContain(AdversarialItems.Hidden.Name, payload, StringComparison.Ordinal));
        Log($"Bootstrap for ta-admin: {bootstrap[2]}; user-a: {bootstrap[1]}; user-b: {bootstrap[0]}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_B02_CacheAndUniqueKeysKeepEveryScopeDimension_Integration()
    {
        await using var database = await AdversarialDatabase.CreateAsync();
        AdversarialTenants seed;
        Account userA2;
        await using (var setup = AdversarialHost.Create(database))
        {
            seed = await AdversarialTenants.SeedAsync(setup);
            await ValuesAsync(setup, seed);
            userA2 = await setup.AccountAsync("user-a2", seed.TenantA);
            await using var admin = await setup.EnterAsync(seed.TaAdmin, seed.TenantA);
            await setup.GrantAsync(admin, userA2, AdversarialItems.Read);
        }
        await using var server = await AdversarialServer.StartAsync(database);
        var administrator = server.NewClient();
        await administrator.SignInAsync(seed.TaAdmin);
        var a = server.NewClient();
        await a.SignInAsync(seed.UserA);
        var a2 = server.NewClient();
        await a2.SignInAsync(userA2);
        var b = server.NewClient(AdversarialHost.HostB);
        await b.SignInAsync(seed.UserB);

        // 1. Each request warms its own entry after the other of its pair; the key and the hit or miss of each are recorded.
        async Task<CachedCodes> CachedAsync(AdversarialClient client, Guid tenant, string query)
        {
            var reply = await client.SendAsync(HttpMethod.Get, Path(tenant, "/cached/items" + query));
            Assert.Equal(HttpStatusCode.OK, reply.Status);
            return JsonSerializer.Deserialize<CachedCodes>(reply.Text, JsonSerializerOptions.Web)!;
        }
        var requests = new (string Name, AdversarialClient Client, Guid Tenant, string Query)[]
        {
            ("user-a, tenant-A", a, seed.TenantA, "?mine=true"), ("user-b, tenant-B", b, seed.TenantB, "?mine=true"),
            ("user-a2, tenant-A", a2, seed.TenantA, "?mine=true"), ("user-a, schema 2", a, seed.TenantA, "?mine=true&version=2"),
            ("user-a, tenant-wide", a, seed.TenantA, "")
        };
        var first = new List<CachedCodes>();
        foreach (var (name, client, tenant, query) in requests)
        {
            var cached = await CachedAsync(client, tenant, query);
            Assert.True(cached.Built, name);
            first.Add(cached);
            Log($"{name}: key {cached.Key}, miss, {string.Join(",", cached.Codes)}.");
        }
        Assert.Equal(first.Count, first.Select(cached => cached.Key).Distinct().Count());
        for (var index = 0; index < requests.Length; index++)
        {
            var again = await CachedAsync(requests[index].Client, requests[index].Tenant, requests[index].Query);
            Assert.Equal((first[index].Key, false), (again.Key, again.Built));
            Assert.Equal(first[index].Codes, again.Codes);
        }
        Assert.Contains("for:" + seed.UserA.Id.ToString("D"), first[0].Codes);
        Assert.DoesNotContain(seed.ItemA1.Code, first[1].Codes);
        Assert.Contains("for:" + userA2.Id.ToString("D"), first[2].Codes);
        // A tenant-wide entry is shared by tenant-A's members, as declared, and still never by tenant-B's.
        var shared = await CachedAsync(a2, seed.TenantA, "");
        Assert.Equal((first[4].Key, false), (shared.Key, shared.Built));

        // 2. code-1 in tenant-A, again in tenant-A, and in tenant-B.
        var created = await a.SendAsync(HttpMethod.Post, Path(seed.TenantA, "/items"), new { code = "code-1", name = "Tenant A's code-1" });
        var duplicate = await a.SendAsync(HttpMethod.Post, Path(seed.TenantA, "/items"), new { code = "code-1", name = "Tenant A's second code-1" });
        var other = await b.SendAsync(HttpMethod.Post, Path(seed.TenantB, "/items"), new { code = "code-1", name = "Tenant B's code-1" });
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Assert.Equal((HttpStatusCode.Conflict, ApiCodes.Conflict), (duplicate.Status, duplicate.Code));
        Assert.DoesNotContain(seed.TenantB.ToString("D"), duplicate.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code-1", duplicate.Text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, other.Status);
        Log($"code-1: tenant-A {created.Status}, again {duplicate.Contract}, tenant-B {other.Status}.");

        // 3. Bootstrap configuration for each principal after warming the others'.
        async Task<string> BootstrapAsync(AdversarialClient client, Guid tenant)
        {
            var reply = await client.SendAsync(HttpMethod.Get, "/api/v2/t/" + TenantIdentifier.Format(tenant) + "/configuration");
            Assert.Equal(HttpStatusCode.OK, reply.Status);
            Assert.Equal("no-store", reply.Header("Cache-Control"));
            return reply.Text;
        }
        var sequence = new List<string>();
        foreach (var (client, tenant) in new[] { (b, seed.TenantB), (a, seed.TenantA), (administrator, seed.TenantA), (administrator, seed.TenantA),
            (a, seed.TenantA), (b, seed.TenantB) })
            sequence.Add(await BootstrapAsync(client, tenant));
        Assert.Equal(Bootstrap("Banner of tenant B", false), sequence[0]);
        Assert.Equal(Bootstrap("Banner of tenant A", true), sequence[1]);
        Assert.Equal(Bootstrap(AdministratorBanner, true), sequence[2]);
        Assert.Equal(Enumerable.Reverse(sequence[..3]), sequence[3..]);
        // user-b asking for tenant-A's configuration is no member there.
        var foreign = await b.SendAsync(HttpMethod.Get, "/api/v2/t/" + TenantIdentifier.Format(seed.TenantA) + "/configuration");
        Assert.Equal((HttpStatusCode.NotFound, ApiCodes.NotFound), (foreign.Status, foreign.Code));
        Log($"Bootstrap payloads: ta-admin {sequence[2]}; user-a {sequence[1]}; user-b {sequence[0]}.");
    }

    /// <summary>
    /// The values the case prepares: each tenant's banner, set by its administrator, a user override of tenant-A's banner that
    /// only ta-admin sees, tenant-A's preview feature, and a hidden value that is never client-visible.
    /// </summary>
    private static async Task ValuesAsync(AdversarialHost host, AdversarialTenants seed)
    {
        var settings = host.Get<SettingsService>();
        await using (var admin = await host.EnterAsync(seed.TaAdmin, seed.TenantA))
        {
            await settings.SetAsync(admin.Context, admin.Principal, AdversarialItems.Banner, ValueSource.Tenant, "Banner of tenant A", cancellationToken: Cancellation);
            await settings.SetAsync(admin.Context, admin.Principal, AdversarialItems.Banner, ValueSource.User, AdministratorBanner, cancellationToken: Cancellation);
            await settings.SetAsync(admin.Context, admin.Principal, AdversarialItems.Hidden, ValueSource.Tenant, "tenant-a internal", cancellationToken: Cancellation);
            await new FeatureService(settings, new EntitledGate()).SetAsync(admin.Context, admin.Principal, AdversarialItems.Preview, ValueSource.Tenant, true,
                cancellationToken: Cancellation);
        }
        await using (var admin = await host.EnterAsync(seed.OwnerB, seed.TenantB))
        {
            await settings.SetAsync(admin.Context, admin.Principal, AdversarialItems.Banner, ValueSource.Tenant, "Banner of tenant B", cancellationToken: Cancellation);
            await settings.SetAsync(admin.Context, admin.Principal, AdversarialItems.Hidden, ValueSource.Tenant, "tenant-b internal", cancellationToken: Cancellation);
        }
    }

    private static string Bootstrap(string banner, bool preview) => JsonSerializer.Serialize(new BootstrapValues(
        new Dictionary<string, object?> { [AdversarialItems.ModuleId + ":" + AdversarialItems.Banner.Name] = banner },
        new Dictionary<string, object?> { [AdversarialItems.ModuleId + ":" + AdversarialItems.Preview.Name] = preview }), JsonSerializerOptions.Web);
}
