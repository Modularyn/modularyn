using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// The one browser the tests run: the Chrome Headless Shell archive that <c>.github/browsers.json</c> pins by SHA-256 for this
/// platform, never a build Playwright downloads itself. The archive named by <see cref="ArchiveVariable"/>, which the
/// real-provider job downloads and checks, is copied into a private directory while its bytes are hashed, and only that copy,
/// once its hash equals the pin, is extracted and launched. Anything else is no verified browser, and the test is skipped
/// with the reason, never reported as passed; the real-provider job's tests script fails on a skipped test.
/// </summary>
internal static class VerifiedBrowser
{
    internal const string ArchiveVariable = "MODULARYN_TEST_BROWSER_ARCHIVE";
    private const string PinPath = ".github/browsers.json";

    internal sealed record Prepared(string? Executable, string Detail);

    internal static async Task<Prepared> PrepareAsync(string directory, CancellationToken cancellationToken)
    {
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64 ? "linux64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "mac-arm64" : null;
        if (platform is null)
            return new(null, $"No verified browser: {PinPath} pins no archive for {RuntimeInformation.RuntimeIdentifier}, so nothing was installed or run.");
        if (Environment.GetEnvironmentVariable(ArchiveVariable) is not { Length: > 0 } archive || !File.Exists(archive))
            return new(null, $"No verified browser: {ArchiveVariable} names no archive. Download the pinned {platform} archive as docs/ci.md describes; "
                + "no browser was installed or run.");

        using var pin = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root(), PinPath), cancellationToken));
        var pinned = pin.RootElement.GetProperty("archives").GetProperty(platform);
        var expected = pinned.GetProperty("sha256").GetString()!;
        var name = new Uri(pinned.GetProperty("url").GetString()!).Segments[^1];

        // The copy that is hashed is the copy that is extracted, so the archive cannot change between its check and its use.
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var copy = Path.Combine(directory, name);
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using (var source = File.OpenRead(archive))
            await using (var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != expected)
            {
                File.Delete(copy);
                return new(null, $"No verified browser: the archive's SHA-256 is {actual}, not the pinned {expected}, so it was not extracted or run.");
            }
        }

        var extracted = Path.Combine(directory, "browser");
        ZipFile.ExtractToDirectory(copy, extracted);
        File.Delete(copy);
        var executable = Path.Combine(extracted, Path.GetFileNameWithoutExtension(name), "chrome-headless-shell");
        return File.Exists(executable) ? new(executable, $"Chrome Headless Shell {pin.RootElement.GetProperty("version").GetString()} for {platform}, "
            + $"archive SHA-256 {expected}") : new(null, "No verified browser: the verified archive holds no chrome-headless-shell executable.");
    }

    // The repository root, above the test assembly, that holds the pin file.
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Modularyn.slnx")))
            directory = directory.Parent ?? throw new InvalidOperationException("Cannot find Modularyn.slnx above the test assembly.");
        return directory.FullName;
    }
}

/// <summary>
/// The other sites the browser visits, on loopback over TLS behind host mapping: origin-1, the listed cross-origin caller, and
/// an unlisted origin, both under the application's registrable domain, and an attacker's site under another one. Each serves
/// a plain page; the test acts from it through the browser.
/// </summary>
internal sealed class BrowserSites : IAsyncDisposable
{
    internal const string AppHost = "app.example.com";
    internal const string ListedHost = "origin-1.example.com";
    internal const string UnlistedHost = "unlisted.example.com";
    internal const string AttackerHost = "attacker.example.org";
    private readonly WebApplication application;

    private BrowserSites(WebApplication application, int port)
    {
        this.application = application;
        Port = port;
    }

    public int Port { get; }

    public string Origin(string host) => "https://" + host + ":" + Port;

    /// <summary>A free loopback port, for an origin that must be configured before its server starts.</summary>
    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static async Task<BrowserSites> StartAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(Loopback.Certificate.Value)));
        var application = builder.Build();
        application.MapGet("/", (HttpContext context) => Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>"
            + WebUtility.HtmlEncode(context.Request.Host.Host) + "</title></head><body><p>Another site.</p></body></html>", "text/html"));
        try { await application.StartAsync(TestContext.Current.CancellationToken); }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
        return new(application, port);
    }

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
    }
}
