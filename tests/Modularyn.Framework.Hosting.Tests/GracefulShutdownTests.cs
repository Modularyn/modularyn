using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

public sealed class GracefulShutdownTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC_001_P01_Shutdown_refuses_new_work_and_drains_before_stopping_modules()
    {
        using var fixture = new LifecycleFixture();
        var host = fixture.Host();
        Assert.False(host.Work.TryEnter(out _));
        await host.StartAsync(Cancellation);
        Assert.True(host.Work.TryEnter(out var work));
        using (work)
        {
            var shutdown = host.StopAsync(Cancellation);
            Assert.False(host.Report.IsReady);
            Assert.False(host.Work.TryEnter(out _));
            Assert.False(work!.CancellationToken.IsCancellationRequested);
            Assert.False(shutdown.IsCompleted);
            Assert.DoesNotContain(fixture.Observations, item => item.Stage == ModuleStage.Shutdown);
            work.Dispose();
            await shutdown;
        }
        Assert.Equal(new[] { "A:stop", "B:stop", "C:stop" }, fixture.Calls.Where(call => call.EndsWith(":stop", StringComparison.Ordinal)));
        Assert.Empty(fixture.ActiveResources);
        Assert.Empty(host.Report.RetainedInstances);
        Assert.Equal(0, host.Report.PendingWork);
    }

    [Fact]
    public async Task Drain_deadline_cancels_outstanding_work_reports_it_and_requires_cleanup_before_restart()
    {
        using var fixture = new LifecycleFixture();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var host = fixture.Host(clock: clock, shutdownTimeout: TimeSpan.FromSeconds(5));
        await host.StartAsync(Cancellation);
        Assert.True(host.Work.TryEnter(out var work));
        using (work)
        {
            var shutdown = host.StopAsync(Cancellation);
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shutdown);
            Assert.True(work!.CancellationToken.IsCancellationRequested);
            Assert.Equal(1, host.Report.PendingWork);
            Assert.Contains(host.Report.Diagnostics, diagnostic => diagnostic.Code == ModuleHostDiagnosticCodes.WorkDrainIncomplete);
            Assert.False(host.Work.IsAccepting);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(Cancellation));
        }
        await host.StopAsync(Cancellation);
        Assert.Empty(fixture.ActiveResources);
        await host.StartAsync(Cancellation);
        Assert.True(host.Work.TryEnter(out var restarted));
        using (restarted) Assert.False(restarted!.CancellationToken.IsCancellationRequested);
        await host.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Host_shutdown_timeout_bounds_an_uncooperative_callback_and_retry_observes_the_same_task()
    {
        using var fixture = new LifecycleFixture();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.IgnoreShutdownCancellation.Add("A");
        fixture.DuringStage = async (id, stage, _) =>
        {
            if (id == "A" && stage == ModuleStage.Shutdown)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()), LifecycleFixture.Unverified);
        using var application = builder.Build();
        var modules = application.Services.GetRequiredService<ModuleHost>();
        await application.StartAsync(Cancellation);
        var shutdown = application.StopAsync(Cancellation);
        await entered.Task.WaitAsync(Cancellation);
        Task? retry = null;
        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shutdown);
            Assert.False(modules.Work.IsAccepting);
            Assert.Contains("A", modules.Report.RetainedInstances);
            Assert.Contains(modules.Report.Events, item => item.ModuleId == "A" && item.Stage == ModuleStage.Shutdown
                && item.Outcome == ModuleStageOutcome.Cancelled);
            retry = modules.StopAsync(Cancellation);
            Assert.False(retry.IsCompleted);
            Assert.Single(fixture.Calls, call => call == "A:stop");
        }
        finally { release.TrySetResult(); }
        if (retry is not null) await retry;
        Assert.Empty(modules.Report.RetainedInstances);
        Assert.Empty(fixture.ActiveResources);
        Assert.Single(fixture.Calls, call => call == "A:stop");
    }

    [Fact]
    public async Task Stopping_notification_closes_admission_before_any_stop_callback()
    {
        using var fixture = new LifecycleFixture();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()), LifecycleFixture.Unverified);
        using var application = builder.Build();
        var modules = application.Services.GetRequiredService<ModuleHost>();
        await application.StartAsync(Cancellation);
        application.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        Assert.False(modules.Work.TryEnter(out _));
        Assert.DoesNotContain(fixture.Observations, item => item.Stage == ModuleStage.Shutdown);
        fixture.DuringStage = (_, stage, _) =>
        {
            if (stage == ModuleStage.Shutdown) Assert.False(modules.Work.TryEnter(out _));
            return Task.CompletedTask;
        };
        await application.StopAsync(Cancellation);
        Assert.Empty(fixture.ActiveResources);
    }

    [Fact]
    public async Task Failed_start_cleanup_is_bounded_even_when_a_callback_ignores_its_deadline()
    {
        using var fixture = new LifecycleFixture();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FailStart.Add("B");
        fixture.IgnoreShutdownCancellation.Add("B");
        fixture.DuringStage = async (id, stage, _) =>
        {
            if (id == "B" && stage == ModuleStage.Shutdown)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var host = fixture.Host(clock: clock, options: new() { FailedStartCleanupTimeout = TimeSpan.FromSeconds(5), AllowUnverifiedComposition = true });
        var starting = host.StartAsync(Cancellation);
        await entered.Task.WaitAsync(Cancellation);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            var failure = await Assert.ThrowsAsync<ModuleLifecycleException>(() => starting);
            Assert.False(failure.Report.IsReady);
            Assert.Contains("B", failure.Report.RetainedInstances);
            Assert.Contains(failure.Report.Diagnostics, item => item.Code == ModuleHostDiagnosticCodes.ShutdownFailed);
        }
        finally { release.TrySetResult(); }
        await host.StopAsync(Cancellation);
        Assert.Empty(fixture.ActiveResources);
        Assert.Single(fixture.Calls, call => call == "B:stop");
    }
}
