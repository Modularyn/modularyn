using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Ingress;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.HttpApi;
using Modularyn.Modules.Identity.Persistence.EfCore;
using Xunit;

namespace Modularyn.Modules.Identity.Testing;

/// <summary>
/// A real Kestrel server on loopback, over plain HTTP and over TLS with a certificate generated for this run, composed the
/// way a host composes the identity module: data protection, ingress, the secret store, the identity application, its
/// PostgreSQL stores and its HTTP adapter. The loopback peer is a declared proxy, so a test can name a client source.
/// </summary>
internal sealed class IdentityTestServer : IAsyncDisposable
{
    internal const string PublicHost = "app.example.test";
    internal const string PublicOrigin = "https://" + PublicHost;
    private static readonly Lazy<X509Certificate2> Certificate = new(CreateCertificate);
    private static readonly Lazy<string> KeyRing = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "modularyn-identity-keys-" + Guid.NewGuid().ToString("N"));
        KeyRingDirectory.Create(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        };
        return path;
    });
    private readonly WebApplication application;
    private readonly CredentialKeyDirectory keys;

    private IdentityTestServer(WebApplication application, ManualClock clock, CapturedLogs logs, CredentialKeyDirectory keys, Uri http, Uri https)
    {
        this.application = application;
        this.keys = keys;
        Clock = clock;
        Logs = logs;
        Http = http;
        Https = https;
        Client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Pinned }
        }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public ManualClock Clock { get; }
    public CapturedLogs Logs { get; }
    public Uri Http { get; }
    public Uri Https { get; }
    public HttpClient Client { get; }
    public IServiceProvider Services => application.Services;

    /// <summary>Identity limits small enough to reach in a test; every other value is the module's default.</summary>
    public static Dictionary<string, string?> TestLimits() => new()
    {
        ["Modularyn:Identity:Throttling:SourceThreshold"] = "6",
        ["Modularyn:Identity:Throttling:AccountFreeAttempts"] = "3",
        ["Modularyn:Identity:Throttling:AccountBaseDelay"] = "00:00:02",
        ["Modularyn:Identity:Throttling:AccountMaximumDelay"] = "00:00:16"
    };

    public static async Task<IdentityTestServer> StartAsync(IdentityDatabase database, Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null, Action<WebApplication>? endpoints = null)
    {
        var clock = ManualClock.StartingNow();
        var logs = new CapturedLogs();
        var keys = new CredentialKeyDirectory(1);
        var configuration = new Dictionary<string, string?>(keys.Settings(1, 1))
        {
            ["Modularyn:DataProtection:ApplicationName"] = "identity-tests",
            ["Modularyn:DataProtection:Directory"] = KeyRing.Value,
            ["Modularyn:Ingress:PublicOrigins:0"] = PublicOrigin,
            ["Modularyn:Ingress:Proxies:0"] = "127.0.0.1"
        };
        foreach (var (key, value) in settings ?? TestLimits()) configuration[key] = value;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Certificate.Value));
        });
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddModularynDataProtection(builder.Configuration);
        builder.Services.AddModularynIngress(builder.Configuration);
        builder.Services.AddModularynSecrets(builder.Configuration);
        builder.Services.AddModularynIdentity(builder.Configuration);
        builder.Services.AddModularynIdentityPostgreSql(database.Module);
        builder.Services.AddModularynIdentityHttp();
        services?.Invoke(builder.Services);
        var application = builder.Build();
        try
        {
            application.UseModularynIngress();
            application.MapModularynIdentityEndpoints();
            endpoints?.Invoke(application);
            await application.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            keys.Dispose();
            throw;
        }
        var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(address => new Uri(address)).ToArray();
        return new IdentityTestServer(application, clock, logs, keys, addresses.Single(address => address.Scheme == "http"),
            addresses.Single(address => address.Scheme == "https"));
    }

    public T Get<T>() where T : notnull => application.Services.GetRequiredService<T>();

    public Browser Browser(string? source = null) => new(this, source);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => Client.SendAsync(request, TestContext.Current.CancellationToken);

    public HttpRequestMessage Request(HttpMethod method, string path, bool https = true, string host = PublicHost)
    {
        var request = new HttpRequestMessage(method, new Uri(https ? Https : Http, path));
        request.Headers.Host = host;
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
        keys.Dispose();
    }

    private static bool Pinned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256) == Certificate.Value.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + PublicHost, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(PublicHost);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }
}

/// <summary>
/// A browser-like client: it keeps the cookies the server sets, sends them back, and sends the antiforgery request token
/// with state changes. A source, when given, is sent as the declared proxy's forwarded client address.
/// </summary>
internal sealed class Browser(IdentityTestServer server, string? source)
{
    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);
    public string? RequestToken { get; set; }

    /// <summary>Set for a copy that replays what it holds: it ignores every cookie the server sets or deletes.</summary>
    public bool Replay { get; init; }
    public List<string> SetCookieHeaders { get; } = [];

    public string? Session => Cookies.GetValueOrDefault(SessionCookie.Name);

    public async Task RefreshTokenAsync()
    {
        var response = await SendAsync(HttpMethod.Get, IdentityEndpoints.AntiforgeryPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        RequestToken = body.RootElement.GetProperty("requestToken").GetString();
    }

    public async Task<HttpResponseMessage> SignInAsync(string userName, string password, string? returnUrl = null)
    {
        if (RequestToken is null) await RefreshTokenAsync();
        var response = await SendAsync(HttpMethod.Post, IdentityEndpoints.SignInPath,
            content: JsonContent.Create(new { userName, password, returnUrl }));
        // Tokens are bound to the signed-in user, so a browser fetches a new one after signing in.
        if (response.StatusCode == HttpStatusCode.OK) await RefreshTokenAsync();
        return response;
    }

    public Task<HttpResponseMessage> SignOutAsync() => SendAsync(HttpMethod.Post, IdentityEndpoints.SignOutPath, content: new StringContent(""));

    public async Task<JsonElement?> CurrentUserAsync(Guid? delegation = null)
    {
        var response = await SendAsync(HttpMethod.Get, IdentityEndpoints.CurrentUserPath, delegation);
        if (response.StatusCode != HttpStatusCode.OK) return null;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.Clone();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? delegation = null, HttpContent? content = null,
        bool https = true, bool sendToken = true)
    {
        var request = server.Request(method, path, https);
        if (Cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
        if (sendToken && RequestToken is not null && method != HttpMethod.Get) request.Headers.Add("RequestVerificationToken", RequestToken);
        if (source is not null) request.Headers.Add("X-Forwarded-For", source);
        if (delegation is { } grant) request.Headers.Add(SessionCookie.DelegationHeader, grant.ToString("D"));
        request.Content = content;
        var response = await server.SendAsync(request);
        if (!Replay && response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            var headers = values.ToArray();
            SetCookieHeaders.AddRange(headers);
            if (SetCookieHeaderValue.TryParseList(headers, out var cookies))
                foreach (var cookie in cookies)
                {
                    var name = cookie.Name.Value!;
                    if (cookie.Expires is { } expires && expires < DateTimeOffset.UtcNow || string.IsNullOrEmpty(cookie.Value.Value)) Cookies.Remove(name);
                    else Cookies[name] = cookie.Value.Value!;
                }
        }
        return response;
    }

    /// <summary>A copy of this browser's cookies and token, as an attacker who copied them would hold them.</summary>
    public Browser Copy(string? from = null)
    {
        var copy = new Browser(server, from ?? source) { RequestToken = RequestToken, Replay = true };
        foreach (var cookie in Cookies) copy.Cookies[cookie.Key] = cookie.Value;
        return copy;
    }
}
