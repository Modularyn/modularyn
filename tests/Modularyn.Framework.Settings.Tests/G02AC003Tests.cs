using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Caching;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

[Collection(SettingsPostgreSqlCollection.Name)]
public sealed partial class G02AC003Tests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC_003_P02_PrecedenceAndInvalidationKeepTenantValuesSeparate_Unit()
    {
        // Exhaust every combination of absent scopes, including the empty text override.
        for (var mask = 0; mask < 8; mask++)
        {
            var user = (mask & 4) != 0 ? "" : null;
            var tenant = (mask & 2) != 0 ? "tenant" : null;
            var global = (mask & 1) != 0 ? "global" : null;
            var resolved = ValuePrecedence.Resolve(user, tenant, global, "default");
            Assert.Equal(user ?? tenant ?? global ?? "default", resolved.Value);
            Assert.Equal(user is not null ? ValueSource.User : tenant is not null ? ValueSource.Tenant
                : global is not null ? ValueSource.Global : ValueSource.Default, resolved.Source);
        }
        Assert.Equal(new[] { ValueSource.User, ValueSource.Tenant, ValueSource.Global, ValueSource.Default }, ValuePrecedence.HighestFirst);

        // Unit scaffolding checks key and invalidation rules; the companion integration case supplies provider evidence.
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        var a = directory.Add("Tenant A");
        var b = directory.Add("Tenant B");
        foreach (var tenant in new[] { a, b })
        foreach (var user in new[] { "same-user", "other-user" }) seams.Membership.Add(user, tenant);
        var clock = new ManualTimeProvider();
        await using var services = UnitServices.Build(directory, seams, services => services.AddSingleton<TimeProvider>(clock));
        var (scopeA, tenantA) = await Units.EnterAsync(services, "same-user", a);
        await using var heldA = scopeA;
        var (scopeB, tenantB) = await Units.EnterAsync(services, "same-user", b);
        await using var heldB = scopeB;
        var (scopeOther, other) = await Units.EnterAsync(services, "other-user", a);
        await using var heldOther = scopeOther;
        var cache = new ScopedValueCache(services.GetRequiredService<TenantCache>());
        var calls = 0;
        Task<int> Read(TenantContext context, string module = "Preferences") => cache.GetOrCreateAsync(context, module, "1", "setting.Title",
            TestDefinitions.Staleness, _ => Task.FromResult(++calls), Cancellation);

        Assert.Equal(1, await Read(tenantA));
        Assert.Equal(2, await Read(tenantB));
        Assert.Equal(3, await Read(other));
        Assert.Equal(1, await Read(tenantA));
        cache.InvalidateUser(a, tenantA.Actor);
        Assert.Equal(4, await Read(tenantA));
        Assert.Equal(2, await Read(tenantB));
        Assert.Equal(3, await Read(other));
        cache.InvalidateTenant(a);
        Assert.Equal(5, await Read(tenantA));
        Assert.Equal(6, await Read(other));
        Assert.Equal(2, await Read(tenantB));
        Assert.Equal(7, await Read(tenantA, "OtherModule"));
        cache.InvalidateGlobal("Preferences");
        Assert.Equal(8, await Read(tenantA));
        Assert.Equal(9, await Read(tenantB));
        Assert.Equal(7, await Read(tenantA, "OtherModule"));
        clock.Advance(TestDefinitions.Staleness - TimeSpan.FromTicks(1));
        Assert.Equal(8, await Read(tenantA));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(10, await Read(tenantA));
    }

    [Fact]
    public async Task A_global_invalidation_during_a_cache_fill_cannot_repopulate_the_new_generation()
    {
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        var tenant = directory.Add("Tenant");
        seams.Membership.Add("user", tenant);
        await using var services = UnitServices.Build(directory, seams);
        var (scope, context) = await Units.EnterAsync(services, "user", tenant);
        await using var held = scope;
        var cache = new ScopedValueCache(services.GetRequiredService<TenantCache>());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<string> Old(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            await release.Task.WaitAsync(token);
            return "before";
        }
        Task<string> Read(Func<CancellationToken, Task<string>> factory) => cache.GetOrCreateAsync(context, "Preferences", "1", "setting.Title",
            TestDefinitions.Staleness, factory, Cancellation);
        var first = Read(Old);
        await started.Task.WaitAsync(Cancellation);
        var shared = Read(Old);
        cache.InvalidateGlobal("Preferences");
        Assert.Equal("after", await Read(_ => Task.FromResult("after")));
        release.SetResult();
        Assert.Equal("before", await first);
        Assert.Equal("before", await shared);
        Assert.Equal(1, calls);
        Assert.Equal("after", await Read(_ => throw new InvalidOperationException("Expected the new generation.")));
    }
}
