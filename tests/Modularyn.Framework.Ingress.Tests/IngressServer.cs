using System.Collections.Concurrent;
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
    private int uploadsCompleted;

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
                RemoteCertificateValidationCallback = Pinned
            }
        };
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal Uri Http { get; }
    internal Uri Https { get; }
    internal HttpClient Client { get; }
    internal int Changes => Volatile.Read(ref changes);
    internal int Handled => Volatile.Read(ref handled);
    internal int UploadsCompleted => Volatile.Read(ref uploadsCompleted);
    /// <summary>The status and bytes received for each upload whose body read the server refused.</summary>
    internal ConcurrentQueue<(int Status, long Received)> UploadRefusals { get; } = new();
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

    internal static WebApplication Build(Dictionary<string, string?> configuration, Action<IServiceCollection>? services = null)
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
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    internal static async Task<IngressServer> StartAsync(Dictionary<string, string?> configuration, Action<IServiceCollection>? services = null,
        Action<WebApplication>? endpoints = null)
    {
        var application = Build(configuration, services);
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
            try
            {
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0) length += read;
            }
            catch (BadHttpRequestException exception)
            {
                // Recorded before the server writes its refusal, so a client that reads the response can rely on it.
                server.UploadRefusals.Enqueue((exception.StatusCode, length));
                throw;
            }
            Interlocked.Increment(ref server.uploadsCompleted);
            return Results.Text(length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }).DisableAntiforgery();
        application.MapPost("/json", (JsonElement body) => Results.Text(body.ValueKind.ToString())).DisableAntiforgery();
        endpoints?.Invoke(application);
        application.MapGet("/leaky", (HttpContext context) =>
        {
            context.Response.Headers.AccessControlAllowOrigin = "*";
            context.Response.Headers.AccessControlAllowCredentials = "true";
            return "leaky";
        });
        try
        {
            await application.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
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

    /// <summary>
    /// Sends one request over TLS in a single write, smaller than one TLS record, and reads the response head before
    /// sending anything else. The server therefore holds every byte the client sent before it can refuse, so closing the
    /// connection after a refusal cannot reset it with request bytes still in flight.
    /// </summary>
    internal async Task<RawResponse> RawHttpsAsync(string head, byte[]? body = null)
    {
        var request = Encoding.ASCII.GetBytes(head).Concat(body ?? []).ToArray();
        Assert.True(request.Length <= 12 * 1024, "A raw request must fit in one TLS record.");
        var cancellation = TestContext.Current.CancellationToken;
        using var client = new TcpClient();
        await client.ConnectAsync(Https.Host, Https.Port, cancellation);
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = PublicHost, RemoteCertificateValidationCallback = Pinned }, cancellation);
        await tls.WriteAsync(request, cancellation);
        await tls.FlushAsync(cancellation);
        var received = new List<byte>();
        var buffer = new byte[4096];
        int end;
        while ((end = HeadEnd(received)) < 0)
        {
            var read = await tls.ReadAsync(buffer, cancellation);
            Assert.True(read > 0, "The connection closed before the response head arrived.");
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        var lines = Encoding.ASCII.GetString(received.Take(end).ToArray()).Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }
        return new RawResponse(int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), headers);
    }

    private static int HeadEnd(List<byte> received)
    {
        for (var index = 3; index < received.Count; index++)
            if (received[index - 3] == '\r' && received[index - 2] == '\n' && received[index - 1] == '\r' && received[index] == '\n') return index - 3;
        return -1;
    }

    // Pin this run's certificate instead of disabling validation.
    private static bool Pinned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256) == Certificate.Value.GetCertHashString(HashAlgorithmName.SHA256);

    internal sealed record RawResponse(int Status, IReadOnlyDictionary<string, string> Headers);

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
