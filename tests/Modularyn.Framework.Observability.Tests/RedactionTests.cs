using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Observability;
using Xunit;

namespace Modularyn.Framework.Observability.Tests;

// TM-G02-053: the secret forms the framework recognizes are scrubbed from every log event before any provider sees it,
// through the real logger factory and the real console provider. Every canary is generated at run time.
[Collection("Diagnostic console")]
public sealed class RedactionTests
{
    internal static string Base64Url(int bytes) => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>One fresh canary of each recognized form.</summary>
    internal static string[] Canaries() =>
    [
        "Bearer " + Base64Url(24),
        "Basic " + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)),
        "Password=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
        "pwd = '" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)) + "'",
        "__Host-Modularyn-Session=" + Base64Url(32),
        Base64Url(32),
        "mfg1_" + Guid.NewGuid().ToString("N") + Base64Url(32),
        "CfDJ8" + Base64Url(48),
        "ey" + "J" + Base64Url(12) + ".ey" + "J" + Base64Url(18) + "." + Base64Url(24),
        "-----BEGIN " + "PRIVATE KEY-----\n" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) + "\n-----END " + "PRIVATE KEY-----",
        // A secret reference names where a secret is kept; Settings masks it, and so does the scrubber (SEC-08).
        "secret:" + "db-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))
    ];

    [Fact]
    public void TM_G02_053_Each_recognized_secret_form_is_scrubbed_and_ordinary_text_is_kept()
    {
        foreach (var canary in Canaries())
        {
            var text = "before " + canary + " after";
            Assert.True(SecretForms.Contains(text), canary[..5]);
            var scrubbed = SecretForms.Scrub(text);
            Assert.DoesNotContain(canary, scrubbed, StringComparison.Ordinal);
            Assert.StartsWith("before ", scrubbed, StringComparison.Ordinal);
            Assert.Contains(SecretForms.Redacted, scrubbed, StringComparison.Ordinal);
        }
        // Identifiers, correlation, codes and ordinary words are not secret forms.
        foreach (var ordinary in new[]
        {
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            "MAZ005", "events.replayed", "Bearer", "The password was rejected.", "item-quoted 'value'",
            // Words about secrets, and the configuration contract's own text for a discarded or expected reference, name none.
            "The secret was rotated.", "secret: rotated", "secret:<discarded>", "a reference of the form 'secret:<name>'",
            "Operation http.request ended with succeeded, correlation 4bf92f3577b34da6a3ce929d0e0e4736 and span 00f067aa0ba902b7."
        })
        {
            Assert.False(SecretForms.Contains(ordinary), ordinary);
            Assert.Equal(ordinary, SecretForms.Scrub(ordinary));
        }
    }

    [Fact]
    public void TM_G02_053_No_logger_provider_receives_a_secret_form_in_a_message_value_scope_or_exception()
    {
        var provider = new RecordingProvider();
        var services = new ServiceCollection();
        services.AddModularynObservability();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        using var built = services.BuildServiceProvider();
        var logger = built.GetRequiredService<ILoggerFactory>().CreateLogger("Any.Category");
        var canaries = Canaries();
        foreach (var canary in canaries)
        {
            using (logger.BeginScope(new Dictionary<string, object?> { ["Header"] = canary, ["CorrelationId"] = "kept" }))
                logger.LogWarning(new InvalidOperationException("Failed with " + canary), "Value {Value} and {Other}", canary, 7);
            logger.LogInformation("Inline " + canary);
        }
        logger.LogInformation("Plain {Code}", "MAZ005");

        var everything = string.Join("\n", provider.Events.Select(entry => entry.Text));
        foreach (var canary in canaries) Assert.DoesNotContain(canary, everything, StringComparison.Ordinal);
        Assert.Contains("Value " + SecretForms.Redacted + " and 7", everything, StringComparison.Ordinal);
        Assert.Contains("CorrelationId=kept", everything, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", everything, StringComparison.Ordinal);
        // An event with no secret form reaches the provider with its own state.
        var plain = provider.Events.Single(entry => entry.Text.StartsWith("Plain MAZ005", StringComparison.Ordinal));
        Assert.Equal("MAZ005", plain.Values["Code"]);
        Assert.Equal("Plain {Code}", plain.Values["{OriginalFormat}"]);
    }

    [Fact]
    public void TM_G02_053_The_console_provider_writes_no_secret_form()
    {
        using var output = new ConsoleLogCapture();
        var canaries = Canaries();
        var services = new ServiceCollection();
        services.AddModularynObservability();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddJsonConsole(options => options.IncludeScopes = true));
        using (var built = services.BuildServiceProvider())
        {
            var logger = built.GetRequiredService<ILogger<RedactionTests>>();
            foreach (var canary in canaries)
                using (logger.BeginScope("Scope {Scoped}", canary))
                    logger.LogError(new FormatException(canary), "Refused {Credential}", canary);
        }
        var written = output.Read();
        Assert.Contains("Refused " + SecretForms.Redacted, written, StringComparison.Ordinal);
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, written, StringComparison.Ordinal);
            // The JSON writer escapes some characters; neither form of a canary is written.
            Assert.DoesNotContain(System.Text.Json.JsonSerializer.Serialize(canary).Trim('"'), written, StringComparison.Ordinal);
        }
    }

    private sealed class RecordingProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? scopes;
        internal ConcurrentQueue<(string Text, IReadOnlyDictionary<string, object?> Values)> Events { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public void Dispose() { }

        private sealed class Recorder(RecordingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes?.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
                var scopeText = new List<string>();
                owner.scopes?.ForEachScope((scope, list) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs) list.AddRange(pairs.Select(pair => pair.Key + "=" + pair.Value));
                    else list.Add(scope?.ToString() ?? "");
                }, scopeText);
                owner.Events.Enqueue((formatter(state, exception) + " | " + string.Join(";", values.Select(pair => pair.Key + "=" + pair.Value))
                    + " | " + string.Join(";", scopeText) + " | " + exception, values));
            }
        }
    }
}
