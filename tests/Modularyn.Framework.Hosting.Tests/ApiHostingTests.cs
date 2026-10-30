using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

// The API conventions that every module host registers: the inventory check and the status-only, anonymous health probes.
public sealed class ApiHostingTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "TM-G02-011 an undeclared endpoint never answers: the host refuses to start before its server listens")]
    public async Task TM_G02_011_An_undeclared_endpoint_never_answers_because_startup_is_refused_before_the_server_listens()
    {
        using var fixture = new LifecycleFixture();
        await using var application = Build(fixture);
        var answered = 0;
        application.UseRouting();
        application.MapModularynHealthChecks();
        application.MapGet("/undeclared", () => { Interlocked.Increment(ref answered); return "content"; });

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => application.StartAsync(Cancellation));
        Assert.Contains("inventory declaration", refused.Message, StringComparison.Ordinal);
        // Kestrel records each address as it binds one. None was bound, so no connection, and no request, ever reached the host.
        Assert.Empty(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Assert.Equal(0, answered);
    }

    [Fact(DisplayName = "TM-G02-011 health probes answer GET and HEAD with the status only, and refuse other methods")]
    public async Task TM_G02_011_Health_probes_answer_GET_and_HEAD_with_the_status_only_and_refuse_other_methods()
    {
        using var fixture = new LifecycleFixture();
        await using var application = Build(fixture);
        application.UseRouting();
        application.UseModularynRequests();
        application.MapModularynHealthChecks();
        await application.StartAsync(Cancellation);
        using var client = Client(application);
        foreach (var path in new[] { "/health/live", "/health/ready" })
        {
            using var get = await client.GetAsync(path, Cancellation);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("text/plain", get.Content.Headers.ContentType?.MediaType);
            Assert.Equal("Healthy", await get.Content.ReadAsStringAsync(Cancellation));

            using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path), Cancellation);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal("text/plain", head.Content.Headers.ContentType?.MediaType);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync(Cancellation));

            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options })
            {
                using var other = await client.SendAsync(new HttpRequestMessage(method, path), Cancellation);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, other.StatusCode);
                Assert.Empty(await other.Content.ReadAsByteArrayAsync(Cancellation));
            }
        }
        await application.StopAsync(Cancellation);
    }

    [Fact(DisplayName = "TM-G02-011 health probes are deliberately anonymous, so a host's fallback authorization policy does not cover them")]
    public async Task TM_G02_011_Health_probes_stay_anonymous_under_a_host_fallback_authorization_policy()
    {
        using var fixture = new LifecycleFixture();
        await using var application = Build(fixture, services =>
        {
            services.AddAuthentication(NoCredential.SchemeName).AddScheme<AuthenticationSchemeOptions, NoCredential>(NoCredential.SchemeName, _ => { });
            services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        });
        application.UseRouting();
        application.UseAuthentication();
        application.UseAuthorization();
        application.UseModularynRequests();
        application.MapModularynHealthChecks();
        await application.StartAsync(Cancellation);
        using var client = Client(application);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, "/health/ready"), Cancellation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        await application.StopAsync(Cancellation);
    }

    private static WebApplication Build(LifecycleFixture fixture, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()), LifecycleFixture.Unverified);
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    private static HttpClient Client(WebApplication application) => new()
    {
        BaseAddress = new Uri(Assert.Single(application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses))
    };

    private sealed class NoCredential(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "no-credential";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
