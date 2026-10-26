using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modularyn.Framework.Observability;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

[Collection("Diagnostic console")]
public sealed class ObservabilityTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task W3c_context_correlates_console_logs_trace_events_metrics_and_module_callbacks()
    {
        using var output = new ConsoleLogCapture();
        using var logs = LoggerFactory.Create(builder => builder.AddJsonConsole(options => options.IncludeScopes = true));
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        using var diagnostics = new ModularynDiagnostics(logs, clock);
        var traces = new ConcurrentQueue<Activity>();
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, diagnostics.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = traces.Enqueue
        };
        ActivitySource.AddActivityListener(tracing);
        var measurements = new ConcurrentQueue<(string Instrument, double Value, string Trace, KeyValuePair<string, object?>[] Tags)>();
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, diagnostics.Meter)) listener.EnableMeasurementEvents(instrument);
            }
        };
        metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, Activity.Current!.TraceId.ToHexString(), tags.ToArray())));
        metrics.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, Activity.Current!.TraceId.ToHexString(), tags.ToArray())));
        metrics.Start();
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, isRemote: true);
        using var incoming = new Activity("incoming").SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags).Start();
        using var fixture = new LifecycleFixture();
        fixture.DuringStage = async (_, _, _) =>
        {
            await Task.Yield();
            Assert.Equal(parent.TraceId, Activity.Current!.TraceId);
            clock.Advance(TimeSpan.FromSeconds(1));
        };
        var host = fixture.Host(clock: clock, telemetry: diagnostics);
        await host.StartAsync(Cancellation);
        await host.StopAsync(Cancellation);
        logs.Dispose();

        var correlation = parent.TraceId.ToHexString();
        Assert.All(fixture.Observations, item => Assert.Equal(correlation, item.Context.CorrelationId));
        Assert.All(host.Report.Events, item =>
        {
            Assert.Equal(correlation, item.CorrelationId);
            Assert.Equal(correlation, item.TraceId);
            Assert.Equal(16, item.SpanId.Length);
        });
        Assert.Equal(new[] { "host.start", "host.stop" }, traces.Select(trace => trace.OperationName));
        Assert.All(traces, trace => Assert.Equal(parent.TraceId, trace.TraceId));
        Assert.Equal(new[] { 6d, 3d }, traces.Select(trace => trace.Duration.TotalSeconds));
        Assert.Equal(24, traces.Sum(trace => trace.Events.Count()));
        Assert.All(measurements, measurement =>
        {
            Assert.Equal(correlation, measurement.Trace);
            Assert.Equal(new[] { "operation", "outcome" }, measurement.Tags.Select(tag => tag.Key));
        });
        Assert.Equal(2, measurements.Count(measurement => measurement.Instrument == ModularynDiagnostics.OperationsInstrument));
        Assert.Equal(new[] { 6d, 3d }, measurements.Where(measurement => measurement.Instrument == ModularynDiagnostics.DurationInstrument).Select(measurement => measurement.Value));
        var lines = output.Read().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.Equal(ModularynDiagnostics.LogCategory, root.GetProperty("Category").GetString());
            var scope = Assert.Single(root.GetProperty("Scopes").EnumerateArray(), item => item.TryGetProperty("CorrelationId", out _));
            Assert.Equal(correlation, scope.GetProperty("CorrelationId").GetString());
            Assert.False(root.TryGetProperty("Exception", out _));
        }
    }

    [Fact]
    public async Task Lifecycle_failures_log_only_the_structural_outcome()
    {
        using var output = new ConsoleLogCapture();
        using var logs = LoggerFactory.Create(builder => builder.AddJsonConsole(options => options.IncludeScopes = true));
        using var diagnostics = new ModularynDiagnostics(logs, TimeProvider.System);
        using var fixture = new LifecycleFixture();
        fixture.DuringStage = (_, stage, _) => stage == ModuleStage.Start
            ? throw new InvalidOperationException("private-failure-content") : Task.CompletedTask;
        var host = fixture.Host(telemetry: diagnostics);
        var failure = await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StartAsync(Cancellation));
        Assert.Contains("private-failure-content", failure.Message, StringComparison.Ordinal);
        logs.Dispose();
        Assert.DoesNotContain("private-failure-content", output.Read(), StringComparison.Ordinal);
        Assert.Contains("failed", output.Read(), StringComparison.Ordinal);
        Assert.Empty(fixture.ActiveResources);
    }

    [Fact]
    public async Task Cancelled_start_has_a_distinct_trace_and_metric_outcome()
    {
        using var diagnostics = new ModularynDiagnostics(NullLoggerFactory.Instance, TimeProvider.System);
        var traces = new ConcurrentQueue<Activity>();
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, diagnostics.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = traces.Enqueue
        };
        ActivitySource.AddActivityListener(tracing);
        var outcomes = new ConcurrentQueue<string>();
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, diagnostics.Meter) && instrument.Name == ModularynDiagnostics.OperationsInstrument)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        metrics.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            outcomes.Enqueue((string)tags.ToArray().Single(tag => tag.Key == "outcome").Value!));
        metrics.Start();
        using var fixture = new LifecycleFixture();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        fixture.DuringStage = (_, stage, _) =>
        {
            if (stage == ModuleStage.Start) cancelled.Cancel();
            return Task.CompletedTask;
        };
        var host = fixture.Host(telemetry: diagnostics);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(cancelled.Token));
        Assert.Equal("cancelled", Assert.Single(traces).GetTagItem("outcome"));
        Assert.Equal("cancelled", Assert.Single(outcomes));
        Assert.Empty(fixture.ActiveResources);
        Assert.False(host.Report.IsReady);
    }
}
