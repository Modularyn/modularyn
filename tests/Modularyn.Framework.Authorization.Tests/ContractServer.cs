using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// A real Kestrel server on loopback over TLS for the endpoint contract: SEC-02's ingress, a header stand-in for
/// authentication, SEC-04's tenancy over an in-memory directory and the evaluator over in-memory authority. Its item
/// endpoints declare their permissions; one is protected only by an ASP.NET Core policy, as a fixture for the report.
/// </summary>
internal sealed class ContractServer : IAsyncDisposable
{
    internal const string LegacyRoute = "/t/{tenant}/legacy/items/{code}";
    private readonly WebApplication application;

    private ContractServer(WebApplication application, Uri address, EventLog events, MemoryDirectory directory, TestSeams seams, MemoryAuthority authority,
        TestFreshness freshness)
    {
        this.application = application;
        Address = address;
        Events = events;
        Directory = directory;
        Seams = seams;
        Authority = authority;
        Freshness = freshness;
        Client = Loopback.Client();
    }

    public Uri Address { get; }
    public EventLog Events { get; }
    public MemoryDirectory Directory { get; }
    public TestSeams Seams { get; }
    public MemoryAuthority Authority { get; }
    public TestFreshness Freshness { get; }
    public HttpClient Client { get; }
    public IServiceProvider Services => application.Services;

    /// <summary>
    /// Starts the server. With <paramref name="interception"/> false, the pipeline has no authorization middleware and the
    /// endpoint middleware does not insist on one, so only the endpoints' own declarations stand between a request and them.
    /// </summary>
    public static async Task<ContractServer> StartAsync(bool interception = true, bool evaluator = true)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(Loopback.Settings());
        builder.Logging.ClearProviders();
        var events = new EventLog();
        builder.Logging.AddProvider(events);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(Loopback.Certificate.Value)));
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        var authority = new MemoryAuthority();
        var freshness = new TestFreshness();
        var services = builder.Services;
        services.AddModularynDataProtection(builder.Configuration);
        services.AddModularynIngress(builder.Configuration);
        services.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
        services.AddSingleton<ITenantDirectory>(directory);
        services.AddScoped<TenantExecution>();
        services.AddSingleton<TenantWorkRunner>();
        services.AddSingleton<TenantCache>();
        seams.Register(services, authority: false);
        services.AddSingleton<IPrincipalFreshness>(freshness);
        services.AddScoped<ExecutionPrincipalScope>();
        services.AddScoped<IExecutionPrincipalAccessor>(scope => scope.GetRequiredService<ExecutionPrincipalScope>());
        if (evaluator)
        {
            var module = AuthorizationModule.Create(new PostgreSqlPersistenceOptions("Host=localhost;Database=unused"));
            services.AddModularynAuthorization(builder.Configuration, module, UnitAuthorization.Registry());
            services.Replace(ServiceDescriptor.Singleton<IAuthoritySource>(authority));
        }
        if (interception) services.AddAuthorization();
        else services.Configure<RouteOptions>(options => options.SuppressCheckForUnhandledSecurityMetadata = true);
        var application = builder.Build();
        try
        {
            // Routing is left to WebApplication, which places it first, so the ingress sees each endpoint's antiforgery declaration.
            application.UseModularynIngress();
            application.UseModularynTenancy();
            if (interception) application.UseAuthorization();
            Task<IResult> Updated(string code) => Task.FromResult(Results.Json(new { updated = code }));
            // Bearer-style clients send no cookies, so these endpoints disable antiforgery, as the ingress requires.
            application.MapPut("/t/{tenant}/items/{code}", Updated).RequireTenantMember().RequirePermission(Items.Update).DisableAntiforgery();
            application.MapPost("/t/{tenant}/integration/items/{code}", Updated).RequireTenantMember().RequirePermission(Items.Update).DisableAntiforgery();
            application.MapGet(LegacyRoute, (string code) => Results.Json(new { read = code })).RequireTenantMember()
                .RequireAuthorization(PermissionRequirement.Policy(Items.Read));
            application.MapGet("/status", () => Results.Ok()).AllowAnonymous();
            await application.StartAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
        return new(application, Loopback.Address(application), events, directory, seams, authority, freshness);
    }

    public async Task<Answer> CallAsync(HttpMethod method, string path, string? user)
    {
        using var request = new HttpRequestMessage(method, new Uri(Address, path)) { Content = method == HttpMethod.Get ? null : new StringContent("") };
        request.Headers.Host = Loopback.PublicHost;
        if (user is not null) request.Headers.Add(HeaderAuthenticationHandler.Header, user);
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (text.Length == 0) return new(response.StatusCode, null, null);
        using var document = System.Text.Json.JsonDocument.Parse(text);
        var root = document.RootElement.Clone();
        return new(response.StatusCode, root.TryGetProperty("code", out var code) ? code.GetString() : null, root);
    }

    public T Get<T>() where T : notnull => application.Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }
}
