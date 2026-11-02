using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.BackgroundJobs.Domain;

namespace Modularyn.Modules.BackgroundJobs.Application;

/// <summary>The host invokes a bounded pass while its lifecycle admits background work.</summary>
public sealed class BackgroundJobRuntime(JobStore store, JobWorker jobs, ScheduleWorker schedules, IServiceIdentityScopes identities, ModuleWorkGate gate)
{
    public JobStore Store { get; } = store;

    public async Task<int> RunAsync(string serviceIdentity, CancellationToken cancellationToken = default)
    {
        if (!gate.TryEnter(out var entered)) return 0;
        using var admission = entered!;
        await using var identity = identities.Begin(serviceIdentity);
        var execution = identity.Services.GetRequiredService<TenantExecution>();
        var decision = await execution.EstablishServiceHostAsync(identity.Principal, BackgroundJobsDefinition.Dispatch.Id,
            "Dispatch due background jobs", cancellationToken).ConfigureAwait(false);
        if (!decision.Succeeded) throw new InvalidOperationException("The background dispatcher host scope was refused: " + decision.Code);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, admission.CancellationToken);
        await schedules.MaterializeAsync(decision.Context!, identity.Principal, cancellationToken: linked.Token).ConfigureAwait(false);
        return await jobs.RunDueAsync(decision.Context!, identity.Principal, cancellationToken: linked.Token).ConfigureAwait(false);
    }
}

public static class BackgroundJobsServiceCollectionExtensions
{
    public static IServiceCollection AddModularynBackgroundJobs(this IServiceCollection services, PostgreSqlModule module,
        IEnumerable<JobDefinition> definitions, JobPolicy? policy = null)
    {
        if (module.ModuleId != BackgroundJobsDefinition.ModuleId) throw new ArgumentException("Use the Background Jobs module.", nameof(module));
        var handlers = definitions.ToArray();
        services.AddSingleton(provider => new JobStore(module, provider.GetRequiredService<TimeProvider>(), policy ?? new JobPolicy(),
            provider.GetService<IAuditTrail>()));
        services.AddSingleton(provider => new JobWorker(provider.GetRequiredService<JobStore>(), provider.GetRequiredService<AuthorizedWorkRunner>(),
            provider.GetRequiredService<IPermissionEvaluator>(), handlers));
        services.AddSingleton<ScheduleWorker>();
        services.AddSingleton<BackgroundJobRuntime>();
        return services;
    }
}
