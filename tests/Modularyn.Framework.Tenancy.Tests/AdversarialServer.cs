using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Identity.HttpApi;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// The items module's endpoints as an API inventory declares them: CRUD over items by identifier, a list, a count and an
/// export filtered and sorted by a key, notes that reference an item, a batch import, a link that names the item, a
/// principal-scoped cached view, Files downloads by identifier and by grant, and the cross-tenant report under host authority.
/// Bodies bind strictly to their operation's input contract, so a request field the contract does not list refuses the request.
/// </summary>
internal static class AdversarialEndpoints
{
    internal const string GrantHeader = "X-Modularyn-Grant";
    internal const string ImportType = "text/csv";

    internal sealed record ItemCreate(string Code, string Name);
    internal sealed record ItemChange(string Name);
    internal sealed record NoteCreate(Guid Item, string Text);

    internal static ApiInventory Inventory { get; } = new(1, 1,
    [
        new("ItemCreate", [new("Code", "string"), new("Name", "string")]),
        new("ItemChange", [new("Name", "string")]),
        new("NoteCreate", [new("Item", "uuid"), new("Text", "string")])
    ],
    [
        Json("CreateItem", "POST", "/t/{tenant}/items", AdversarialItems.Write, "ItemCreate"),
        Json("GetItem", "GET", "/t/{tenant}/items/{id}", AdversarialItems.Read),
        Json("UpdateItem", "PUT", "/t/{tenant}/items/{id}", AdversarialItems.Write, "ItemChange"),
        Json("DeleteItem", "DELETE", "/t/{tenant}/items/{id}", AdversarialItems.Delete),
        Json("ListItems", "GET", "/t/{tenant}/items", AdversarialItems.Read),
        Json("CountItems", "GET", "/t/{tenant}/items/count", AdversarialItems.Read),
        Json("ExportItems", "POST", "/t/{tenant}/items/exports", AdversarialItems.Export),
        Json("ItemLink", "GET", "/t/{tenant}/items/{id}/link", AdversarialItems.Read),
        Json("CachedItems", "GET", "/t/{tenant}/cached/items", AdversarialItems.Read),
        Json("CreateNote", "POST", "/t/{tenant}/notes", AdversarialItems.Write, "NoteCreate"),
        new("ImportItems", "POST", "/t/{tenant}/imports", "Permission", AdversarialItems.Import, false, null, null, Mutation: true, Versioned: false,
            Authentication: "session", Upload: new ApiUpload([ImportType])),
        Download("DownloadFile", "/t/{tenant}/files/{file}/revisions/{revision}"),
        Download("RedeemGrant", "/t/{tenant}/downloads"),
        new("ItemsReport", "GET", "/host/items/report", "HostAuthority", null, false, null, null, Versioned: false, Authentication: "session")
    ]);

    private static ApiEndpoint Json(string name, string method, string route, string permission, string? request = null) =>
        new(name, method, route, "Permission", permission, false, request, null, Mutation: method != "GET", Versioned: false, Authentication: "session");

    private static ApiEndpoint Download(string name, string route) => new(name, "GET", route, "Permission", FilePermissions.Read, false, null, null,
        Versioned: false, ResponseMediaType: ApiMediaTypes.Binary, Authentication: "session", Download: new ApiDownload("attachment", ["text/plain"]));

    internal static void Map(WebApplication application, AdversarialDatabase database)
    {
        var items = database.Items;
        void Declared(string name, Delegate handler)
        {
            var contract = Inventory.Endpoints.Single(endpoint => endpoint.Name == name);
            var mapped = application.MapMethods(contract.Route, [contract.Method], handler);
            if (contract.Authorization == "HostAuthority") mapped.RequireHostAuthority(AdversarialItems.Report.Id, "Count every tenant's items");
            else mapped.RequireTenantMember().RequirePermission(contract.Permission!);
            mapped.WithApiContract(contract, Inventory);
        }

        Declared("CreateItem", async (HttpContext http) =>
        {
            var create = await ApiEndpoints.ReadRequestAsync<ItemCreate>(http);
            if (string.IsNullOrWhiteSpace(create.Code) || create.Code.Length > 64 || string.IsNullOrWhiteSpace(create.Name)) throw new ApiException(ApiCodes.Validation, 400);
            var context = Context(http);
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: http.RequestAborted);
            var item = new Item { Id = Guid.NewGuid(), Code = create.Code, Name = create.Name };
            work.Context.Add(item);
            // A duplicate key answers a stable conflict and nothing of the row it collided with, nor of any other tenant's keys.
            try { await work.CompleteAsync(http.RequestAborted); }
            catch (DbUpdateException) { throw new ApiException(ApiCodes.Conflict, 409); }
            return Results.Json(View(item));
        });
        Declared("GetItem", async (Guid id, HttpContext http) => Results.Json(View(await FindAsync(items, Context(http), id, http.RequestAborted))));
        Declared("UpdateItem", async (Guid id, HttpContext http) =>
        {
            var change = await ApiEndpoints.ReadRequestAsync<ItemChange>(http);
            var context = Context(http);
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: http.RequestAborted);
            var row = await ((ItemsContext)work.Context).Items.SingleOrDefaultAsync(row => row.Id == id, http.RequestAborted) ?? throw new ApiException(ApiCodes.NotFound, 404);
            row.Name = change.Name;
            await work.CompleteAsync(http.RequestAborted);
            return Results.Json(View(row));
        });
        Declared("DeleteItem", async (Guid id, HttpContext http) =>
        {
            var context = Context(http);
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: http.RequestAborted);
            var db = (ItemsContext)work.Context;
            db.Items.Remove(await db.Items.SingleOrDefaultAsync(row => row.Id == id, http.RequestAborted) ?? throw new ApiException(ApiCodes.NotFound, 404));
            await work.CompleteAsync(http.RequestAborted);
            return Results.NoContent();
        });
        Declared("ListItems", async (HttpContext http) =>
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, Context(http), UnitOfWorkMode.Suppress, cancellationToken: http.RequestAborted);
            var query = Filter(((ItemsContext)work.Context).Items.AsNoTracking(), http.Request.Query);
            query = http.Request.Query["sort"].ToString() == "name" ? query.OrderBy(row => row.Name).ThenBy(row => row.Id) : query.OrderBy(row => row.Code).ThenBy(row => row.Id);
            return Results.Json(new { items = await query.Select(row => new ItemView(row.TenantId, row.Id, row.Code)).ToArrayAsync(http.RequestAborted),
                total = await query.CountAsync(http.RequestAborted) });
        });
        Declared("CountItems", async (HttpContext http) =>
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, Context(http), UnitOfWorkMode.Suppress, cancellationToken: http.RequestAborted);
            return Results.Json(new { count = await Filter(((ItemsContext)work.Context).Items, http.Request.Query).CountAsync(http.RequestAborted) });
        });
        Declared("ExportItems", async (HttpContext http) =>
        {
            // The export reads through the module's direct-SQL path, which composes the tenant predicate over its raw SELECT.
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, Context(http), UnitOfWorkMode.Suppress, cancellationToken: http.RequestAborted);
            var rows = await Filter(work.Sql.Query<Item>(), http.Request.Query).OrderBy(row => row.Code)
                .Select(row => new { row.Id, row.Code, row.Name }).ToArrayAsync(http.RequestAborted);
            return Results.Json(new { rows = rows.Select(row => row.Id.ToString("D") + "," + row.Code + "," + row.Name).ToArray() });
        });
        Declared("ItemLink", async (Guid id, HttpContext http) =>
        {
            var item = await FindAsync(items, Context(http), id, http.RequestAborted);
            // An absolute link comes from the configured public origin that matched the validated host, never a request header.
            var origin = http.RequestServices.GetRequiredService<PublicOrigins>().ForRequest(http.Request) ?? throw new ApiException(ApiCodes.NotFound, 404);
            return Results.Json(new { link = origin + "/t/" + item.TenantId.ToString("D") + "/items/" + item.Id.ToString("D") });
        });
        Declared("CachedItems", async (HttpContext http) =>
        {
            var context = Context(http);
            var cache = http.RequestServices.GetRequiredService<TenantCache>();
            var scoped = http.Request.Query["mine"] == "true";
            var entry = new TenantCacheEntry(AdversarialItems.ModuleId, http.Request.Query["version"] == "2" ? "2" : "1", "item-codes", TimeSpan.FromMinutes(5),
                principalScoped: scoped);
            var built = false;
            var codes = await cache.GetOrCreateAsync(context, entry, async token =>
            {
                built = true;
                await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, UnitOfWorkMode.Suppress, cancellationToken: token);
                var rows = await ((ItemsContext)work.Context).Items.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(token);
                return scoped ? [.. rows, "for:" + context.Actor.Subject] : rows;
            }, http.RequestAborted);
            return Results.Json(new CachedCodes(cache.KeyFor(context, entry), built, codes));
        });
        Declared("CreateNote", async (HttpContext http) =>
        {
            var create = await ApiEndpoints.ReadRequestAsync<NoteCreate>(http);
            await using var work = await EfCoreUnitOfWork.BeginAsync(items, Context(http), cancellationToken: http.RequestAborted);
            var note = new ItemNote { Id = Guid.NewGuid(), ItemId = create.Item, Text = create.Text };
            work.Context.Add(note);
            // The reference is the tenant and the item together, so another tenant's item is no item at all.
            try { await work.CompleteAsync(http.RequestAborted); }
            catch (DbUpdateException) { throw new ApiException(ApiCodes.NotFound, 404); }
            return Results.Json(new { id = note.Id, item = note.ItemId });
        });
        Declared("ImportItems", async (HttpContext http) =>
        {
            var context = Context(http);
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var rows = (await reader.ReadToEndAsync(http.RequestAborted)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            try { return Results.Json(new { imported = await ImportAsync(items, context, rows, http.RequestAborted) }); }
            catch (FormatException) { throw new ApiException(ApiCodes.Validation, 400); }
            // A row naming another tenant, or a reference into one, refuses the whole batch, and nothing of it is written.
            catch (PersistenceContractException) { throw new ApiException(ApiCodes.Validation, 400); }
            catch (DbUpdateException) { throw new ApiException(ApiCodes.Validation, 400); }
        });
        Declared("DownloadFile", async (Guid file, Guid revision, HttpContext http) =>
        {
            var (context, principal) = Principal(http);
            await DeliverAsync(http, () => http.RequestServices.GetRequiredService<FileService>().ReadAsync(context, principal, file, revision,
                cancellationToken: http.RequestAborted));
            return Results.Empty;
        });
        Declared("RedeemGrant", async (HttpContext http) =>
        {
            if (http.Request.QueryString.HasValue || http.Request.Headers[GrantHeader] is not [{ } raw] || !FileGrantToken.TryParse(raw, out var grant))
                throw new ApiException(ApiCodes.Validation, 400);
            var (context, principal) = Principal(http);
            await DeliverAsync(http, () => http.RequestServices.GetRequiredService<FileService>().RedeemGrantAsync(context, principal, grant, FileGrantPurpose.Download,
                cancellationToken: http.RequestAborted));
            return Results.Empty;
        });
        Declared("ItemsReport", async (HttpContext http) =>
        {
            var context = Context(http);
            TenantCount[] counts;
            await using (var work = await EfCoreUnitOfWork.BeginAsync(items, context, UnitOfWorkMode.Suppress, cancellationToken: http.RequestAborted))
                counts = await (await work.Context.AcrossTenantsAsync<Item>(http.RequestAborted)).Query.GroupBy(row => row.TenantId)
                    .Select(group => new TenantCount(group.Key, group.Count())).ToArrayAsync(http.RequestAborted);
            // The module records which tenants its host operation reached, under the operation's actor, before it answers.
            var audit = http.RequestServices.GetRequiredService<IAuditTrail>();
            foreach (var count in counts)
                await audit.RecordAccessAsync(new AuditEntry(ReportAction, context.Actor)
                {
                    TenantId = count.Tenant, UnitId = context.UnitId, OperationId = AdversarialItems.Report.Id, Reason = "Count every tenant's items"
                }, http.RequestAborted);
            return Results.Json(counts);
        });
    }

    internal const string ReportAction = "items.report-read";

    /// <summary>
    /// The module's batch import: each row is <c>code,name,tenant,note-item</c>. The module trusts its rows as an importer
    /// naively would, placing the named tenant on the item and the named item on its note, so only the adapter's checks
    /// stand between a row and another tenant. The batch commits whole or not at all.
    /// </summary>
    internal static async Task<int> ImportAsync(PostgreSqlModule items, TenantContext context, IReadOnlyList<string> rows, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, cancellationToken: cancellationToken);
        foreach (var row in rows)
        {
            var fields = row.Split(',');
            if (fields.Length != 4 || fields[0].Length == 0 || fields[1].Length == 0) throw new FormatException("An import row has four fields.");
            var item = new Item { Id = Guid.NewGuid(), Code = fields[0], Name = fields[1] };
            if (fields[2].Length > 0) item.TenantId = Guid.ParseExact(fields[2], "D");
            work.Context.Add(item);
            if (fields[3].Length > 0)
                work.Context.Add(new ItemNote { Id = Guid.NewGuid(), TenantId = item.TenantId, ItemId = Guid.ParseExact(fields[3], "D"), Text = "imported" });
        }
        await work.FlushAsync(cancellationToken);
        if ((await work.CompleteAsync(cancellationToken)).State != UnitOfWorkState.Committed) throw new DbUpdateException("The import did not commit.");
        return rows.Count;
    }

    /// <summary>The key filters a list, count or export takes: an item's code or identifier.</summary>
    private static IQueryable<Item> Filter(IQueryable<Item> query, IQueryCollection parameters)
    {
        if (parameters["code"].ToString() is { Length: > 0 } code) query = query.Where(row => row.Code == code);
        if (Guid.TryParseExact(parameters["id"].ToString(), "D", out var id)) query = query.Where(row => row.Id == id);
        return query;
    }

    private static async Task<Item> FindAsync(PostgreSqlModule items, TenantContext context, Guid id, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(items, context, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken);
        return await ((ItemsContext)work.Context).Items.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw new ApiException(ApiCodes.NotFound, 404);
    }

    // A file the subject may not use, a foreign or unknown grant and a missing file answer alike: no such file.
    private static async Task DeliverAsync(HttpContext http, Func<Task<FileReadResult>> read)
    {
        FileReadResult result;
        try { result = await read(); }
        catch (FileStorageException exception) when (exception.Code is FileStorageError.NotFound or FileStorageError.GrantRefused)
        { throw new ApiException(ApiCodes.NotFound, 404); }
        await using (result)
        {
            foreach (var header in result.Delivery.Headers()) http.Response.Headers[header.Key] = header.Value;
            http.Response.ContentLength = result.Version.Length;
            await result.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
    }

    private static object View(Item item) => new ItemView(item.TenantId, item.Id, item.Code);

    private static TenantContext Context(HttpContext http) => http.RequestServices.GetRequiredService<TenantExecution>().Context!;

    private static (TenantContext Context, ExecutionPrincipal? Principal) Principal(HttpContext http) =>
        (Context(http), http.RequestServices.GetRequiredService<IExecutionPrincipalAccessor>().Current);
}

/// <summary>
/// A real Kestrel server on loopback over TLS, composed as a host composes these packages under the API conventions:
/// <c>UseModularynApi</c>, SEC-02's ingress with its declared proxies, identity's session authentication and endpoints,
/// SEC-04's tenancy, the evaluator, the bootstrap configuration and the items module's declared endpoints over PostgreSQL
/// and the filesystem provider.
/// </summary>
internal sealed class AdversarialServer : IAsyncDisposable
{
    private static readonly Lazy<X509Certificate2> Certificate = new(CreateCertificate);
    private readonly WebApplication application;

    private AdversarialServer(WebApplication application, Uri address)
    {
        this.application = application;
        Address = address;
        Client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Pinned }
        }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    internal Uri Address { get; }
    internal HttpClient Client { get; }
    internal IServiceProvider Services => application.Services;

    /// <summary>Starts the server; <paramref name="proxies"/> are the exact addresses whose forwarded headers ingress honors.</summary>
    internal static async Task<AdversarialServer> StartAsync(AdversarialDatabase database, IReadOnlyList<string>? proxies = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(AdversarialHost.Configuration(database, proxies));
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Certificate.Value)));
        AdversarialHost.Compose(builder.Services, builder.Configuration, database);
        builder.Services.AddAuthorization();
        builder.Services.AddModularynApi();
        var application = builder.Build();
        try
        {
            application.UseModularynApi();
            application.UseModularynIngress();
            application.UseModularynTenancy();
            application.UseAuthorization();
            application.MapModularynIdentityEndpoints();
            application.MapModularynBootstrap();
            AdversarialEndpoints.Map(application, database);
            await application.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
        var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Select(item => new Uri(item)).Single();
        return new(application, address);
    }

    internal T Get<T>() where T : notnull => application.Services.GetRequiredService<T>();

    internal AdversarialClient NewClient(string host = AdversarialHost.HostA) => new(this) { Host = host };

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
        var request = new CertificateRequest("CN=" + AdversarialHost.AppHost, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        foreach (var host in new[] { AdversarialHost.AppHost, AdversarialHost.HostA, AdversarialHost.HostB }) names.AddDnsName(host);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }
}

/// <summary>A response as a test reads it: status, the problem or JSON body, the raw text and the headers.</summary>
internal sealed record Reply(HttpStatusCode Status, string? Code, JsonElement? Body, string Text, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);

    /// <summary>The status and the body without its correlation, which differs per request by design.</summary>
    public string Contract => (int)Status + " " + (Body is { ValueKind: JsonValueKind.Object } body
        ? string.Join(";", body.EnumerateObject().Where(property => property.Name != "correlationId").Select(property => property.Name + "=" + property.Value))
        : Text);
}

/// <summary>
/// A browser-like client of the server: it keeps its cookies, sends the antiforgery token with every state change and the
/// Host it is pointed at, and lets a test add any header, such as a forged tenant candidate or forwarded value.
/// </summary>
internal sealed class AdversarialClient(AdversarialServer server)
{
    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);
    public string Host { get; set; } = AdversarialHost.HostA;
    public string? Token { get; set; }

    public async Task<Reply> SignInAsync(Account account, string? returnUrl = null, Action<HttpRequestMessage>? configure = null)
    {
        await RefreshTokenAsync();
        var reply = await SendAsync(HttpMethod.Post, IdentityEndpoints.SignInPath, returnUrl is null
            ? new { userName = account.Name, password = account.Password } : new { userName = account.Name, password = account.Password, returnUrl }, configure);
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        // Antiforgery tokens are bound to the signed-in user, so a browser fetches a new one after signing in.
        await RefreshTokenAsync();
        return reply;
    }

    public async Task RefreshTokenAsync()
    {
        var reply = await SendAsync(HttpMethod.Get, IdentityEndpoints.AntiforgeryPath);
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Token = reply.Body!.Value.GetProperty("requestToken").GetString();
    }

    public async Task<Reply> SendAsync(HttpMethod method, string path, object? body = null, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(server.Address, path))
        {
            Content = body switch
            {
                null => method == HttpMethod.Get || method == HttpMethod.Head ? null : new StringContent(""),
                HttpContent content => content,
                string text => new StringContent(text, Encoding.UTF8, "application/json"),
                _ => JsonContent.Create(body)
            }
        };
        request.Headers.Host = Host;
        if (Cookies.Count > 0) request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(cookie => cookie.Key + "=" + cookie.Value)));
        if (Token is not null && method != HttpMethod.Get && method != HttpMethod.Head) request.Headers.Add("RequestVerificationToken", Token);
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
        JsonElement? json = null;
        string? code = null;
        if (raw.Length > 0 && response.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json")
        {
            using var document = JsonDocument.Parse(raw);
            json = document.RootElement.Clone();
            if (json.Value.ValueKind == JsonValueKind.Object && json.Value.TryGetProperty("code", out var named)) code = named.GetString();
        }
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        return new(response.StatusCode, code, json, raw, headers);
    }
}

/// <summary>
/// Every command EF Core executes against one schema, with its parameters, from the provider's own diagnostics: the SQL a
/// query, a direct-SQL read or a bulk update actually sent, not the text a test composed. Filtering by the schema keeps other
/// tests' commands out.
/// </summary>
internal sealed class ExecutedSql : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string schema;
    private readonly IDisposable all;
    private readonly List<IDisposable> subscriptions = [];

    internal ExecutedSql(string schema)
    {
        this.schema = schema;
        all = DiagnosticListener.AllListeners.Subscribe(this);
    }

    internal ConcurrentQueue<(string Text, string[] Parameters)> Commands { get; } = new();

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name != DbLoggerCategory.Name) return;
        lock (subscriptions) subscriptions.Add(value.Subscribe(this));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != RelationalEventId.CommandExecuting.Name || value.Value is not CommandEventData { Command: var command }
            || !command.CommandText.Contains(schema, StringComparison.Ordinal)) return;
        Commands.Enqueue((command.CommandText, command.Parameters.Cast<DbParameter>()
            .Select(parameter => parameter.ParameterName + "=" + Convert.ToString(parameter.Value, CultureInfo.InvariantCulture)).ToArray()));
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void Dispose()
    {
        all.Dispose();
        lock (subscriptions) foreach (var subscription in subscriptions) subscription.Dispose();
    }
}
