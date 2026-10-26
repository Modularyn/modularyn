using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// A real Kestrel server on loopback over TLS, composed as a host composes tenancy: SEC-02's ingress, a test stand-in for
/// SEC-03's authentication, the tenancy middleware and endpoints over the PostgreSQL items module.
/// </summary>
internal sealed class TenancyServer : IAsyncDisposable
{
    internal const string SubjectHeader = "X-Test-Subject";
    internal const string ClaimHeader = "X-Test-Tenant-Claim";
    internal const string HostReport = "test.host.report";
    private static readonly Lazy<X509Certificate2> Certificate = new(CreateCertificate);
    private static readonly Lazy<string> KeyRing = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "modularyn-tenancy-keys-" + Guid.NewGuid().ToString("N"));
        KeyRingDirectory.Create(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { }
        };
        return path;
    });
    private readonly WebApplication application;
    private int handled;

    private TenancyServer(WebApplication application, Uri address)
    {
        this.application = application;
        Address = address;
        Client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Pinned }
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal Uri Address { get; }
    internal HttpClient Client { get; }
    internal int Handled => Volatile.Read(ref handled);
    internal IServiceProvider Services => application.Services;

    /// <summary>Each handled request's context, as the handler observed it.</summary>
    internal ConcurrentQueue<ContextSnapshot> Observed { get; } = new();

    /// <summary>The server's log events, by identifier and level, without messages or values.</summary>
    internal RecordingLoggerProvider Events { get; private init; } = new();

    /// <summary>The context objects handlers received, kept to show that each ends with its request.</summary>
    internal ConcurrentQueue<TenantContext> Contexts { get; } = new();

    internal static async Task<TenancyServer> StartAsync(TenancyDatabase database, TenancyPipeline pipeline = TenancyPipeline.Standard)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        var settings = new Dictionary<string, string?>
        {
            ["Modularyn:DataProtection:ApplicationName"] = "tenancy-tests",
            ["Modularyn:DataProtection:Directory"] = KeyRing.Value
        };
        for (var index = 0; index < TenancyDatabase.PublicOrigins.Length; index++)
            settings["Modularyn:Ingress:PublicOrigins:" + index] = TenancyDatabase.PublicOrigins[index];
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        var events = new RecordingLoggerProvider();
        builder.Logging.AddProvider(events);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Certificate.Value)));
        builder.Services.AddModularynDataProtection(builder.Configuration);
        builder.Services.AddModularynIngress(builder.Configuration);
        builder.Services.AddModularynTenancy(database.Directory);
        database.Seams.Register(builder.Services);
        var application = builder.Build();
        TenancyServer? server = null;

        application.UseModularynIngress();
        // Stands in for SEC-03's authentication: an authenticated principal named by a test header.
        application.Use((context, next) =>
        {
            if (context.Request.Headers[SubjectHeader] is [{ } subject])
                context.User = TestIdentity.User(subject, [.. context.Request.Headers[ClaimHeader].Select(value => value ?? "")]);
            return next(context);
        });
        switch (pipeline)
        {
            case TenancyPipeline.Standard:
                application.UseModularynTenancy();
                break;
            case TenancyPipeline.TenancyBeforeRouting:
                // Misplaced: routing runs after tenancy, so the middleware sees no endpoint and establishes nothing.
                application.UseModularynTenancy();
                application.UseRouting();
                break;
            case TenancyPipeline.TenancyOmitted:
                break;
        }

        application.MapPost("/items", async (HttpContext http, TenantExecution execution, string code, string name) =>
        {
            var context = server!.Observe(execution);
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: http.RequestAborted);
            var item = new Item { Id = Guid.NewGuid(), Code = code, Name = name };
            work.Context.Add(item);
            try { await work.CompleteAsync(http.RequestAborted); }
            catch (DbUpdateException) { return Results.Conflict(); }
            return Results.Json(new ItemView(item.TenantId, item.Id, item.Code));
        }).RequireTenantMember().DisableAntiforgery();
        RequestDelegate list = async http =>
        {
            var context = server!.Observe(http.RequestServices.GetRequiredService<TenantExecution>());
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: http.RequestAborted);
            var rows = await ((ItemsContext)work.Context).Items.OrderBy(row => row.Code)
                .Select(row => new ItemView(row.TenantId, row.Id, row.Code)).ToArrayAsync(http.RequestAborted);
            await http.Response.WriteAsJsonAsync(rows, http.RequestAborted);
        };
        application.MapGet("/items", list).RequireTenantMember();
        application.MapGet("/t/{tenant}/items", list).RequireTenantMember();
        application.MapGet("/items/{id:guid}", async (HttpContext http, TenantExecution execution, Guid id) =>
        {
            var context = server!.Observe(execution);
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: http.RequestAborted);
            var row = await ((ItemsContext)work.Context).Items.SingleOrDefaultAsync(item => item.Id == id, http.RequestAborted);
            return row is null ? Results.NotFound() : Results.Json(new ItemView(row.TenantId, row.Id, row.Code));
        }).RequireTenantMember();
        // Enqueues propagated work in the producing tenant's unit, with the validated context's references beside the payload.
        application.MapPost("/work", async (HttpContext http, TenantExecution execution, string kind, string payload) =>
        {
            var context = server!.Observe(execution);
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: http.RequestAborted);
            var item = new WorkItem { Id = Guid.NewGuid(), Kind = kind, Payload = payload, Propagated = PropagatedTenantContext.Capture(context).ToJson() };
            work.Context.Add(item);
            await work.CompleteAsync(http.RequestAborted);
            return Results.Json(new ItemView(item.TenantId, item.Id, item.Kind));
        }).RequireTenantMember().DisableAntiforgery();
        application.MapGet("/throw", (TenantExecution execution) =>
        {
            server!.Observe(execution);
            throw new InvalidOperationException("The handler failed.");
        }).RequireTenantMember();
        application.MapGet("/context", (TenantExecution execution) => Results.Json(server!.Observe(execution).Snapshot())).RequireTenantMember();
        application.MapGet("/plain", (TenantExecution execution) =>
        {
            Interlocked.Increment(ref server!.handled);
            return Results.Text(execution.Context is null ? "none" : "present");
        });
        application.MapGet("/host/report", async (HttpContext http, TenantExecution execution) =>
        {
            var context = server!.Observe(execution);
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: http.RequestAborted);
            var counts = await work.Context.AcrossTenants<Item>().Query.GroupBy(row => row.TenantId)
                .Select(group => new TenantCount(group.Key, group.Count())).ToArrayAsync(http.RequestAborted);
            return Results.Json(counts);
        }).RequireHostAuthority(HostReport, "Count items for every tenant");
        application.MapGet("/cached/items", async (HttpContext http, TenantExecution execution, TenantCache cache) =>
        {
            var context = server!.Observe(execution);
            var built = false;
            var entry = new TenantCacheEntry("Items", "1", "item-codes", TimeSpan.FromMinutes(5), principalScoped: false);
            var codes = await cache.GetOrCreateAsync(context, entry, async cancellation =>
            {
                built = true;
                await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: cancellation);
                return await ((ItemsContext)work.Context).Items.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(cancellation);
            }, http.RequestAborted);
            return Results.Json(new CachedCodes(cache.KeyFor(context, entry), built, codes));
        }).RequireTenantMember();

        await application.StartAsync(TestContext.Current.CancellationToken);
        var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(item => new Uri(item)).Single();
        server = new TenancyServer(application, address) { Events = events };
        return server;
    }

    internal Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string host, string? subject = null,
        Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(method, new Uri(Address, path));
        request.Headers.Host = host;
        if (subject is not null) request.Headers.Add(SubjectHeader, subject);
        configure?.Invoke(request);
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    internal static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), JsonSerializerOptions.Web)!;
    }

    private TenantContext Observe(TenantExecution execution)
    {
        Interlocked.Increment(ref handled);
        var context = execution.Context ?? throw new InvalidOperationException("The handler ran without an established tenant context.");
        Observed.Enqueue(ContextSnapshot.Of(context));
        Contexts.Enqueue(context);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }

    private static bool Pinned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256) == Certificate.Value.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + TenancyDatabase.AppHost, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(TenancyDatabase.AppHost);
        names.AddDnsName(TenancyDatabase.HostA);
        names.AddDnsName(TenancyDatabase.HostB);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }
}

/// <summary>Records each log event's identifier and level.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    internal ConcurrentQueue<(int Id, LogLevel Level)> Recorded { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(RecordingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Recorded.Enqueue((eventId.Id, logLevel));
    }
}

/// <summary>Where the test server places the tenancy middleware.</summary>
public enum TenancyPipeline { Standard, TenancyBeforeRouting, TenancyOmitted }

internal sealed record ItemView(Guid Tenant, Guid Id, string Code);

internal sealed record TenantCount(Guid Tenant, int Count);

internal sealed record CachedCodes(string Key, bool Built, string[] Codes);

/// <summary>A context as evidence records it: scope, tenant, actor, source and unit, with no other request value.</summary>
internal sealed record ContextSnapshot(string Scope, Guid? Tenant, string ActorKind, string Actor, string Source, Guid Unit)
{
    internal static ContextSnapshot Of(TenantContext context) => new(context.Scope.ToString(), context.TenantId, context.Actor.Kind.ToString(),
        context.Actor.Subject, context.Source.ToString(), context.UnitId);
}

internal static class SnapshotExtensions
{
    internal static ContextSnapshot Snapshot(this TenantContext context) => ContextSnapshot.Of(context);
}
