using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Observability;

namespace Modularyn.Framework.Hosting;

/// <summary>Serializes lifecycle transitions for one fixed selection of installed modules.</summary>
public sealed class ModuleHost : IHostedLifecycleService, IDisposable
{
    private readonly ModuleRegistration[] registrations;
    private readonly CompositionSelection selection;
    private readonly ModuleComposer composer;
    private readonly IServiceProvider services;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupTimeout;
    private readonly TimeSpan? shutdownTimeout;
    private readonly ModularynDiagnostics telemetry;
    private readonly bool ownsTelemetry;
    private readonly bool ownsWorkGate;
    private readonly CancellationTokenRegistration applicationStopping;
    private readonly bool allowUnverifiedComposition;
    private readonly SemaphoreSlim transition = new(1, 1);
    private readonly List<Instance> instances = [];
    private readonly List<ModuleLifecycleEvent> events = [];
    private readonly List<CompositionDiagnostic> diagnostics = [];
    private readonly Dictionary<string, ModuleState> states = new(StringComparer.Ordinal);
    private ModuleHostReport report;
    private ModuleHostState hostState = ModuleHostState.Stopped;
    private ActivationPlan? plan;
    private int attempt;
    private ActivityContext activationContext;

    public ModuleHost(IEnumerable<ModuleRegistration> registrations, CompositionEnvironment environment,
        ModuleComposer composer, IServiceProvider services, TimeProvider timeProvider, ModuleHostOptions? options = null,
        ModularynDiagnostics? telemetry = null, ModuleWorkGate? workGate = null,
        IHostApplicationLifetime? lifetime = null, TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        this.registrations = registrations.ToArray();
        if (this.registrations.Any(item => item is null)) throw new ArgumentException("Registrations cannot contain null entries.", nameof(registrations));
        selection = new(this.registrations.Select(item => item.Manifest), environment);
        this.composer = composer ?? throw new ArgumentNullException(nameof(composer));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        cleanupTimeout = (options ?? new()).FailedStartCleanupTimeout;
        allowUnverifiedComposition = (options ?? new()).AllowUnverifiedComposition;
        if (cleanupTimeout <= TimeSpan.Zero || cleanupTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Failed-start cleanup requires a positive, finite timer deadline.");
        if (shutdownTimeout is { } timeout && timeout != Timeout.InfiniteTimeSpan
            && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        this.shutdownTimeout = shutdownTimeout;
        ownsTelemetry = telemetry is null;
        this.telemetry = telemetry ?? new ModularynDiagnostics(NullLoggerFactory.Instance, timeProvider);
        ownsWorkGate = workGate is null;
        Work = workGate ?? new();
        if (lifetime is not null)
            applicationStopping = lifetime.ApplicationStopping.Register(Work.StopAccepting);
        foreach (var registration in this.registrations) states[registration.Manifest.Id] = ModuleState.Selected;
        report = Snapshot();
    }

    public ModuleHostReport Report => Volatile.Read(ref report);
    public ModuleWorkGate Work { get; }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        Work.StopAccepting();
        return Task.CompletedTask;
    }
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        DiagnosticScope? operation = null;
        try
        {
            operation = telemetry.BeginOperation(DiagnosticOperation.HostStart);
            cancellationToken.ThrowIfCancellationRequested();
            if (hostState == ModuleHostState.Ready) { operation.Succeed(); return; }
            if (instances.Count != 0)
                throw new InvalidOperationException($"Cleanup is still required for {string.Join(", ", instances.Select(item => item.Manifest.Id))}. Call StopAsync successfully before restarting.");
            if (Work.ActiveCount != 0)
                throw new InvalidOperationException("Work from the previous activation still requires cleanup before restarting.");

            attempt++;
            activationContext = operation.Activity.Context;
            diagnostics.Clear();
            plan = null;
            foreach (var id in states.Keys.ToArray()) states[id] = ModuleState.Selected;
            SetHostState(ModuleHostState.Validating);
            if (!composer.VerifiesTrust && !allowUnverifiedComposition)
            {
                foreach (var id in states.Keys.ToArray()) states[id] = ModuleState.Refused;
                var refusal = new CompositionDiagnostic(ModuleHostDiagnosticCodes.TrustNotConfigured,
                    "No composition trust validator is registered, so the selection cannot be verified against a deployment lock. Register the release's deployment lock with AddModularynDeploymentTrust before activation.",
                    states.Keys.Order(StringComparer.Ordinal));
                diagnostics.Add(refusal);
                SetHostState(ModuleHostState.Failed);
                throw new ModuleCompositionException([refusal]);
            }
            CompositionResult composition;
            if (!composer.VerifiesMigrations && selection.Modules.Any(module => module.Migrations.Count > 0))
            {
                foreach (var id in states.Keys.ToArray()) states[id] = ModuleState.Refused;
                var refusal = new CompositionDiagnostic(ModuleHostDiagnosticCodes.MigrationsNotConfigured,
                    "Selected modules declare migrations, but no migration-completion validator is registered. Register the persistence migration gate before activation.",
                    selection.Modules.Where(module => module.Migrations.Count > 0).Select(module => module.Id));
                diagnostics.Add(refusal);
                SetHostState(ModuleHostState.Failed);
                throw new ModuleCompositionException([refusal]);
            }
            try
            {
                composition = await composer.ComposeAsync(selection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                foreach (var id in states.Keys.ToArray()) states[id] = ModuleState.Refused;
                var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
                if (cancelled) operation.Cancel();
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

            foreach (var registration in registrations)
            {
                if (!registration.Manifest.HealthChecks.Select(check => check.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(registration.HealthChecks.Select(check => check.Name)))
                    diagnostics.Add(new(ModuleHostDiagnosticCodes.HealthCheckBindingFailed,
                        "Bind each declared module health check exactly once, with no undeclared checks.", [registration.Manifest.Id]));
            }
            if (diagnostics.Count != 0)
            {
                foreach (var id in states.Keys.ToArray()) states[id] = ModuleState.Refused;
                SetHostState(ModuleHostState.Failed);
                throw new ModuleCompositionException(diagnostics);
            }

            plan = validatedPlan;
            SetHostState(ModuleHostState.Starting);
            var factories = registrations.ToDictionary(item => item.Manifest.Id, StringComparer.Ordinal);
            var traceContext = activationContext;
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
                Work.Open(applicationStopping.Token);
                operation.Succeed();
            }
            catch (Exception exception)
            {
                var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
                if (cancelled) operation.Cancel();
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
                var failedOperation = currentId is null ? "Module selection startup" : $"Module '{currentId}' {stage}";
                var message = $"{failedOperation} {(cancelled ? "was cancelled" : "failed")}: {exception.GetType().Name}: {exception.Message}. Complete cleanup and resolve the cause before restarting.";
                diagnostics.Add(new(code, message, currentId is null ? [] : [currentId]));
                SetHostState(ModuleHostState.Stopping);
                Work.StopAccepting();
                // Startup cancellation must not prevent release of resources already acquired.
                using var cleanup = new CancellationTokenSource(cleanupTimeout, timeProvider);
                await CleanupAsync(cleanup.Token).ConfigureAwait(false);
                SetHostState(ModuleHostState.Failed);
                if (cancelled) throw;
                throw new ModuleLifecycleException(message, Report, exception);
            }
        }
        finally
        {
            try { operation?.Dispose(); }
            finally { transition.Release(); }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var deadline = shutdownTimeout is { } timeout ? new CancellationTokenSource(timeout, timeProvider) : null;
        using var combined = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var shutdownToken = combined?.Token ?? cancellationToken;
        // An idle host can notify every owned module even with an already-cancelled token.
        // Waiting behind another lifecycle transition must still honor cancellation.
        if (!transition.Wait(0)) await transition.WaitAsync(shutdownToken).ConfigureAwait(false);
        DiagnosticScope? operation = null;
        try
        {
            operation = telemetry.BeginOperation(DiagnosticOperation.HostStop, activationContext);
            Work.StopAccepting();
            SetHostState(ModuleHostState.Stopping);
            try { await Work.DrainAsync(shutdownToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                Work.CancelRemaining();
                if (Work.ActiveCount != 0)
                    diagnostics.Add(new(ModuleHostDiagnosticCodes.WorkDrainIncomplete,
                        "Work did not drain before the shutdown deadline. Complete the retained work before restarting.", []));
                Publish();
            }
            var failures = await CleanupAsync(shutdownToken).ConfigureAwait(false);
            SetHostState(instances.Count == 0 && Work.ActiveCount == 0 ? ModuleHostState.Stopped : ModuleHostState.Failed);
            shutdownToken.ThrowIfCancellationRequested();
            if (failures.Count != 0)
                throw new ModuleLifecycleException("Module shutdown failed. Inspect the cleanup diagnostics and retry StopAsync before starting again.",
                    Report, new AggregateException(failures));
            operation.Succeed();
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { operation?.Cancel(); throw; }
        finally
        {
            try { operation?.Dispose(); }
            finally { transition.Release(); }
        }
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
                // Retain the actual callback task if it outlives the deadline. A retry observes
                // that same task instead of invoking StopAsync concurrently on the instance.
                instance.PendingStop ??= instance.Module.StopAsync(instance.Context, cancellationToken);
                ObserveFailure(instance.PendingStop);
                await instance.PendingStop.WaitAsync(cancellationToken).ConfigureAwait(false);
                instances.Remove(instance);
                SetModuleState(id, failedStart ? ModuleState.Failed : ModuleState.Stopped);
                Record(id, ModuleStage.Shutdown, ModuleStageOutcome.Succeeded);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                if (instance.PendingStop is { IsFaulted: true } or { IsCanceled: true }) instance.PendingStop = null;
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
        var activity = Activity.Current;
        events.Add(new(attempt, id, stage, outcome, timeProvider.GetUtcNow())
        {
            CorrelationId = activity?.TraceId.ToHexString() ?? string.Empty,
            TraceId = activity?.TraceId.ToHexString() ?? string.Empty,
            SpanId = activity?.SpanId.ToHexString() ?? string.Empty
        });
        Publish();
        var operation = stage switch
        {
            ModuleStage.Construction => DiagnosticOperation.ModuleConstruction,
            ModuleStage.Initialization => DiagnosticOperation.ModuleInitialization,
            ModuleStage.Start => DiagnosticOperation.ModuleStart,
            _ => DiagnosticOperation.ModuleStop
        };
        var result = outcome switch
        {
            ModuleStageOutcome.Began => DiagnosticOutcome.Began,
            ModuleStageOutcome.Succeeded => DiagnosticOutcome.Succeeded,
            ModuleStageOutcome.Cancelled => DiagnosticOutcome.Cancelled,
            _ => DiagnosticOutcome.Failed
        };
        telemetry.RecordLifecycle(operation, result, id, registrations.First(item => item.Manifest.Id == id).Manifest.Version.ToString());
    }
    private void Publish() => Volatile.Write(ref report, Snapshot());
    private ModuleHostReport Snapshot() => new(hostState, plan,
        states.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ModuleStatus(pair.Key, pair.Value)),
        events, diagnostics, instances.Select(item => item.Manifest.Id), Work.ActiveCount);

    private static void ObserveFailure(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Dispose()
    {
        applicationStopping.Dispose();
        if (ownsTelemetry) telemetry.Dispose();
        if (ownsWorkGate) Work.Dispose();
    }

    private sealed record Instance(ModuleManifest Manifest, IModule Module, ModuleContext Context)
    {
        internal Task? PendingStop { get; set; }
    }
}
