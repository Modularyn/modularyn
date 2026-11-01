using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Identity.HttpApi;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>Captures every log event's identifier, level and message, for startup reports and warnings.</summary>
internal sealed class EventLog : ILoggerProvider
{
    private readonly ConcurrentQueue<(int Id, LogLevel Level, string Message)> events = new();

    public IReadOnlyCollection<(int Id, LogLevel Level, string Message)> Events => events.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(events);

    public void Dispose() { }

    private sealed class Logger(ConcurrentQueue<(int Id, LogLevel Level, string Message)> events) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            events.Enqueue((eventId.Id, logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// Stands in for authentication in the contract tests: a request names its user in a header, as a bearer token would,
/// and the handler establishes that principal in the request's own scope and the claims tenancy's stand-in resolves.
/// </summary>
internal sealed class HeaderAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "test-header";
    internal const string Header = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers[Header] is not [{ } subject]) return Task.FromResult(AuthenticateResult.NoResult());
        var principal = ExecutionPrincipal.ForUser(new PrincipalReference(subject, IdentityKind.User), HeaderSessions.Of(subject), DateTimeOffset.UtcNow);
        Context.RequestServices.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(principal);
        var user = TestIdentity.User(subject);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName)));
    }
}

/// <summary>One stable session identifier per header user, so a test can end that user's session.</summary>
internal static class HeaderSessions
{
    private static readonly ConcurrentDictionary<string, Guid> Sessions = new(StringComparer.Ordinal);

    internal static Guid Of(string subject) => Sessions.GetOrAdd(subject, _ => Guid.NewGuid());
}

/// <summary>Loopback TLS and a key ring for the Kestrel fixtures, generated for this run.</summary>
internal static class Loopback
{
    internal const string PublicHost = "app.example.test";
    internal static readonly Lazy<X509Certificate2> Certificate = new(Create);

    internal static readonly Lazy<string> KeyRing = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "modularyn-authorization-keys-" + Guid.NewGuid().ToString("N"));
        KeyRingDirectory.Create(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        };
        return path;
    });

    internal static Dictionary<string, string?> Settings() => new()
    {
        ["Modularyn:DataProtection:ApplicationName"] = "authorization-tests",
        ["Modularyn:DataProtection:Directory"] = KeyRing.Value,
        ["Modularyn:Ingress:PublicOrigins:0"] = "https://" + PublicHost
    };

    internal static HttpClient Client() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Pinned }
    }) { Timeout = TimeSpan.FromSeconds(60) };

    internal static Uri Address(WebApplication application) => application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
        .Addresses.Select(address => new Uri(address)).Single();

    private static bool Pinned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256) == Certificate.Value.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 Create()
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

/// <summary>A response's status and the stable code its body carries, if any.</summary>
internal sealed record Answer(HttpStatusCode Status, string? Code, JsonElement? Body);

/// <summary>
/// A real Kestrel server on loopback over TLS, composed as a host composes these packages: SEC-02's ingress, identity's
/// session authentication, SEC-04's tenancy and this order's authorization, over PostgreSQL, with the item endpoints of
/// the fixture module. Each endpoint declares its tenancy and permission; the handlers ask the evaluator for each resource.
/// </summary>
internal sealed class AuthorizationServer : IAsyncDisposable
{
    private readonly WebApplication application;

    private AuthorizationServer(WebApplication application, Uri address, EventLog events)
    {
        this.application = application;
        Address = address;
        Events = events;
        Client = Loopback.Client();
    }

    public Uri Address { get; }
    public HttpClient Client { get; }
    public EventLog Events { get; }
    public IServiceProvider Services => application.Services;

    public static async Task<AuthorizationServer> StartAsync(AuthorizationDatabase database, TestClock clock, Action<WebApplication>? endpoints = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        var settings = Loopback.Settings();
        foreach (var (key, value) in AuthorizationHost.Configuration(database)) settings[key] = value;
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        var events = new EventLog();
        builder.Logging.AddProvider(events);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Loopback.Certificate.Value)));
        builder.Services.AddModularynDataProtection(builder.Configuration);
        builder.Services.AddModularynIngress(builder.Configuration);
        AuthorizationHost.Compose(builder.Services, builder.Configuration, database, clock, new TestSeams(), new RecordingAuthorityAudit(), claimsActors: false);
        builder.Services.AddModularynIdentityHttp();
        builder.Services.AddAuthorization();
        var application = builder.Build();
        try
        {
            application.UseModularynIngress();
            application.UseRouting();
            application.UseModularynTenancy();
            application.UseAuthorization();
            application.MapModularynIdentityEndpoints();
            ItemEndpoints.Map(application, database);
            endpoints?.Invoke(application);
            await application.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
        return new AuthorizationServer(application, Loopback.Address(application), events);
    }

    public T Get<T>() where T : notnull => application.Services.GetRequiredService<T>();

    public Browser Browser() => new(this);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }
}

/// <summary>A browser-like client: it keeps its session cookie, and sends the antiforgery token with every state change.</summary>
internal sealed class Browser(AuthorizationServer server)
{
    private readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);
    private string? token;

    public string? Session => cookies.GetValueOrDefault(SessionCookie.Name);

    public async Task SignInAsync(User user)
    {
        await RefreshTokenAsync();
        var response = await SendAsync(HttpMethod.Post, IdentityEndpoints.SignInPath, JsonContent.Create(new { userName = user.Name, password = user.Password }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Antiforgery tokens are bound to the signed-in user, so a browser fetches a new one after signing in.
        await RefreshTokenAsync();
    }

    public async Task<Answer> CallAsync(HttpMethod method, string path, object? body = null)
    {
        var response = await SendAsync(method, path, body is null ? (method == HttpMethod.Get ? null : new StringContent("")) : JsonContent.Create(body));
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (text.Length == 0 || response.Content.Headers.ContentType?.MediaType is not ("application/json" or "application/problem+json")) return new(response.StatusCode, null, null);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement.Clone();
        return new(response.StatusCode, root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) ? code.GetString() : null, root);
    }

    private async Task RefreshTokenAsync()
    {
        var response = await SendAsync(HttpMethod.Get, IdentityEndpoints.AntiforgeryPath, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        token = body.RootElement.GetProperty("requestToken").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, new Uri(server.Address, path)) { Content = content };
        request.Headers.Host = Loopback.PublicHost;
        if (cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
        if (token is not null && method != HttpMethod.Get) request.Headers.Add("RequestVerificationToken", token);
        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values) && SetCookieHeaderValue.TryParseList(values.ToArray(), out var parsed))
            foreach (var cookie in parsed)
            {
                var name = cookie.Name.Value!;
                if (cookie.Expires is { } expires && expires < DateTimeOffset.UtcNow || string.IsNullOrEmpty(cookie.Value.Value)) cookies.Remove(name);
                else cookies[name] = cookie.Value.Value!;
            }
        return response;
    }
}
