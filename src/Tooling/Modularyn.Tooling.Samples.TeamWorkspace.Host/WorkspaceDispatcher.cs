using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

public sealed class WorkspaceDispatcher(WorkspaceService workspace, WorkspaceHandlers handlers, AuthorizedWorkRunner runner,
    IPermissionEvaluator permissions, IServiceIdentityScopes identities, ModuleWorkGate gate, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim pass = new(1, 1);

    public async Task<int> RunAsync(CancellationToken token)
    {
        await pass.WaitAsync(token);
        try { return await RunPassAsync(token); }
        finally { pass.Release(); }
    }

    private async Task<int> RunPassAsync(CancellationToken token)
    {
        if (!gate.TryEnter(out var admission)) return 0;
        using var held = admission!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, held.CancellationToken);
        var ct = cancellation.Token;
        await using var identity = identities.Begin(WorkspaceDefinition.Worker);
        var execution = identity.Services.GetRequiredService<TenantExecution>();
        var decision = await execution.EstablishServiceHostAsync(identity.Principal, WorkspaceDefinition.Dispatch.Id, "Dispatch workspace work", ct);
        if (!decision.Succeeded) return 0;
        var host = decision.Context!;
        var pump = new EventPump(workspace.Events, workspace.Events, handlers.Activity, runner, clock, new EventRetryPolicy());
        await pump.DispatchAsync(host, identity.Principal, cancellationToken: ct);
        var applied = await pump.ConsumeAsync(host, identity.Principal, cancellationToken: ct);
        await new ScheduleWorker(workspace.Jobs, runner).MaterializeAsync(host, identity.Principal, cancellationToken: ct);
        return applied + await new JobWorker(workspace.Jobs, runner, permissions, handlers.Jobs).RunDueAsync(host, identity.Principal, cancellationToken: ct);
    }

    public void Dispose() => pass.Dispose();
}

internal sealed class WorkspaceWorker(WorkspaceDispatcher dispatcher, TimeProvider clock, ILogger<WorkspaceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await dispatcher.RunAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning(new EventId(9100), "Workspace work deferred; dependency unavailable."); }
        }
    }
}
