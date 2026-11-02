using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

/// <summary>Immediate file steps are revalidated just like background file steps, before opening Files' units.</summary>
public sealed class FileSteps(AuthorizedWorkRunner runner, IFiles files, TimeProvider clock)
{
    public const string GrantHeader = "X-Modularyn-File-Grant";
    public static readonly TimeSpan DownloadDeadline = TimeSpan.FromSeconds(15);

    public async Task<T> RunAsync<T>(HttpContext http, string permission,
        Func<IFiles, TenantContext, ExecutionPrincipal, CancellationToken, Task<T>> action)
    {
        var tenant = http.RequestServices.GetRequiredService<TenantExecution>().Context!;
        var principal = http.RequestServices.GetRequiredService<IExecutionPrincipalAccessor>().Current!;
        var binding = PropagationBinding.ForRow("workspace.immediate-file-step", Guid.NewGuid());
        // Captured only from this request's validated server context, never accepted from a request or payload.
        var reference = PropagatedTenantContext.Capture(tenant, clock).ToJson(binding);
        T result = default!;
        TenantWorkOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(principal, reference, binding, permission, async (unit, _, token) =>
            {
                result = await action(files, unit.Context, principal, token);
            }, http.RequestAborted);
        }
        catch (FileAccessDeniedException exception) { throw new ApiException(exception.Code, 403); }
        catch (FileStorageException exception)
        {
            throw exception.Code switch
            {
                FileStorageError.NotFound or FileStorageError.Quarantined or FileStorageError.GrantRefused => new ApiException(ApiCodes.NotFound, 404),
                FileStorageError.Conflict => new ApiException(ApiCodes.Conflict, 409),
                FileStorageError.InvalidName or FileStorageError.IntegrityMismatch or FileStorageError.InvalidRange => new ApiException(ApiCodes.Validation, 400),
                FileStorageError.InvalidLength => new ApiException(ApiCodes.Validation, 413),
                FileStorageError.UnsupportedContent => new ApiException(ApiCodes.Validation, 415),
                FileStorageError.QuotaExceeded => new ApiException(ApiCodes.Refused, 429),
                FileStorageError.InvalidScope => new ApiException(ApiCodes.Refused, 403),
                _ => new ApiException(ApiCodes.Unavailable, 503)
            };
        }
        if (outcome.Disposition != TenantWorkDisposition.Completed)
            throw new ApiException(outcome.Code ?? ApiCodes.Unavailable, outcome.Disposition == TenantWorkDisposition.Deferred ? 503 : 403);
        return result;
    }

    public async Task DownloadAsync(HttpContext http)
    {
        // The URL carries only the tenant. No query option is accepted, and the token is never formatted or attached to diagnostics.
        if (http.Request.QueryString.HasValue || http.Request.Headers[GrantHeader] is not [{ } raw]
            || !FileGrantToken.TryParse(raw, out var grant)) throw new ApiException(ApiCodes.Validation, 400);
        http.Request.Headers.Remove(GrantHeader);
        using var timeout = new CancellationTokenSource(DownloadDeadline, clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, timeout.Token);
        using var abort = timeout.Token.Register(http.Abort);
        var original = http.RequestAborted;
        http.RequestAborted = cancellation.Token;
        try
        {
            await RunAsync(http, FilePermissions.Read, async (service, tenant, principal, token) =>
            {
                await using var read = await service.RedeemGrantAsync(tenant, principal, grant, FileGrantPurpose.Download, cancellationToken: token);
                if (!TeamWorkspaceHost.ReleasedMediaTypes.Contains(read.Delivery.ContentType, StringComparer.Ordinal))
                    throw new ApiException(ApiCodes.NotFound, 404);
                foreach (var header in read.Delivery.Headers()) http.Response.Headers[header.Key] = header.Value;
                http.Response.ContentLength = read.Version.Length;
                await read.Content.CopyToAsync(http.Response.Body, token);
                await http.Response.Body.FlushAsync(token);
                return true;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { http.Abort(); }
        finally { http.RequestAborted = original; }
    }
}
