using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Modules.Identity.HttpApi;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// The fixture module's endpoints as an API inventory declares them, for the contract and browser layers: every route is
/// declared with its authorization classification and media types, so the host runs under the API conventions, its startup
/// inventory check and its runtime media-type check. Bodies bind strictly to their operation's input contract.
/// </summary>
internal static class DeclaredItems
{
    public sealed record TitleChange(string Title);
    public sealed record GrantChange(string Member, string Permission, bool Revoke);
    public sealed record ValueChange(string Value);

    public static ApiInventory Inventory { get; } = new(1, 1,
    [
        new("TitleChange", [new("Title", "string")]),
        new("GrantChange", [new("Member", "string"), new("Permission", "string"), new("Revoke", "boolean")]),
        new("ValueChange", [new("Value", "string")])
    ],
    [
        Json("GetItem", "GET", "/t/{tenant}/items/{code}", Items.Read),
        Json("UpdateItem", "PUT", "/t/{tenant}/items/{code}", Items.Update, "TitleChange"),
        Json("DeleteItem", "DELETE", "/t/{tenant}/items/{code}", Items.Delete),
        Json("SearchItems", "GET", "/t/{tenant}/items", Items.Read),
        Json("ExportItems", "POST", "/t/{tenant}/exports", Items.Export),
        Json("GrantPermission", "POST", "/t/{tenant}/authority/grants", AuthorizationModule.ManagePermission, "GrantChange"),
        Json("SetTenantSetting", "PUT", "/t/{tenant}/settings/{module}/{name}", SettingsModule.TenantOverridesPermission, "ValueChange"),
        new("AppPage", "GET", "/app", "Anonymous", null, false, null, null, Versioned: false, ResponseMediaType: ApiMediaTypes.Html,
            Authentication: "none", Page: true)
    ]);

    private static ApiEndpoint Json(string name, string method, string route, string permission, string? request = null) =>
        new(name, method, route, "Permission", permission, false, request, null, Mutation: method != "GET", Versioned: false, Authentication: "session");
}

/// <summary>One request as the declared server saw it: the Origin header, the names of the cookies presented, never their values, and the answer.</summary>
internal sealed record Trace(string Method, string Path, string? Origin, string[] Cookies, int Status, string? AllowOrigin, string? AllowCredentials,
    string? Vary);

/// <summary>
/// A real Kestrel server on loopback over TLS, composed as a host composes these packages under the API conventions:
/// <c>UseModularynApi</c> before SEC-02's ingress, identity's session authentication and endpoints, SEC-04's tenancy, the
/// evaluator and the declared item endpoints over PostgreSQL. It records every request's trace, and lets a test hold an
/// authority change inside its transaction through <see cref="Audit"/>.
/// </summary>
internal sealed class DeclaredServer : IAsyncDisposable
{
    private readonly WebApplication application;

    private DeclaredServer(WebApplication application, Uri address, EventLog events, RecordingAuthorityAudit audit, ConcurrentQueue<Trace> traces)
    {
        this.application = application;
        Address = address;
        Events = events;
        Audit = audit;
        Traces = traces;
        Client = Loopback.Client();
    }

    public Uri Address { get; }
    public HttpClient Client { get; }
    public EventLog Events { get; }
    public RecordingAuthorityAudit Audit { get; }
    public ConcurrentQueue<Trace> Traces { get; }
    public IServiceProvider Services => application.Services;

    /// <summary>
    /// Starts the server. <paramref name="settings"/> adds or replaces deployment configuration, such as the listed cross-origin
    /// callers or a public origin with its port; <paramref name="port"/> fixes the listening port, which such an origin names.
    /// </summary>
    public static async Task<DeclaredServer> StartAsync(AuthorizationDatabase database, TestClock clock, IDictionary<string, string?>? settings = null,
        int port = 0, Action<WebApplication>? endpoints = null, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        var values = Loopback.Settings();
        foreach (var (key, value) in AuthorizationHost.Configuration(database)) values[key] = value;
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) values[key] = value;
        builder.Configuration.AddInMemoryCollection(values);
        builder.Logging.ClearProviders();
        var events = new EventLog();
        builder.Logging.AddProvider(events);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(Loopback.Certificate.Value)));
        builder.Services.AddModularynDataProtection(builder.Configuration);
        builder.Services.AddModularynIngress(builder.Configuration);
        var audit = new RecordingAuthorityAudit();
        AuthorizationHost.Compose(builder.Services, builder.Configuration, database, clock, new TestSeams(), audit, claimsActors: false);
        builder.Services.AddModularynIdentityHttp();
        builder.Services.AddAuthorization();
        builder.Services.AddModularynApi();
        services?.Invoke(builder.Services);
        var application = builder.Build();
        var traces = new ConcurrentQueue<Trace>();
        try
        {
            // The trace records what reached the host and what it answered, before any policy runs.
            application.Use(async (context, next) =>
            {
                var cookies = CookieHeaderValue.ParseList(context.Request.Headers.Cookie.OfType<string>().ToList()).Select(cookie => cookie.Name.Value!)
                    .Order(StringComparer.Ordinal).ToArray();
                var origin = context.Request.Headers.Origin is [{ } named] ? named : null;
                try { await next(context); }
                finally
                {
                    var headers = context.Response.Headers;
                    traces.Enqueue(new(context.Request.Method, context.Request.Path.Value ?? "", origin, cookies, context.Response.StatusCode,
                        headers.AccessControlAllowOrigin.FirstOrDefault(), headers.AccessControlAllowCredentials.FirstOrDefault(), headers.Vary.ToString()));
                }
            });
            // Routing is left to WebApplication, which places it first, so the ingress sees each endpoint's antiforgery declaration.
            application.UseModularynApi();
            application.UseModularynIngress();
            application.UseModularynTenancy();
            application.UseAuthorization();
            application.MapModularynIdentityEndpoints();
            Map(application, database);
            endpoints?.Invoke(application);
            await application.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
        return new(application, Loopback.Address(application), events, audit, traces);
    }

    public T Get<T>() where T : notnull => application.Services.GetRequiredService<T>();

    public DeclaredClient NewClient() => new(this);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }

    private static void Map(WebApplication application, AuthorizationDatabase database)
    {
        var items = database.ItemsModule;
        void Declared(string name, Delegate handler)
        {
            var contract = DeclaredItems.Inventory.Endpoints.Single(endpoint => endpoint.Name == name);
            var mapped = application.MapMethods(contract.Route, [contract.Method], handler);
            if (contract.Permission is { } permission) mapped.RequireTenantMember().RequirePermission(permission);
            else mapped.AllowAnonymous();
            mapped.WithApiContract(contract, DeclaredItems.Inventory);
        }

        Declared("GetItem", async (string code, HttpContext http) =>
        {
            var (context, principal, evaluator) = Request(http);
            var item = await ResolveAsync(evaluator, items, context, principal, code, Items.Read, http.RequestAborted);
            return Results.Json(View(item));
        });
        Declared("UpdateItem", async (string code, HttpContext http) =>
        {
            // The body binds strictly to the operation's input contract: a field it does not list refuses the request (MAP001).
            var change = await ApiEndpoints.ReadRequestAsync<DeclaredItems.TitleChange>(http);
            if (string.IsNullOrWhiteSpace(change.Title) || change.Title.Length > 200) throw new ApiException(ApiCodes.Validation, 400);
            var (context, principal, evaluator) = Request(http);
            var item = await ResolveAsync(evaluator, items, context, principal, code, Items.Update, http.RequestAborted);
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: http.RequestAborted);
            var row = await ((ItemsContext)work.Context).Items.SingleAsync(entry => entry.Code == item.Code, http.RequestAborted);
            // Owner, tenant, revision and version are the server's: only the title comes from the request.
            row.Title = change.Title;
            row.Revision++;
            await work.CompleteAsync(http.RequestAborted);
            return Results.Json(View(row));
        });
        Declared("DeleteItem", async (string code, HttpContext http) =>
        {
            var (context, principal, evaluator) = Request(http);
            var item = await ResolveAsync(evaluator, items, context, principal, code, Items.Delete, http.RequestAborted);
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: http.RequestAborted);
            var db = (ItemsContext)work.Context;
            db.Items.Remove(await db.Items.SingleAsync(entry => entry.Code == item.Code, http.RequestAborted));
            await work.CompleteAsync(http.RequestAborted);
            return Results.NoContent();
        });
        Declared("SearchItems", async (HttpContext http) =>
        {
            var (context, principal, evaluator) = Request(http);
            var prefix = http.Request.Query["prefix"].ToString();
            var offset = int.TryParse(http.Request.Query["offset"], out var skip) && skip >= 0 ? skip : 0;
            var limit = int.TryParse(http.Request.Query["limit"], out var take) && take is > 0 and <= 100 ? take : 100;
            var visibility = await evaluator.VisibleAsync(new AuthorizationRequest(context, principal, Items.Read), Items.ResourceType, http.RequestAborted);
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, UnitOfWorkMode.Suppress, cancellationToken: http.RequestAborted);
            // The filter, the count and the page are all built over the visible rows, so hidden rows affect none of them.
            var visible = visibility.Apply(((ItemsContext)work.Context).Items.AsNoTracking(), row => row.Owner, row => row.Code)
                .Where(row => row.Code.StartsWith(prefix));
            return Results.Json(new
            {
                items = await visible.OrderBy(row => row.Code).Skip(offset).Take(limit).Select(row => row.Code).ToArrayAsync(http.RequestAborted),
                total = await visible.CountAsync(http.RequestAborted)
            });
        });
        Declared("ExportItems", async (HttpContext http) =>
        {
            var (context, principal, _) = Request(http);
            var operation = await ItemWork.ExportAsync(http.RequestServices, items, context, principal, null, http.RequestAborted);
            return Results.Json(new { operation = operation.Id, state = operation.State, disposition = operation.Disposition });
        });
        Declared("GrantPermission", async (HttpContext http) =>
        {
            var grant = await ApiEndpoints.ReadRequestAsync<DeclaredItems.GrantChange>(http);
            var (context, principal, _) = Request(http);
            var administration = http.RequestServices.GetRequiredService<AuthorityAdministration>();
            var member = new ActorReference(ActorKind.User, grant.Member);
            try
            {
                var changed = grant.Revoke ? await administration.RevokePermissionAsync(context, principal!, member, grant.Permission, http.RequestAborted)
                    : await administration.GrantPermissionAsync(context, principal!, member, grant.Permission, http.RequestAborted);
                return Results.Json(new { changed });
            }
            catch (AuthorityChangeException exception)
            {
                throw new ApiException(exception.Code, exception.Code is AuthorizationCodes.AuditUnavailable ? 503 : 403);
            }
        });
        Declared("SetTenantSetting", async (string module, string name, HttpContext http) =>
        {
            var change = await ApiEndpoints.ReadRequestAsync<DeclaredItems.ValueChange>(http);
            var (context, principal, _) = Request(http);
            // Only a declared setting can be set; a deployment option is no setting at all, so the API has nothing to change.
            var definition = http.RequestServices.GetRequiredService<ValueDefinitionRegistry>().Definitions
                .SingleOrDefault(entry => entry.ModuleId == module && entry.Name == name) ?? throw new ApiException(ApiCodes.NotFound, 404);
            try
            {
                var version = await http.RequestServices.GetRequiredService<SettingsService>().SetAsync(context, principal, definition, ValueSource.Tenant,
                    change.Value, cancellationToken: http.RequestAborted);
                return Results.Json(new { version });
            }
            catch (ValueValidationException) { throw new ApiException(ApiCodes.Validation, 400); }
            catch (ValueAccessDeniedException exception) { throw new ApiException(exception.Code, 403); }
        });
        Declared("AppPage", () => Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Items</title></head>"
            + "<body><h1>Items</h1></body></html>", ApiMediaTypes.Html));
    }

    // Resolved within the tenant first; a missing item and one the subject may not use answer alike, revealing nothing.
    private static async Task<Item> ResolveAsync(IPermissionEvaluator evaluator, PostgreSqlModule items, TenantContext context, ExecutionPrincipal? principal,
        string code, string permission, CancellationToken cancellationToken)
    {
        var item = await ItemWork.FindAsync(items, context, code, cancellationToken) ?? throw new ApiException(ApiCodes.NotFound, 404);
        var decision = await evaluator.AuthorizeAsync(new AuthorizationRequest(context, principal, permission) { Resource = Items.Reference(item) }, cancellationToken);
        if (decision.Permitted) return item;
        throw decision.Code == AuthorizationCodes.ResourceNotFound ? new ApiException(ApiCodes.NotFound, 404)
            : new ApiException(decision.Code!, decision.IsTransient ? 503 : 403);
    }

    private static object View(Item item) => new { code = item.Code, title = item.Title, owner = item.Owner, revision = item.Revision, version = item.Version };

    private static (TenantContext Context, ExecutionPrincipal? Principal, IPermissionEvaluator Evaluator) Request(HttpContext http) =>
        (http.RequestServices.GetRequiredService<TenantExecution>().Context!, http.RequestServices.GetRequiredService<IExecutionPrincipalAccessor>().Current,
            http.RequestServices.GetRequiredService<IPermissionEvaluator>());
}

/// <summary>A response as a test reads it: status, headers, the problem or JSON body and the raw text.</summary>
internal sealed record Reply(HttpStatusCode Status, string? Code, JsonElement? Body, string Text, IReadOnlyDictionary<string, string> Headers, string? MediaType)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>
/// A browser-like client of the declared server: it keeps its cookies and sends the antiforgery token with every state change,
/// unless a request says otherwise, and can present any cookie or Origin header a test chooses.
/// </summary>
internal sealed class DeclaredClient(DeclaredServer server)
{
    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);
    public string? Token { get; set; }

    public async Task SignInAsync(User user)
    {
        await RefreshTokenAsync();
        var reply = await SendAsync(HttpMethod.Post, IdentityEndpoints.SignInPath, new { userName = user.Name, password = user.Password });
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        // Antiforgery tokens are bound to the signed-in user, so a browser fetches a new one after signing in.
        await RefreshTokenAsync();
    }

    public async Task RefreshTokenAsync()
    {
        var reply = await SendAsync(HttpMethod.Get, IdentityEndpoints.AntiforgeryPath);
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Token = reply.Body!.Value.GetProperty("requestToken").GetString();
    }

    public async Task<Reply> SendAsync(HttpMethod method, string path, object? body = null, bool antiforgery = true, string? origin = null,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(server.Address, path))
        {
            Content = body is null ? (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options ? null : new StringContent(""))
                : body is string text ? new StringContent(text, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body)
        };
        request.Headers.Host = Loopback.PublicHost;
        if (Cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
        if (antiforgery && Token is not null && method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options)
            request.Headers.Add("RequestVerificationToken", Token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        configure?.Invoke(request);
        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values) && SetCookieHeaderValue.TryParseList(values.ToArray(), out var parsed))
            foreach (var cookie in parsed)
            {
                var name = cookie.Name.Value!;
                if (cookie.Expires is { } expires && expires < DateTimeOffset.UtcNow || string.IsNullOrEmpty(cookie.Value.Value)) Cookies.Remove(name);
                else Cookies[name] = cookie.Value.Value!;
            }
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        JsonElement? json = null;
        string? code = null;
        if (raw.Length > 0 && mediaType is "application/json" or "application/problem+json")
        {
            using var document = JsonDocument.Parse(raw);
            json = document.RootElement.Clone();
            if (json.Value.ValueKind == JsonValueKind.Object && json.Value.TryGetProperty("code", out var named)) code = named.GetString();
        }
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        return new(response.StatusCode, code, json, raw, headers, mediaType);
    }
}
