using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Modularyn.Modules.Identity.HttpApi.Tests;

// TM-G02-016: the session is how a request is authenticated. Startup refuses a host whose default authentication scheme,
// set as the authenticate default or the overall default, is another scheme than the session's, unless the host opts out
// explicitly, so a handler registered later cannot quietly become every request's identity.
public sealed class DefaultSchemeTests
{
    private const string Refusal = "authenticated by default through another scheme";

    [Theory]
    [InlineData("none", false, false)]
    [InlineData("authenticate", false, true)]
    [InlineData("overall", false, true)]
    [InlineData("authenticate", true, false)]
    [InlineData("overall", true, false)]
    public async Task TM_G02_016_Startup_refuses_another_default_scheme_unless_the_host_opts_out(string other, bool optOut, bool refused)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynIdentityHttp(optOut ? new IdentityHttpOptions { AllowOtherDefaultScheme = true } : null);
        if (other != "none")
            builder.Services.AddAuthentication(options =>
            {
                if (other == "authenticate") options.DefaultAuthenticateScheme = OtherHandler.Name;
                else options.DefaultScheme = OtherHandler.Name;
            }).AddScheme<AuthenticationSchemeOptions, OtherHandler>(OtherHandler.Name, _ => { });
        using var host = builder.Build();
        // This host registers neither the ingress nor the identity module, so startup is refused regardless; only the
        // default-scheme failure depends on the case.
        var failures = (await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken))).Failures;
        Assert.Equal(refused, failures.Any(failure => failure.Contains(Refusal, StringComparison.Ordinal)));
    }

    private sealed class OtherHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string Name = "other";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
