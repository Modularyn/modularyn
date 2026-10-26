using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

public sealed class AcceptanceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "AC-001-P01 dependency initialization, readiness and reverse shutdown")]
    public async Task AC_001_P01_Valid_graph_activates_each_module_once_and_shuts_down_in_reverse()
    {
        using var fixture = new LifecycleFixture();
        var host = fixture.Host(); // Discovery A, B, C is the reverse of initialization order.
        Assert.False(host.Report.IsReady);
        Assert.Empty(host.Report.RetainedInstances);
        await host.StartAsync(Cancellation);
        Assert.True(host.Report.IsReady);
        Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.Ready, module.State));
        Assert.Equal(new[] { "C", "B", "A" }, host.Report.Plan!.Modules.Select(module => module.Id));
        Assert.Equal(new[] { "1.0.0", "2.1.0", "1.0.0" }, host.Report.Plan.Modules.Select(module => module.Version.ToString()));
        Assert.Equal(new[] { "C:initialize", "C:start", "B:initialize", "B:start", "A:initialize", "A:start" }, fixture.LifecycleCalls);
        var readyReport = host.Report;
        await host.StartAsync(Cancellation);
        Assert.Same(readyReport, host.Report);
        Assert.Equal(3, fixture.Constructed);
        Assert.Equal(new[] { "A", "B", "C" }, fixture.ActiveResources);

        await host.StopAsync(Cancellation);
        await host.StopAsync(Cancellation);
        Assert.False(host.Report.IsReady);
        Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.Stopped, module.State));
        Assert.Empty(host.Report.RetainedInstances);
        Assert.Empty(fixture.ActiveResources);
        Assert.Equal(new[] { "C:initialize", "C:start", "B:initialize", "B:start", "A:initialize", "A:start", "A:stop", "B:stop", "C:stop" }, fixture.LifecycleCalls);
        Assert.Equal(new[] { "A", "B", "C" }, host.Report.Events.Where(item => item.Stage == ModuleStage.Shutdown && item.Outcome == ModuleStageOutcome.Succeeded).Select(item => item.ModuleId));
        Assert.All(new[] { "A", "B", "C" }, id => Assert.Equal(new[] { "initialized" }, fixture.DurableEffects(id)));
        Assert.True(readyReport.IsReady); // Previously observed snapshots do not mutate.
        Assert.Empty(host.Report.Diagnostics);
    }

    [Theory(DisplayName = "AC-001-N01 refusals precede all activation")]
    [InlineData("missing", "MDC001", "requires missing module 'C'")]
    [InlineData("cycle", "MDC002", "A -> B -> C -> A")]
    [InlineData("incompatible", "MDC003", "candidate 'B' is 3.0.0")]
    public async Task AC_001_N01_Independently_mutated_control_graphs_are_refused_without_constructing_modules(string mutation, string code, string cause)
    {
        var graph = LifecycleFixture.Graph();
        var control = await new ModuleComposer().ComposeAsync(new(graph, LifecycleFixture.Environment), Cancellation);
        Assert.True(control.Succeeded);
        using (var baseline = new LifecycleFixture())
        {
            var baselineHost = baseline.Host(graph);
            await baselineHost.StartAsync(Cancellation);
            Assert.True(baselineHost.Report.IsReady);
            await baselineHost.StopAsync(Cancellation);
        }
        ModuleManifest[] changed = mutation switch
        {
            "missing" => graph[..2],
            "cycle" => [graph[0], graph[1], new("C", LifecycleFixture.One, [new("A", VersionRange.Any)],
                providedProviders: graph[2].ProvidedProviders, runtimeVersions: graph[2].RuntimeVersions, frameworkVersions: graph[2].FrameworkVersions)],
            _ => LifecycleFixture.Graph("3.0.0")
        };
        using var fixture = new LifecycleFixture();
        var host = fixture.Host(changed);
        var before = host.Report;
        var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => host.StartAsync(Cancellation));
        var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == code);
        Assert.Contains(cause, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(mutation == "cycle" ? "Remove or redirect" : "Select", diagnostic.Message, StringComparison.Ordinal);
        if (mutation == "missing") Assert.Equal(new[] { "B", "C" }, diagnostic.ModuleIds);
        if (mutation == "cycle") Assert.Equal(new[] { "A", "B", "C", "A" }, diagnostic.ModuleIds);
        if (mutation == "incompatible")
        {
            Assert.Equal(new[] { "A", "B" }, diagnostic.ModuleIds);
            Assert.Contains("[2.0.0, 2.2.0]", diagnostic.Message, StringComparison.Ordinal);
        }
        Assert.False(before.IsReady);
        Assert.Empty(before.RetainedInstances);
        AssertRefusedBeforeActivation(host, fixture);
    }

    [Theory(DisplayName = "AC-001-B01 selected version boundaries at activation")]
    [InlineData("1.9.9", false)]
    [InlineData("2.0.0", true)]
    [InlineData("2.2.0", true)]
    [InlineData("2.2.1", false)]
    public async Task AC_001_B01_Only_inclusive_boundary_versions_activate(string candidate, bool compatible)
    {
        using var fixture = new LifecycleFixture();
        var host = fixture.Host(LifecycleFixture.Graph(candidate));
        if (compatible)
        {
            await host.StartAsync(Cancellation);
            Assert.True(host.Report.IsReady);
            await host.StopAsync(Cancellation);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => host.StartAsync(Cancellation));
            var diagnostic = Assert.Single(exception.Diagnostics);
            Assert.Equal("MDC003", diagnostic.Code);
            Assert.Contains("[2.0.0, 2.2.0]", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains(candidate, diagnostic.Message, StringComparison.Ordinal);
            AssertRefusedBeforeActivation(host, fixture);
        }
    }

    [Theory(DisplayName = "AC-001-R01 failed initialization or start cleans up and recovers")]
    [InlineData(ModuleStage.Initialization, "MDC101")]
    [InlineData(ModuleStage.Start, "MDC102")]
    public async Task AC_001_R01_Recovery_cleans_every_initialized_component_and_preserves_single_durable_effects(ModuleStage faultStage, string code)
    {
        using var fixture = new LifecycleFixture();
        var faults = faultStage == ModuleStage.Initialization ? fixture.FailInitialization : fixture.FailStart;
        faults.Add("B");
        var host = fixture.Host();
        var exception = await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StartAsync(Cancellation));
        var failed = exception.Report;
        Assert.False(failed.IsReady);
        Assert.Equal(ModuleState.Failed, Assert.Single(failed.Modules, module => module.ModuleId == "B").State);
        Assert.Equal(ModuleState.Unavailable, Assert.Single(failed.Modules, module => module.ModuleId == "A").State);
        Assert.DoesNotContain(failed.Modules, module => module.State == ModuleState.Ready);
        Assert.Equal(new[] { "B", "C" }, failed.Events.Where(item => item.Stage == ModuleStage.Shutdown && item.Outcome == ModuleStageOutcome.Succeeded).Select(item => item.ModuleId));
        Assert.Empty(failed.RetainedInstances);
        Assert.Empty(fixture.ActiveResources);
        Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("A:", StringComparison.Ordinal));
        var diagnostic = Assert.Single(failed.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Contains($"Module 'B' {faultStage}", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("fault in B", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "initialized" }, fixture.DurableEffects("C"));
        Assert.Equal(new[] { "initialized" }, fixture.DurableEffects("B"));

        faults.Clear();
        await host.StartAsync(Cancellation);
        Assert.True(host.Report.IsReady);
        Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.Ready, module.State));
        Assert.Equal(new[] { "A", "B", "C" }, fixture.ActiveResources);
        Assert.Equal(5, fixture.Constructed);
        Assert.Equal(new[] { "C", "B", "A" }, host.Report.Events.Where(item => item.Attempt == 2 && item.Stage == ModuleStage.Initialization && item.Outcome == ModuleStageOutcome.Succeeded).Select(item => item.ModuleId));
        Assert.All(new[] { "A", "B", "C" }, id => Assert.Equal(new[] { "initialized" }, fixture.DurableEffects(id)));
        await host.StartAsync(Cancellation);
        Assert.Equal(5, fixture.Constructed);
        await host.StopAsync(Cancellation);
        Assert.Empty(fixture.ActiveResources);
        Assert.Equal(new[] { "A", "B", "C" }, host.Report.Events.Where(item => item.Attempt == 2 && item.Stage == ModuleStage.Shutdown && item.Outcome == ModuleStageOutcome.Succeeded).Select(item => item.ModuleId));
        Assert.False(failed.IsReady);
        Assert.Empty(host.Report.Diagnostics);
    }

    [Theory]
    [InlineData("exclusive", "MDC004", "Exclusive capability 'Storage'")]
    [InlineData("route", "MDC005", "GET /records")]
    [InlineData("duplicate", "MDC006", "selected more than once")]
    [InlineData("runtime", "MDC009", "candidate runtime is 10.0.0")]
    [InlineData("framework", "MDC010", "candidate framework is 1.0.0")]
    [InlineData("contract", "MDC007", "missing contract 'Records'")]
    [InlineData("provider", "MDC008", "'B' 2.0.0")]
    [InlineData("declaration", "MDC011", "health check 'Ready'")]
    public async Task All_builtin_refusals_leave_the_factory_and_lifecycle_untouched(string mutation, string code, string cause)
    {
        var one = LifecycleFixture.One;
        var two = SemanticVersion.Parse("2.0.0");
        ModuleManifest[] modules = mutation switch
        {
            "exclusive" => [new("A", one, providedCapabilities: [new("Storage", one, true)]), new("B", one, providedCapabilities: [new("Storage", one)])],
            "route" => [new("A", one, routes: [new("GET", "/records")]), new("B", one, routes: [new("GET", "/records")])],
            "duplicate" => [new("A", one), new("A", two)],
            "runtime" => [new("A", one, runtimeVersions: VersionRange.Exact(one))],
            "framework" => [new("A", one, frameworkVersions: VersionRange.Exact(two))],
            "contract" => [new("A", one, requiredContracts: [new("Records", VersionRange.Any)])],
            "provider" => [new("A", one, requiredProviders: [new("Storage", VersionRange.Exact(one))]), new("B", one, providedProviders: [new("Storage", two)])],
            _ => [new("A", one, healthChecks: [new("Ready", one), new("Ready", one)])]
        };
        using var fixture = new LifecycleFixture();
        var host = fixture.Host(modules);
        var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => host.StartAsync(Cancellation));
        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Contains("A", diagnostic.ModuleIds);
        Assert.Contains(cause, diagnostic.Message, StringComparison.Ordinal);
        AssertRefusedBeforeActivation(host, fixture);
    }

    private static void AssertRefusedBeforeActivation(ModuleHost host, LifecycleFixture fixture)
    {
        Assert.False(host.Report.IsReady);
        Assert.Null(host.Report.Plan);
        Assert.All(host.Report.Modules, module => Assert.Equal(ModuleState.Refused, module.State));
        Assert.Empty(host.Report.Events);
        Assert.Empty(host.Report.RetainedInstances);
        Assert.Equal(0, fixture.Constructed);
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.ActiveResources);
    }
}
