using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using static Modularyn.Framework.Configuration.Tests.TestSupport;

namespace Modularyn.Framework.Configuration.Tests;

public sealed class KeyRingTests : IDisposable
{
    private const string Purpose = "Modularyn.Framework.Configuration.Tests";
    private readonly string root = TemporaryDirectory("key-ring");
    private readonly string keys;

    public KeyRingTests() => keys = Path.Combine(root, "keys");

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact(DisplayName = "AC-031-R04 row 4: a deployment without a declared key ring stays unready and generates no keys")]
    public async Task AC_031_R04_A_deployment_without_a_declared_key_ring_stays_unready_and_generates_no_keys()
    {
        var logs = new CapturedLogs();
        var builder = Builder(new Dictionary<string, string?> { ["Modularyn:DataProtection:ApplicationName"] = "team-workspace" }, logs);
        builder.Services.AddModularynDataProtection(builder.Configuration);
        var probe = new ModuleProbe();
        probe.Add(builder.Services);
        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Cancellation));
        Assert.Contains("'Modularyn:DataProtection:Directory' must declare the key-ring location. Keys are never generated in an undeclared location.",
            exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, probe.Constructed);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        // No fallback location is chosen: protecting anything still fails on the missing declaration.
        var failure = Record.Exception(() => host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("payload"u8.ToArray()));
        Assert.NotNull(failure);
        var causes = new List<Exception>();
        for (var current = failure; current is not null; current = current.InnerException) causes.Add(current);
        Assert.Contains(causes, cause => cause is OptionsValidationException);
    }

    [Theory(DisplayName = "TM-G02-050 the declared key ring must exist with owner-only access and a declared lifetime")]
    [InlineData("relative", "'Modularyn:DataProtection:Directory' must be a fully qualified directory.")]
    [InlineData("missing", "'Modularyn:DataProtection:Directory' names a directory that does not exist.")]
    [InlineData("shared", "'Modularyn:DataProtection:Directory' is a link or is accessible to other accounts.")]
    [InlineData("link", "'Modularyn:DataProtection:Directory' is a link or is accessible to other accounts.")]
    [InlineData("no-application", "'Modularyn:DataProtection:ApplicationName' must name the application boundary")]
    [InlineData("short-lifetime", "'Modularyn:DataProtection:KeyLifetime' must declare a key lifetime from 7 to 90 days.")]
    [InlineData("long-lifetime", "'Modularyn:DataProtection:KeyLifetime' must declare a key lifetime from 7 to 90 days.")]
    [InlineData("unknown-key", "contains keys that its options do not declare: 'Directroy'.")]
    public async Task TM_G02_050_The_declared_key_ring_must_exist_with_owner_only_access_and_a_declared_lifetime(string mutation, string expected)
    {
        var values = Declaration();
        switch (mutation)
        {
            case "relative": values["Modularyn:DataProtection:Directory"] = "keys"; break;
            case "missing": break;
            case "shared":
                Directory.CreateDirectory(keys);
                // A directory created without KeyRingDirectory inherits the temporary directory's shared access on Windows.
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keys, (UnixFileMode)Convert.ToInt32("755", 8));
                break;
            case "link":
                var target = Path.Combine(root, "elsewhere");
                KeyRingDirectory.Create(target);
                Link(() => Directory.CreateSymbolicLink(keys, target));
                break;
            case "no-application": KeyRingDirectory.Create(keys); values.Remove("Modularyn:DataProtection:ApplicationName"); break;
            case "short-lifetime": KeyRingDirectory.Create(keys); values["Modularyn:DataProtection:KeyLifetime"] = "6.23:59:59"; break;
            case "long-lifetime": KeyRingDirectory.Create(keys); values["Modularyn:DataProtection:KeyLifetime"] = "90.00:00:01"; break;
            default: KeyRingDirectory.Create(keys); values["Modularyn:DataProtection:Directroy"] = keys; break;
        }
        using var host = DataProtectionHost(values, new CapturedLogs(), out var probe);
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Cancellation));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(root, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, probe.Constructed);
        if (mutation != "link" && Directory.Exists(keys)) Assert.Empty(Directory.EnumerateFileSystemEntries(keys));
    }

    [Fact(DisplayName = "AC-031-R04 rows 1 and 3: protected payloads survive host replacement only within the declared key ring and application")]
    public async Task AC_031_R04_Protected_payloads_survive_host_replacement_only_within_the_declared_key_ring_and_application()
    {
        KeyRingDirectory.Create(keys);
        Assert.True(KeyRingDirectory.IsRestricted(keys));
        byte[] protectedPayload;
        using (var first = DataProtectionHost(Declaration(), new CapturedLogs(), out _))
        {
            await first.StartAsync(Cancellation);
            protectedPayload = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("session"u8.ToArray());
            await first.StopAsync(Cancellation);
        }
        var stored = Directory.GetFiles(keys);
        Assert.Single(stored);
        Assert.EndsWith(".xml", stored[0], StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stored[0]));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(keys));
        }

        using (var replacement = DataProtectionHost(Declaration(), new CapturedLogs(), out _))
        {
            await replacement.StartAsync(Cancellation);
            var protector = replacement.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
            Assert.Equal("session"u8.ToArray(), protector.Unprotect(protectedPayload));
            await replacement.StopAsync(Cancellation);
        }
        Assert.Single(Directory.GetFiles(keys));

        var otherApplication = Declaration();
        otherApplication["Modularyn:DataProtection:ApplicationName"] = "another-application";
        using (var other = DataProtectionHost(otherApplication, new CapturedLogs(), out _))
        {
            await other.StartAsync(Cancellation);
            var protector = other.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(protectedPayload));
            await other.StopAsync(Cancellation);
        }
    }

    [Fact(DisplayName = "TM-G02-050 keys follow the declared lifetime, rotation keeps older payloads and revocation ends them")]
    public async Task TM_G02_050_Keys_follow_the_declared_lifetime_rotation_keeps_older_payloads_and_revocation_ends_them()
    {
        KeyRingDirectory.Create(keys);
        var values = Declaration();
        values["Modularyn:DataProtection:KeyLifetime"] = "30.00:00:00";
        using var host = DataProtectionHost(values, new CapturedLogs(), out _);
        await host.StartAsync(Cancellation);
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        var manager = host.Services.GetRequiredService<IKeyManager>();
        var earlier = protector.Protect("earlier"u8.ToArray());
        var original = Assert.Single(manager.GetAllKeys());
        Assert.Equal(TimeSpan.FromDays(30), original.ExpirationDate - original.ActivationDate);
        Assert.Equal(TimeSpan.FromDays(30), host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.NewKeyLifetime);

        var now = DateTimeOffset.UtcNow;
        var rotated = manager.CreateNewKey(now, now.AddDays(30));
        Assert.Equal(2, manager.GetAllKeys().Count);
        Assert.Equal("earlier"u8.ToArray(), protector.Unprotect(earlier));

        manager.RevokeKey(original.KeyId, "Superseded by rotation.");
        // The running host refreshes its cached key ring asynchronously after a revocation.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Accepts(protector, earlier) && DateTime.UtcNow < deadline) await Task.Delay(50, Cancellation);
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(earlier));
        var later = protector.Protect("later"u8.ToArray());
        Assert.Equal("later"u8.ToArray(), protector.Unprotect(later));
        Assert.Contains(manager.GetAllKeys(), key => key.KeyId == rotated.KeyId && !key.IsRevoked);
        Assert.All(Directory.GetFiles(keys), file =>
        {
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        });
        await host.StopAsync(Cancellation);

        // A replacement host reads the persisted revocation at once.
        using var replacement = DataProtectionHost(values, new CapturedLogs(), out _);
        await replacement.StartAsync(Cancellation);
        var replaced = replacement.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        Assert.ThrowsAny<CryptographicException>(() => replaced.Unprotect(earlier));
        Assert.Equal("later"u8.ToArray(), replaced.Unprotect(later));
        await replacement.StopAsync(Cancellation);
    }

    private static bool Accepts(IDataProtector protector, byte[] payload)
    {
        try
        {
            protector.Unprotect(payload);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    [Fact(DisplayName = "TM-G02-050 an application key store replaces the directory declaration")]
    public async Task TM_G02_050_An_application_key_store_replaces_the_directory_declaration()
    {
        var store = new ListRepository();
        var values = Declaration();
        values.Remove("Modularyn:DataProtection:Directory");
        var builder = Builder(values, new CapturedLogs());
        builder.Services.AddModularynDataProtection(builder.Configuration, keyStore: _ => store);
        builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = new ListRepository());
        using (var host = builder.Build())
        {
            await host.StartAsync(Cancellation);
            host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("payload"u8.ToArray());
            Assert.Same(store, host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
            await host.StopAsync(Cancellation);
        }
        Assert.NotEmpty(store.GetAllElements());

        var both = Builder(Declaration(), new CapturedLogs());
        both.Services.AddModularynDataProtection(both.Configuration, keyStore: _ => new ListRepository());
        using var refused = both.Build();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => refused.StartAsync(Cancellation));
        Assert.Contains("'Modularyn:DataProtection:Directory' must be absent when the application supplies its own key store.", exception.Message, StringComparison.Ordinal);
    }

    private Dictionary<string, string?> Declaration() => new()
    {
        ["Modularyn:DataProtection:ApplicationName"] = "team-workspace",
        ["Modularyn:DataProtection:Directory"] = keys
    };

    private static IHost DataProtectionHost(Dictionary<string, string?> values, CapturedLogs logs, out ModuleProbe probe)
    {
        var builder = Builder(values, logs);
        builder.Services.AddModularynDataProtection(builder.Configuration);
        probe = new ModuleProbe();
        probe.Add(builder.Services);
        return builder.Build();
    }

    private sealed class ListRepository : IXmlRepository
    {
        private readonly List<XElement> elements = [];
        public IReadOnlyCollection<XElement> GetAllElements() { lock (elements) return elements.Select(element => new XElement(element)).ToArray(); }
        public void StoreElement(XElement element, string friendlyName) { lock (elements) elements.Add(new XElement(element)); }
    }
}
