using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Hosting;
using Xunit;
using static Modularyn.Framework.Configuration.Tests.TestSupport;

namespace Modularyn.Framework.Configuration.Tests;

public sealed class DeploymentOptionsTests
{
    [Theory(DisplayName = "TM-G02-048 invalid deployment options refuse startup before any module is constructed")]
    [InlineData("missing-required", "The Name field is required.")]
    [InlineData("out-of-range", "The field Port must be between 1 and 65535.")]
    [InlineData("unknown-key", "Configuration section 'Sample' contains keys that its options do not declare: 'Prot'. Remove or correct them.")]
    [InlineData("nested-unknown-key", "contains keys that its options do not declare: 'Tokn'.")]
    [InlineData("unconvertible", "The configuration value at 'Sample:Port' cannot be converted to System.Int32. The value is not reported.")]
    [InlineData("literal-secret", "'Sample:Password' must be a secret reference of the form 'secret:<name>'. The configured value was discarded and is not reported.")]
    [InlineData("literal-nested-secret", "'Sample:Nested:Token' must be a secret reference of the form 'secret:<name>'.")]
    [InlineData("literal-listed-secret", "'Sample:Keys:1' must be a secret reference of the form 'secret:<name>'.")]
    [InlineData("malformed-reference", "'Sample:Password' must be a secret reference of the form 'secret:<name>'.")]
    public async Task TM_G02_048_Invalid_deployment_options_refuse_startup_before_any_module_is_constructed(string mutation, string expected)
    {
        var sentinel = Sentinel();
        var values = Valid();
        switch (mutation)
        {
            case "missing-required": values.Remove("Sample:Name"); break;
            case "out-of-range": values["Sample:Port"] = "70000"; break;
            case "unknown-key": values["Sample:Prot"] = sentinel; break;
            case "nested-unknown-key": values["Sample:Nested:Tokn"] = sentinel; break;
            case "unconvertible": values["Sample:Port"] = sentinel; break;
            case "literal-secret": values["Sample:Password"] = sentinel; break;
            case "literal-nested-secret": values["Sample:Nested:Token"] = sentinel; break;
            case "literal-listed-secret": values["Sample:Keys:1"] = sentinel; break;
            default: values["Sample:Password"] = "secret:../" + sentinel; break;
        }
        var logs = new CapturedLogs();
        var builder = Builder(values, logs);
        builder.Services.AddModularynDeploymentOptions<SampleOptions>(builder.Configuration, "Sample");
        var probe = new ModuleProbe();
        probe.Add(builder.Services);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Cancellation));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, probe.Constructed);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        Assert.Equal(ModuleHostState.Stopped, host.Services.GetRequiredService<ModuleHost>().Report.State);
        AssertAbsent(sentinel, logs, exception.ToString(), string.Join("\n", exception.Failures));
    }

    [Fact(DisplayName = "TM-G02-048 deployment options bind once at startup and ignore later reloads")]
    public async Task TM_G02_048_Deployment_options_bind_once_at_startup_and_ignore_later_reloads()
    {
        var logs = new CapturedLogs();
        var builder = Builder(Valid(), logs);
        builder.Services.AddModularynDeploymentOptions<SampleOptions>(builder.Configuration, "Sample");
        var probe = new ModuleProbe();
        probe.Add(builder.Services);
        using var host = builder.Build();
        await host.StartAsync(Cancellation);
        Assert.Equal(1, probe.Constructed);
        var started = host.Services.GetRequiredService<IOptions<SampleOptions>>().Value;
        Assert.Equal(8443, started.Port);
        Assert.Equal("database-password", started.Password!.Name);
        Assert.Equal(new[] { "signing", "encryption" }, started.Keys.Select(key => key.Name));
        Assert.Equal("secret:database-password", started.Password.ToString());

        var configuration = (IConfigurationRoot)host.Services.GetRequiredService<IConfiguration>();
        configuration["Sample:Port"] = "9443";
        configuration["Sample:Password"] = "not-a-reference";
        configuration.Reload();
        Assert.Equal("9443", configuration["Sample:Port"]);
        Assert.Same(started, host.Services.GetRequiredService<IOptions<SampleOptions>>().Value);
        Assert.Equal(8443, host.Services.GetRequiredService<IOptionsMonitor<SampleOptions>>().CurrentValue.Port);
        using (var scope = host.Services.CreateScope())
        {
            var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<SampleOptions>>().Value;
            Assert.Equal(8443, snapshot.Port);
            Assert.Equal("database-password", snapshot.Password!.Name);
        }
        await host.StopAsync(Cancellation);
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddModularynDeploymentOptions<SampleOptions>(configuration, "Sample").Services
            .AddModularynDeploymentOptions<SampleOptions>(configuration, "Sample"));
    }

    [Theory(DisplayName = "TM-G02-047 secret references have one exact form and never echo a refused value")]
    [InlineData("secret:database-password", true)]
    [InlineData("secret:a", true)]
    [InlineData("secret:Smtp.Credential_2", true)]
    [InlineData("secret:", false)]
    [InlineData("Secret:database-password", false)]
    [InlineData("secret: database-password", false)]
    [InlineData("secret:database-password\n", false)]
    [InlineData("secret:../database-password", false)]
    [InlineData("secret:.hidden", false)]
    [InlineData("secret:trailing.", false)]
    [InlineData("secret:nested/name", false)]
    [InlineData("secret:nested\\name", false)]
    [InlineData("database-password", false)]
    public void TM_G02_047_Secret_references_have_one_exact_form_and_never_echo_a_refused_value(string value, bool valid)
    {
        Assert.Equal(valid, SecretReference.TryParse(value, out var reference));
        if (valid)
        {
            Assert.Equal(value, reference!.ToString());
            Assert.Equal(value["secret:".Length..], SecretReference.Parse(value).Name);
            Assert.Equal(reference, SecretReference.Parse(value));
        }
        else
        {
            var exception = Assert.Throws<FormatException>(() => SecretReference.Parse(value));
            Assert.DoesNotContain(value.Trim(), exception.Message.Replace("'secret:<name>'", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Binder_messages_that_do_not_match_a_known_shape_are_reported_without_detail()
    {
        Assert.Equal("Configuration section 'Sample' cannot be bound to SampleOptions. The values are not reported.",
            StrictBindingProbe.Describe("Sample", "Unexpected binder failure for value 'x'."));
        Assert.Equal("Configuration section 'Sample' cannot be bound to SampleOptions. The values are not reported.",
            StrictBindingProbe.Describe("Sample", "Failed to convert configuration value 'x' at 'Sample:Po'rt' to type 'System.Int32'."));
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Sample:Name"] = "workspace",
        ["Sample:Port"] = "8443",
        ["Sample:Password"] = "secret:database-password",
        ["Sample:Nested:Token"] = "secret:api-token",
        ["Sample:Keys:0"] = "secret:signing",
        ["Sample:Keys:1"] = "secret:encryption"
    };

    private static class StrictBindingProbe
    {
        internal static string Describe(string path, string message) => StrictBinding<SampleOptions>.Describe(path, message);
    }
}
