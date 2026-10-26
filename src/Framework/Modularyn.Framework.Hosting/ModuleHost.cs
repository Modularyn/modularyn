using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Hosting;

/// <summary>Serializes lifecycle transitions for one fixed selection of installed modules.</summary>
public sealed class ModuleHost : IHostedService
{
    private readonly ModuleRegistration[] registrations;
    private readonly CompositionSelection selection;
    private readonly ModuleComposer composer;
    private readonly IServiceProvider services;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupTimeout;
    private readonly SemaphoreSlim transition = new(1, 1);
    private readonly List<Instance> instances = [];
    private readonly List<ModuleLifecycleEvent> events = [];
    private readonly List<CompositionDiagnostic> diagnostics = [];
    private readonly Dictionary<string, ModuleState> states = new(StringComparer.Ordinal);
    private ModuleHostReport report;
    private ModuleHostState hostState = ModuleHostState.Stopped;
    private ActivationPlan? plan;
    private int attempt;

    public ModuleHost(IEnumerable<ModuleRegistration> registrations, CompositionEnvironment environment,
        ModuleComposer composer, IServiceProvider services, TimeProvider timeProvider, ModuleHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        this.registrations = registrations.ToArray();
        if (this.registrations.Any(item => item is null)) throw new ArgumentException("Registrations cannot contain null entries.", nameof(registrations));
        selection = new(this.registrations.Select(item => item.Manifest), environment);
        this.composer = composer ?? throw new ArgumentNullException(nameof(composer));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        cleanupTimeout = (options ?? new()).FailedStartCleanupTimeout;
        if (cleanupTimeout <= TimeSpan.Zero || cleanupTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Failed-start cleanup requires a positive, finite timer deadline.");
        foreach (var registration in this.registrations) states[registration.Manifest.Id] = ModuleState.Selected;
        report = Snapshot();
    }

    public ModuleHostReport Report => Volatile.Read(ref report);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hostState == ModuleHostState.Ready) return;
            if (instances.Count != 0)
                throw new InvalidOperationException($"Cleanup is still required for {string.Join(", ", instances.Select(item => item.Manifest.Id))}. Call StopAsync successfully before restarting.");

            attempt++;
            diagnostics.Clear();
            plan = null;
            foreach (var id in states.Keys.ToArray()) states[id] = ModuleState.Selected;
            SetHostState(ModuleHostState.Validating);
            CompositionResult composition;
            try
            {
                composition = await composer.ComposeAsync(selection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                foreach (var id in states.Keys.ToArray()) states[id] = ModuleState.Refused;
                var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
                diagnostics.Add(new(cancelled ? ModuleHostDiagnosticCodes.StartupCancelled : ModuleHostDiagnosticCodes.ValidationFailed,
                    cancelled ? "Composition validation was cancelled. Retry startup with an active cancellation token."
                        : $"Composition validation did not complete: {exception.GetType().Name}: {exception.Message}. Resolve the validation failure before retrying.", states.Keys));
                SetHostState(ModuleHostState.Failed);
                throw;
            }

            if (composition.Plan is not { } validatedPlan)
            {
                diagnostics.AddRange(composition.Diagnostics);
                foreach (var id in states.Keys.ToArray()) states[id] = ModuleState.Refused;
                SetHostState(ModuleHostState.Failed);
                throw new ModuleCompositionException(composition.Diagnostics);
            }

            plan = validatedPlan;
            SetHostState(ModuleHostState.Starting);
            var factories = registrations.ToDictionary(item => item.Manifest.Id, StringComparer.Ordinal);
            var traceContext = Activity.Current?.Context ?? default;
            string? currentId = null;
            var stage = ModuleStage.Construction;
            try
            {
                foreach (var manifest in plan.Modules)
                {
                    currentId = manifest.Id;
                    stage = ModuleStage.Construction;
                    cancellationToken.ThrowIfCancellationRequested();
                    Record(manifest.Id, stage, ModuleStageOutcome.Began);
                    var module = factories[manifest.Id].Factory(services)
                        ?? throw new InvalidOperationException("The module factory returned null.");
                    if (instances.Any(item => ReferenceEquals(item.Module, module)))
                        throw new InvalidOperationException("A module factory reused an instance from another registration. Return a fresh instance.");
                    var instance = new Instance(manifest, module, new ModuleContext(manifest, timeProvider, traceContext));
                    // Register ownership before initialization so partial initialization is also cleaned up.
                    instances.Add(instance);
                    Record(manifest.Id, stage, ModuleStageOutcome.Succeeded);

                    stage = ModuleStage.Initialization;
                    SetModuleState(manifest.Id, ModuleState.Initializing);
                    Record(manifest.Id, stage, ModuleStageOutcome.Began);
                    await module.InitializeAsync(instance.Context, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    SetModuleState(manifest.Id, ModuleState.Initialized);
                    Record(manifest.Id, stage, ModuleStageOutcome.Succeeded);

                    stage = ModuleStage.Start;
                    SetModuleState(manifest.Id, ModuleState.Starting);
                    Record(manifest.Id, stage, ModuleStageOutcome.Began);
                    await module.StartAsync(instance.Context, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    SetModuleState(manifest.Id, ModuleState.Ready);
                    Record(manifest.Id, stage, ModuleStageOutcome.Succeeded);
                }
                cancellationToken.ThrowIfCancellationRequested();
                SetHostState(ModuleHostState.Ready);
            }
            catch (Exception exception)
            {
                var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
                if (currentId is not null)
                {
                    states[currentId] = ModuleState.Failed;
                    Record(currentId, stage, cancelled ? ModuleStageOutcome.Cancelled : ModuleStageOutcome.Failed);
                }
                foreach (var id in states.Where(pair => pair.Value == ModuleState.Selected).Select(pair => pair.Key).ToArray())
                    states[id] = ModuleState.Unavailable;
                var code = cancelled ? ModuleHostDiagnosticCodes.StartupCancelled : stage switch
                {
                    ModuleStage.Construction => ModuleHostDiagnosticCodes.ConstructionFailed,
                    ModuleStage.Initialization => ModuleHostDiagnosticCodes.InitializationFailed,
                    _ => ModuleHostDiagnosticCodes.StartFailed
                };
                var operation = currentId is null ? "Module selection startup" : $"Module '{currentId}' {stage}";
                var message = $"{operation} {(cancelled ? "was cancelled" : "failed")}: {exception.GetType().Name}: {exception.Message}. Complete cleanup and resolve the cause before restarting.";
                diagnostics.Add(new(code, message, currentId is null ? [] : [currentId]));
                SetHostState(ModuleHostState.Stopping);
                // Startup cancellation must not prevent release of resources already acquired.
                using var cleanup = new CancellationTokenSource(cleanupTimeout, timeProvider);
                await CleanupAsync(cleanup.Token).ConfigureAwait(false);
                SetHostState(ModuleHostState.Failed);
                if (cancelled) throw;
                throw new ModuleLifecycleException(message, Report, exception);
            }
        }
        finally { transition.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // An idle host can notify every owned module even with an already-cancelled token.
        // Waiting behind another lifecycle transition must still honor cancellation.
        if (!transition.Wait(0)) await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetHostState(ModuleHostState.Stopping);
            var failures = await CleanupAsync(cancellationToken).ConfigureAwait(false);
            SetHostState(instances.Count == 0 ? ModuleHostState.Stopped : ModuleHostState.Failed);
            cancellationToken.ThrowIfCancellationRequested();
            if (failures.Count != 0)
                throw new ModuleLifecycleException("Module shutdown failed. Inspect the cleanup diagnostics and retry StopAsync before starting again.",
                    Report, new AggregateException(failures));
        }
        finally { transition.Release(); }
    }

    private async Task<List<Exception>> CleanupAsync(CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        foreach (var instance in instances.ToArray().Reverse())
        {
            var id = instance.Manifest.Id;
            var failedStart = states[id] == ModuleState.Failed;
            SetModuleState(id, ModuleState.Stopping);
            Record(id, ModuleStage.Shutdown, ModuleStageOutcome.Began);
            try
            {
                await instance.Module.StopAsync(instance.Context, cancellationToken).ConfigureAwait(false);
                instances.Remove(instance);
                SetModuleState(id, failedStart ? ModuleState.Failed : ModuleState.Stopped);
                Record(id, ModuleStage.Shutdown, ModuleStageOutcome.Succeeded);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                SetModuleState(id, ModuleState.CleanupFailed);
                Record(id, ModuleStage.Shutdown, exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? ModuleStageOutcome.Cancelled : ModuleStageOutcome.Failed);
                diagnostics.Add(new(ModuleHostDiagnosticCodes.ShutdownFailed,
                    $"Module '{id}' Shutdown did not complete: {exception.GetType().Name}: {exception.Message}. Retry StopAsync to release this instance before restarting.", [id]));
                Publish();
            }
        }
        return failures;
    }

    private void SetHostState(ModuleHostState state) { hostState = state; Publish(); }
    private void SetModuleState(string id, ModuleState state) { states[id] = state; Publish(); }
    private void Record(string id, ModuleStage stage, ModuleStageOutcome outcome)
    {
        events.Add(new(attempt, id, stage, outcome, timeProvider.GetUtcNow()));
        Publish();
    }
    private void Publish() => Volatile.Write(ref report, Snapshot());
    private ModuleHostReport Snapshot() => new(hostState, plan,
        states.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ModuleStatus(pair.Key, pair.Value)),
        events, diagnostics, instances.Select(item => item.Manifest.Id));

    private sealed record Instance(ModuleManifest Manifest, IModule Module, ModuleContext Context);
}
