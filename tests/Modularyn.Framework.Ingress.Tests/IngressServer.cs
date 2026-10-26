using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Configuration;
using Xunit;

namespace Modularyn.Framework.Ingress.Tests;

/// <summary>A real Kestrel server on loopback, over plain HTTP and over TLS with a certificate generated for this run.</summary>
internal sealed class IngressServer : IAsyncDisposable
{
    internal const string PublicHost = "app.example.test";
    internal const string OtherTenantHost = "tenant-b.example.test";
    internal const string ListedOrigin = "https://origin-1.example.test";
    private static readonly Lazy<X509Certificate2> Certificate = new(CreateCertificate);

    // One owner-only key ring for the test run, under the temporary directory.
    private static readonly Lazy<string> KeyRing = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "modularyn-ingress-keys-" + Guid.NewGuid().ToString("N"));
        KeyRingDirectory.Create(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        };
        return path;
    });
    private readonly WebApplication application;
    private int changes;
    private int handled;

    private IngressServer(WebApplication application, Uri http, Uri https)
    {
        this.application = application;
        Http = http;
        Https = https;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                // Pin this run's certificate instead of disabling validation.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256) == Certificate.Value.GetCertHashString(HashAlgorithmName.SHA256)
            }
        };
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal Uri Http { get; }
    internal Uri Https { get; }
    internal HttpClient Client { get; }
    internal int Changes => Volatile.Read(ref changes);
    internal int Handled => Volatile.Read(ref handled);
    internal IServiceProvider Services => application.Services;

    internal static Dictionary<string, string?> Settings(params (string Key, string? Value)[] values)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Modularyn:DataProtection:ApplicationName"] = "ingress-tests",
            ["Modularyn:DataProtection:Directory"] = KeyRing.Value,
            ["Modularyn:Ingress:PublicOrigins:0"] = "https://" + PublicHost,
            ["Modularyn:Ingress:PublicOrigins:1"] = "https://" + OtherTenantHost
        };
        foreach (var (key, value) in values) configuration["Modularyn:Ingress:" + key] = value;
        return configuration;
    }

    internal static WebApplication Build(Dictionary<string, string?> configuration)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Certificate.Value));
        });
        builder.Services.AddModularynDataProtection(builder.Configuration);
        builder.Services.AddModularynIngress(builder.Configuration);
        return builder.Build();
    }

    internal static async Task<IngressServer> StartAsync(Dictionary<string, string?> configuration)
    {
        var application = Build(configuration);
        IngressServer? server = null;
        application.UseModularynIngress();
        application.MapGet("/", (HttpContext context) =>
        {
            Interlocked.Increment(ref server!.handled);
            context.Response.Cookies.Append("probe", "1", new CookieOptions { Secure = true, HttpOnly = true });
            return "content";
        });
        application.MapGet("/echo", (HttpContext context, PublicOrigins origins) =>
        {
            Interlocked.Increment(ref server!.handled);
            return Results.Json(new Echo(context.Request.Scheme, context.Request.Host.Value ?? "", context.Connection.RemoteIpAddress?.ToString() ?? "",
                context.Request.Headers.Keys.Where(name => name.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("X-Original", StringComparison.OrdinalIgnoreCase) || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)).ToArray(),
                origins.ForRequest(context.Request)));
        });
        application.MapGet("/token", (HttpContext context, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(context).RequestToken);
        application.MapPost("/change", () =>
        {
            Interlocked.Increment(ref server!.changes);
            return Results.NoContent();
        });
        application.MapPost("/bearer", () =>
        {
            Interlocked.Increment(ref server!.changes);
            return Results.NoContent();
        }).DisableAntiforgery();
        application.MapPost("/upload", async (HttpContext context) =>
        {
            Interlocked.Increment(ref server!.handled);
            var length = 0L;
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0) length += read;
            return Results.Text(length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }).DisableAntiforgery();
        application.MapPost("/json", (JsonElement body) => Results.Text(body.ValueKind.ToString())).DisableAntiforgery();
        application.MapGet("/leaky", (HttpContext context) =>
        {
            context.Response.Headers.AccessControlAllowOrigin = "*";
            context.Response.Headers.AccessControlAllowCredentials = "true";
            return "leaky";
        });
        await application.StartAsync(TestContext.Current.CancellationToken);
        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(address => new Uri(address)).ToArray();
        server = new IngressServer(application, addresses.Single(address => address.Scheme == "http"), addresses.Single(address => address.Scheme == "https"));
        return server;
    }

    internal Task<HttpResponseMessage> SendAsync(bool https, HttpMethod method, string path, Action<HttpRequestMessage>? configure = null,
        string host = PublicHost)
    {
        var request = new HttpRequestMessage(method, new Uri(https ? Https : Http, path));
        request.Headers.Host = host;
        configure?.Invoke(request);
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal static async Task<Echo> EchoAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<Echo>(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), JsonSerializerOptions.Web)!;
    }

    /// <summary>Sends raw bytes over plain HTTP and returns the status line, for requests HttpClient will not form.</summary>
    internal async Task<string> RawAsync(string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(Http.Host, Http.Port, TestContext.Current.CancellationToken);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + PublicHost, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(PublicHost);
        names.AddDnsName(OtherTenantHost);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }

    internal sealed record Echo(string Scheme, string Host, string Remote, string[] Forwarded, string? Origin);
}
