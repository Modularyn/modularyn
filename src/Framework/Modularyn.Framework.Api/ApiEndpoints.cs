using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

public static partial class ApiEndpoints
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
    /// <remarks>It also replaces the server's trace-context propagator, so no inbound trace context or baggage is extracted
    /// and every request's correlation is server-issued, or a declared proxy's (<see cref="RequestCorrelation"/>).</remarks>
    public static IServiceCollection AddModularynApi(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, ApiInventoryStartupCheck>());
        services.Replace(ServiceDescriptor.Singleton<System.Diagnostics.DistributedContextPropagator>(
            new ServerIssuedPropagator(System.Diagnostics.DistributedContextPropagator.CreateDefaultPropagator())));
        return services;
    }

    /// <summary>
    /// Place before ingress so even an early refusal retains a server-issued correlation: a trace context the peer
    /// supplied is adopted only from a declared proxy (<see cref="RequestCorrelation"/>). A refusal that left no body, such
    /// as the ingress's own or a missing route, is answered with a problem whose code says only what the status says:
    /// <see cref="ApiCodes.Refused"/> for every status but a missing route, a version mismatch or unavailability. It never
    /// claims a permission decision or a peer's contract violation.
    /// </summary>
    public static IApplicationBuilder UseModularynApi(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        using var correlation = RequestCorrelation.Begin(context);
        context.Response.Headers[FrameworkApi.CorrelationHeader] = RequestCorrelation.Current(context);
        await next(context).ConfigureAwait(false);
        if (context.Response.StatusCode >= 400 && !context.Response.HasStarted
            && context.Response.ContentLength is null or 0)
        {
            var code = context.Response.StatusCode switch
            {
                404 => ApiCodes.NotFound, 426 => ApiCodes.Version, 503 => ApiCodes.Unavailable, _ => ApiCodes.Refused
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
            // A GET declaration may also answer HEAD, which HTTP defines as GET without the response body.
            var methods = endpoint.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault()?.HttpMethods;
            if (methods is null || !(methods.Count == 1 && methods[0] == contract.Method
                || contract.Method == "GET" && methods.Count == 2 && methods.Contains("GET") && methods.Contains("HEAD")))
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
            // Each refusal names the routed endpoint it is about, its methods and route template, never a request value.
            var named = string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"]) + " " + endpoint.RoutePattern.RawText;
            var declared = endpoint.Metadata.GetMetadata<ApiEndpointMetadata>()
                ?? throw new InvalidOperationException("Every endpoint requires an API inventory declaration; " + named + " has none.");
            var point = points.Single(point => point.Route == endpoint.RoutePattern.RawText
                && point.Methods.SequenceEqual(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Order(StringComparer.Ordinal)));
            if (point.Kind.ToString() != declared.Contract.Authorization || point.Permission != declared.Contract.Permission
                || point.Kind is AuthorizationEntryPointKind.InterceptionOnly or AuthorizationEntryPointKind.Undeclared)
                throw new InvalidOperationException("The endpoint authorization wrapper must match its inventory classification; " + named + " does not.");
            if (OptsOutOfAntiforgery(declared.Contract, endpoint))
                throw new InvalidOperationException("A form post must keep its antiforgery validation; " + named + " does not.");
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

    // A form post, a page's form or a form upload, is declared with antiforgery, which the ingress validates unless the endpoint
    // opts out. The ingress reads the endpoint's last declaration, as this does, from its final metadata; an endpoint that opts
    // out is not the declared form post, so the inventory check refuses the host and the endpoint refuses each request.
    private static bool OptsOutOfAntiforgery(ApiEndpoint contract, Endpoint endpoint) =>
        contract.Antiforgery && endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: false };

    public static Guid OperationId(HttpContext context) => context.Request.Headers[FrameworkApi.OperationHeader] is [{ } value]
        && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new ApiException(ApiCodes.Validation, 400);

    private static async Task InvokeAsync(HttpContext context, ApiEndpointMetadata declared, RequestDelegate inner)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers[FrameworkApi.CorrelationHeader] = RequestCorrelation.Current(context);
        // Authorization delegates are retained inside this wrapper; no inventory field grants permission.
        var endpoint = context.GetEndpoint();
        var source = new DefaultEndpointDataSource(endpoint!);
        var classification = AuthorizationEntryPoints.Describe(source).Single();
        if (classification.Kind.ToString() != declared.Contract.Authorization || classification.Permission != declared.Contract.Permission
            || OptsOutOfAntiforgery(declared.Contract, endpoint!))
        {
            await ProblemAsync(context, ApiCodes.Protocol, 500).ConfigureAwait(false);
            return;
        }
        var originalCancellation = context.RequestAborted;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(originalCancellation, deadline.Token);
        context.RequestAborted = cancellation.Token;
        // Nothing the handler sends by a supported write path reaches the client until its media type is one the declaration
        // allows (TM-G02-011). A handler that casts the request's IHttpResponseFeature to the server's own body feature writes
        // past this one, as trusted in-process module code can (RR-G02-019).
        var originalBody = context.Features.Get<IHttpResponseBodyFeature>()!;
        var body = new DeclaredResponseBody(context, declared.Contract, originalBody);
        context.Features.Set<IHttpResponseBodyFeature>(body);
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
            // Bytes the handler left in the body's writer pass the same check on their way out.
            await body.CompleteWriterAsync(context.RequestAborted).ConfigureAwait(false);
            // A handler that caught the refusal and returned normally is refused all the same.
            if (body.Refused) await RefuseUndeclaredAsync(context, declared.Contract, originalBody).ConfigureAwait(false);
        }
        catch (Exception) when (body.Refused)
        { await RefuseUndeclaredAsync(context, declared.Contract, originalBody).ConfigureAwait(false); }
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
        finally
        {
            context.Features.Set(originalBody);
            context.RequestAborted = originalCancellation;
        }
    }

    // The handler sent a type its declaration does not allow, and none of its bytes has left. Before the response starts the
    // endpoint answers 500 with MAP008 in place of everything the handler set; once it has started, the connection is aborted.
    private static async Task RefuseUndeclaredAsync(HttpContext context, ApiEndpoint contract, IHttpResponseBodyFeature original)
    {
        context.Features.Set(original);
        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ApiEndpoints).FullName!);
        if (logger is not null) UndeclaredMediaType(logger, contract.Name);
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        context.Response.Clear();
        await ProblemAsync(context, ApiCodes.Protocol, 500).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 8400, Level = LogLevel.Error,
        Message = "The endpoint {Endpoint} sent a response type its inventory declaration does not allow, so the response was refused.")]
    private static partial void UndeclaredMediaType(ILogger logger, string endpoint);

    private static Task ProblemAsync(HttpContext context, string code, int status, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        var operation = Guid.TryParse(context.Request.Headers[FrameworkApi.OperationHeader], out var id) ? (Guid?)id : null;
        // The header and the body always carry the request's one correlation.
        var correlation = RequestCorrelation.Current(context);
        context.Response.Headers[FrameworkApi.CorrelationHeader] = correlation;
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
