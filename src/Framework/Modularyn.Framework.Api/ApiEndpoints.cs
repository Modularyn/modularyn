using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Tenancy;

namespace Modularyn.Framework.Api;

public sealed class ApiException(string code, int status, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
}

public sealed record ApiEndpointMetadata(ApiEndpoint Contract, ApiInventory Inventory)
{
    public int OldestVersion => Inventory.OldestVersion;
    public int CurrentVersion => Inventory.CurrentVersion;
}

public static class ApiEndpoints
{
    private static readonly JsonSerializerOptions Reading = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static async Task<T> ReadRequestAsync<T>(HttpContext context) where T : class
    {
        if (!context.Request.HasJsonContentType()) throw new ApiException(ApiCodes.Validation, 415);
        return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, Reading, context.RequestAborted).ConfigureAwait(false)
            ?? throw new ApiException(ApiCodes.Validation, 400);
    }

    /// <summary>
    /// Registers the inventory check, which refuses a host whose routed endpoints are not all declared with a matching
    /// authorization classification while the host builds its request pipeline, before the server accepts any request.
    /// </summary>
    public static IServiceCollection AddModularynApi(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, ApiInventoryStartupCheck>());
        return services;
    }

    /// <summary>Place before ingress so even an early refusal retains a server-issued correlation.</summary>
    public static IApplicationBuilder UseModularynApi(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.Headers[FrameworkApi.CorrelationHeader] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        await next(context).ConfigureAwait(false);
        if (context.Response.StatusCode >= 400 && !context.Response.HasStarted
            && context.Response.ContentLength is null or 0)
        {
            var code = context.Response.StatusCode switch
            {
                400 => ApiCodes.Validation, 401 => "MAZ001", 403 => "MAZ005", 404 => ApiCodes.NotFound,
                409 => ApiCodes.Conflict, 426 => ApiCodes.Version, 503 => ApiCodes.Unavailable, _ => ApiCodes.Protocol
            };
            await ProblemAsync(context, code, context.Response.StatusCode).ConfigureAwait(false);
        }
    });

    /// <summary>Final convention: preserves the existing authorization delegate and refuses unclassified endpoints.</summary>
    public static TBuilder WithApiContract<TBuilder>(this TBuilder builder, ApiEndpoint contract, ApiInventory? inventory = null)
        where TBuilder : IEndpointConventionBuilder
    {
        inventory ??= FrameworkApi.Inventory;
        inventory.Validate();
        var declared = new ApiEndpointMetadata(contract, inventory);
        builder.WithMetadata(declared);
        builder.Finally(endpoint =>
        {
            if (endpoint is not RouteEndpointBuilder route || route.RoutePattern.RawText != contract.Route)
                throw new InvalidOperationException("The endpoint route must match its inventory declaration.");
            var methods = endpoint.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault()?.HttpMethods;
            if (methods is null || methods.Count != 1 || methods[0] != contract.Method)
                throw new InvalidOperationException("The endpoint method must match its inventory declaration.");
            var inner = endpoint.RequestDelegate ?? throw new InvalidOperationException("An API endpoint needs a handler.");
            endpoint.RequestDelegate = context => InvokeAsync(context, declared, inner);
        });
        return builder;
    }

    /// <summary>Call after mapping all endpoints. A host may generate its document from this exact active inventory.</summary>
    public static ApiInventory Describe(EndpointDataSource source)
    {
        var points = AuthorizationEntryPoints.Describe(source);
        var endpoints = source.Endpoints.OfType<RouteEndpoint>().ToArray();
        var declarations = new List<ApiEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var declared = endpoint.Metadata.GetMetadata<ApiEndpointMetadata>()
                ?? throw new InvalidOperationException("Every endpoint requires an API inventory declaration.");
            var point = points.Single(point => point.Route == endpoint.RoutePattern.RawText
                && point.Methods.SequenceEqual(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Order(StringComparer.Ordinal)));
            if (point.Kind.ToString() != declared.Contract.Authorization || point.Permission != declared.Contract.Permission
                || point.Kind is AuthorizationEntryPointKind.InterceptionOnly or AuthorizationEntryPointKind.Undeclared)
                throw new InvalidOperationException("The endpoint authorization wrapper must match its inventory classification.");
            declarations.Add(declared.Contract);
        }
        var models = endpoints.SelectMany(endpoint => endpoint.Metadata.GetMetadata<ApiEndpointMetadata>()!.Inventory.Models)
            .GroupBy(model => model.Name, StringComparer.Ordinal).Select(group =>
            {
                if (group.Select(model => JsonSerializer.Serialize(model)).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw new InvalidOperationException("An API model has conflicting declarations.");
                return group.First();
            }).OrderBy(model => model.Name, StringComparer.Ordinal).ToArray();
        var inventory = FrameworkApi.Inventory with { Models = models, Endpoints = declarations.OrderBy(endpoint => endpoint.Name, StringComparer.Ordinal).ToArray() };
        inventory.Validate();
        return inventory;
    }

    public static void MapModularynBootstrap(this IEndpointRouteBuilder endpoints) => endpoints
        .MapGet(FrameworkApi.Endpoint("GetConfiguration").Route, async (HttpContext context) =>
        {
            var tenant = context.RequestServices.GetRequiredService<TenantExecution>().Context!;
            var reader = ActivatorUtilities.CreateInstance<BootstrapReader>(context.RequestServices);
            return Results.Json(await reader.ReadAsync(tenant, context.RequestAborted).ConfigureAwait(false));
        }).RequireTenantMember().WithApiContract(FrameworkApi.Endpoint("GetConfiguration"));

    public static Guid OperationId(HttpContext context) => context.Request.Headers[FrameworkApi.OperationHeader] is [{ } value]
        && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new ApiException(ApiCodes.Validation, 400);

    private static async Task InvokeAsync(HttpContext context, ApiEndpointMetadata declared, RequestDelegate inner)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers[FrameworkApi.CorrelationHeader] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        // Authorization delegates are retained inside this wrapper; no inventory field grants permission.
        var endpoint = context.GetEndpoint();
        var source = new DefaultEndpointDataSource(endpoint!);
        var classification = AuthorizationEntryPoints.Describe(source).Single();
        if (classification.Kind.ToString() != declared.Contract.Authorization || classification.Permission != declared.Contract.Permission)
        {
            await ProblemAsync(context, ApiCodes.Protocol, 500).ConfigureAwait(false);
            return;
        }
        var originalCancellation = context.RequestAborted;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(originalCancellation, deadline.Token);
        context.RequestAborted = cancellation.Token;
        try
        {
            if (declared.Contract.Versioned)
            {
                var text = Convert.ToString(context.Request.RouteValues["version"], CultureInfo.InvariantCulture);
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                    || version < declared.OldestVersion || version > declared.CurrentVersion)
                    throw new ApiException(ApiCodes.Version, 426);
                context.Response.Headers[FrameworkApi.VersionHeader] = version.ToString(CultureInfo.InvariantCulture);
            }
            if (declared.Contract.Idempotent) context.Response.Headers[FrameworkApi.OperationHeader] = OperationId(context).ToString("D");
            await inner(context).ConfigureAwait(false);
        }
        catch (ApiException exception) when (!context.Response.HasStarted)
        { await ProblemAsync(context, exception.Code, exception.Status, exception.Errors).ConfigureAwait(false); }
        catch (JsonException) when (!context.Response.HasStarted)
        { await ProblemAsync(context, ApiCodes.Validation, 400).ConfigureAwait(false); }
        catch (BadHttpRequestException) when (!context.Response.HasStarted)
        { await ProblemAsync(context, ApiCodes.Validation, 400).ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception) when (!context.Response.HasStarted)
        {
            await ProblemAsync(context, declared.Contract.Mutation ? ApiCodes.OutcomeUnknown : ApiCodes.Unavailable, 503).ConfigureAwait(false);
        }
        finally { context.RequestAborted = originalCancellation; }
    }

    private static Task ProblemAsync(HttpContext context, string code, int status, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        var operation = Guid.TryParse(context.Request.Headers[FrameworkApi.OperationHeader], out var id) ? (Guid?)id : null;
        var correlation = context.Response.Headers[FrameworkApi.CorrelationHeader].ToString();
        return context.Response.WriteAsJsonAsync(new ApiProblem("urn:modularyn:problem:" + code, "The request could not be completed.", status,
            code, correlation, operation, errors), options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}

/// <summary>
/// Validates the complete inventory as the host builds its request pipeline. The web host runs every startup filter, and
/// with them the application's pipeline, which is where WebApplication adds its endpoint data sources to routing, before it
/// starts the server; so a refusal here means the server never listens and no endpoint answers any request.
/// </summary>
internal sealed class ApiInventoryStartupCheck(IServiceProvider services) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
    {
        next(builder);
        if (services.GetService<EndpointDataSource>() is { } endpoints) _ = ApiEndpoints.Describe(endpoints);
    };
}
