using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

public sealed class HostingIntegrationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Standard_host_starts_modules_before_application_readiness_and_stops_them_on_shutdown()
    {
        using var fixture = new LifecycleFixture();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var clock = new ManualClock(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()));
        using var application = builder.Build();
        var modules = application.Services.GetRequiredService<ModuleHost>();
        Assert.Same(modules, Assert.Single(application.Services.GetServices<IHostedService>(), service => service is ModuleHost));
        var lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.False(lifetime.ApplicationStarted.IsCancellationRequested);
        await application.StartAsync(Cancellation);
        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);
        Assert.True(modules.Report.IsReady);
        Assert.All(fixture.Observations, item => Assert.Same(clock, item.Context.TimeProvider));
        await application.StopAsync(Cancellation);
        Assert.False(modules.Report.IsReady);
        Assert.Empty(fixture.ActiveResources);
    }

    [Fact]
    public async Task Standard_host_does_not_report_started_after_a_composition_refusal()
    {
        using var fixture = new LifecycleFixture();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()[..2]));
        using var application = builder.Build();
        await Assert.ThrowsAsync<ModuleCompositionException>(() => application.StartAsync(Cancellation));
        Assert.False(application.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, fixture.Constructed);
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task An_asynchronous_extension_refusal_completes_before_any_factory_can_run()
    {
        using var fixture = new LifecycleFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validator = new CallbackValidator(async (selection, token) =>
        {
            Assert.Equal(new[] { "A", "B", "C" }, selection.Modules.Select(module => module.Id));
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return [new("MDC901", "Module 'B' requires configuration 'Mode'. Supply Mode before activation.", ["B"])];
        });
        var host = fixture.Host(validators: [validator]);
        var startup = host.StartAsync(Cancellation);
        await entered.Task.WaitAsync(Cancellation);
        try
        {
            Assert.Equal(ModuleHostState.Validating, host.Report.State);
            Assert.Equal(0, fixture.Constructed);
            Assert.Empty(host.Report.Events);
        }
        finally { release.TrySetResult(); }
        var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => startup);
        Assert.Equal("MDC901", Assert.Single(exception.Diagnostics).Code);
        Assert.Equal(new[] { "B" }, exception.Diagnostics[0].ModuleIds);
        Assert.Equal(0, fixture.Constructed);
        Assert.Empty(fixture.Calls);
        Assert.All(host.Report.Modules, item => Assert.Equal(ModuleState.Refused, item.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_extension_exception_or_cancellation_never_constructs_modules(bool cancel)
    {
        using var fixture = new LifecycleFixture();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var validator = new CallbackValidator((_, token) =>
        {
            if (cancel)
            {
                startup.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("Configuration could not be read.");
        });
        var host = fixture.Host(validators: [validator]);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(startup.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(startup.Token));
        Assert.False(host.Report.IsReady);
        Assert.Equal(0, fixture.Constructed);
        Assert.Empty(fixture.Calls);
        Assert.Empty(host.Report.Events);
        Assert.Equal(cancel ? "MDC104" : "MDC105", Assert.Single(host.Report.Diagnostics).Code);
        Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.Refused, module.State));
    }

    [Fact]
    public async Task Validators_registered_in_dependency_injection_can_refuse_host_start()
    {
        using var fixture = new LifecycleFixture();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<ICompositionValidator>(new CallbackValidator((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<CompositionDiagnostic>>([new("MDC902", "Module 'A' needs a configured data directory. Configure the directory before activation.", ["A"])]);
        }));
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()));
        using var application = builder.Build();
        var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => application.StartAsync(Cancellation));
        Assert.Equal("MDC902", Assert.Single(exception.Diagnostics).Code);
        Assert.Equal(0, fixture.Constructed);
    }

    [Fact]
    public void Registering_the_module_host_twice_is_refused()
    {
        var services = new ServiceCollection();
        services.AddModularynModules(LifecycleFixture.Environment, []);
        Assert.Throws<InvalidOperationException>(() => services.AddModularynModules(LifecycleFixture.Environment, []));
    }

    private sealed class CallbackValidator(Func<CompositionSelection, CancellationToken, ValueTask<IReadOnlyList<CompositionDiagnostic>>> callback) : ICompositionValidator
    {
        public int Order => 0;
        public ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken) => callback(selection, cancellationToken);
    }
}
