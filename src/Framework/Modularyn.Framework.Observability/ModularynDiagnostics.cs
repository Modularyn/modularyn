using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Modularyn.Framework.Observability;

/// <summary>
/// Shared diagnostic sources. Exporters subscribe by source and meter name and use
/// the application's ILogger providers. This package installs no exporter.
/// </summary>
public sealed class ModularynDiagnostics : IDisposable
{
    public const string SourceName = "Modularyn.Framework";
    public const string MeterName = "Modularyn.Framework";
    public const string LogCategory = "Modularyn.Framework.Observability";
    public const string OperationsInstrument = "modularyn.operations";
    public const string DurationInstrument = "modularyn.operation.duration";
    public const string LifecycleInstrument = "modularyn.module.lifecycle.events";

    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;
    private readonly IDiagnosticRedactor? redactor;
    private readonly Counter<long> operations;
    private readonly Histogram<double> duration;
    private readonly Counter<long> lifecycle;

    public ModularynDiagnostics(ILoggerFactory loggerFactory, TimeProvider timeProvider, IDiagnosticRedactor? redactor = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.redactor = redactor;
        logger = loggerFactory.CreateLogger(LogCategory);
        Source = new(SourceName);
        Meter = new(MeterName);
        operations = Meter.CreateCounter<long>(OperationsInstrument, "{operation}");
        duration = Meter.CreateHistogram<double>(DurationInstrument, "s");
        lifecycle = Meter.CreateCounter<long>(LifecycleInstrument, "{event}");
    }

    /// <summary>Owned by this instance; consumers attach listeners without disposing the source.</summary>
    public ActivitySource Source { get; }
    /// <summary>Owned by this instance; consumers attach listeners without disposing the meter.</summary>
    public Meter Meter { get; }

    /// <summary>
    /// Begins a unit of work using an explicit W3C parent, or the ambient context.
    /// Correlation remains available when no trace listener samples the operation.
    /// Correlation identifiers carry no authority. No baggage or request values are logged.
    /// </summary>
    public DiagnosticScope BeginOperation(DiagnosticOperation operation, ActivityContext parentContext = default)
    {
        var name = OperationName(operation);
        if (parentContext == default && Activity.Current is { IdFormat: ActivityIdFormat.W3C } current)
            parentContext = current.Context;
        var kind = operation == DiagnosticOperation.HttpRequest ? ActivityKind.Server : ActivityKind.Internal;
        var activity = Source.CreateActivity(name, kind, parentContext, idFormat: ActivityIdFormat.W3C);
        if (activity is null)
        {
            activity = new Activity(name);
            if (parentContext != default)
            {
                activity.SetParentId(parentContext.TraceId, parentContext.SpanId, parentContext.TraceFlags);
                activity.TraceStateString = parentContext.TraceState;
            }
        }
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.SetStartTime(timeProvider.GetUtcNow().UtcDateTime);
        activity.Start();
        IDisposable? scope = null;
        try
        {
            scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["CorrelationId"] = activity.TraceId.ToHexString(),
                ["TraceId"] = activity.TraceId.ToHexString(),
                ["SpanId"] = activity.SpanId.ToHexString()
            });
            logger.LogInformation(new EventId(8000, "OperationBegan"),
                "Operation {Operation} began with correlation {CorrelationId} and span {SpanId}.",
                name, activity.TraceId.ToHexString(), activity.SpanId.ToHexString());
            return new(this, activity, scope, name, timeProvider.GetTimestamp());
        }
        catch
        {
            try { scope?.Dispose(); }
            finally { activity.Dispose(); }
            throw;
        }
    }

    /// <summary>Emits structural lifecycle metadata only, through the redaction extension point.</summary>
    public void RecordLifecycle(DiagnosticOperation operation, DiagnosticOutcome outcome, string moduleId, string moduleVersion)
    {
        var name = OperationName(operation);
        var result = OutcomeName(outcome);
        var id = redactor is null ? moduleId : redactor.Redact(DiagnosticField.ModuleId, moduleId);
        var version = redactor is null ? moduleVersion : redactor.Redact(DiagnosticField.ModuleVersion, moduleVersion);
        var tags = new ActivityTagsCollection { ["operation"] = name, ["outcome"] = result };
        if (id is not null) tags.Add("module.id", id);
        if (version is not null) tags.Add("module.version", version);
        Activity.Current?.AddEvent(new ActivityEvent(name, timeProvider.GetUtcNow(), tags));
        logger.Log(outcome is DiagnosticOutcome.Failed or DiagnosticOutcome.Cancelled ? LogLevel.Warning : LogLevel.Information,
            new EventId(8002, "ModuleLifecycle"),
            "Module {ModuleId} version {ModuleVersion} operation {Operation} outcome {Outcome} with correlation {CorrelationId} and span {SpanId}.",
            id, version, name, result, Activity.Current?.TraceId.ToHexString(), Activity.Current?.SpanId.ToHexString());
        // Measurements are made inside the active trace for exporter exemplars. Identifiers
        // never become metric labels; each instrument has a finite operation/outcome vocabulary.
        lifecycle.Add(1, new("operation", name), new("outcome", result));
    }

    internal void Complete(DiagnosticScope scope, string name, long started, DiagnosticOutcome outcome)
    {
        var result = OutcomeName(outcome);
        scope.Activity.SetEndTime(timeProvider.GetUtcNow().UtcDateTime);
        scope.Activity.SetTag("outcome", result);
        scope.Activity.SetStatus(outcome == DiagnosticOutcome.Succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        operations.Add(1, new("operation", name), new("outcome", result));
        duration.Record(timeProvider.GetElapsedTime(started).TotalSeconds, new("operation", name), new("outcome", result));
        logger.Log(outcome == DiagnosticOutcome.Succeeded ? LogLevel.Information : LogLevel.Warning,
            new EventId(8001, "OperationEnded"), "Operation {Operation} ended with {Outcome}, correlation {CorrelationId} and span {SpanId}.",
            name, result, scope.CorrelationId, scope.Activity.SpanId.ToHexString());
    }

    private static string OperationName(DiagnosticOperation operation) => operation switch
    {
        DiagnosticOperation.HostStart => "host.start",
        DiagnosticOperation.HostStop => "host.stop",
        DiagnosticOperation.ModuleConstruction => "module.construct",
        DiagnosticOperation.ModuleInitialization => "module.initialize",
        DiagnosticOperation.ModuleStart => "module.start",
        DiagnosticOperation.ModuleStop => "module.stop",
        DiagnosticOperation.HttpRequest => "http.request",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static string OutcomeName(DiagnosticOutcome outcome) => outcome switch
    {
        DiagnosticOutcome.Began => "began",
        DiagnosticOutcome.Succeeded => "succeeded",
        DiagnosticOutcome.Failed => "failed",
        DiagnosticOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    public void Dispose()
    {
        Source.Dispose();
        Meter.Dispose();
    }
}

/// <summary>An operation fails unless explicitly completed; dispose in the creating execution context.</summary>
public sealed class DiagnosticScope : IDisposable
{
    private readonly ModularynDiagnostics owner;
    private readonly IDisposable? logScope;
    private readonly string name;
    private readonly long started;
    private DiagnosticOutcome outcome = DiagnosticOutcome.Failed;
    private bool disposed;

    internal DiagnosticScope(ModularynDiagnostics owner, Activity activity, IDisposable? logScope, string name, long started)
    {
        this.owner = owner;
        Activity = activity;
        this.logScope = logScope;
        this.name = name;
        this.started = started;
    }

    public Activity Activity { get; }
    public string CorrelationId => Activity.TraceId.ToHexString();
    public void Succeed() => outcome = DiagnosticOutcome.Succeeded;
    public void Cancel() => outcome = DiagnosticOutcome.Cancelled;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { owner.Complete(this, name, started, outcome); }
        finally
        {
            try { logScope?.Dispose(); }
            finally { Activity.Dispose(); }
        }
    }
}
