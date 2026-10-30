using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Api;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.HttpApi;
using Xunit;

namespace Modularyn.Framework.Sdk.Client.Tests;

/// <summary>A test transport adapter validates explicit session credentials through SEC-03's real PostgreSQL session service.</summary>
internal sealed class ExplicitSessionAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, ISessionService sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !SessionToken.TryParse(header[7..], out var token))
            return AuthenticateResult.NoResult();
        var session = await sessions.ValidateAsync(token!, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("The session is not current.");
        var principal = session.ToPrincipal();
        if (!Context.RequestServices.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(principal))
            return AuthenticateResult.Fail("A principal is already established.");
        return AuthenticateResult.Success(new AuthenticationTicket(TestIdentity.User(principal.Actor.Id), Scheme.Name));
    }
}

internal sealed class Fault
{
    internal string Mode { get; init; } = "";
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Actual TLS, ingress, sessions, tenancy, permissions and module transactions. Only SEC-07's audit seam is scaffolding.</summary>
internal sealed class ClientServer : IAsyncDisposable
{
    internal static ApiInventory Inventory { get; } = ApiInventory.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture-inventory.json")));
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;
    private readonly string directory;
    private readonly AuthorizationDatabase database;
    internal EventLog Events { get; }
    private readonly ConcurrentDictionary<Guid, Fault> faults = new();
    internal ConcurrentQueue<(Guid Operation, string Correlation)> Requests { get; } = new();

    private ClientServer(WebApplication app, X509Certificate2 certificate, string directory, string origin, AuthorizationDatabase database, EventLog events)
    { this.app = app; this.certificate = certificate; this.directory = directory; this.database = database; Events = events; Origin = EndpointOrigin.Parse(origin); }

    internal EndpointOrigin Origin { get; }
    internal string CertificateFile => Path.Combine(directory, "ca.pem");
    internal Fault Pause(Guid id, string mode) { var fault = new Fault { Mode = mode }; faults[id] = fault; return fault; }
    internal void Clear(Guid id) => faults.TryRemove(id, out _);

    internal ApiClient Transport(string? session, int version = 2) => new(Origin,
        session is null ? new NoCredential() : new OriginBoundCredentialProvider([Origin], (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ClientCredential("Bearer", new SecretText(session)));
        }), version, Handler());

    internal SocketsHttpHandler Handler() => new()
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, remote, _, _) => remote is not null
                    && remote.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256)
            }
        };

    private sealed class NoCredential : IClientCredentialProvider
    {
        public ValueTask<ClientCredential?> GetCredentialAsync(EndpointOrigin target, CancellationToken cancellationToken) => ValueTask.FromResult<ClientCredential?>(null);
    }

    internal static async Task<ClientServer> StartAsync(AuthorizationDatabase database, TestClock clock)
    {
        var directory = Path.Combine(Path.GetTempPath(), "modularyn-client-" + Guid.NewGuid().ToString("N"));
        KeyRingDirectory.Create(directory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Modularyn", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var now = clock.GetUtcNow();
        using var signed = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
        var certificate = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pkcs12), null);
        await File.WriteAllTextAsync(Path.Combine(directory, "ca.pem"), certificate.ExportCertificatePem(), TestContext.Current.CancellationToken);
        var portLease = new TcpListener(IPAddress.Loopback, 0);
        portLease.Start();
        var port = ((IPEndPoint)portLease.LocalEndpoint).Port;
        portLease.Stop();
        var origin = "https://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        var configuration = AuthorizationHost.Configuration(database);
        configuration["Modularyn:DataProtection:ApplicationName"] = "client-tests";
        configuration["Modularyn:DataProtection:Directory"] = directory;
        configuration["Modularyn:Ingress:PublicOrigins:0"] = origin;
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        var events = new EventLog();
        builder.Logging.AddProvider(events);
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port, listener => listener.UseHttps(certificate)));
        builder.Services.AddModularynDataProtection(builder.Configuration);
        builder.Services.AddModularynIngress(builder.Configuration);
        AuthorizationHost.Compose(builder.Services, builder.Configuration, database, clock, new TestSeams(), new RecordingAuthorityAudit());
        builder.Services.AddModularynIdentityHttp();
        // This adapter tests the credential-provider seam against the actual session validator, without an authentication gateway double.
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = "explicit-session";
            options.DefaultChallengeScheme = "explicit-session";
        }).AddScheme<AuthenticationSchemeOptions, ExplicitSessionAuthentication>("explicit-session", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddModularynApi();
        var app = builder.Build();
        var fixture = new ClientServer(app, certificate, directory, origin, database, events);
        try
        {
            // Routing is left to WebApplication, which places it first, so the ingress sees each endpoint's antiforgery declaration.
            app.UseModularynApi();
            app.UseModularynIngress();
            app.UseModularynTenancy();
            app.UseAuthorization();
            app.MapModularynIdentityEndpoints();
            app.MapModularynBootstrap();
            fixture.Map();
            await app.StartAsync(TestContext.Current.CancellationToken);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private void Map()
    {
        foreach (var endpoint in Inventory.Endpoints.Where(endpoint => endpoint.Permission is not null))
        {
            var mapped = app.MapMethods(endpoint.Route, [endpoint.Method], (HttpContext context) => HandleAsync(context, endpoint));
            // The existing ingress permits this declaration only without cookies; any cookie still requires antiforgery.
            if (endpoint.Mutation) mapped.DisableAntiforgery();
            mapped.RequireTenantMember().RequirePermission(endpoint.Permission!).WithApiContract(endpoint, Inventory);
        }
    }

    private async Task HandleAsync(HttpContext context, ApiEndpoint endpoint)
    {
        var tenant = context.RequestServices.GetRequiredService<TenantExecution>().Context!;
        var principal = context.RequestServices.GetRequiredService<IExecutionPrincipalAccessor>().Current!;
        var token = context.RequestAborted;
        var id = context.Request.RouteValues.TryGetValue("id", out var routeId) && Guid.TryParse(routeId?.ToString(), out var parsed) ? parsed : Guid.Empty;
        if (endpoint.Name == "GetCheckpoint")
        {
            await context.Response.WriteAsJsonAsync(new CheckpointView(faults.TryGetValue(id, out var checkpoint)
                ? checkpoint.Finished.Task.IsCompleted ? "finished" : checkpoint.Ready.Task.IsCompleted ? "waiting" : "pending" : "absent"), token);
            return;
        }
        if (endpoint.Name == "GetOperation")
        {
            var result = await ApiOperations.FindAsync<ItemView>(database.ItemsModule, tenant, principal, id, token);
            await context.Response.WriteAsJsonAsync(new OperationView(id, result is null ? "notObserved" : "committed", result), token);
            return;
        }
        if (endpoint.Name == "GetItem")
        {
            await using var unit = await EfCoreUnitOfWork.BeginAsync(database.ItemsModule, tenant, cancellationToken: token);
            var code = id.ToString("D");
            var item = await unit.Context.Set<Item>().SingleOrDefaultAsync(item => item.Code == code, token)
                ?? throw new ApiException(ApiCodes.NotFound, 404);
            await context.Response.WriteAsJsonAsync(View(item), token);
            return;
        }
        var operationId = ApiEndpoints.OperationId(context);
        Requests.Enqueue((operationId, context.Response.Headers[FrameworkApi.CorrelationHeader].ToString()));
        var version = int.Parse(context.Request.RouteValues["version"]!.ToString()!, CultureInfo.InvariantCulture);
        var body = endpoint.Name == "CreateItem"
            ? (object)(await ApiEndpoints.ReadRequestAsync<CreateItemRequest>(context))
            : await ApiEndpoints.ReadRequestAsync<UpdateItemRequest>(context);
        var title = body is CreateItemRequest create ? create.Title : ((UpdateItemRequest)body).Title;
        var note = body is CreateItemRequest createBody ? createBody.Note : ((UpdateItemRequest)body).Note;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || note?.Length > 4096)
            throw new ApiException(ApiCodes.Validation, 400, new Dictionary<string, string[]> { ["title"] = ["required-or-too-long"] });
        faults.TryGetValue(operationId, out var fault);
        try
        {
            var result = await ApiOperations.ExecuteAsync(database.ItemsModule, tenant, principal, operationId, endpoint.Name, version,
                new { id, body }, async (unit, cancellation) =>
                {
                    Item item;
                    if (body is CreateItemRequest)
                    {
                        item = new Item { Code = Guid.NewGuid().ToString("D"), Owner = principal.EffectiveSubject.Id, Title = title, Note = note };
                        unit.Context.Add(item);
                    }
                    else
                    {
                        var code = id.ToString("D");
                        item = await unit.Context.Set<Item>().SingleOrDefaultAsync(row => row.Code == code, cancellation)
                            ?? throw new ApiException(ApiCodes.NotFound, 404);
                        if (item.Version != ((UpdateItemRequest)body).Version) throw new ApiException(ApiCodes.Conflict, 409);
                        item.Title = title;
                        item.Note = note;
                    }
                    await unit.FlushAsync(cancellation);
                    if (fault?.Mode == "before")
                    {
                        fault.Ready.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                    }
                    return View(item);
                }, token);
            if (fault?.Mode == "interrupt") { faults.TryRemove(operationId, out _); context.Abort(); return; }
            if (fault?.Mode == "after")
            {
                fault.Ready.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            if (fault?.Mode == "version") context.Response.Headers[FrameworkApi.VersionHeader] = "0";
            if (fault?.Mode == "protocol")
            {
                await context.Response.WriteAsJsonAsync(new { result.Id, result.Title, result.Note, Version = 42 }, token);
                return;
            }
            await context.Response.WriteAsJsonAsync(result, token);
        }
        finally { fault?.Finished.TrySetResult(); }
    }

    private static ItemView View(Item item) => new(Guid.Parse(item.Code), item.Title, item.Note, item.Version);

    public async ValueTask DisposeAsync()
    {
        try { await app.StopAsync(); await app.DisposeAsync(); }
        finally { certificate.Dispose(); Directory.Delete(directory, recursive: true); }
    }
}

internal sealed class EventLog : ILoggerProvider
{
    internal ConcurrentQueue<int> Codes { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Recorder(Codes);
    public void Dispose() { }
    private sealed class Recorder(ConcurrentQueue<int> codes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => codes.Enqueue(eventId.Id);
    }
}
