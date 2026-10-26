using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Modularyn.Modules.Identity.Testing;

/// <summary>A clock the test moves explicitly. Every identity limit is measured against it.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long ticks = start.UtcTicks;

    public static ManualClock StartingNow() => new(new DateTimeOffset(DateTime.UtcNow.Ticks - DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero));

    public DateTimeOffset Now => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);

    public void Set(DateTimeOffset at) => Interlocked.Exchange(ref ticks, at.UtcTicks);
}

/// <summary>Credentials and sentinels generated for this run only, so finding one anywhere it should not be proves a leak.</summary>
internal static class Generated
{
    public static string Password() => "pw-" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

    public static string Sentinel() => "sentinel-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static byte[] Key() => RandomNumberGenerator.GetBytes(32);
}

/// <summary>
/// A secret-store directory holding freshly generated 32-byte credential keys, one file per secret, with the settings that
/// reference them. It is removed when disposed.
/// </summary>
internal sealed class CredentialKeyDirectory : IDisposable
{
    public CredentialKeyDirectory(params int[] versions)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "modularyn-identity-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        foreach (var version in versions) Add(version);
    }

    public string Path { get; }

    public void Add(int version) => File.WriteAllBytes(System.IO.Path.Combine(Path, SecretName(version)), Generated.Key());

    public void Remove(int version) => File.Delete(System.IO.Path.Combine(Path, SecretName(version)));

    public void Write(int version, byte[] material) => File.WriteAllBytes(System.IO.Path.Combine(Path, SecretName(version)), material);

    public static string SecretName(int version) => "identity-credential-key-" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Settings that list the given key versions, with <paramref name="current"/> as the version for new credentials.</summary>
    public Dictionary<string, string?> Settings(int current, params int[] versions)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Modularyn:Secrets:Directory"] = Path,
            ["Modularyn:Identity:CredentialKeys:CurrentVersion"] = current.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        for (var index = 0; index < versions.Length; index++)
        {
            settings[$"Modularyn:Identity:CredentialKeys:Keys:{index}:Version"] = versions[index].ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[$"Modularyn:Identity:CredentialKeys:Keys:{index}:Secret"] = "secret:" + SecretName(versions[index]);
        }
        return settings;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Records every log entry, including scopes, state values and exceptions, as text.</summary>
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
