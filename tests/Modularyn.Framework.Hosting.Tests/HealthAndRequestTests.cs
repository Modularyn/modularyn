using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

[Collection("Diagnostic console")]
public sealed class HealthAndRequestTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_checks_are_bound_to_manifests_and_readiness_follows_start_failure_and_stop()
    {
        using var fixture = new LifecycleFixture();
        using var dependency = new FileHealthCheck();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DuringStage = async (_, stage, token) =>
        {
            if (stage == ModuleStage.Start)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynModules(LifecycleFixture.Environment, [Registration(fixture, dependency)]);
        using var application = builder.Build();
        var host = application.Services.GetRequiredService<ModuleHost>();
        var health = application.Services.GetRequiredService<HealthCheckService>();
        var registrations = application.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Equal(3, registrations.Count);
        Assert.Equal(2, registrations.Count(item => item.Tags.Contains(ModuleHealthChecks.ReadinessTag)));
        Assert.Equal(HealthStatus.Healthy, (await Check(health, ModuleHealthChecks.LivenessTag)).Status);
        Assert.Equal(HealthStatus.Unhealthy, (await Check(health, ModuleHealthChecks.ReadinessTag)).Status);

        var startup = application.StartAsync(Cancellation);
        await entered.Task.WaitAsync(Cancellation);
        try
        {
            Assert.Equal(HealthStatus.Unhealthy, (await Check(health, ModuleHealthChecks.ReadinessTag)).Status);
            Assert.Equal(0, dependency.Calls);
        }
        finally { release.TrySetResult(); }
        await startup;
        Assert.Equal(HealthStatus.Healthy, (await Check(health, ModuleHealthChecks.ReadinessTag)).Status);
        Assert.True(dependency.Calls > 0);
        dependency.Remove();
        Assert.Equal(HealthStatus.Unhealthy, (await Check(health, ModuleHealthChecks.ReadinessTag)).Status);
        Assert.Equal(HealthStatus.Healthy, (await Check(health, ModuleHealthChecks.LivenessTag)).Status);
        dependency.Restore();
        Assert.Equal(HealthStatus.Healthy, (await Check(health, ModuleHealthChecks.ReadinessTag)).Status);

        Assert.True(host.Work.TryEnter(out var work));
        using (work)
        {
            var stopping = host.StopAsync(Cancellation);
            Assert.Equal(HealthStatus.Unhealthy, (await Check(health, ModuleHealthChecks.ReadinessTag)).Status);
            Assert.Equal(HealthStatus.Healthy, (await Check(health, ModuleHealthChecks.LivenessTag)).Status);
            work!.Dispose();
            await stopping;
        }
        fixture.FailStart.Add("A");
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StartAsync(Cancellation));
        Assert.Equal(HealthStatus.Unhealthy, (await Check(health, ModuleHealthChecks.ReadinessTag)).Status);
        Assert.Equal(HealthStatus.Healthy, (await Check(health, ModuleHealthChecks.LivenessTag)).Status);
        await application.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Missing_and_undeclared_health_bindings_refuse_start_before_module_construction()
    {
        foreach (var missing in new[] { true, false })
        {
            using var fixture = new LifecycleFixture();
            using var dependency = new FileHealthCheck();
            var manifest = new ModuleManifest("A", LifecycleFixture.One,
                healthChecks: missing ? [new("FileReady", LifecycleFixture.One)] : []);
            var factory = Assert.Single(fixture.Registrations([manifest])).Factory;
            var registration = new ModuleRegistration(manifest, factory,
                missing ? [] : [new ModuleHealthCheckRegistration("FileReady", _ => dependency)]);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddModularynModules(LifecycleFixture.Environment, [registration]);
            using var application = builder.Build();
            var error = await Assert.ThrowsAsync<ModuleCompositionException>(() => application.StartAsync(Cancellation));
            Assert.Equal(ModuleHostDiagnosticCodes.HealthCheckBindingFailed, Assert.Single(error.Diagnostics).Code);
            Assert.Equal(0, fixture.Constructed);
            Assert.False(application.Services.GetRequiredService<ModuleHost>().Work.IsAccepting);
        }
    }

    [Fact]
    public async Task Http_health_discloses_only_status_and_requests_retain_W3c_context_without_logging_content()
    {
        using var output = new ConsoleLogCapture();
        using var fixture = new LifecycleFixture();
        using var dependency = new FileHealthCheck();
        var trace = ActivityTraceId.CreateRandom();
        var parentSpan = ActivitySpanId.CreateRandom();
        var settings = Path.Combine(Path.GetTempPath(), "modularyn-settings-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(settings, "{\"ModuleMode\":\"private-configuration-content\"}", Cancellation);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", Args = [] });
            builder.Configuration.AddJsonFile(settings, optional: false, reloadOnChange: false);
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            builder.Services.AddModularynModules(LifecycleFixture.Environment, [Registration(fixture, dependency)]);
            await using (var application = builder.Build())
            {
                application.UseRouting();
                application.UseModularynRequests();
                application.MapModularynHealthChecks();
                application.MapPost("/work", async (HttpContext context) =>
                {
                    var correlation = Activity.Current!.TraceId;
                    await Task.Yield();
                    Assert.Equal(correlation, Activity.Current!.TraceId);
                    await context.Response.WriteAsync(correlation.ToHexString(), context.RequestAborted);
                });
                await application.StartAsync(Cancellation);
                var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses.Addresses)) };

                foreach (var status in new[] { HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy })
                {
                    dependency.Status = status;
                    using var response = await client.GetAsync("/health/ready", Cancellation);
                    Assert.Equal(status == HealthStatus.Healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
                    Assert.Equal(status.ToString(), await response.Content.ReadAsStringAsync(Cancellation));
                    Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
                    var health = await Check(application.Services.GetRequiredService<HealthCheckService>(), ModuleHealthChecks.ReadinessTag);
                    Assert.All(health.Entries.Values, entry =>
                    {
                        Assert.Null(entry.Exception);
                        Assert.Null(entry.Description);
                        Assert.Empty(entry.Data);
                    });
                }
                dependency.Throw = true;
                using (var failed = await client.GetAsync("/health/ready", Cancellation))
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
                    Assert.Equal("Unhealthy", await failed.Content.ReadAsStringAsync(Cancellation));
                }
                dependency.Throw = false;
                dependency.Status = HealthStatus.Healthy;

                using var request = new HttpRequestMessage(HttpMethod.Post, "/work");
                request.Headers.Add("traceparent", $"00-{trace.ToHexString()}-{parentSpan.ToHexString()}-01");
                request.Content = new StringContent("private-request-content", Encoding.UTF8, "text/plain");
                using (var response = await client.SendAsync(request, Cancellation))
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(trace.ToHexString(), await response.Content.ReadAsStringAsync(Cancellation));
                }

                var host = application.Services.GetRequiredService<ModuleHost>();
                await host.StoppingAsync(Cancellation);
                using (var refused = await client.PostAsync("/work", new StringContent("private-request-content"), Cancellation))
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
                using (var live = await client.GetAsync("/health/live", Cancellation))
                {
                    Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                    Assert.Equal("Healthy", await live.Content.ReadAsStringAsync(Cancellation));
                }
                using (var ready = await client.GetAsync("/health/ready", Cancellation))
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
                await application.StopAsync(Cancellation);
                Assert.Empty(fixture.ActiveResources);
            }
            var logs = output.Read();
            Assert.Contains(trace.ToHexString(), logs, StringComparison.Ordinal);
            Assert.DoesNotContain("private-health-content", logs, StringComparison.Ordinal);
            Assert.DoesNotContain("private-configuration-content", logs, StringComparison.Ordinal);
            Assert.DoesNotContain("private-request-content", logs, StringComparison.Ordinal);
        }
        finally { File.Delete(settings); }
    }

    private static Task<HealthReport> Check(HealthCheckService health, string tag) =>
        health.CheckHealthAsync(registration => registration.Tags.Contains(tag), Cancellation);

    private static ModuleRegistration Registration(LifecycleFixture fixture, FileHealthCheck dependency)
    {
        var manifest = new ModuleManifest("A", LifecycleFixture.One, healthChecks: [new("FileReady", LifecycleFixture.One)]);
        var factory = Assert.Single(fixture.Registrations([manifest])).Factory;
        return new(manifest, factory, [new ModuleHealthCheckRegistration("FileReady", _ => dependency)]);
    }

    private sealed class FileHealthCheck : IHealthCheck, IDisposable
    {
        private readonly string file = Path.Combine(Path.GetTempPath(), "modularyn-health-" + Guid.NewGuid().ToString("N"));
        private int calls;
        internal FileHealthCheck() => Restore();
        internal HealthStatus Status { get; set; } = HealthStatus.Healthy;
        internal bool Throw { get; set; }
        internal int Calls => Volatile.Read(ref calls);
        internal void Remove() => File.Delete(file);
        internal void Restore() => File.WriteAllText(file, "available");

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            if (Throw) throw new InvalidOperationException("private-health-content");
            return new(content == "available" ? Status : HealthStatus.Unhealthy, "private-health-content",
                new InvalidOperationException("private-health-content"), new Dictionary<string, object> { ["detail"] = "private-health-content" });
        }

        public void Dispose() => File.Delete(file);
    }
}
