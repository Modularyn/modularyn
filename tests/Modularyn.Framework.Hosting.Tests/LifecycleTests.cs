using System.Diagnostics;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

public sealed class LifecycleTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_start_calls_initialize_once_and_readiness_waits_for_every_module()
    {
        using var fixture = new LifecycleFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DuringStage = async (id, stage, token) =>
        {
            if (id == "B" && stage == ModuleStage.Start)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var host = fixture.Host();
        var first = host.StartAsync(Cancellation);
        await entered.Task.WaitAsync(Cancellation);
        var second = host.StartAsync(Cancellation);
        try
        {
            Assert.False(host.Report.IsReady);
            Assert.False(second.IsCompleted);
            Assert.Equal(ModuleState.Ready, Assert.Single(host.Report.Modules, module => module.ModuleId == "C").State);
            Assert.Equal(ModuleState.Starting, Assert.Single(host.Report.Modules, module => module.ModuleId == "B").State);
            Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("A:", StringComparison.Ordinal));
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(first, second);
        Assert.True(host.Report.IsReady);
        Assert.Equal(3, fixture.Constructed);
        Assert.Equal(new[] { "C:initialize", "C:start", "B:initialize", "B:start", "A:initialize", "A:start" }, fixture.LifecycleCalls);
        await host.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Initialization_cancellation_cleans_partial_resources_with_an_independent_token_and_allows_recovery()
    {
        using var fixture = new LifecycleFixture();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        fixture.DuringStage = (id, stage, _) =>
        {
            if (id == "B" && stage == ModuleStage.Initialization) startup.Cancel();
            return Task.CompletedTask;
        };
        var host = fixture.Host();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(startup.Token));
        Assert.Equal(startup.Token, exception.CancellationToken);
        Assert.False(host.Report.IsReady);
        Assert.Empty(fixture.ActiveResources);
        Assert.Empty(host.Report.RetainedInstances);
        Assert.Equal(new[] { "C:initialize", "C:start", "B:initialize", "B:stop", "C:stop" }, fixture.LifecycleCalls);
        Assert.Equal("MDC104", Assert.Single(host.Report.Diagnostics).Code);
        Assert.Contains(host.Report.Events, item => item.ModuleId == "B" && item.Stage == ModuleStage.Initialization && item.Outcome == ModuleStageOutcome.Cancelled);
        Assert.All(fixture.Observations.Where(item => item.Stage != ModuleStage.Shutdown), item => Assert.Equal(startup.Token, item.Token));
        Assert.All(fixture.Observations.Where(item => item.Stage == ModuleStage.Shutdown), item =>
        {
            Assert.NotEqual(startup.Token, item.Token);
            Assert.False(item.Token.IsCancellationRequested);
        });
        fixture.DuringStage = null;
        await host.StartAsync(Cancellation);
        Assert.True(host.Report.IsReady);
        Assert.All(new[] { "A", "B", "C" }, id => Assert.Equal(new[] { "initialized" }, fixture.DurableEffects(id)));
        await host.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Shutdown_cancelled_while_waiting_for_start_returns_without_interrupting_the_transition()
    {
        using var fixture = new LifecycleFixture();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DuringStage = async (id, stage, token) =>
        {
            if (id == "C" && stage == ModuleStage.Initialization)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var host = fixture.Host();
        var startup = host.StartAsync(Cancellation);
        await entered.Task.WaitAsync(Cancellation);
        try
        {
            var stopping = host.StopAsync(shutdown.Token);
            shutdown.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
            Assert.DoesNotContain(fixture.Observations, item => item.Stage == ModuleStage.Shutdown);
        }
        finally { release.TrySetResult(); }
        await startup;
        Assert.True(host.Report.IsReady);
        await host.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Multiline_start_failures_keep_the_original_cause_and_still_release_resources()
    {
        using var fixture = new LifecycleFixture();
        fixture.DuringStage = (id, stage, _) =>
        {
            if (id == "B" && stage == ModuleStage.Start)
                throw new InvalidOperationException("Required configuration failed.\nMode is unavailable.");
            return Task.CompletedTask;
        };
        var host = fixture.Host();
        var exception = await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StartAsync(Cancellation));
        Assert.Contains("Mode is unavailable.", exception.Report.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Empty(exception.Report.RetainedInstances);
        Assert.Empty(fixture.ActiveResources);
    }

    [Fact]
    public async Task Precancellation_does_not_construct_or_initialize_modules()
    {
        using var fixture = new LifecycleFixture();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        startup.Cancel();
        var host = fixture.Host();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(startup.Token));
        Assert.Equal(0, fixture.Constructed);
        Assert.Empty(fixture.Calls);
        Assert.Empty(host.Report.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_cancellation_reaches_all_owned_modules_and_pending_cleanup_can_be_retried(bool alreadyCancelled)
    {
        using var fixture = new LifecycleFixture();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var host = fixture.Host();
        await host.StartAsync(Cancellation);
        if (alreadyCancelled) shutdown.Cancel();
        else fixture.DuringStage = (id, stage, _) =>
        {
            if (id == "A" && stage == ModuleStage.Shutdown) shutdown.Cancel();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StopAsync(shutdown.Token));
        Assert.False(host.Report.IsReady);
        Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.CleanupFailed, module.State));
        Assert.Equal(new[] { "A", "B", "C" }, fixture.Observations.Where(item => item.Stage == ModuleStage.Shutdown).Select(item => item.Id));
        Assert.All(fixture.Observations.Where(item => item.Stage == ModuleStage.Shutdown), item => Assert.Equal(shutdown.Token, item.Token));
        Assert.Equal(new[] { "A", "B", "C" }, host.Report.Diagnostics.Select(item => Assert.Single(item.ModuleIds)));
        Assert.All(host.Report.Diagnostics, item => Assert.Equal("MDC103", item.Code));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(Cancellation));
        Assert.Equal(3, fixture.Constructed);
        fixture.DuringStage = null;
        await host.StopAsync(Cancellation);
        Assert.Empty(host.Report.RetainedInstances);
        Assert.Empty(fixture.ActiveResources);
        await host.StartAsync(Cancellation);
        Assert.True(host.Report.IsReady);
        await host.StopAsync(Cancellation);
    }

    [Fact]
    public async Task A_cleanup_failure_does_not_skip_other_components_or_allow_duplicate_start_effects()
    {
        using var fixture = new LifecycleFixture();
        fixture.FailStart.Add("B");
        fixture.FailShutdown.Add("B");
        var host = fixture.Host();
        var exception = await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StartAsync(Cancellation));
        Assert.Equal(new[] { "B:stop", "C:stop" }, fixture.Calls.Where(call => call.EndsWith(":stop", StringComparison.Ordinal)));
        Assert.Equal(new[] { "B" }, fixture.ActiveResources);
        Assert.Equal(new[] { "B" }, exception.Report.RetainedInstances);
        Assert.DoesNotContain(exception.Report.Modules, module => module.State == ModuleState.Ready);
        Assert.Equal(new[] { "MDC102", "MDC103" }, exception.Report.Diagnostics.Select(item => item.Code));
        Assert.Contains("Module 'B' Shutdown", exception.Report.Diagnostics[1].Message, StringComparison.Ordinal);
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(Cancellation));
        Assert.Contains("B", retry.Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Constructed);
        fixture.FailShutdown.Clear();
        await host.StopAsync(Cancellation);
        Assert.Empty(fixture.ActiveResources);
        fixture.FailStart.Clear();
        await host.StartAsync(Cancellation);
        Assert.True(host.Report.IsReady);
        Assert.All(new[] { "A", "B", "C" }, id => Assert.Equal(new[] { "initialized" }, fixture.DurableEffects(id)));
        await host.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Normal_shutdown_reports_all_failures_and_continues_in_reverse_order()
    {
        using var fixture = new LifecycleFixture();
        var host = fixture.Host();
        await host.StartAsync(Cancellation);
        fixture.FailShutdown.UnionWith(["A", "C"]);
        var exception = await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StopAsync(Cancellation));
        Assert.Equal(new[] { "A:stop", "B:stop", "C:stop" }, fixture.Calls.Where(call => call.EndsWith(":stop", StringComparison.Ordinal)));
        Assert.Equal(new[] { "C", "A" }, exception.Report.RetainedInstances);
        Assert.Equal(new[] { "A", "C" }, exception.Report.Diagnostics.Select(item => Assert.Single(item.ModuleIds)));
        fixture.FailShutdown.Clear();
        await host.StopAsync(Cancellation);
        Assert.Empty(fixture.ActiveResources);
    }

    [Fact]
    public async Task Clock_and_trace_context_flow_through_every_lifecycle_stage()
    {
        using var fixture = new LifecycleFixture();
        var instant = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var clock = new ManualClock(instant);
        using var activity = new Activity("module-lifecycle").SetIdFormat(ActivityIdFormat.W3C).Start();
        var trace = activity.Context;
        fixture.DuringStage = (_, _, _) =>
        {
            Assert.Equal(trace.TraceId, Activity.Current!.TraceId);
            return Task.CompletedTask;
        };
        var host = fixture.Host(clock: clock);
        await host.StartAsync(Cancellation);
        Assert.All(host.Report.Events, item => Assert.Equal(instant, item.Timestamp));
        clock.Advance(TimeSpan.FromMinutes(1));
        await host.StopAsync(Cancellation);
        Assert.All(host.Report.Events.Where(item => item.Stage == ModuleStage.Shutdown), item => Assert.Equal(instant.AddMinutes(1), item.Timestamp));
        Assert.All(fixture.Observations, item =>
        {
            Assert.Same(clock, item.Context.TimeProvider);
            Assert.Equal(trace.TraceId, item.Context.TraceContext.TraceId);
            Assert.Equal(trace.TraceId.ToHexString(), item.Context.CorrelationId);
            Assert.Equal(item.Id, item.Context.Manifest.Id);
            Assert.Equal(Cancellation, item.Token);
        });
        foreach (var module in fixture.Observations.GroupBy(item => item.Id))
            Assert.All(module, item => Assert.Same(module.First().Context, item.Context));
    }

    [Fact]
    public async Task Failed_start_cleanup_deadline_uses_the_injected_clock_and_preserves_unreleased_instances()
    {
        using var fixture = new LifecycleFixture();
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        fixture.FailStart.Add("B");
        fixture.DuringStage = (_, stage, _) =>
        {
            if (stage == ModuleStage.Shutdown) clock.Advance(TimeSpan.FromSeconds(5));
            return Task.CompletedTask;
        };
        var host = fixture.Host(clock: clock, options: new() { FailedStartCleanupTimeout = TimeSpan.FromSeconds(5) });
        var exception = await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StartAsync(Cancellation));
        Assert.Equal(new[] { "C", "B" }, exception.Report.RetainedInstances);
        Assert.All(exception.Report.Modules.Where(item => item.ModuleId != "A"), item => Assert.Equal(ModuleState.CleanupFailed, item.State));
        Assert.Equal(new[] { "B", "C" }, exception.Report.Events.Where(item => item.Stage == ModuleStage.Shutdown && item.Outcome == ModuleStageOutcome.Cancelled).Select(item => item.ModuleId));
        fixture.DuringStage = null;
        await host.StopAsync(Cancellation);
        Assert.Empty(fixture.ActiveResources);
    }
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    private readonly List<ManualTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => current;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => current.UtcTicks;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime);
        timers.Add(timer);
        return timer;
    }
    internal void Advance(TimeSpan elapsed)
    {
        current += elapsed;
        foreach (var timer in timers.ToArray()) timer.FireIfDue();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private DateTimeOffset due = clock.current + dueTime;
        private bool disposed;
        public bool Change(TimeSpan nextDueTime, TimeSpan period)
        {
            if (disposed) return false;
            due = clock.current + nextDueTime;
            return true;
        }
        internal void FireIfDue()
        {
            if (disposed || clock.current < due) return;
            disposed = true;
            callback(state);
        }
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
