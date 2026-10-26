using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Configuration.Tests;

public sealed class SampleOptions
{
    [Required]
    public string? Name { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = 443;

    public SecretReference? Password { get; set; }

    public NestedOptions Nested { get; set; } = new();

    public List<SecretReference> Keys { get; set; } = [];
}

public sealed class NestedOptions
{
    public SecretReference? Token { get; set; }
}

internal static class TestSupport
{
    internal static readonly CompositionEnvironment Environment = new(SemanticVersion.Parse("10.0.0"), SemanticVersion.Parse("1.0.0"));
    internal static readonly ModuleHostOptions Unverified = new() { AllowUnverifiedComposition = true };

    internal static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>A value generated for this run only, so finding it anywhere proves a leak.</summary>
    internal static string Sentinel() => "sentinel-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    internal static HostApplicationBuilder Builder(IDictionary<string, string?> values, CapturedLogs logs)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        return builder;
    }

    internal static string TemporaryDirectory(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), $"modularyn-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void AssertAbsent(string sentinel, CapturedLogs logs, params string?[] texts)
    {
        Assert.All(texts, text => Assert.DoesNotContain(sentinel, text ?? "", StringComparison.Ordinal));
        Assert.All(logs.Entries, entry => Assert.DoesNotContain(sentinel, entry, StringComparison.Ordinal));
    }

    internal static void Link(Action create)
    {
        try { create(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account cannot create symbolic links.");
        }
    }
}

/// <summary>Records every log entry, including scopes, state and exceptions, as text.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> entries = new();

    public IReadOnlyCollection<string> Entries => entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            entries.Enqueue($"{category} scope {state}");
            return NoScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(";", pairs.Select(pair => $"{pair.Key}={pair.Value}")) : "";
            entries.Enqueue($"{category} {logLevel} {formatter(state, exception)} {values} {exception}");
        }
    }

    private sealed class NoScope : IDisposable
    {
        internal static readonly NoScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>A one-module selection whose factory and initializer are observable.</summary>
internal sealed class ModuleProbe
{
    internal int Constructed { get; private set; }
    internal Func<IServiceProvider, CancellationToken, Task>? Initialize { get; set; }

    internal ModuleRegistration Registration() => new(new ModuleManifest("A", SemanticVersion.Parse("1.0.0")), services =>
    {
        Constructed++;
        return new Module(this, services);
    });

    internal void Add(IServiceCollection services) => services.AddModularynModules(TestSupport.Environment, [Registration()], TestSupport.Unverified);

    private sealed class Module(ModuleProbe probe, IServiceProvider services) : IModule
    {
        public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) =>
            probe.Initialize?.Invoke(services, cancellationToken) ?? Task.CompletedTask;

        public Task StartAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
