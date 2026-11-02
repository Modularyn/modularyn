using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Observability;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

// TM-G02-053 and TM-G02-054 against the real pipelines a module host runs: canaries of every recognized secret form, put
// where an application or a dependency would carelessly put them (a failing module's exception, a health check's
// description, exception and data, request headers, a handler's log message, values, scope and exception, and an
// unhandled exception), reach no console log line, no activity of any source, no lifecycle diagnostic or exception
// message, no probe or problem response and no host diagnostics report. Every canary is generated at run time.
[Collection("Diagnostic console")]
public sealed class CanaryTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TM_G02_053_No_canary_reaches_a_log_trace_lifecycle_diagnostic_probe_problem_or_report()
    {
        var canaries = Canaries();
        var joined = string.Join(" ", canaries);
        var seen = new ConcurrentQueue<string>();
        using var output = new ConsoleLogCapture();
        using var tracing = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => seen.Enqueue(Describe(activity))
        };
        ActivitySource.AddActivityListener(tracing);
        using var fixture = new LifecycleFixture();

        // A module whose start fails with an exception that carries every canary.
        var failing = true;
        fixture.DuringStage = (id, stage, _) => failing && stage == ModuleStage.Start
            ? throw new InvalidOperationException("The store at " + joined + " refused the module.") : Task.CompletedTask;
        var manifest = new ModuleManifest("A", LifecycleFixture.One, healthChecks: [new("Store", LifecycleFixture.One)]);
        var factory = Assert.Single(fixture.Registrations([manifest])).Factory;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddModularynModules(LifecycleFixture.Environment,
            [new(manifest, factory, [new ModuleHealthCheckRegistration("Store", _ => new CarelessCheck(joined))])], LifecycleFixture.Unverified);
        await using var application = builder.Build();
        application.UseModularynApi();
        application.UseRouting();
        application.UseModularynRequests();
        application.MapModularynHealthChecks();
        application.MapGet("/work", (HttpContext context, ILoggerFactory loggers) =>
        {
            var logger = loggers.CreateLogger("Careless.Handler");
            using (logger.BeginScope(new Dictionary<string, object?> { ["Authorization"] = context.Request.Headers.Authorization.ToString() }))
                logger.LogWarning(new InvalidOperationException("Handler saw " + joined), "Request {Credential} with cookie {Cookie} and query {Query}",
                    context.Request.Headers.Authorization.ToString(), context.Request.Headers.Cookie.ToString(), context.Request.QueryString.Value);
            throw new InvalidOperationException("Unhandled " + joined);
        }).AllowAnonymous().WithApiContract(new ApiEndpoint("GetWork", "GET", "/work", "Anonymous", null, false, null, null, Versioned: false));

        var host = application.Services.GetRequiredService<ModuleHost>();
        var refused = await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StartAsync(Cancellation));
        seen.Enqueue(refused.Message);
        foreach (var diagnostic in host.Report.Diagnostics) seen.Enqueue(diagnostic.Message);
        Assert.NotEmpty(host.Report.Diagnostics);

        // Started cleanly, the host serves a probe, a careless handler and its host diagnostics.
        failing = false;
        await application.StartAsync(Cancellation);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses))
        };
        foreach (var path in new[] { "/health/ready", "/health/live" })
        {
            using var probe = await client.GetAsync(path, Cancellation);
            seen.Enqueue(await probe.Content.ReadAsStringAsync(Cancellation));
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/work?grant=" + Uri.EscapeDataString(canaries[6])))
        {
            request.Headers.TryAddWithoutValidation("Authorization", canaries[0]);
            request.Headers.TryAddWithoutValidation("Cookie", canaries[4]);
            using var response = await client.SendAsync(request, Cancellation);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var problem = await response.Content.ReadAsStringAsync(Cancellation);
            Assert.Equal(ApiCodes.Unavailable, JsonDocument.Parse(problem).RootElement.GetProperty("code").GetString());
            seen.Enqueue(problem + string.Join("|", response.Headers.Select(header => header.Key + "=" + string.Join(",", header.Value))));
        }
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        seams.Authority.Grant("operator", AuditModule.DiagnosticsHostReadOperation);
        await using (var tenancy = UnitServices.Build(directory, seams))
        {
            var (scope, operatorScope) = await Units.EnterHostAsync(tenancy, "operator", AuditModule.DiagnosticsHostReadOperation.Id);
            await using (scope)
                seen.Enqueue(JsonSerializer.Serialize(await application.Services.GetRequiredService<ModuleHostDiagnostics>().ReadAsync(operatorScope, Cancellation)));
        }
        await application.StopAsync(Cancellation);
        await application.DisposeAsync();

        var console = output.Read();
        Assert.Contains("Careless.Handler", console, StringComparison.Ordinal);
        Assert.Contains(SecretForms.Redacted, console, StringComparison.Ordinal);
        Assert.NotEmpty(seen);
        foreach (var canary in canaries)
        {
            var escaped = JsonSerializer.Serialize(canary).Trim('"');
            Assert.DoesNotContain(canary, console, StringComparison.Ordinal);
            Assert.DoesNotContain(escaped, console, StringComparison.Ordinal);
            Assert.All(seen, text => Assert.DoesNotContain(canary, text, StringComparison.Ordinal));
            Assert.All(seen, text => Assert.DoesNotContain(escaped, text, StringComparison.Ordinal));
        }
    }

    /// <summary>One fresh canary of each recognized form; the first is an authorization header, the fifth a session cookie, the seventh a file grant.</summary>
    private static string[] Canaries() =>
    [
        "Bearer " + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24)),
        "Basic " + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)),
        "Password=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
        "pwd = '" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + "'",
        "__Host-Modularyn-Session=" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
        "mfg1_" + Guid.NewGuid().ToString("N") + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
        "CfDJ8" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48)),
        "ey" + "J" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(12)) + ".ey" + "J" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(18))
            + "." + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24))
    ];

    // Everything an exporter could read from a stopped activity.
    private static string Describe(Activity activity) => string.Join("|", new[] { activity.Source.Name, activity.DisplayName, activity.StatusDescription ?? "" }
        .Concat(activity.TagObjects.Select(tag => tag.Key + "=" + tag.Value))
        .Concat(activity.Events.SelectMany(entry => entry.Tags.Select(tag => entry.Name + ":" + tag.Key + "=" + tag.Value)))
        .Concat(activity.Baggage.Select(item => item.Key + "=" + item.Value)));

    private sealed class CarelessCheck(string secret) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Degraded("Store at " + secret, new InvalidOperationException(secret),
                new Dictionary<string, object> { ["connection"] = secret }));
    }
}
