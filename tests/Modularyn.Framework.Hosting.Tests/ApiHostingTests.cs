using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

// The API conventions that every module host registers.
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

    private static WebApplication Build(LifecycleFixture fixture, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddModularynModules(LifecycleFixture.Environment, fixture.Registrations(LifecycleFixture.Graph()), LifecycleFixture.Unverified);
        services?.Invoke(builder.Services);
        return builder.Build();
    }
}
