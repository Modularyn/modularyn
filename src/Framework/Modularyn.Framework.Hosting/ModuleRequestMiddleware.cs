using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Modularyn.Framework.Observability;

namespace Modularyn.Framework.Hosting;

public static class ModuleRequestExtensions
{
    /// <summary>
    /// Place after routing and before module endpoints. Health endpoints remain reachable
    /// while normal requests require a work lease. ASP.NET Core supplies W3C request context.
    /// </summary>
    public static IApplicationBuilder UseModularynRequests(this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return application.UseMiddleware<ModuleRequestMiddleware>();
    }
}

internal sealed class ModuleRequestMiddleware
{
    private readonly RequestDelegate next;
    public ModuleRequestMiddleware(RequestDelegate next) => this.next = next;

    public async Task InvokeAsync(HttpContext context, ModuleWorkGate work, ModularynDiagnostics diagnostics)
    {
        using var operation = diagnostics.BeginOperation(DiagnosticOperation.HttpRequest);
        ModuleWorkLease? lease = null;
        try
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<ModuleHealthChecks.HealthEndpoint>() is null
                && !work.TryEnter(out lease))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Unhealthy", context.RequestAborted).ConfigureAwait(false);
                return;
            }
            using var cancellation = lease is null ? null : CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.CancellationToken);
            var original = context.RequestAborted;
            if (cancellation is not null) context.RequestAborted = cancellation.Token;
            try { await next(context).ConfigureAwait(false); }
            finally { context.RequestAborted = original; }
            if (context.Response.StatusCode < StatusCodes.Status500InternalServerError) operation.Succeed();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || lease?.CancellationToken.IsCancellationRequested == true)
        {
            operation.Cancel();
            throw;
        }
        finally { lease?.Dispose(); }
    }
}
