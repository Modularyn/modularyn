using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Modularyn.Framework.Hosting;

public static class ModuleHealthChecks
{
    public const string LivenessTag = "modularyn.live";
    public const string ReadinessTag = "modularyn.ready";

    /// <summary>Maps independent liveness and readiness endpoints with status-only responses.</summary>
    public static IEndpointRouteBuilder MapModularynHealthChecks(this IEndpointRouteBuilder endpoints,
        string livenessPattern = "/health/live", string readinessPattern = "/health/ready")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapHealthChecks(livenessPattern, Options(LivenessTag)).WithMetadata(new HealthEndpoint());
        endpoints.MapHealthChecks(readinessPattern, Options(ReadinessTag)).WithMetadata(new HealthEndpoint());
        return endpoints;
    }

    internal static void Register(IServiceCollection services, ModuleRegistration[] modules)
    {
        var health = services.AddHealthChecks();
        health.AddCheck("modularyn.live", () => HealthCheckResult.Healthy(), [LivenessTag]);
        health.Add(new HealthCheckRegistration("modularyn.ready", provider =>
            new ReadinessCheck(provider.GetRequiredService<ModuleHost>()), HealthStatus.Unhealthy, [ReadinessTag]));
        foreach (var module in modules)
        foreach (var check in module.HealthChecks)
        {
            // Lengths make the two-part name unambiguous even if a declaration contains separators.
            var name = $"modularyn.module.{module.Manifest.Id.Length}:{module.Manifest.Id}.{check.Name.Length}:{check.Name}";
            health.Add(new HealthCheckRegistration(name, provider => new StatusCheck(
                provider.GetRequiredService<ModuleHost>(), check, provider), HealthStatus.Unhealthy, [ReadinessTag]));
        }
    }

    private static HealthCheckOptions Options(string tag) => new()
    {
        Predicate = registration => registration.Tags.Contains(tag),
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = static (context, report) =>
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            return context.Response.WriteAsync(report.Status.ToString(), context.RequestAborted);
        }
    };

    internal sealed class HealthEndpoint { }

    private sealed class ReadinessCheck(ModuleHost host) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(host.Report.IsReady && host.Work.IsAccepting ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
    }

    private sealed class StatusCheck(ModuleHost host, ModuleHealthCheckRegistration check, IServiceProvider services) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (!host.Report.IsReady || !host.Work.TryEnter(out var lease)) return HealthCheckResult.Unhealthy();
            using var work = lease;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease!.CancellationToken);
            try
            {
                var result = await check.Factory(services).CheckHealthAsync(context, cancellation.Token).ConfigureAwait(false);
                // Neither the health service's own logging nor HTTP serialization sees the
                // provider's description, exception or data. The provider owns its own logging.
                return host.Report.IsReady && host.Work.IsAccepting && Enum.IsDefined(result.Status)
                    ? new(result.Status) : HealthCheckResult.Unhealthy();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
            catch (Exception) { return HealthCheckResult.Unhealthy(); }
        }
    }
}
