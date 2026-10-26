using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Hosting;
using Xunit;
using static Modularyn.Framework.Configuration.Tests.TestSupport;

namespace Modularyn.Framework.Configuration.Tests;

public sealed class SecretStoreTests : IDisposable
{
    private readonly string root = TemporaryDirectory("secrets");
    private readonly string secrets;

    public SecretStoreTests()
    {
        secrets = Path.Combine(root, "mounted");
        Directory.CreateDirectory(secrets);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact(DisplayName = "TM-G02-047 secrets resolve only through references to the mounted store and stay redacted")]
    public async Task TM_G02_047_Secrets_resolve_only_through_references_to_the_mounted_store_and_stay_redacted()
    {
        var sentinel = Sentinel();
        File.WriteAllBytes(Path.Combine(secrets, "database-password"), Encoding.UTF8.GetBytes(sentinel));
        var logs = new CapturedLogs();
        using var host = Host(logs);
        await host.StartAsync(Cancellation);
        var options = host.Services.GetRequiredService<IOptions<SampleOptions>>().Value;
        var store = host.Services.GetRequiredService<ISecretStore>();
        Assert.IsType<FileSecretStore>(store);

        var value = await store.GetAsync(options.Password!, Cancellation);
        Assert.Equal(sentinel, value.RevealString());
        Assert.Equal(Encoding.UTF8.GetBytes(sentinel), value.RevealBytes().ToArray());
        Assert.Equal("[secret]", value.ToString());
        Assert.Equal("[secret]", $"{value}");
        var serialized = JsonSerializer.Serialize(new { options.Password, Value = value, Options = options });
        Assert.Contains("\"Value\":\"[secret]\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"Password\":\"secret:database-password\"", serialized, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecretValue>("\"" + sentinel + "\""));
        AssertAbsent(sentinel, logs, serialized, options.ToString());

        value.Dispose();
        Assert.Throws<ObjectDisposedException>(() => value.RevealBytes().Length);
        await host.StopAsync(Cancellation);
        AssertAbsent(sentinel, logs);
    }

    [Theory(DisplayName = "TM-G02-047 unavailable or escaping secrets are refused by reference name only")]
    [InlineData("missing", "Secret 'database-password' is not present in the secret store.")]
    [InlineData("empty", "Secret 'database-password' is empty.")]
    [InlineData("oversized", "Secret 'database-password' is larger than 65536 bytes.")]
    [InlineData("directory", "Secret 'database-password' is not present in the secret store.")]
    [InlineData("outside-link", "Secret 'database-password' is a link whose target is outside the secret store.")]
    [InlineData("discarded", "A discarded literal value is not a secret reference, so nothing was resolved.")]
    public async Task TM_G02_047_Unavailable_or_escaping_secrets_are_refused_by_reference_name_only(string mutation, string expected)
    {
        var sentinel = Sentinel();
        var path = Path.Combine(secrets, "database-password");
        var reference = SecretReference.Parse("secret:database-password");
        switch (mutation)
        {
            case "empty": File.WriteAllBytes(path, []); break;
            case "oversized": File.WriteAllText(path, sentinel + new string('x', FileSecretStore.MaximumBytes)); break;
            case "directory": Directory.CreateDirectory(path); break;
            case "outside-link":
                var outside = Path.Combine(root, "outside");
                File.WriteAllText(outside, sentinel);
                Link(() => File.CreateSymbolicLink(path, outside));
                break;
            case "discarded":
                reference = (SecretReference)System.ComponentModel.TypeDescriptor.GetConverter(typeof(SecretReference)).ConvertFrom(sentinel)!;
                Assert.False(reference.IsValid);
                Assert.Equal("secret:<discarded>", reference.ToString());
                Assert.Throws<InvalidOperationException>(() => reference.Name);
                break;
        }
        var store = new FileSecretStore(Options.Create(new SecretStoreOptions { Directory = secrets }));
        var exception = await Assert.ThrowsAsync<SecretResolutionException>(() => store.GetAsync(reference, Cancellation).AsTask());
        Assert.StartsWith(expected, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(root, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "TM-G02-047 a link inside the mounted store resolves to its target")]
    public async Task TM_G02_047_A_link_inside_the_mounted_store_resolves_to_its_target()
    {
        var sentinel = Sentinel();
        var data = Path.Combine(secrets, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "database-password"), sentinel);
        Link(() => File.CreateSymbolicLink(Path.Combine(secrets, "database-password"), Path.Combine(data, "database-password")));
        var store = new FileSecretStore(Options.Create(new SecretStoreOptions { Directory = secrets }));
        using var value = await store.GetAsync(SecretReference.Parse("secret:database-password"), Cancellation);
        Assert.Equal(sentinel, value.RevealString());
    }

    [Theory(DisplayName = "TM-G02-047 the secret store requires an existing, fully qualified mounted directory")]
    [InlineData("absent", "'Modularyn:Secrets:Directory' must name the directory where the deployment mounts its secrets.")]
    [InlineData("relative", "'Modularyn:Secrets:Directory' must be a fully qualified directory.")]
    [InlineData("missing", "'Modularyn:Secrets:Directory' names a directory that does not exist.")]
    public async Task TM_G02_047_The_secret_store_requires_an_existing_fully_qualified_mounted_directory(string mutation, string expected)
    {
        var logs = new CapturedLogs();
        var values = new Dictionary<string, string?>
        {
            ["Modularyn:Secrets:Directory"] = mutation switch { "absent" => null, "relative" => "mounted", _ => Path.Combine(root, "unmounted") }
        };
        var builder = Builder(values, logs);
        builder.Services.AddModularynSecrets(builder.Configuration);
        var probe = new ModuleProbe();
        probe.Add(builder.Services);
        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Cancellation));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, probe.Constructed);
    }

    [Fact(DisplayName = "TM-G02-047 secret values stay out of logs and lifecycle diagnostics")]
    public async Task TM_G02_047_Secret_values_stay_out_of_logs_and_lifecycle_diagnostics()
    {
        var sentinel = Sentinel();
        File.WriteAllText(Path.Combine(secrets, "database-password"), sentinel);
        var logs = new CapturedLogs();
        var resolved = new List<string>();
        using (var host = Host(logs, probe => probe.Initialize = async (services, token) =>
               {
                   var options = services.GetRequiredService<IOptions<SampleOptions>>().Value;
                   using var value = await services.GetRequiredService<ISecretStore>().GetAsync(options.Password!, token);
                   resolved.Add(value.RevealString());
               }))
        {
            await host.StartAsync(Cancellation);
            Assert.True(host.Services.GetRequiredService<ModuleHost>().Report.IsReady);
            await host.StopAsync(Cancellation);
        }
        Assert.Equal(new[] { sentinel }, resolved);

        using (var host = Host(logs, probe => probe.Initialize = async (services, token) =>
               {
                   using var value = await services.GetRequiredService<ISecretStore>().GetAsync(SecretReference.Parse("secret:absent"), token);
               }))
        {
            var exception = await Assert.ThrowsAsync<ModuleLifecycleException>(() => host.StartAsync(Cancellation));
            var diagnostic = Assert.Single(exception.Report.Diagnostics);
            Assert.Equal("MDC101", diagnostic.Code);
            Assert.Contains("SecretResolutionException: Secret 'absent' is not present in the secret store.", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(root, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
            AssertAbsent(sentinel, logs, diagnostic.Message, exception.ToString());
        }
        AssertAbsent(sentinel, logs);
    }

    private Microsoft.Extensions.Hosting.IHost Host(CapturedLogs logs, Action<ModuleProbe>? configure = null)
    {
        var builder = Builder(new Dictionary<string, string?>
        {
            ["Modularyn:Secrets:Directory"] = secrets,
            ["Sample:Name"] = "workspace",
            ["Sample:Password"] = "secret:database-password"
        }, logs);
        builder.Services.AddModularynSecrets(builder.Configuration);
        builder.Services.AddModularynDeploymentOptions<SampleOptions>(builder.Configuration, "Sample");
        var probe = new ModuleProbe();
        configure?.Invoke(probe);
        probe.Add(builder.Services);
        return builder.Build();
    }
}
