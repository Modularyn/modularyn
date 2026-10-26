using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Hosting;

public enum ModuleHostState { Stopped, Validating, Starting, Ready, Stopping, Failed }
public enum ModuleState { Selected, Initializing, Initialized, Starting, Ready, Stopping, Stopped, Failed, Unavailable, Refused, CleanupFailed }
public enum ModuleStage { Construction, Initialization, Start, Shutdown }
public enum ModuleStageOutcome { Began, Succeeded, Failed, Cancelled }

public sealed record ModuleStatus(string ModuleId, ModuleState State);
public sealed record ModuleLifecycleEvent(int Attempt, string ModuleId, ModuleStage Stage,
    ModuleStageOutcome Outcome, DateTimeOffset Timestamp)
{
    public string CorrelationId { get; init; } = string.Empty;
    public string TraceId { get; init; } = string.Empty;
    public string SpanId { get; init; } = string.Empty;
}

/// <summary>An immutable observation; history includes prior failed activation attempts.</summary>
public sealed class ModuleHostReport
{
    internal ModuleHostReport(ModuleHostState state, ActivationPlan? plan, IEnumerable<ModuleStatus> modules,
        IEnumerable<ModuleLifecycleEvent> events, IEnumerable<CompositionDiagnostic> diagnostics, IEnumerable<string> retainedInstances,
        int pendingWork)
    {
        State = state;
        Plan = plan;
        Modules = Array.AsReadOnly(modules.ToArray());
        Events = Array.AsReadOnly(events.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        RetainedInstances = Array.AsReadOnly(retainedInstances.ToArray());
        PendingWork = pendingWork;
    }
    public ModuleHostState State { get; }
    public bool IsReady => State == ModuleHostState.Ready;
    public ActivationPlan? Plan { get; }
    public IReadOnlyList<ModuleStatus> Modules { get; }
    public IReadOnlyList<ModuleLifecycleEvent> Events { get; }
    public IReadOnlyList<CompositionDiagnostic> Diagnostics { get; }
    /// <summary>Instances still requiring shutdown, including any whose cleanup failed.</summary>
    public IReadOnlyList<string> RetainedInstances { get; }
    /// <summary>Outstanding work leases when this snapshot was published.</summary>
    public int PendingWork { get; }
}

public sealed class ModuleCompositionException : Exception
{
    public ModuleCompositionException(IEnumerable<CompositionDiagnostic> diagnostics)
        : this(diagnostics.ToArray()) { }

    private ModuleCompositionException(CompositionDiagnostic[] diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(item => $"{item.Code}: {item.Message}"))) =>
        Diagnostics = Array.AsReadOnly(diagnostics);

    public IReadOnlyList<CompositionDiagnostic> Diagnostics { get; }
}

public sealed class ModuleLifecycleException : Exception
{
    public ModuleLifecycleException(string message, ModuleHostReport report, Exception innerException)
        : base(message, innerException) => Report = report;

    public ModuleHostReport Report { get; }
}

public static class ModuleHostDiagnosticCodes
{
    public const string ConstructionFailed = "MDC100";
    public const string InitializationFailed = "MDC101";
    public const string StartFailed = "MDC102";
    public const string ShutdownFailed = "MDC103";
    public const string StartupCancelled = "MDC104";
    public const string ValidationFailed = "MDC105";
    public const string WorkDrainIncomplete = "MDC106";
    public const string HealthCheckBindingFailed = "MDC107";
    public const string TrustNotConfigured = "MDC108";
    public const string MigrationsNotConfigured = "MDC200";
}
