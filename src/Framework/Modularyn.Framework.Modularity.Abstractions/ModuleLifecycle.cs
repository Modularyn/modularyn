using System.Diagnostics;

namespace Modularyn.Framework.Modularity.Abstractions;

/// <summary>Clock and trace context shared by a module's lifecycle calls for one activation.</summary>
public sealed class ModuleContext
{
    public ModuleContext(ModuleManifest manifest, TimeProvider timeProvider, ActivityContext traceContext)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        TraceContext = traceContext;
    }
    public ModuleManifest Manifest { get; }
    public TimeProvider TimeProvider { get; }
    public ActivityContext TraceContext { get; }
    /// <summary>The activation's W3C trace identifier; correlation carries no authority.</summary>
    public string CorrelationId => TraceContext.TraceId.ToHexString();
}

public interface IModule
{
    /// <summary>
    /// Called once per activation attempt. Durable effects must be idempotent across attempts.
    /// StopAsync must release resources even when this call only partially completes.
    /// </summary>
    Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken);

    /// <summary>Completes when this module is ready for its dependents.</summary>
    Task StartAsync(ModuleContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Drains work and releases all resources owned by this instance. Honors cancellation;
    /// if interrupted or failed, it must support a subsequent cleanup call.
    /// </summary>
    Task StopAsync(ModuleContext context, CancellationToken cancellationToken);
}
