using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

public sealed class CacheLifetimeTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fill_time_counts_toward_staleness_and_expired_results_are_never_cached()
    {
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        var tenant = directory.Add("Tenant");
        seams.Membership.Add("user", tenant);
        var clock = new ManualTimeProvider();
        await using var services = UnitServices.Build(directory, seams, services => services.AddSingleton<TimeProvider>(clock));
        var (scope, context) = await Units.EnterAsync(services, "user", tenant);
        await using var held = scope;
        var cache = services.GetRequiredService<TenantCache>();
        var entry = new TenantCacheEntry("Preferences", "1", "slow", TimeSpan.FromSeconds(10), true);
        await Assert.ThrowsAsync<TenantCacheStaleException>(() => cache.GetOrCreateAsync(context, entry, _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            return Task.FromResult("expired");
        }, Cancellation));
        Assert.Equal("fresh", await cache.GetOrCreateAsync(context, entry, _ => Task.FromResult("fresh"), Cancellation));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("fresh", await cache.GetOrCreateAsync<string>(context, entry, _ => throw new InvalidOperationException("Expected a cache hit."), Cancellation));
    }

    [Fact]
    public async Task Cancellation_is_per_waiter_and_ending_a_context_prevents_delivery()
    {
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        var tenant = directory.Add("Tenant");
        seams.Membership.Add("user", tenant);
        await using var services = UnitServices.Build(directory, seams);
        var (firstScope, firstContext) = await Units.EnterAsync(services, "user", tenant);
        await using var firstHeld = firstScope;
        var (secondScope, secondContext) = await Units.EnterAsync(services, "user", tenant);
        await using var secondHeld = secondScope;
        var cache = services.GetRequiredService<TenantCache>();
        var entry = new TenantCacheEntry("Preferences", "1", "shared", TestDefinitions.Staleness, true);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var first = cache.GetOrCreateAsync(firstContext, entry, _ => release.Task, cancelled.Token);
        var second = cache.GetOrCreateAsync<string>(secondContext, entry, _ => throw new InvalidOperationException("The fill must be shared."), Cancellation);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        firstScope.ServiceProvider.GetRequiredService<TenantExecution>().End();
        release.SetResult("value");
        Assert.Equal("value", await second);

        var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.GetOrCreateAsync(secondContext, new("Preferences", "1", "ended", TestDefinitions.Staleness, true), _ => late.Task, Cancellation);
        secondScope.ServiceProvider.GetRequiredService<TenantExecution>().End();
        late.SetResult("not delivered");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
    }
}
