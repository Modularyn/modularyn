using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;

namespace Modularyn.Framework.Authorization;

/// <summary>Declares the permission an endpoint requires. The endpoint itself refuses to run without the evaluator's permission.</summary>
public sealed record PermissionEndpointMetadata(string Permission);

/// <summary>
/// The explicit HTTP entry point of authorization (TM-G02-019, TM-G02-023). The declaration wraps the endpoint's request
/// delegate, so the endpoint runs only after the evaluator permits, whatever the pipeline contains: it does not rely on an
/// authorization middleware intercepting the request. Declare <c>RequireTenantMember()</c> too, so the request has a
/// tenant context.
/// </summary>
public static class AuthorizationEndpointConventionBuilderExtensions
{
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!PermissionNames.IsValid(permission)) throw new ArgumentException("Declare a stable permission identifier, such as item.read.", nameof(permission));
        var declared = new PermissionEndpointMetadata(permission);
        builder.WithMetadata(declared);
        builder.Add(endpoint =>
        {
            var inner = endpoint.RequestDelegate
                ?? throw new InvalidOperationException("Authorization cannot guard an endpoint without a request delegate; declare it on a routed endpoint.");
            endpoint.RequestDelegate = context => PermissionEndpointGuard.InvokeAsync(context, declared, inner);
        });
        return builder;
    }
}

/// <summary>
/// Runs a declared endpoint only when the evaluator permits its permission for the request's tenant context and principal.
/// A refusal answers with the decision's stable code: 401 without a principal or with one that is no longer current, 404
/// for a resource the caller may not see, 503 when authorization data is unavailable, and 403 otherwise.
/// </summary>
internal static partial class PermissionEndpointGuard
{
    internal static async Task InvokeAsync(HttpContext context, PermissionEndpointMetadata declared, RequestDelegate inner)
    {
        var tenant = context.RequestServices.GetService<TenantExecution>()?.Context;
        var evaluator = context.RequestServices.GetService<IPermissionEvaluator>();
        if (tenant is null || evaluator is null)
        {
            if (context.RequestServices.GetService<ILoggerFactory>() is { } loggers)
                Unguarded(loggers.CreateLogger(typeof(PermissionEndpointGuard).FullName!));
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }
        var principal = context.RequestServices.GetService<IExecutionPrincipalAccessor>()?.Current;
        var decision = await evaluator.AuthorizeAsync(new(tenant, principal, declared.Permission), context.RequestAborted).ConfigureAwait(false);
        if (decision.Permitted)
        {
            await inner(context).ConfigureAwait(false);
            return;
        }
        await RefuseAsync(context, decision).ConfigureAwait(false);
    }

    internal static Task RefuseAsync(HttpContext context, AuthorizationDecision decision)
    {
        context.Response.StatusCode = Status(decision.Code!);
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new Refusal(decision.Code!), context.RequestAborted);
    }

    internal static int Status(string code) => code switch
    {
        AuthorizationCodes.Unauthenticated or AuthorizationCodes.PrincipalNotCurrent => StatusCodes.Status401Unauthorized,
        AuthorizationCodes.ResourceNotFound => StatusCodes.Status404NotFound,
        AuthorizationCodes.Unavailable => StatusCodes.Status503ServiceUnavailable,
        AuthorizationCodes.NoTenantContext => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status403Forbidden
    };

    private sealed record Refusal(string Code);

    [LoggerMessage(EventId = 8303, Level = LogLevel.Error,
        Message = "An endpoint that requires a permission ran without a tenant context or an evaluator. Declare RequireTenantMember and register authorization.")]
    private static partial void Unguarded(ILogger logger);
}

/// <summary>
/// The standard ASP.NET Core policy integration (FND-015): a requirement that the same evaluator decides. A policy runs
/// only where the authorization middleware intercepts the request, so an endpoint protected by it alone is reported at
/// startup; declare <see cref="AuthorizationEndpointConventionBuilderExtensions.RequirePermission"/> on it as well.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission)
    {
        if (!PermissionNames.IsValid(permission)) throw new ArgumentException("Declare a stable permission identifier, such as item.read.", nameof(permission));
        Permission = permission;
    }

    public string Permission { get; }

    /// <summary>A policy that requires the permission, for <c>RequireAuthorization</c> and the authorization middleware.</summary>
    public static AuthorizationPolicy Policy(string permission) => new AuthorizationPolicyBuilder().AddRequirements(new PermissionRequirement(permission)).Build();
}

internal sealed class PermissionAuthorizationHandler(IPermissionEvaluator evaluator) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.Resource is not HttpContext http || http.RequestServices.GetService<TenantExecution>()?.Context is not { } tenant)
        {
            context.Fail();
            return;
        }
        var principal = http.RequestServices.GetService<IExecutionPrincipalAccessor>()?.Current;
        var decision = await evaluator.AuthorizeAsync(new(tenant, principal, requirement.Permission), http.RequestAborted).ConfigureAwait(false);
        if (decision.Permitted) context.Succeed(requirement);
        else context.Fail(new AuthorizationFailureReason(this, decision.Code!));
    }
}

/// <summary>How an endpoint is authorized, as the startup report classifies it.</summary>
public enum AuthorizationEntryPointKind
{
    /// <summary>The endpoint itself requires a permission from the evaluator.</summary>
    Permission = 1,

    /// <summary>The endpoint itself requires separately granted host authority for its operation.</summary>
    HostAuthority = 2,

    /// <summary>Anonymous by explicit declaration.</summary>
    Anonymous = 3,

    /// <summary>Tenant membership only, with no permission for the operation.</summary>
    MemberOnly = 4,

    /// <summary>Protected only by an authorization policy, which holds only where the middleware intercepts the request.</summary>
    InterceptionOnly = 5,

    /// <summary>No authorization declaration at all.</summary>
    Undeclared = 6
}

public sealed record AuthorizationEntryPoint(string Route, IReadOnlyList<string> Methods, AuthorizationEntryPointKind Kind, string? Permission);

/// <summary>
/// The invocation-path report (TM-G02-023): every routed endpoint with the way it is authorized. An endpoint that relies on
/// interception alone, or declares nothing, is a path the explicit entry points do not cover; the host logs each at startup.
/// </summary>
public static class AuthorizationEntryPoints
{
    public static IReadOnlyList<AuthorizationEntryPoint> Describe(EndpointDataSource endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.Endpoints.OfType<RouteEndpoint>().Select(Describe)
            .OrderBy(point => point.Route, StringComparer.Ordinal).ThenBy(point => string.Join(",", point.Methods), StringComparer.Ordinal).ToArray();
    }

    private static AuthorizationEntryPoint Describe(RouteEndpoint endpoint)
    {
        var metadata = endpoint.Metadata;
        var methods = metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Order(StringComparer.Ordinal).ToArray() ?? ["*"];
        var route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "";
        var tenancy = metadata.GetMetadata<TenantEndpointMetadata>();
        if (metadata.GetMetadata<PermissionEndpointMetadata>() is { } permission)
            return new(route, methods, AuthorizationEntryPointKind.Permission, permission.Permission);
        if (tenancy is { Access: TenantAccess.Host }) return new(route, methods, AuthorizationEntryPointKind.HostAuthority, null);
        if (metadata.GetMetadata<IAllowAnonymous>() is not null) return new(route, methods, AuthorizationEntryPointKind.Anonymous, null);
        if (metadata.GetMetadata<IAuthorizeData>() is not null || metadata.GetMetadata<AuthorizationPolicy>() is not null)
            return new(route, methods, AuthorizationEntryPointKind.InterceptionOnly, null);
        return new(route, methods, tenancy is null ? AuthorizationEntryPointKind.Undeclared : AuthorizationEntryPointKind.MemberOnly, null);
    }
}

/// <summary>
/// Logs, once the host has started and its pipeline and endpoints exist, every endpoint that the explicit authorization
/// entry points do not cover.
/// </summary>
internal sealed partial class AuthorizationEntryPointReporter(IServiceProvider services, ILogger<AuthorizationEntryPointReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IHostApplicationLifetime>() is { } lifetime) lifetime.ApplicationStarted.Register(Report);
        else Report();
        return Task.CompletedTask;
    }

    private void Report()
    {
        if (services.GetService<EndpointDataSource>() is not { } endpoints) return;
        foreach (var point in AuthorizationEntryPoints.Describe(endpoints))
        {
            if (point.Kind == AuthorizationEntryPointKind.InterceptionOnly) InterceptionOnly(logger, string.Join(",", point.Methods), point.Route);
            else if (point.Kind == AuthorizationEntryPointKind.Undeclared) Undeclared(logger, string.Join(",", point.Methods), point.Route);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 8304, Level = LogLevel.Warning,
        Message = "Endpoint {Methods} {Route} is protected only by an authorization policy, which holds only where the middleware intercepts it. Declare RequirePermission.")]
    private static partial void InterceptionOnly(ILogger logger, string methods, string route);

    [LoggerMessage(EventId = 8305, Level = LogLevel.Warning,
        Message = "Endpoint {Methods} {Route} declares no authorization. Declare RequirePermission, host authority or explicit anonymous access.")]
    private static partial void Undeclared(ILogger logger, string methods, string route);
}
