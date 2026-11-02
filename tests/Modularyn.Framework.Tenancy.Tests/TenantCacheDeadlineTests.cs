using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-034, in part: the one factory call that concurrent misses share starts on the thread pool with its first caller's
// execution context and runs under the cache's factory deadline. At the deadline its token fires and every waiter is
// released with TenantCacheDeadlineException, whether the factory stops, ignores its token or blocks its thread; the call
// is forgotten, so the next caller starts a new one; and a value that arrives after the deadline is never cached. Time is
// the test's TimerClock, so the deadline passes without any test waiting for it.
public sealed class TenantCacheDeadlineTests : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = new TenantCacheOptions().FactoryDeadline;
    private static readonly AsyncLocal<string?> Ambient = new();
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private readonly TimerClock clock = new();
    private readonly MemoryDirectory directory = new();
    private readonly TestSeams seams = new();
    private readonly ServiceProvider services;
    private readonly List<AsyncServiceScope> scopes = [];
    private readonly Guid tenant;

    public TenantCacheDeadlineTests()
    {
        tenant = directory.Add("Tenant A");
        seams.Membership.Add("user-a", tenant);
        services = UnitServices.Build(directory, seams, collection => collection.AddSingleton<TimeProvider>(clock));
    }

    private TenantCache Cache => services.GetRequiredService<TenantCache>();

    private static TenantCacheEntry Entry(string name) => new("Items", "1", name, TimeSpan.FromMinutes(5), principalScoped: false);

    private async Task<TenantContext> EnterAsync()
    {
        var (scope, context) = await Units.EnterAsync(services, "user-a", tenant);
        scopes.Add(scope);
        return context;
    }

    // A safety net that a passing test never reaches: a waiter the clock should have released fails the test instead of hanging it.
    private static Task<T> Released<T>(Task<T> waiter) => waiter.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);

    [Fact]
    public async Task TM_G02_034_A_stalled_factory_releases_every_waiter_at_the_deadline_and_the_next_call_starts_a_new_one()
    {
        var a = await EnterAsync();
        var entry = Entry("stalled");
        var calls = 0;
        CancellationToken given = default;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Stalled(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            given = token;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        }
        // Every waiter joins the one call, each with a token of its own that never fires.
        var waiters = Enumerable.Range(0, 8).Select(index => Cache.GetOrCreateAsync(a, entry, Stalled, Cancellation)).ToArray();
        await started.Task.WaitAsync(Cancellation);
        Assert.Equal(1, calls);
        clock.Advance(Deadline - TimeSpan.FromTicks(1));
        Assert.False(given.IsCancellationRequested);
        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
        // At the deadline the factory's own token fires, apart from every waiter's, and each waiter receives the fault.
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(given.IsCancellationRequested);
        foreach (var waiter in waiters) await Assert.ThrowsAsync<TenantCacheDeadlineException>(() => Released(waiter));
        Assert.Equal(1, calls);
        // The faulted call was forgotten: the next caller starts a new one, whose value is then cached.
        Assert.Equal("rebuilt", await Cache.GetOrCreateAsync(a, entry, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("rebuilt");
        }, Cancellation));
        Assert.Equal(2, calls);
        Assert.Equal("rebuilt", await Cache.GetOrCreateAsync<string>(a, entry, _ => throw new InvalidOperationException("Expected a hit."), Cancellation));
    }

    [Fact]
    public async Task TM_G02_034_A_factory_that_ignores_its_token_still_releases_its_waiters_at_the_deadline()
    {
        var a = await EnterAsync();
        var entry = Entry("deaf");
        var never = new TaskCompletionSource<string>();
        var waiters = Enumerable.Range(0, 4).Select(index => Cache.GetOrCreateAsync(a, entry, _ => never.Task, Cancellation)).ToArray();
        clock.Advance(Deadline);
        foreach (var waiter in waiters) await Assert.ThrowsAsync<TenantCacheDeadlineException>(() => Released(waiter));
        // The call was forgotten though it still runs, so the next caller does not wait on it, and its eventual value is discarded.
        Assert.Equal("next", await Released(Cache.GetOrCreateAsync(a, entry, _ => Task.FromResult("next"), Cancellation)));
        never.SetResult("abandoned");
        Assert.Equal("next", await Cache.GetOrCreateAsync<string>(a, entry, _ => throw new InvalidOperationException("Expected a hit."), Cancellation));
    }

    [Fact]
    public async Task TM_G02_034_A_factory_that_blocks_its_thread_still_releases_every_waiter_at_the_deadline()
    {
        var a = await EnterAsync();
        var entry = Entry("blocking");
        using var gate = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<string> Blocking(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            // It blocks its thread and ignores its token.
            gate.Wait();
            returned.TrySetResult();
            return Task.FromResult("blocked");
        }
        try
        {
            // Every caller, the first included, gets its task back at once although the factory has not returned. The
            // real-time limits here are safety nets that a passing test never reaches.
            var waiters = await Task.Run(() => Enumerable.Range(0, 4).Select(index => Cache.GetOrCreateAsync(a, entry, Blocking, Cancellation)).ToArray(),
                Cancellation).WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
            await started.Task.WaitAsync(Cancellation);
            clock.Advance(Deadline);
            foreach (var waiter in waiters) await Assert.ThrowsAsync<TenantCacheDeadlineException>(() => Released(waiter));
            Assert.Equal(1, calls);
            // The next call starts afresh while the blocked factory still holds its pool thread.
            Assert.Equal("fresh", await Released(Cache.GetOrCreateAsync(a, entry, _ => Task.FromResult("fresh"), Cancellation)));
            Assert.False(returned.Task.IsCompleted);
        }
        finally
        {
            gate.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        }
        // Once it returns, its value reaches nothing.
        Assert.Equal("fresh", await Cache.GetOrCreateAsync<string>(a, entry, _ => throw new InvalidOperationException("Expected a hit."), Cancellation));
    }

    [Fact]
    public async Task TM_G02_034_The_shared_factory_keeps_its_first_caller_s_execution_context()
    {
        var a = await EnterAsync();
        // An AsyncLocal value of the caller that starts the call, such as an ambient tenant or trace, reaches the factory on the pool.
        Ambient.Value = "first caller";
        Assert.Equal("first caller", await Cache.GetOrCreateAsync(a, Entry("ambient"), _ => Task.FromResult(Ambient.Value ?? "none"), Cancellation));
    }

    [Fact]
    public async Task TM_G02_034_A_waiter_s_own_cancellation_releases_only_that_waiter()
    {
        var a = await EnterAsync();
        var entry = Entry("shared");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken given = default;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Shared(CancellationToken token)
        {
            given = token;
            started.TrySetResult();
            await gate.Task;
            return "shared";
        }
        // The waiter that starts the call is the one that gives up.
        using var impatient = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var first = Cache.GetOrCreateAsync(a, entry, Shared, impatient.Token);
        var others = Enumerable.Range(0, 4).Select(index => Cache.GetOrCreateAsync<string>(a, entry,
            _ => throw new InvalidOperationException("The call must be shared."), Cancellation)).ToArray();
        await started.Task.WaitAsync(Cancellation);
        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Released(first));
        // Neither the shared call nor any other waiter was cancelled with it, and a value just inside the deadline reaches them all.
        Assert.False(given.IsCancellationRequested);
        Assert.All(others, waiter => Assert.False(waiter.IsCompleted));
        clock.Advance(Deadline - TimeSpan.FromTicks(1));
        gate.SetResult();
        foreach (var waiter in others) Assert.Equal("shared", await Released(waiter));
        Assert.False(given.IsCancellationRequested);
    }

    [Fact]
    public async Task TM_G02_034_A_value_that_arrives_after_the_deadline_is_never_cached()
    {
        var a = await EnterAsync();
        // A value the factory returns once the deadline has passed, before the deadline's timer has run, is neither delivered
        // nor cached.
        var entry = Entry("late");
        var arriving = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = Cache.GetOrCreateAsync(a, entry, _ => arriving.Task, Cancellation);
        clock.AdvanceBeforeTimersRun(Deadline);
        arriving.SetResult("late");
        await Assert.ThrowsAsync<TenantCacheDeadlineException>(() => Released(late));
        clock.Advance(TimeSpan.Zero);
        Assert.Equal("fresh", await Cache.GetOrCreateAsync(a, entry, _ => Task.FromResult("fresh"), Cancellation));

        // Nor does one that arrives after its waiters were released replace the value a later call cached.
        var abandoned = Entry("abandoned");
        var gate = new TaskCompletionSource<string>();
        var released = Cache.GetOrCreateAsync(a, abandoned, _ => gate.Task, Cancellation);
        clock.Advance(Deadline);
        await Assert.ThrowsAsync<TenantCacheDeadlineException>(() => Released(released));
        Assert.Equal("current", await Cache.GetOrCreateAsync(a, abandoned, _ => Task.FromResult("current"), Cancellation));
        gate.SetResult("late");
        await gate.Task;
        Assert.Equal("current", await Cache.GetOrCreateAsync<string>(a, abandoned, _ => throw new InvalidOperationException("Expected a hit."), Cancellation));
    }

    [Fact]
    public async Task TM_G02_034_The_factory_deadline_is_thirty_seconds_unless_set_and_is_bounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Deadline);
        var directoryModule = TenantDirectoryModule.Create(new PostgreSqlPersistenceOptions("Host=localhost;Database=unused"));
        foreach (var invalid in new[] { TimeSpan.Zero, TenantCacheOptions.MinimumFactoryDeadline - TimeSpan.FromTicks(1),
                     TenantCacheOptions.MaximumFactoryDeadline + TimeSpan.FromTicks(1) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TenantCache(clock, new() { FactoryDeadline = invalid }));
            // Registration refuses it before the host starts.
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddModularynTenancy(directoryModule, new() { FactoryDeadline = invalid }));
        }
        // A deadline the host sets is the one that applies.
        using var configured = new TenantCache(clock, new() { FactoryDeadline = TenantCacheOptions.MinimumFactoryDeadline });
        var a = await EnterAsync();
        var waiter = configured.GetOrCreateAsync(a, Entry("configured"), _ => new TaskCompletionSource<string>().Task, Cancellation);
        clock.Advance(TenantCacheOptions.MinimumFactoryDeadline);
        await Assert.ThrowsAsync<TenantCacheDeadlineException>(() => Released(waiter));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes) await scope.DisposeAsync();
        await services.DisposeAsync();
    }
}
