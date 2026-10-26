using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Tenancy;

/// <summary>The request inputs that tenancy reads as tenant candidates.</summary>
public static class TenancyHttp
{
    /// <summary>A header that names a tenant by its canonical identifier.</summary>
    public const string HeaderName = "X-Modularyn-Tenant";

    /// <summary>A route value that names a tenant by its canonical identifier.</summary>
    public const string RouteValueName = "tenant";
}

/// <summary>Declares what an endpoint requires of its tenant context. An endpoint without it gets no tenant context.</summary>
public sealed record TenantEndpointMetadata
{
    internal TenantEndpointMetadata(TenantAccess access, string? operationId, string? reason)
    {
        Access = access;
        OperationId = operationId;
        Reason = reason;
    }

    public TenantAccess Access { get; }
    public string? OperationId { get; }
    public string? Reason { get; }
}

public static class TenancyEndpointConventionBuilderExtensions
{
    /// <summary>The endpoint runs in the tenant its candidates name, only for an authenticated current member.</summary>
    public static TBuilder RequireTenantMember<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new TenantEndpointMetadata(TenantAccess.Member, null, null));
        return builder;
    }

    /// <summary>The endpoint runs in host scope, only under separately granted host authority for the operation, recorded in audit first.</summary>
    public static TBuilder RequireHostAuthority<TBuilder>(this TBuilder builder, string operationId, string reason) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!PropagatedTenantContext.ValidOperation(operationId) || !PropagatedTenantContext.ValidReason(reason))
            throw new ArgumentException("Declare a host operation identity of letters, digits, '.', '-' or '_', and a reason.");
        builder.WithMetadata(new TenantEndpointMetadata(TenantAccess.Host, operationId, reason));
        return builder;
    }
}

public static class TenancyApplicationBuilderExtensions
{
    /// <summary>
    /// Establishes each request's tenant context from its endpoint's declaration. Place it after the ingress, routing and
    /// authentication, and before authorization and the endpoints.
    /// </summary>
    public static IApplicationBuilder UseModularynTenancy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService<ITenantDirectory>() is null)
            throw new InvalidOperationException("Call AddModularynTenancy before UseModularynTenancy.");
        if (app.ApplicationServices.GetService<PublicOrigins>() is null)
            throw new InvalidOperationException("Tenancy reads hosts through the ingress policy; call AddModularynIngress first.");
        return app.UseMiddleware<TenancyMiddleware>();
    }
}

/// <summary>
/// Collects the request's candidates (the configured origin's host, the route value and the header; resolution adds the
/// principal's tenant claims), has the unit's <see cref="TenantExecution"/> validate them, and refuses before any handler
/// when it refuses, with a status and no body. Malformed input answers 400 and an unauthenticated principal 401, before
/// any lookup. An unavailable directory answers 503. Every other refusal of a member endpoint answers 404, so an unknown
/// tenant, another tenant and a tenant the caller cannot use look alike; a host endpoint answers 403 or, when audit or a
/// seam is unavailable, 503. The context ends when the request leaves this middleware, on every path.
/// </summary>
internal sealed partial class TenancyMiddleware(RequestDelegate next, PublicOrigins origins, ILogger<TenancyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var execution = context.RequestServices.GetRequiredService<TenantExecution>();
        try
        {
            var declared = context.GetEndpoint()?.Metadata.GetOrderedMetadata<TenantEndpointMetadata>() ?? [];
            if (declared.Count == 0)
            {
                await next(context).ConfigureAwait(false);
                return;
            }
            var decision = declared.Distinct().Count() != 1
                ? null
                : await EstablishAsync(context, execution, declared[0]).ConfigureAwait(false);
            if (decision is not { Succeeded: true })
            {
                var code = decision?.Code ?? TenancyCodes.Conflict;
                Refused(logger, code);
                context.Response.StatusCode = Status(code, declared[0].Access);
                return;
            }
            await next(context).ConfigureAwait(false);
        }
        finally { execution.End(); }
    }

    private async ValueTask<TenantDecision> EstablishAsync(HttpContext context, TenantExecution execution, TenantEndpointMetadata declared)
    {
        // The host comes from configuration: the configured origin that matches the validated request host, never the raw header.
        if (origins.ForRequest(context.Request) is not { } origin) return TenantDecision.Refused(TenancyCodes.OriginNotConfigured);
        var candidates = new List<TenantCandidate> { new(TenantCandidateSource.Host, new Uri(origin).Host.ToLowerInvariant()) };
        if (context.Request.RouteValues.TryGetValue(TenancyHttp.RouteValueName, out var route))
            candidates.Add(new(TenantCandidateSource.Route, route as string ?? ""));
        foreach (var header in context.Request.Headers[TenancyHttp.HeaderName]) candidates.Add(new(TenantCandidateSource.Header, header ?? ""));
        var request = declared.Access == TenantAccess.Host
            ? TenantResolutionRequest.ForHostOperation(context.User, declared.OperationId!, declared.Reason!, candidates)
            : TenantResolutionRequest.ForMember(context.User, candidates);
        return await execution.EstablishAsync(request, context.RequestAborted).ConfigureAwait(false);
    }

    private static int Status(string code, TenantAccess access) => code switch
    {
        TenancyCodes.Malformed or TenancyCodes.Ambiguous => StatusCodes.Status400BadRequest,
        TenancyCodes.Unauthenticated => StatusCodes.Status401Unauthorized,
        TenancyCodes.HostAuthorityRefused or TenancyCodes.HostCandidates => StatusCodes.Status403Forbidden,
        TenancyCodes.AuditUnavailable or TenancyCodes.DirectoryUnavailable => StatusCodes.Status503ServiceUnavailable,
        TenancyCodes.SeamUnavailable when access == TenantAccess.Host => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status404NotFound
    };

    [LoggerMessage(EventId = 8100, Level = LogLevel.Information, Message = "Tenant context refused with {Code}.")]
    private static partial void Refused(ILogger logger, string code);
}
