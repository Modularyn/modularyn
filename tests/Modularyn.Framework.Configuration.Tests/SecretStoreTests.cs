using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact(DisplayName = "TM-G02-047 a secret reference is masked in telemetry as Settings masks it, while configuration still binds it")]
    public async Task TM_G02_047_A_secret_reference_is_masked_in_telemetry_as_settings_mask_it_while_configuration_still_binds_it()
    {
        // SEC-08's decision: a reference is not the secret, but it names where one is kept, and Settings displays a secret
        // setting only as [secret]. So the log scrubber treats the reference form as a secret form too: every log event,
        // whatever carries it, shows [redacted] in its place, and audit records and durable payloads refuse it. Binding and
        // serialization of the options themselves keep it, since configuration needs it.
        var name = "db-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var reference = SecretReference.Parse("secret:" + name);
        var logs = new CapturedLogs();
        using var host = Host(logs);
        await host.StartAsync(Cancellation);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Any.Module");
        var options = host.Services.GetRequiredService<IOptions<SampleOptions>>().Value;
        logger.LogInformation("Using {Reference} for {Setting}", reference, "Sample:Password");
        logger.LogInformation("Options {Options}", JsonSerializer.Serialize(options));
        logger.LogInformation("Inline " + reference);
        using (logger.BeginScope(new Dictionary<string, object?> { ["Reference"] = reference.ToString() }))
            logger.LogWarning(new InvalidOperationException("Could not use " + reference), "Failed");
        await host.StopAsync(Cancellation);

        var everything = string.Join("\n", logs.Entries);
        Assert.DoesNotContain(reference.ToString(), everything, StringComparison.Ordinal);
        Assert.DoesNotContain("secret:database-password", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(name, everything, StringComparison.Ordinal);
        Assert.Contains("Using [redacted] for Sample:Password", everything, StringComparison.Ordinal);
        Assert.True(Observability.SecretForms.Contains(reference.ToString()));
        Assert.Equal(Observability.SecretForms.Redacted, Observability.SecretForms.Scrub(reference.ToString()));
        // The configuration contract's own texts for a discarded or expected reference name none, and are kept.
        Assert.False(Observability.SecretForms.Contains("secret:<discarded>"));
        // Configuration binds and serializes the reference itself; only telemetry masks it.
        Assert.Equal("secret:database-password", options.Password!.ToString());
        Assert.Contains("\"Password\":\"secret:database-password\"", JsonSerializer.Serialize(options), StringComparison.Ordinal);
    }

    // What every refusal of a referenced secret asks of the operator, since it cannot name the secret (SEC-08).
    private const string Remedy = " The secret's name is not reported: compare the mounted secret directory with the configuration's secret references.";

    [Theory(DisplayName = "TM-G02-047 unavailable or escaping secrets are refused for their reason, never by their name")]
    [InlineData("missing", "A referenced secret is not present in the secret store." + Remedy)]
    [InlineData("empty", "A referenced secret is empty." + Remedy)]
    [InlineData("oversized", "A referenced secret is larger than 65536 bytes." + Remedy)]
    [InlineData("directory", "A referenced secret is not present in the secret store." + Remedy)]
    [InlineData("outside-link", "A referenced secret is a link whose target is outside the secret store." + Remedy)]
    [InlineData("discarded", "A discarded literal value is not a secret reference, so nothing was resolved.")]
    public async Task TM_G02_047_Unavailable_or_escaping_secrets_are_refused_for_their_reason_never_by_their_name(string mutation, string expected)
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
        Assert.Equal(expected, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("database-password", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(root, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sentinel, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "TM-G02-047 a refused resolution, logged through the framework's logging and scrubber, carries no secret name")]
    public async Task TM_G02_047_A_refused_resolution_logged_through_the_framework_carries_no_secret_name()
    {
        // Names generated for this run, in no form the scrubber recognizes: the old refusal, Secret '<name>', passed it
        // untouched, so only the refusal's own text can keep a name out of the log.
        var missing = "db-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var empty = "db-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        Assert.Equal($"Secret '{missing}' is not present", Observability.SecretForms.Scrub($"Secret '{missing}' is not present"));
        File.WriteAllBytes(Path.Combine(secrets, empty), []);
        var logs = new CapturedLogs();
        using var host = Host(logs);
        await host.StartAsync(Cancellation);
        var store = host.Services.GetRequiredService<ISecretStore>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Any.Module");
        var refusals = new List<SecretResolutionException>();
        foreach (var name in new[] { missing, empty })
        {
            var refused = await Assert.ThrowsAsync<SecretResolutionException>(() => store.GetAsync(SecretReference.Parse(SecretReference.Scheme + name), Cancellation).AsTask());
            refusals.Add(refused);
            logger.LogError(refused, "Resolution refused: {Reason}", refused.Message);
        }
        await host.StopAsync(Cancellation);

        // Each logged refusal keeps its reason and its remedy, and no part of it, its exception included, names the secret.
        var logged = logs.Entries.Where(entry => entry.Contains("Resolution refused: ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, logged.Length);
        Assert.Contains(logged, entry => entry.Contains("A referenced secret is not present in the secret store." + Remedy, StringComparison.Ordinal));
        Assert.Contains(logged, entry => entry.Contains("A referenced secret is empty." + Remedy, StringComparison.Ordinal));
        var everything = string.Join("\n", logs.Entries.Concat(refusals.Select(refused => refused.ToString())));
        Assert.DoesNotContain(missing, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(empty, everything, StringComparison.Ordinal);
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
            Assert.Contains("SecretResolutionException: A referenced secret is not present in the secret store." + Remedy, diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("absent", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("absent", exception.ToString(), StringComparison.Ordinal);
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
