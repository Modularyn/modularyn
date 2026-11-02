using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Ingress;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

// TM-G02-053: a request's correlation is server-issued. A trace context the peer supplies is adopted only from a declared
// proxy, never from any other peer, and no inbound baggage reaches the request. TM-G02-010: a refusal that left no body is
// answered with a neutral code that claims nothing beyond its status, never a permission decision or a contract violation.
public sealed class CorrelationTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TM_G02_053_A_peer_supplied_trace_is_adopted_only_from_a_declared_proxy(bool declared)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var fixture = new LifecycleFixture();
        await using var application = Build(fixture, declared ? "127.0.0.1" : null);
        await application.StartAsync(Cancellation);
        using var client = Client(application);
        var trace = ActivityTraceId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/trace");
        request.Headers.Add("traceparent", $"00-{trace.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        request.Headers.Add("baggage", "tenant=" + Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request, Cancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation)).RootElement;
        var correlation = Assert.Single(response.Headers.GetValues(FrameworkApi.CorrelationHeader));
        // The response carries the trace the request ran under, which is the proxy's only when the proxy is declared.
        Assert.Equal(seen.GetProperty("trace").GetString(), correlation);
        Assert.Equal(declared, correlation == trace.ToHexString());
        Assert.Matches("^[0-9a-f]{32}$", correlation);
        Assert.Equal(0, seen.GetProperty("baggage").GetInt32());

        // Without any inbound context the server issues its own.
        using var plain = await client.GetAsync("/trace", Cancellation);
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(plain.Headers.GetValues(FrameworkApi.CorrelationHeader)));
        Assert.NotEqual(correlation, Assert.Single(plain.Headers.GetValues(FrameworkApi.CorrelationHeader)));
        await application.StopAsync(Cancellation);
    }

    [Fact]
    public async Task TM_G02_010_A_bare_refusal_is_answered_with_a_neutral_code()
    {
        using var fixture = new LifecycleFixture();
        await using var application = Build(fixture, null);
        await application.StartAsync(Cancellation);
        using var client = Client(application);
        foreach (var (method, path, status, code) in new[]
        {
            (HttpMethod.Get, "/missing", HttpStatusCode.NotFound, ApiCodes.NotFound),
            (HttpMethod.Get, "/refused/401", HttpStatusCode.Unauthorized, ApiCodes.Refused),
            (HttpMethod.Get, "/refused/403", HttpStatusCode.Forbidden, ApiCodes.Refused),
            (HttpMethod.Get, "/refused/400", HttpStatusCode.BadRequest, ApiCodes.Refused),
            (HttpMethod.Get, "/refused/413", HttpStatusCode.RequestEntityTooLarge, ApiCodes.Refused),
            (HttpMethod.Get, "/refused/503", HttpStatusCode.ServiceUnavailable, ApiCodes.Unavailable),
            (HttpMethod.Delete, "/trace", HttpStatusCode.MethodNotAllowed, ApiCodes.Refused)
        })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, path), Cancellation);
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation)).RootElement;
            Assert.Equal(code, problem.GetProperty("code").GetString());
            Assert.Equal(Assert.Single(response.Headers.GetValues(FrameworkApi.CorrelationHeader)), problem.GetProperty("correlationId").GetString());
        }
        await application.StopAsync(Cancellation);
    }

    [Theory]
    [InlineData("127.0.0.1", "203.0.113.9", true)]
    [InlineData("203.0.113.9", "203.0.113.9", false)]
    public async Task TM_G02_053_The_correlation_is_decided_once_from_the_connection_before_the_ingress_rewrites_the_peer(string proxy, string rewritten,
        bool adopted)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var fixture = new LifecycleFixture();
        // The ingress's forwarded headers replace the peer address between UseModularynApi and UseModularynRequests. Behind a
        // declared proxy the proxy's trace is kept end to end; a direct client whose address is rewritten to a declared
        // proxy's later in the pipeline is never adopted, because the decision was already made from its own connection.
        await using var application = Build(fixture, proxy, rewritten);
        await application.StartAsync(Cancellation);
        using var client = Client(application);
        foreach (var path in new[] { "/trace", "/refused/403" })
        {
            var trace = ActivityTraceId.CreateRandom();
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("traceparent", $"00-{trace.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
            using var response = await client.SendAsync(request, Cancellation);
            var correlation = Assert.Single(response.Headers.GetValues(FrameworkApi.CorrelationHeader));
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation)).RootElement;
            // What the endpoint ran under, or the refusal's problem, carries the same correlation as the header.
            Assert.Equal(correlation, path == "/trace" ? body.GetProperty("trace").GetString() : body.GetProperty("correlationId").GetString());
            Assert.Equal(adopted, correlation == trace.ToHexString());
            Assert.Matches("^[0-9a-f]{32}$", correlation);
        }
        await application.StopAsync(Cancellation);
    }

    private static WebApplication Build(LifecycleFixture fixture, string? proxy, string? rewritten = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()), LifecycleFixture.Unverified);
        if (proxy is not null) builder.Services.Configure<IngressOptions>(options => options.Proxies.Add(proxy));
        var application = builder.Build();
        application.UseModularynApi();
        // A stand-in for the ingress's forwarded-headers step, which replaces the peer address after UseModularynApi.
        if (rewritten is not null)
            application.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(rewritten);
                return next(context);
            });
        application.UseRouting();
        application.UseModularynRequests();
        application.MapGet("/trace", () => Results.Json(new { trace = Activity.Current?.TraceId.ToHexString(), baggage = Activity.Current?.Baggage.Count() ?? 0 }))
            .AllowAnonymous().WithApiContract(new ApiEndpoint("GetTrace", "GET", "/trace", "Anonymous", null, false, null, null, Versioned: false));
        // A bare refusal, as a middleware or a handler without a problem body would leave it.
        application.MapGet("/refused/{status}", (HttpContext context, int status) => { context.Response.StatusCode = status; })
            .AllowAnonymous().WithApiContract(new ApiEndpoint("GetRefused", "GET", "/refused/{status}", "Anonymous", null, false, null, null, Versioned: false));
        return application;
    }

    private static HttpClient Client(WebApplication application) => new()
    {
        BaseAddress = new Uri(Assert.Single(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses))
    };
}
