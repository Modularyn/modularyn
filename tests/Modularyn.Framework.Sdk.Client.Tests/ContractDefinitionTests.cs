using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Api;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Settings.Abstractions;
using Xunit;

namespace Modularyn.Framework.Sdk.Client.Tests;

public sealed class ContractDefinitionTests
{
    [Fact]
    public async Task TM_G02_011_The_inventory_refuses_missing_or_mismatched_authorization_declarations()
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        app.MapGet("/undeclared", () => "value");
        Assert.Throws<InvalidOperationException>(() => ApiEndpoints.Describe(Source(app)));

        await using var mismatched = WebApplication.CreateSlimBuilder().Build();
        mismatched.MapGet("/declared", () => "value").WithApiContract(new ApiEndpoint("Declared", "GET", "/declared", "Anonymous", null,
            false, null, null, Versioned: false));
        Assert.Throws<InvalidOperationException>(() => ApiEndpoints.Describe(Source(mismatched)));

        await using var explicitAnonymous = WebApplication.CreateSlimBuilder().Build();
        explicitAnonymous.MapGet("/declared", () => "value").AllowAnonymous().WithApiContract(new ApiEndpoint("Declared", "GET", "/declared", "Anonymous", null,
            false, null, null, Versioned: false));
        var declared = Assert.Single(ApiEndpoints.Describe(Source(explicitAnonymous)).Endpoints);
        Assert.Equal("Anonymous", declared.Authorization);
        Assert.False(declared.ClientVisible);
    }

    [Fact]
    public async Task TM_G02_011_A_declared_endpoint_refuses_even_without_the_inventory_startup_service()
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();
        var invoked = false;
        app.MapGet("/declared", () => { invoked = true; return "value"; })
            .WithApiContract(new ApiEndpoint("Declared", "GET", "/declared", "Anonymous", null, false, null, null, Versioned: false));
        var endpoint = Assert.Single(Source(app).Endpoints);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);
        await endpoint.RequestDelegate!(context);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.False(invoked);
    }

    [Fact]
    public void TM_G02_011_A_client_visible_endpoint_is_versioned()
    {
        var model = new ApiModel("ItemView", [new("Id", "uuid")]);
        static ApiInventory Inventory(ApiModel model, ApiEndpoint endpoint) => new(1, 1, [model], [endpoint]);
        Inventory(model, new ApiEndpoint("GetItem", "GET", "/api/v{version}/items/{id}", "Anonymous", null, true, null, "ItemView")).Validate();
        // Generated client methods pin a version, so an unversioned route is never client-visible; as a non-client route it is.
        Assert.Throws<FormatException>(() => Inventory(model, new ApiEndpoint("GetItem", "GET", "/api/items/{id}", "Anonymous", null, true, null, "ItemView",
            Versioned: false)).Validate());
        Inventory(model, new ApiEndpoint("GetItem", "GET", "/api/items/{id}", "Anonymous", null, false, null, "ItemView", Versioned: false)).Validate();
    }

    [Fact]
    public void TM_G02_011_HTML_is_declared_only_for_a_page_and_a_page_s_form_declares_antiforgery()
    {
        Valid(Page());
        Valid(Page("POST"));
        Valid(Page("POST") with { RequestSchema = FormSchema });
        // HTML is a page's, and a page answers HTML.
        Invalid(Page() with { Page = false });
        Invalid(Page() with { ResponseMediaType = ApiMediaTypes.Json });
        Invalid(Page() with { ResponseMediaType = ApiMediaTypes.PlainText });
        // A page that accepts a form declares antiforgery, and only a page's form does.
        Invalid(Page("POST") with { Antiforgery = false });
        Invalid(Page() with { Antiforgery = true });
        Invalid(new ApiEndpoint("Mutate", "POST", "/items", "Anonymous", null, false, null, null, Mutation: true, Versioned: false, Antiforgery: true));
        // A page is a browser surface: GET, or POST for its form, reached with a session or no credential, with no JSON models.
        Invalid(Page("PUT"));
        Invalid(Page("POST") with { Idempotent = true });
        Invalid(Page() with { Authentication = "explicitCredential" });
        Invalid(Page() with { Response = "ItemView" });
        Invalid(Page() with { ResponseSchema = FormSchema });
        Invalid(Page() with { RequestSchema = FormSchema });
        Invalid(Page("POST") with { Request = "ItemView" });
    }

    [Fact]
    public void TM_G02_011_Binary_is_declared_only_for_a_download_with_explicit_content_types_and_a_disposition()
    {
        Valid(Download("application/pdf", "image/png", "text/plain"));
        Valid(Download("image/png") with { Download = new("inline", ["image/png"]) });
        Valid(Download(ApiMediaTypes.Binary));
        // Binary is a download's, and a download answers binary.
        Invalid(Download("application/pdf") with { Download = null });
        Invalid(Download("application/pdf") with { ResponseMediaType = ApiMediaTypes.Json });
        Invalid(Download("application/pdf") with { ResponseMediaType = "application/pdf" });
        // Each content type is explicit: concrete, lowercase and without parameters, and neither HTML nor JSON.
        foreach (var type in new[]
        {
            "image/*", "*/*", "application/PDF", "text/plain; charset=utf-8", "pdf", "image/png\n", "", "text/html", "application/xhtml+xml",
            "application/json", "application/problem+json", "application/ld+json", null!
        })
            Invalid(Download(type));
        Invalid(Download());
        Invalid(Download("image/png", "image/png"));
        Invalid(Download("image/png") with { Download = new("preview", ["image/png"]) });
        Invalid(Download("image/png") with { Download = new("attachment", null!) });
        // A download sends bytes, not a model, and it is not a page.
        Invalid(Download("image/png") with { Response = "ItemView" });
        Invalid(Download("image/png") with { ResponseSchema = FormSchema });
        Invalid(Download("image/png") with { Page = true });
    }

    [Fact]
    public void TM_G02_011_A_client_visible_endpoint_answers_JSON()
    {
        // Generated client methods read JSON, so no text, page or download response is ever a client's.
        var client = new ApiEndpoint("GetItem", "GET", "/api/v{version}/items/{id}", "Anonymous", null, true, null, "ItemView");
        Valid(client);
        Invalid(client with { ResponseMediaType = ApiMediaTypes.PlainText });
        Invalid(client with { ResponseMediaType = "application/xml" });
        Invalid(client with { ResponseMediaType = ApiMediaTypes.Html, Page = true, Authentication = "session" });
        Invalid(client with { ResponseMediaType = ApiMediaTypes.Binary, Download = new("attachment", ["image/png"]) });
        // Health's plain text stays an inventoried response that no client calls.
        Valid(FrameworkApi.Endpoint("GetLiveness"));
    }

    [Fact]
    public async Task TM_G02_007_A_page_that_accepts_a_form_never_maps_without_its_antiforgery_validation()
    {
        var form = Page("POST") with { Authorization = "Anonymous" };
        await using var app = WebApplication.CreateSlimBuilder().Build();
        app.MapPost(form.Route, () => Results.Content("<p>Saved</p>", ApiMediaTypes.Html)).AllowAnonymous().WithApiContract(form);
        var declared = Assert.Single(ApiEndpoints.Describe(Source(app)).Endpoints);
        Assert.True(declared.Page && declared.Antiforgery);

        // The ingress validates the token unless the endpoint opts out, so an endpoint that opts out, before or after its
        // declaration, is not the declared page: the inventory check refuses the host, and the endpoint refuses each request.
        foreach (var before in new[] { true, false })
        {
            await using var optedOut = WebApplication.CreateSlimBuilder().Build();
            var invoked = false;
            var mapped = optedOut.MapPost(form.Route, () => { invoked = true; return Results.Content("<p>Saved</p>", ApiMediaTypes.Html); })
                .AllowAnonymous();
            if (before) mapped.DisableAntiforgery().WithApiContract(form);
            else mapped.WithApiContract(form).DisableAntiforgery();
            var refused = Assert.Throws<InvalidOperationException>(() => ApiEndpoints.Describe(Source(optedOut)));
            Assert.Contains("antiforgery", refused.Message, StringComparison.Ordinal);

            var endpoint = Assert.Single(Source(optedOut).Endpoints);
            var context = new DefaultHttpContext { RequestServices = optedOut.Services };
            context.Response.Body = new MemoryStream();
            context.SetEndpoint(endpoint);
            await endpoint.RequestDelegate!(context);
            Assert.Equal(500, context.Response.StatusCode);
            Assert.False(invoked);
        }
    }

    [Fact]
    public void TM_G02_014_Only_explicit_nonsecret_definitions_can_be_client_visible()
    {
        var version = SemanticVersion.Parse("1.0.0");
        Assert.False(new SettingDefinition("Test.Values", "hidden", version, ValueKind.Text, "value", TimeSpan.FromMinutes(1)).ClientVisible);
        Assert.True(new SettingDefinition("Test.Values", "visible", version, ValueKind.Text, "value", TimeSpan.FromMinutes(1), clientVisible: true).ClientVisible);
        Assert.Throws<ValueValidationException>(() => new SettingDefinition("Test.Values", "secret", version, ValueKind.SecretReference, null,
            TimeSpan.FromMinutes(1), clientVisible: true));
    }

    private static EndpointDataSource Source(WebApplication app) => new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources);

    private static readonly JsonElement FormSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}
        """).RootElement;

    private static ApiEndpoint Page(string method = "GET") => new(method[0] + method[1..].ToLowerInvariant() + "NoteForm", method,
        "/t/{tenant}/notes/new", "MemberOnly", null, false, null, null, Mutation: method != "GET", Versioned: false,
        ResponseMediaType: ApiMediaTypes.Html, Authentication: "session", Page: true, Antiforgery: method == "POST");

    private static ApiEndpoint Download(params string[] types) => new("DownloadAttachment", "GET", "/t/{tenant}/attachments/{attachment}/content",
        "MemberOnly", null, false, null, null, Versioned: false, ResponseMediaType: ApiMediaTypes.Binary, Authentication: "session",
        Download: new("attachment", types));

    private static ApiInventory InventoryOf(ApiEndpoint endpoint) => new(1, 1, [new ApiModel("ItemView", [new("Id", "uuid")])], [endpoint]);
    private static void Valid(ApiEndpoint endpoint) => InventoryOf(endpoint).Validate();
    private static void Invalid(ApiEndpoint endpoint) => Assert.Throws<FormatException>(() => InventoryOf(endpoint).Validate());
}
