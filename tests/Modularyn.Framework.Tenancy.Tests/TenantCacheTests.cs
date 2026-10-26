using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-033: keys come only from a validated context and a declared entry, so entries that differ in tenant,
// principal, module, schema version, name or parts never collide; staleness is declared and invalidation is immediate.
// TM-G02-034, in part: concurrent misses share one factory call and a failure is not cached. The store is this process's
// memory cache, the single-node profile's provider.
public sealed class TenantCacheTests : IAsyncDisposable
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private readonly MemoryDirectory directory = new();
    private readonly TestSeams seams = new();
    private readonly ServiceProvider services;
    private readonly List<AsyncServiceScope> scopes = [];
    private readonly Guid tenantA;
    private readonly Guid tenantB;

    public TenantCacheTests()
    {
        tenantA = directory.Add("Tenant A");
        tenantB = directory.Add("Tenant B");
        foreach (var subject in new[] { "user-a", "admin-a" }) seams.Membership.Add(subject, tenantA);
        seams.Membership.Add("user-b", tenantB);
        seams.Authority.Grant("host-1", "test.host");
        services = UnitServices.Build(directory, seams);
    }

    private TenantCache Cache => services.GetRequiredService<TenantCache>();

    private async Task<TenantContext> EnterAsync(string subject, Guid tenant)
    {
        var (scope, context) = await Units.EnterAsync(services, subject, tenant);
        scopes.Add(scope);
        return context;
    }

    private static TenantCacheEntry Entry(string module = "Items", string schema = "1", string name = "list", bool principal = false, params string[] parts) =>
        new(module, schema, name, TimeSpan.FromMinutes(5), principal, parts);

    [Fact]
    public async Task TM_G02_033_Keys_differ_whenever_one_dimension_differs()
    {
        var a = await EnterAsync("user-a", tenantA);
        var admin = await EnterAsync("admin-a", tenantA);
        var b = await EnterAsync("user-b", tenantB);
        var (hostScope, host) = await Units.EnterHostAsync(services, "host-1", "test.host");
        scopes.Add(hostScope);
        var keys = new[]
        {
            Cache.KeyFor(a, Entry()), Cache.KeyFor(b, Entry()), Cache.KeyFor(host, Entry()),
            Cache.KeyFor(a, Entry(principal: true)), Cache.KeyFor(admin, Entry(principal: true)),
            Cache.KeyFor(a, Entry(module: "Files")), Cache.KeyFor(a, Entry(schema: "2")), Cache.KeyFor(a, Entry(name: "count")),
            Cache.KeyFor(a, Entry(parts: "page-1")), Cache.KeyFor(a, Entry(parts: "page-2")),
            // Length prefixes keep part boundaries: none of these collide.
            Cache.KeyFor(a, Entry(parts: ["a|b"])), Cache.KeyFor(a, Entry(parts: ["a", "b"])), Cache.KeyFor(a, Entry(parts: ["ab", ""])),
            Cache.KeyFor(a, Entry(parts: "|part=1:x")), Cache.KeyFor(a, Entry(name: "list|part=1:x"))
        };
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        // Entries without the principal dimension are shared within the tenant, by declaration.
        Assert.Equal(Cache.KeyFor(a, Entry()), Cache.KeyFor(admin, Entry()));
        foreach (var key in keys) TestContext.Current.TestOutputHelper?.WriteLine(key);
    }

    [Fact]
    public async Task TM_G02_033_An_entry_built_in_one_context_is_never_served_to_another()
    {
        var a = await EnterAsync("user-a", tenantA);
        var admin = await EnterAsync("admin-a", tenantA);
        var b = await EnterAsync("user-b", tenantB);
        foreach (var (entry, contexts) in new[] { (Entry(), new[] { a, b }), (Entry(principal: true), new[] { a, admin, b }) })
        {
            foreach (var context in contexts)
                Assert.Equal(Owner(context), await Cache.GetOrCreateAsync(context, entry, _ => Task.FromResult(Owner(context)), Cancellation));
            foreach (var context in contexts)
                Assert.Equal(Owner(context), await Cache.GetOrCreateAsync<string>(context, entry, _ => throw new InvalidOperationException("Expected a hit."), Cancellation));
        }
        // The non-principal entry was built once per tenant, so admin-a reads user-a's tenant-wide value.
        Assert.Equal(Owner(a), await Cache.GetOrCreateAsync<string>(admin, Entry(), _ => throw new InvalidOperationException("Expected a hit."), Cancellation));

        static string Owner(TenantContext context) => context.TenantId + "/" + context.Actor.Subject;
    }

    [Fact]
    public async Task TM_G02_033_Staleness_is_declared_bounded_and_enforced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantCacheEntry("Items", "1", "list", TimeSpan.Zero, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantCacheEntry("Items", "1", "list", TimeSpan.FromDays(2), false));
        Assert.Throws<ArgumentException>(() => new TenantCacheEntry("Items", " 1", "list", TimeSpan.FromMinutes(1), false));
        var a = await EnterAsync("user-a", tenantA);
        var entry = new TenantCacheEntry("Items", "1", "short", TimeSpan.FromMilliseconds(200), false);
        var builds = 0;
        Task<int> Build(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref builds));
        Assert.Equal(1, await Cache.GetOrCreateAsync(a, entry, Build, Cancellation));
        Assert.Equal(1, await Cache.GetOrCreateAsync(a, entry, Build, Cancellation));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await Cache.GetOrCreateAsync(a, entry, Build, Cancellation) == 1 && DateTime.UtcNow < deadline)
            await Task.Delay(100, Cancellation);
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task TM_G02_033_Invalidation_reaches_exactly_the_tenant_or_principal_it_names()
    {
        var a = await EnterAsync("user-a", tenantA);
        var admin = await EnterAsync("admin-a", tenantA);
        var b = await EnterAsync("user-b", tenantB);
        var builds = new Dictionary<string, int>();
        Task<string> Build(TenantContext context, TenantCacheEntry entry)
        {
            var key = context.Actor.Subject + ":" + entry.PrincipalScoped;
            builds[key] = builds.GetValueOrDefault(key) + 1;
            return Task.FromResult(key);
        }
        async Task WarmAsync()
        {
            foreach (var context in new[] { a, admin, b })
            foreach (var entry in new[] { Entry(name: "grants", principal: true) })
                await Cache.GetOrCreateAsync(context, entry, _ => Build(context, entry), Cancellation);
            foreach (var context in new[] { a, b })
                await Cache.GetOrCreateAsync(context, Entry(), _ => Build(context, Entry()), Cancellation);
        }
        await WarmAsync();
        await WarmAsync();
        Assert.All(builds.Values, count => Assert.Equal(1, count));
        Cache.InvalidatePrincipal(tenantA, admin.Actor);
        await WarmAsync();
        Assert.Equal(2, builds["admin-a:True"]);
        Assert.Equal(1, builds["user-a:True"]);
        Assert.Equal(1, builds["user-a:False"]);
        Cache.InvalidateTenant(tenantA);
        await WarmAsync();
        Assert.Equal(3, builds["admin-a:True"]);
        Assert.Equal(2, builds["user-a:True"]);
        Assert.Equal(2, builds["user-a:False"]);
        Assert.Equal(1, builds["user-b:True"]);
        Assert.Equal(1, builds["user-b:False"]);
    }

    [Fact]
    public async Task TM_G02_033_A_context_whose_unit_ended_cannot_read_or_build_entries()
    {
        var (scope, a) = await Units.EnterAsync(services, "user-a", tenantA);
        Assert.Equal("value", await Cache.GetOrCreateAsync(a, Entry(), _ => Task.FromResult("value"), Cancellation));
        await scope.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => Cache.KeyFor(a, Entry()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cache.GetOrCreateAsync(a, Entry(), _ => Task.FromResult("value"), Cancellation));
    }

    [Fact]
    public async Task TM_G02_034_Concurrent_misses_share_one_factory_call_and_failures_are_not_cached()
    {
        var a = await EnterAsync("user-a", tenantA);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var entry = Entry(name: "expensive");
        async Task<string> Expensive(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return "built once";
        }
        var first = Task.Run(() => Cache.GetOrCreateAsync(a, entry, Expensive, Cancellation), Cancellation);
        while (Volatile.Read(ref calls) == 0) await Task.Delay(10, Cancellation);
        var waiters = Enumerable.Range(0, 15).Select(_ => Task.Run(() => Cache.GetOrCreateAsync(a, entry, Expensive, Cancellation), Cancellation))
            .Prepend(first).ToArray();
        // A waiter that gives up does not cancel the shared call for the others.
        using var impatient = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var cancelled = Cache.GetOrCreateAsync(a, entry, _ => Task.FromResult("never"), impatient.Token);
        await Task.Delay(100, Cancellation);
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.SetResult();
        Assert.All(await Task.WhenAll(waiters), value => Assert.Equal("built once", value));
        Assert.Equal(1, calls);

        var failing = Entry(name: "failing");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cache.GetOrCreateAsync<string>(a, failing, _ => throw new InvalidOperationException("Dependency unavailable."), Cancellation));
        Assert.Equal("recovered", await Cache.GetOrCreateAsync(a, failing, _ => Task.FromResult("recovered"), Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Cache.GetOrCreateAsync(a, failing, _ => Task.FromResult(1), Cancellation));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes) await scope.DisposeAsync();
        await services.DisposeAsync();
    }
}
