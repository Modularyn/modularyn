using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Modularyn.Framework.Observability;
using Xunit;

namespace Modularyn.Framework.Observability.Tests;

[Collection("Diagnostic console")]
public sealed class ObservabilityTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void Console_logs_retain_correlation_when_scope_rendering_is_disabled()
    {
        using var output = new ConsoleLogCapture();
        using var logs = LoggerFactory.Create(builder => builder.AddJsonConsole());
        using var diagnostics = new ModularynDiagnostics(logs, TimeProvider.System);
        string correlation;
        using (var operation = diagnostics.BeginOperation(DiagnosticOperation.HostStart))
        {
            correlation = operation.CorrelationId;
            diagnostics.RecordLifecycle(DiagnosticOperation.ModuleStart, DiagnosticOutcome.Succeeded, "A", "1.0.0");
            operation.Succeed();
        }
        logs.Dispose();
        var lines = output.Read().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var state = document.RootElement.GetProperty("State");
            Assert.Equal(correlation, state.GetProperty("CorrelationId").GetString());
            Assert.Equal(16, state.GetProperty("SpanId").GetString()!.Length);
        }
    }

    [Fact]
    public async Task Unsampled_operations_keep_distinct_W3c_roots_and_restore_ambient_context()
    {
        using var diagnostics = new ModularynDiagnostics(NullLoggerFactory.Instance, TimeProvider.System);
        using var sampling = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, diagnostics.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None
        };
        ActivitySource.AddActivityListener(sampling);
        var original = Activity.Current;
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None,
            traceState: "modularyn=active", isRemote: true);
        using (var operation = diagnostics.BeginOperation(DiagnosticOperation.HttpRequest, parent))
        {
            await Task.Yield();
            Assert.Equal(parent.TraceId.ToHexString(), operation.CorrelationId);
            Assert.Equal(parent.TraceState, operation.Activity.TraceStateString);
            Assert.Equal(ActivityIdFormat.W3C, operation.Activity.IdFormat);
            using (var child = diagnostics.BeginOperation(DiagnosticOperation.ModuleStart))
            {
                Assert.Equal(operation.CorrelationId, child.CorrelationId);
                Assert.NotEqual(operation.Activity.SpanId, child.Activity.SpanId);
                child.Succeed();
            }
            Assert.Same(operation.Activity, Activity.Current);
            operation.Succeed();
        }
        Assert.Same(original, Activity.Current);

        // Suppress the runner's ambient context when exercising independent entry points.
        Activity.Current = null;
        try
        {
            var roots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                using var operation = diagnostics.BeginOperation(DiagnosticOperation.HttpRequest);
                await Task.Yield();
                Assert.Equal(operation.Activity.Context, Activity.Current!.Context);
                operation.Succeed();
                return operation.CorrelationId;
            }, Cancellation)));
            Assert.Equal(8, roots.Distinct(StringComparer.Ordinal).Count());
            Assert.All(roots, root => Assert.Equal(32, root.Length));
            Assert.Null(Activity.Current);
        }
        finally { Activity.Current = original; }
    }

    [Fact]
    public void Redaction_seam_can_omit_structural_fields_before_logging_and_tracing()
    {
        using var output = new ConsoleLogCapture();
        using var logs = LoggerFactory.Create(builder => builder.AddJsonConsole(options => options.IncludeScopes = true));
        var redactor = new OmitMetadata();
        using var diagnostics = new ModularynDiagnostics(logs, TimeProvider.System, redactor);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, diagnostics.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using (var operation = diagnostics.BeginOperation(DiagnosticOperation.HostStart))
        {
            diagnostics.RecordLifecycle(DiagnosticOperation.ModuleStart, DiagnosticOutcome.Succeeded, "PrivateModule", "1.2.3");
            var entry = Assert.Single(operation.Activity.Events);
            Assert.DoesNotContain(entry.Tags, tag => tag.Key.StartsWith("module.", StringComparison.Ordinal));
            operation.Succeed();
        }
        logs.Dispose();
        Assert.Equal(new[] { DiagnosticField.ModuleId, DiagnosticField.ModuleVersion }, redactor.Fields);
        Assert.DoesNotContain("PrivateModule", output.Read(), StringComparison.Ordinal);
        Assert.DoesNotContain("1.2.3", output.Read(), StringComparison.Ordinal);
    }

    private sealed class OmitMetadata : IDiagnosticRedactor
    {
        internal List<DiagnosticField> Fields { get; } = [];
        public string? Redact(DiagnosticField field, string value) { Fields.Add(field); return null; }
    }
}
