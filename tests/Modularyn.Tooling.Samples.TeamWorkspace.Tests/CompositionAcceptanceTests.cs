using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Modularity;
using Xunit;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

[Collection(TeamWorkspacePostgreSqlCollection.Name)]
public sealed class CompositionAcceptanceTests
{
    [Theory]
    [InlineData(DeploymentMode.Host, Traits = new[] { "Requires", "PostgreSQL", "Deployment", DeploymentMode.Host })]
    [InlineData(DeploymentMode.Container, Traits = new[] { "Deployment", DeploymentMode.Container })]
    public Task AC_001_B01_CompatibilityEdgesHonorTheDeclaredRange_Integration(string mode) =>
        RunAsync("AC-001-B01", mode, async (observations, token) =>
        {
            string? refusedConstraint = null;
            foreach (var (version, compatible) in new[] { ("1.9.9", false), ("2.0.0", true), ("2.2.0", true), ("2.2.1", false) })
            {
                await using var scenario = await CompositionScenario.CreateAsync(CompositionScenario.Graph(version), token);
                observations.Bind(scenario.Host);
                observations.Capture("before-" + version, scenario);
                if (compatible)
                {
                    await scenario.Host.StartApplicationAsync(token);
                    observations.Capture("ready-" + version, scenario);
                    Assert.True(scenario.Modules.Report.IsReady);
                    // The resolved selection names this edge version of B, after C and before A.
                    var resolved = scenario.Modules.Report.Plan!.Modules.Where(module => CompositionScenario.Ids.Contains(module.Id)).ToArray();
                    Assert.Equal(new[] { "C", "B", "A" }, resolved.Select(module => module.Id));
                    Assert.Equal(version, resolved.Single(module => module.Id == "B").Version.ToString());
                    await ReadyOverHttpAsync(scenario.Host, token);
                    Assert.All(scenario.Modules.Report.Modules, module => Assert.Equal(ModuleState.Ready, module.State));
                    var effects = await scenario.EffectsAsync(token);
                    AssertEffects(effects, "A", "B", "C");
                    observations.Effects("effects-" + version, effects);
                    await scenario.Host.StopApplicationAsync(token);
                    Assert.Empty(scenario.ActiveResources);
                    observations.Capture("stopped-" + version, scenario);
                }
                else
                {
                    var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => scenario.Host.StartApplicationAsync(token));
                    observations.Capture("refused-" + version, scenario);
                    var diagnostic = Assert.Single(exception.Diagnostics);
                    Assert.Equal("MDC003", diagnostic.Code);
                    Assert.Equal(new[] { "A", "B" }, diagnostic.ModuleIds);
                    Assert.Contains("[2.0.0, 2.2.0]", diagnostic.Message, StringComparison.Ordinal);
                    Assert.Contains("candidate 'B' is " + version, diagnostic.Message, StringComparison.Ordinal);
                    // Both rejected versions identify the same unsatisfied constraint; only the candidate's version differs.
                    var constraint = diagnostic.Message.Replace("candidate 'B' is " + version + ".", "candidate 'B' is outside it.", StringComparison.Ordinal);
                    Assert.Equal(refusedConstraint ?? constraint, constraint);
                    refusedConstraint = constraint;
                    AssertRefused(scenario);
                    var effects = await scenario.EffectsAsync(token);
                    Assert.Empty(effects);
                    observations.Effects("effects-" + version, effects);
                }
                observations.Observed("B " + version + (compatible ? " reached readiness with three durable effects." : " was refused by the inclusive [2.0.0, 2.2.0] constraint before activation."));
            }
            Assert.NotNull(refusedConstraint);
        });

    [Theory]
    [InlineData(DeploymentMode.Host, Traits = new[] { "Requires", "PostgreSQL", "Deployment", DeploymentMode.Host })]
    [InlineData(DeploymentMode.Container, Traits = new[] { "Deployment", DeploymentMode.Container })]
    public Task AC_001_N01_InvalidGraphsRefuseActivationWithDiagnostics_Integration(string mode) =>
        RunAsync("AC-001-N01", mode, async (observations, token) =>
        {
            await using (var control = await CompositionScenario.CreateAsync(CompositionScenario.Graph(), token))
            {
                observations.Bind(control.Host);
                await control.Host.StartApplicationAsync(token);
                await ReadyOverHttpAsync(control.Host, token);
                var effects = await control.EffectsAsync(token);
                AssertEffects(effects, "A", "B", "C");
                observations.Effects("control-effects", effects);
                observations.Capture("valid-control", control);
                await control.Host.StopApplicationAsync(token);
                Assert.Empty(control.ActiveResources);
            }
            foreach (var (mutation, code) in new[] { ("missing", "MDC001"), ("cycle", "MDC002"), ("incompatible", "MDC003") })
            {
                var graph = CompositionScenario.Graph(mutation == "incompatible" ? "3.0.0" : "2.1.0", mutation);
                await using var scenario = await CompositionScenario.CreateAsync(graph, token);
                observations.Bind(scenario.Host);
                observations.Capture("before-" + mutation, scenario);
                var exception = await Assert.ThrowsAsync<ModuleCompositionException>(() => scenario.Host.StartApplicationAsync(token));
                observations.Capture("refused-" + mutation, scenario);
                var diagnostic = Assert.Single(exception.Diagnostics, item => item.Code == code);
                switch (mutation)
                {
                    case "missing":
                        Assert.Equal(new[] { "B", "C" }, diagnostic.ModuleIds);
                        Assert.Contains("requires missing module 'C'", diagnostic.Message, StringComparison.Ordinal);
                        break;
                    case "cycle":
                        Assert.Equal(new[] { "A", "B", "C", "A" }, diagnostic.ModuleIds);
                        Assert.Contains("A -> B -> C -> A", diagnostic.Message, StringComparison.Ordinal);
                        break;
                    default:
                        Assert.Equal(new[] { "A", "B" }, diagnostic.ModuleIds);
                        Assert.Contains("[2.0.0, 2.2.0]", diagnostic.Message, StringComparison.Ordinal);
                        Assert.Contains("candidate 'B' is 3.0.0", diagnostic.Message, StringComparison.Ordinal);
                        break;
                }
                AssertRefused(scenario);
                var effects = await scenario.EffectsAsync(token);
                Assert.Empty(effects);
                observations.Effects("effects-" + mutation, effects);
                observations.Observed("The " + mutation + " selection was refused with " + code + "; no module activated and no durable effect or active resource remained.");
            }
        });

    [Theory]
    [InlineData(DeploymentMode.Host, Traits = new[] { "Requires", "PostgreSQL", "Deployment", DeploymentMode.Host })]
    [InlineData(DeploymentMode.Container, Traits = new[] { "Deployment", DeploymentMode.Container })]
    public Task AC_001_P01_DependenciesInitializeOnceAndStopInReverseOrder_Integration(string mode) =>
        RunAsync("AC-001-P01", mode, async (observations, token) =>
        {
            await using var scenario = await CompositionScenario.CreateAsync(CompositionScenario.Graph(), token);
            observations.Bind(scenario.Host);
            observations.Capture("before", scenario);
            Assert.False(scenario.Modules.Report.IsReady);
            Assert.Empty(scenario.Modules.Report.RetainedInstances);
            await scenario.Host.StartApplicationAsync(token);
            await ReadyOverHttpAsync(scenario.Host, token);
            var ready = scenario.Modules.Report;
            observations.Capture("ready", scenario);
            Assert.True(ready.IsReady);
            Assert.All(ready.Modules, module => Assert.Equal(ModuleState.Ready, module.State));
            Assert.Equal(ready.Plan!.Modules.Select(module => module.Id), ready.Events
                .Where(item => item.Stage == ModuleStage.Initialization && item.Outcome == ModuleStageOutcome.Succeeded).Select(item => item.ModuleId));
            Assert.Equal(new[] { "C", "B", "A" }, ready.Plan!.Modules.Where(module => CompositionScenario.Ids.Contains(module.Id)).Select(module => module.Id));
            Assert.Equal(new[] { "1.0.0", "2.1.0", "1.0.0" }, ready.Plan.Modules.Where(module => CompositionScenario.Ids.Contains(module.Id)).Select(module => module.Version.ToString()));
            Assert.Equal(new[] { "C:initialize", "C:start", "B:initialize", "B:start", "A:initialize", "A:start" }, scenario.Calls);
            await scenario.Modules.StartAsync(token);
            Assert.Same(ready, scenario.Modules.Report);
            Assert.Equal(3, scenario.Constructed);
            Assert.Equal(new[] { "A", "B", "C" }, scenario.ActiveResources);
            var effects = await scenario.EffectsAsync(token);
            AssertEffects(effects, "A", "B", "C");
            observations.Effects("ready-effects", effects);
            await scenario.Host.StopApplicationAsync(token);
            await scenario.Modules.StopAsync(token);
            observations.Capture("stopped", scenario);
            Assert.False(scenario.Modules.Report.IsReady);
            Assert.All(scenario.Modules.Report.Modules, module => Assert.Equal(ModuleState.Stopped, module.State));
            Assert.Empty(scenario.Modules.Report.RetainedInstances);
            Assert.Empty(scenario.ActiveResources);
            Assert.Equal(new[] { "C:initialize", "C:start", "B:initialize", "B:start", "A:initialize", "A:start", "A:stop", "B:stop", "C:stop" }, scenario.Calls);
            Assert.Equal(ready.Plan.Modules.Select(module => module.Id).Reverse(), Shutdown(scenario.Modules.Report, 1));
            Assert.Equal(effects, await scenario.EffectsAsync(token));
            Assert.Empty(scenario.Modules.Report.Diagnostics);
            observations.Observed("All selected modules reached readiness; C, B and A initialized once, with one verified Files version each.");
            observations.Observed("Shutdown completed in reverse resolved dependency order and released every active resource; repeated start and stop added no effects.");
        });

    [Theory]
    [InlineData(DeploymentMode.Host, Traits = new[] { "Requires", "PostgreSQL", "Deployment", DeploymentMode.Host })]
    [InlineData(DeploymentMode.Container, Traits = new[] { "Deployment", DeploymentMode.Container })]
    public Task AC_001_R01_FailedInitializationCleansUpAndRecovers_Integration(string mode) =>
        RunAsync("AC-001-R01", mode, async (observations, token) =>
        {
            await using var scenario = await CompositionScenario.CreateAsync(CompositionScenario.Graph(), token);
            observations.Bind(scenario.Host);
            scenario.FailB = true;
            observations.Capture("before-fault", scenario);
            // The same ModuleHost is the application's hosted service. Exercise its failed activation before Kestrel
            // starts; recovery then starts the complete WebApplication and observes its readiness over HTTP.
            var exception = await Assert.ThrowsAsync<ModuleLifecycleException>(() => scenario.Modules.StartAsync(token));
            var failed = exception.Report;
            observations.Capture("failed", scenario);
            Assert.False(failed.IsReady);
            Assert.Equal(ModuleState.Failed, Assert.Single(failed.Modules, module => module.ModuleId == "B").State);
            Assert.Equal(ModuleState.Unavailable, Assert.Single(failed.Modules, module => module.ModuleId == "A").State);
            Assert.DoesNotContain(failed.Modules, module => module.State == ModuleState.Ready);
            Assert.Equal("MDC101", Assert.Single(failed.Diagnostics).Code);
            Assert.Equal(new[] { "B", "C" }, Shutdown(failed, 1));
            Assert.Empty(failed.RetainedInstances);
            Assert.Empty(scenario.ActiveResources);
            Assert.DoesNotContain(scenario.Calls, call => call.StartsWith("A:", StringComparison.Ordinal));
            var partial = await scenario.EffectsAsync(token);
            AssertEffects(partial, "B", "C");
            observations.Effects("failed-effects", partial);
            scenario.FailB = false;
            await scenario.Host.StartApplicationAsync(token);
            await ReadyOverHttpAsync(scenario.Host, token);
            var recovered = scenario.Modules.Report;
            observations.Capture("recovered", scenario);
            Assert.True(recovered.IsReady);
            Assert.All(recovered.Modules, module => Assert.Equal(ModuleState.Ready, module.State));
            Assert.Equal(5, scenario.Constructed);
            Assert.Equal(new[] { "C", "B", "A" }, recovered.Events.Where(item => item.Attempt == 2
                && item.Stage == ModuleStage.Initialization && item.Outcome == ModuleStageOutcome.Succeeded
                && CompositionScenario.Ids.Contains(item.ModuleId)).Select(item => item.ModuleId));
            var effects = await scenario.EffectsAsync(token);
            AssertEffects(effects, "A", "B", "C");
            Assert.Equal(partial, effects.Where(effect => effect.Module != "A").ToArray());
            observations.Effects("recovered-effects", effects);
            await scenario.Modules.StartAsync(token);
            Assert.Same(recovered, scenario.Modules.Report);
            Assert.Equal(5, scenario.Constructed);
            await scenario.Host.StopApplicationAsync(token);
            observations.Capture("stopped", scenario);
            Assert.Empty(scenario.ActiveResources);
            Assert.Empty(scenario.Modules.Report.RetainedInstances);
            Assert.Equal(recovered.Plan!.Modules.Select(module => module.Id).Reverse(), Shutdown(scenario.Modules.Report, 2));
            Assert.Empty(scenario.Modules.Report.Diagnostics);
            observations.Observed("B failed during initialization, A remained unavailable, and cleanup stopped B then C with no retained instance or active resource.");
            observations.Observed("The identical selection recovered to HTTP readiness; fresh database and filesystem reads found exactly one immutable effect per module, including B and C from the failed attempt.");
        });

    private static IEnumerable<string> Shutdown(ModuleHostReport report, int attempt) => report.Events
        .Where(item => item.Attempt == attempt && item.Stage == ModuleStage.Shutdown && item.Outcome == ModuleStageOutcome.Succeeded)
        .Select(item => item.ModuleId);

    private static void AssertEffects(EffectObservation[] effects, params string[] modules)
    {
        Assert.Equal(modules, effects.Select(effect => effect.Module));
        Assert.All(effects, effect => Assert.Equal(1, effect.Versions));
    }

    private static void AssertRefused(CompositionScenario scenario)
    {
        Assert.False(scenario.Modules.Report.IsReady);
        Assert.Null(scenario.Modules.Report.Plan);
        Assert.All(scenario.Modules.Report.Modules, module => Assert.Equal(ModuleState.Refused, module.State));
        Assert.Empty(scenario.Modules.Report.Events);
        Assert.Empty(scenario.Modules.Report.RetainedInstances);
        Assert.Equal(0, scenario.Constructed);
        Assert.Empty(scenario.Calls);
        Assert.Empty(scenario.ActiveResources);
    }

    private static async Task ReadyOverHttpAsync(SmokeHost host, CancellationToken token)
    {
        using var client = host.Client();
        using var response = await client.GetAsync("/health/ready", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(token));
    }

    /// <summary>
    /// The published sample image's own readiness, observed over HTTP from inside the composition fixture image. It is a
    /// container observation of the shipped image, not AC-001 coverage: that image carries no test module or composition
    /// switch, so the four cases' container rows run in the fixture image instead.
    /// </summary>
    [Fact]
    [Trait("Deployment", DeploymentMode.Container)]
    public async Task The_published_sample_image_is_ready_over_HTTP()
    {
        Assert.SkipWhen(DeploymentMode.Current() != DeploymentMode.Container, DeploymentMode.ContainerOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        var address = Environment.GetEnvironmentVariable("MODULARYN_SAMPLE_BASE_URL");
        Assert.False(string.IsNullOrWhiteSpace(address), "A container run names the published sample container in MODULARYN_SAMPLE_BASE_URL.");
        Assert.True(Uri.TryCreate(address, UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https"
            && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0,
            "The sample address must be an HTTP origin without credentials, path or query parameters.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        // The image listens on private HTTP behind the TLS proxy its configuration declares (the host README). Over HTTP this
        // run is that declared proxy, so it forwards the scheme; the image applies the header only from its declared proxy.
        if (origin.Scheme == Uri.UriSchemeHttp) request.Headers.Add("X-Forwarded-Proto", Uri.UriSchemeHttps);
        using var health = await client.SendAsync(request, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync(timeout.Token));
    }

    private static async Task RunAsync(string caseId, string mode, Func<CaseObservations, CancellationToken, Task> action)
    {
        // A row runs only in its own deployment: host rows on a runner or workstation, container rows inside the composition
        // fixture image. A skipped row writes no record.
        var current = DeploymentMode.Current();
        Assert.SkipWhen(mode == DeploymentMode.Container && current != DeploymentMode.Container, DeploymentMode.ContainerOnly);
        Assert.SkipWhen(mode == DeploymentMode.Host && current != DeploymentMode.Host,
            "Host rows run outside the composition fixture image, and this process runs inside it (MODULARYN_DEPLOYMENT_MODE=single-host-container).");
        Assert.Equal(current, mode);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var run = EvidenceRun.FromEnvironment();
        var observations = new CaseObservations();
        var outcome = EvidenceOutcome.Failed;
        try
        {
            await action(observations, timeout.Token);
            outcome = EvidenceOutcome.Passed;
        }
        finally
        {
            // All scenario disposal has completed before a successful outcome can reach the writer. No exception,
            // response, credential, log, trace or connection string is ever copied into an evidence artifact.
            if (run is not null) await EvidenceWriter.WriteAsync(run, caseId, mode, outcome, observations);
        }
    }
}

/// <summary>
/// The deployment a process runs in. The composition fixture image sets MODULARYN_DEPLOYMENT_MODE to single-host-container;
/// a runner or workstation leaves it unset. Any other value is refused rather than read as either deployment.
/// </summary>
internal static class DeploymentMode
{
    internal const string Variable = "MODULARYN_DEPLOYMENT_MODE";
    internal const string Host = "single-host";
    internal const string Container = "single-host-container";
    internal const string ContainerOnly = "Container rows and observations run only inside the composition fixture image, which sets "
        + "MODULARYN_DEPLOYMENT_MODE=single-host-container; CI's container-build job runs them there.";

    internal static string Current() => Environment.GetEnvironmentVariable(Variable) switch
    {
        null or "" => Host,
        Container => Container,
        _ => throw new InvalidOperationException(Variable + " must be unset or " + Container + "."),
    };
}

internal sealed class CaseObservations
{
    internal JsonArray Snapshots { get; } = [];
    internal List<string> Outcomes { get; } = [];
    internal int? ServerVersionNumber { get; private set; }
    internal IReadOnlyList<LockedFile>? ImplementationFiles { get; private set; }
    internal void Bind(SmokeHost host) => Bind(host.ServerVersionNumber, host.ImplementationFiles);

    /// <summary>The database version and implementation inventory a fixture host measured; every host of one case agrees.</summary>
    internal void Bind(int serverVersionNumber, IReadOnlyList<LockedFile> implementationFiles)
    {
        if (ServerVersionNumber is { } prior) Assert.Equal(prior, serverVersionNumber);
        ServerVersionNumber = serverVersionNumber;
        if (ImplementationFiles is { } files) Assert.Equal(files, implementationFiles);
        ImplementationFiles = implementationFiles;
    }
    internal void Observed(string outcome) => Outcomes.Add(outcome);
    internal void Effects(string stage, EffectObservation[] effects) => Snapshots.Add(JsonSerializer.SerializeToNode(new { stage, effects }));

    internal void Capture(string stage, CompositionScenario scenario)
    {
        var report = scenario.Modules.Report;
        // Retain only bounded, non-sensitive composition data. Diagnostic messages may contain exception text, so
        // retain codes and module identities; the input graph retains the exact constraint that a refusal names.
        Snapshots.Add(JsonSerializer.SerializeToNode(new
        {
            stage,
            selection = scenario.Selection.Select(module => new
            {
                module.Id, version = module.Version.ToString(),
                dependencies = module.Dependencies.Select(dependency => new { dependency.ModuleId, range = dependency.Versions.ToString() }),
                runtime = module.RuntimeVersions.ToString(), framework = module.FrameworkVersions.ToString(),
                providers = module.ProvidedProviders.Select(provider => new { provider.Name, version = provider.Version.ToString() }),
                requiredProviders = module.RequiredProviders.Select(provider => new { provider.Name, range = provider.Versions.ToString() })
            }),
            state = report.State.ToString(), report.IsReady,
            modules = report.Modules.Select(module => new { module.ModuleId, state = module.State.ToString() }),
            plan = report.Plan?.Modules.Select(module => new { module.Id, version = module.Version.ToString() }),
            events = report.Events.Select(item => new { item.Attempt, item.ModuleId, stage = item.Stage.ToString(), outcome = item.Outcome.ToString() }),
            diagnostics = report.Diagnostics.Select(item => new { item.Code, item.ModuleIds }),
            report.RetainedInstances, scenario.Constructed, calls = scenario.Calls.ToArray(), active = scenario.ActiveResources
        }));
    }
}
