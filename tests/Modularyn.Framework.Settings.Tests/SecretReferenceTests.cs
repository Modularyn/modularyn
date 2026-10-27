using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Caching;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

[Collection(SettingsPostgreSqlCollection.Name)]
public sealed class SecretReferenceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task References_are_persisted_and_cached_while_secret_contents_never_enter_storage_logs_or_diagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), "modularyn-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var capture = new SecretDiagnosticCapture();
            await using var database = await SettingsDatabase.CreateAsync();
            var a = await database.EnterAsync(database.TenantA);
            var b = await database.EnterAsync(database.TenantB);
            var host = await database.HostAsync();
            var first = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var second = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var reference = SecretReference.Parse("secret:connection");
            var mounted = Path.Combine(directory, reference.Name);
            await File.WriteAllTextAsync(mounted, first, Cancellation);
            var store = new FileSecretStore(Options.Create(new SecretStoreOptions { Directory = directory }));
            var settings = new SettingsService(database.Module, database.Definitions, database.Services.GetRequiredService<ScopedValueCache>(),
                database.Services.GetRequiredService<TenantWorkRunner>(), database.Clock, store);
            var errors = new List<string>();
            foreach (var (context, scope) in new[] { (host, ValueSource.Global), (a, ValueSource.Tenant), (a, ValueSource.User) })
            {
                var exception = await Assert.ThrowsAsync<ValueValidationException>(() => settings.SetAsync(context, TestDefinitions.Secret, scope, first,
                    cancellationToken: Cancellation));
                Assert.Null(exception.InnerException);
                errors.Add(exception.ToString());
            }
            Assert.Empty(await database.InventoryAsync());
            Assert.False((await settings.ResolveAsync(a, TestDefinitions.Secret, Cancellation)).IsSet);
            var global = await settings.SetSecretReferenceAsync(host, TestDefinitions.Secret, ValueSource.Global, reference, cancellationToken: Cancellation);
            var resolved = await settings.ResolveAsync(a, TestDefinitions.Secret, Cancellation);
            Assert.Equal(ValueSource.Global, resolved.Source);
            Assert.Equal(reference, resolved.Get<SecretReference>());
            Assert.Equal(SecretValue.Redacted, resolved.DisplayValue);
            Assert.Equal(reference, (await settings.ResolveAsync(b, TestDefinitions.Secret, Cancellation)).Get<SecretReference>());

            using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(capture));
            var logger = loggerFactory.CreateLogger("Modularyn.Settings.Tests");
            using (var secret = await settings.ResolveSecretAsync(a, TestDefinitions.Secret, Cancellation))
            {
                Assert.True(secret.RevealString() == first, "The secret store must supply the mounted bytes.");
                logger.LogInformation("Resolved {Secret}", secret);
                capture.Messages.Enqueue(JsonSerializer.Serialize(new { Effective = resolved, Secret = secret }));
                capture.Messages.Enqueue(resolved.ToString());
            }
            // Rotating the mounted content must be visible even while the reference is still cached.
            await File.WriteAllTextAsync(mounted, second, Cancellation);
            using (var secret = await settings.ResolveSecretAsync(a, TestDefinitions.Secret, Cancellation))
                Assert.True(secret.RevealString() == second, "Secret contents must not be cached with an effective setting.");
            Assert.Same(resolved, await settings.ResolveAsync(a, TestDefinitions.Secret, Cancellation));
            File.Delete(mounted);
            var unavailable = await Assert.ThrowsAsync<ValueUnavailableException>(() => settings.ResolveSecretAsync(a, TestDefinitions.Secret, Cancellation).AsTask());
            Assert.Null(unavailable.InnerException);
            errors.Add(unavailable.ToString());
            await File.WriteAllTextAsync(mounted, first, Cancellation);
            using (var secret = await settings.ResolveSecretAsync(a, TestDefinitions.Secret, Cancellation))
                Assert.True(secret.RevealString() == first, "The recovered store must be read again.");

            // A reference names a deployment-wide secret, so a tenant or a user may not choose one, and a stored one fails closed.
            var userReference = SecretReference.Parse("secret:user-connection");
            foreach (var scope in new[] { ValueSource.Tenant, ValueSource.User })
                await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SetSecretReferenceAsync(a, TestDefinitions.Secret, scope,
                    userReference, cancellationToken: Cancellation));
            Assert.Equal(reference, (await settings.ResolveAsync(a, TestDefinitions.Secret, Cancellation)).Get<SecretReference>());
            await database.ExecuteAsync("INSERT INTO " + database.Table("TenantValues")
                + " (\"TenantId\", \"ModuleId\", \"Name\", \"User\", \"Value\", \"Kind\", \"Version\", \"UpdatedAt\")"
                + " VALUES (@tenant, @module, @name, '', @value, @kind, @version, @at)",
                new NpgsqlParameter("tenant", database.TenantA), new NpgsqlParameter("module", TestDefinitions.Secret.ModuleId),
                new NpgsqlParameter("name", TestDefinitions.Secret.AssetName), new NpgsqlParameter("value", userReference.ToString()),
                new NpgsqlParameter("kind", (int)ValueKind.SecretReference), new NpgsqlParameter("version", Guid.NewGuid()),
                new NpgsqlParameter("at", DateTimeOffset.UnixEpoch));
            var cache = database.Services.GetRequiredService<ScopedValueCache>();
            cache.InvalidateTenant(database.TenantA);
            await Assert.ThrowsAsync<ValueUnavailableException>(() => settings.ResolveAsync(a, TestDefinitions.Secret, Cancellation));
            await Assert.ThrowsAsync<ValueUnavailableException>(() => settings.ResolveSecretAsync(a, TestDefinitions.Secret, Cancellation).AsTask());
            Assert.Equal(reference, (await settings.ResolveAsync(b, TestDefinitions.Secret, Cancellation)).Get<SecretReference>());
            await database.ExecuteAsync("DELETE FROM " + database.Table("TenantValues"));
            cache.InvalidateTenant(database.TenantA);
            Assert.Equal(reference, (await settings.ResolveAsync(a, TestDefinitions.Secret, Cancellation)).Get<SecretReference>());
            var conflict = await Assert.ThrowsAsync<ValueConflictException>(() => settings.SetSecretReferenceAsync(host, TestDefinitions.Secret,
                ValueSource.Global, userReference, Guid.NewGuid(), Cancellation));
            errors.Add(JsonSerializer.Serialize(conflict.Message));
            Assert.NotEqual(Guid.Empty, global);

            var inventory = await database.InventoryAsync();
            Assert.Single(inventory);
            Assert.All(inventory, row => Assert.Contains("secret:", row, StringComparison.Ordinal));
            foreach (var text in inventory.Concat(capture.Messages).Concat(errors))
            {
                Assert.False(text.Contains(first, StringComparison.Ordinal), "Secret content reached an inspection surface.");
                Assert.False(text.Contains(second, StringComparison.Ordinal), "Rotated secret content reached an inspection surface.");
            }
            Assert.Contains(capture.Messages, text => text.Contains(SecretValue.Redacted, StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

/// <summary>Observes real EF diagnostics and ordinary structured logging; it does not replace a provider.</summary>
internal sealed class SecretDiagnosticCapture : ILoggerProvider, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConcurrentBag<IDisposable> subscriptions = [];
    private readonly IDisposable listeners;
    private int disposed;
    internal ConcurrentQueue<string> Messages { get; } = new();

    internal SecretDiagnosticCapture() => listeners = DiagnosticListener.AllListeners.Subscribe(this);

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.EntityFrameworkCore") subscriptions.Add(listener.Subscribe(this));
    }
    public void OnNext(KeyValuePair<string, object?> value) => Messages.Enqueue(value.Key + ": " + value.Value);
    public void OnError(Exception error) { }
    public void OnCompleted() { }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        listeners.Dispose();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }

    private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception));
    }
}
