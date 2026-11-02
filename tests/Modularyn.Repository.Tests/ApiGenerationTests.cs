using System.Text.Json;
using Modularyn.Framework.Api.Contracts;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ApiGenerationTests
{
    private const string Contracts = "src/Framework/Modularyn.Framework.Api.Contracts/";
    private const string Client = "src/Framework/Modularyn.Framework.Sdk.Client/";

    [Fact]
    public void The_OpenAPI_document_and_both_static_clients_match_the_inventory_generator()
    {
        var files = RepositoryFiles.Current;
        var inventory = ApiInventory.Read(files[Contracts + "endpoint-inventory.json"]);
        Assert.Empty(ApiCompatibility.BreakingChanges(ApiInventory.Read(files[Contracts + "compatibility-baseline.json"]), inventory));
        Assert.Equal(ApiGenerator.OpenApi(inventory), files[Contracts + "openapi.json"]);
        Assert.Equal(ApiGenerator.DotNet(inventory, "Modularyn.Framework.Sdk.Client", "ModularynClient"), files[Client + "ModularynClient.g.cs"]);
        Assert.Equal(ApiGenerator.TypeScript(inventory, "ModularynClient"), files[Client + "typescript/client.g.ts"]);
        var fixture = ApiInventory.Read(files["tests/Modularyn.Framework.Sdk.Client.Tests/fixture-inventory.json"]);
        Assert.Equal(ApiGenerator.DotNet(fixture, "Modularyn.Framework.Sdk.Client.Tests", "FixtureClient"), files["tests/Modularyn.Framework.Sdk.Client.Tests/FixtureClient.g.cs"]);
        Assert.Equal(ApiGenerator.TypeScript(fixture, "FixtureClient", "../runtime.ts"), files[Client + "typescript/tests/fixture.g.ts"]);
    }

    [Fact]
    public void Generated_contracts_use_line_feeds_on_every_platform()
    {
        var inventory = ApiInventory.Read(RepositoryFiles.Current[Contracts + "endpoint-inventory.json"]);
        foreach (var generated in new[]
        {
            ApiGenerator.OpenApi(inventory), ApiGenerator.DotNet(inventory, "Modularyn.Framework.Sdk.Client", "ModularynClient"),
            ApiGenerator.TypeScript(inventory, "ModularynClient")
        })
            Assert.DoesNotContain('\r', generated);
    }

    [Fact]
    public void The_OpenAPI_document_states_which_credentials_production_hosts_accept()
    {
        // FND-027: until the authorization server issues tokens, production hosts accept the session cookie, not the explicit credential.
        using var document = JsonDocument.Parse(ApiGenerator.OpenApi(FrameworkApi.Inventory));
        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        var explicitCredential = schemes.GetProperty("explicitCredential").GetProperty("description").GetString()!;
        Assert.Contains("Production hosts do not accept one yet", explicitCredential, StringComparison.Ordinal);
        Assert.Contains("authorization server", explicitCredential, StringComparison.Ordinal);
        var session = schemes.GetProperty("session");
        Assert.Equal("__Host-Modularyn-Session", session.GetProperty("name").GetString());
        Assert.Contains("the credential production hosts accept today", session.GetProperty("description").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_supported_version_cannot_silently_change_a_binding_or_a_field()
    {
        var inventory = FrameworkApi.Inventory;
        Assert.Empty(ApiCompatibility.BreakingChanges(inventory, ApiInventory.Read(JsonSerializer.Serialize(inventory, ApiInventory.Json))));
        var endpoint = inventory.Endpoints[0];
        var changed = inventory with { Endpoints = inventory.Endpoints.Select(value => value == endpoint ? value with { Route = value.Route + "/changed" } : value).ToArray() };
        Assert.NotEmpty(ApiCompatibility.BreakingChanges(inventory, changed));
        var model = inventory.Models[0];
        changed = inventory with { Models = inventory.Models.Select(value => value == model ? value with { Fields = [new("Settings", "string")] } : value).ToArray() };
        Assert.NotEmpty(ApiCompatibility.BreakingChanges(inventory, changed));
        Assert.Throws<InvalidOperationException>(() => inventory.Negotiate(0, 0));
    }

    [Fact]
    public void Pages_downloads_and_uploads_are_described_with_their_declared_media_types_and_generate_no_client_methods()
    {
        // The inventory's own form of the declarations: a page, its form, a download, a binary upload and a form upload.
        var declarations = ApiInventory.Read("""
            {
              "oldestVersion": 1, "currentVersion": 1, "models": [],
              "endpoints": [
                { "name": "GetNoteForm", "method": "GET", "route": "/t/{tenant}/notes/new", "authorization": "MemberOnly", "permission": null,
                  "clientVisible": false, "request": null, "response": null, "versioned": false, "authentication": "session",
                  "responseMediaType": "text/html", "page": true },
                { "name": "PostNoteForm", "method": "POST", "route": "/t/{tenant}/notes/new", "authorization": "MemberOnly", "permission": null,
                  "clientVisible": false, "request": null, "response": null, "mutation": true, "versioned": false, "authentication": "session",
                  "responseMediaType": "text/html", "page": true, "antiforgery": true,
                  "requestSchema": { "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] } },
                { "name": "DownloadAttachment", "method": "GET", "route": "/t/{tenant}/attachments/{attachment}/content",
                  "authorization": "MemberOnly", "permission": null, "clientVisible": false, "request": null, "response": null,
                  "versioned": false, "authentication": "session", "responseMediaType": "application/octet-stream",
                  "download": { "disposition": "attachment", "mediaTypes": ["application/pdf", "image/png"] } },
                { "name": "UploadAttachment", "method": "POST", "route": "/t/{tenant}/attachments", "authorization": "MemberOnly",
                  "permission": null, "clientVisible": false, "request": null, "response": null, "mutation": true, "versioned": false,
                  "authentication": "session", "upload": { "mediaTypes": ["image/png", "application/pdf"] } },
                { "name": "UploadAttachmentForm", "method": "POST", "route": "/t/{tenant}/attachments/form", "authorization": "MemberOnly",
                  "permission": null, "clientVisible": false, "request": null, "response": null, "mutation": true, "versioned": false,
                  "authentication": "session", "antiforgery": true, "upload": { "mediaTypes": ["multipart/form-data"] } }
              ]
            }
            """);
        var inventory = FrameworkApi.Inventory with { Endpoints = [.. FrameworkApi.Inventory.Endpoints, .. declarations.Endpoints] };
        Assert.Empty(ApiCompatibility.BreakingChanges(FrameworkApi.Inventory, inventory));
        using var document = JsonDocument.Parse(ApiGenerator.OpenApi(inventory));
        var paths = document.RootElement.GetProperty("paths");
        static string[] Keys(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();

        var page = paths.GetProperty("/t/{tenant}/notes/new").GetProperty("get");
        Assert.True(page.GetProperty("x-modularyn-page").GetBoolean());
        Assert.False(page.GetProperty("x-modularyn-antiforgery").GetBoolean());
        Assert.Equal(new[] { "text/html" }, Keys(page.GetProperty("responses").GetProperty("200").GetProperty("content")));
        Assert.False(page.TryGetProperty("requestBody", out _));
        var form = paths.GetProperty("/t/{tenant}/notes/new").GetProperty("post");
        Assert.True(form.GetProperty("x-modularyn-antiforgery").GetBoolean());
        Assert.Equal(new[] { "application/x-www-form-urlencoded" }, Keys(form.GetProperty("requestBody").GetProperty("content")));
        var download = paths.GetProperty("/t/{tenant}/attachments/{attachment}/content").GetProperty("get");
        Assert.Equal("attachment", download.GetProperty("x-modularyn-download").GetString());
        Assert.False(download.TryGetProperty("x-modularyn-page", out _));
        var content = download.GetProperty("responses").GetProperty("200").GetProperty("content");
        Assert.Equal(new[] { "application/pdf", "image/png" }, Keys(content));
        Assert.All(content.EnumerateObject(), type => Assert.Empty(Keys(type.Value)));
        var upload = paths.GetProperty("/t/{tenant}/attachments").GetProperty("post");
        Assert.Equal("binary", upload.GetProperty("x-modularyn-upload").GetString());
        Assert.False(upload.TryGetProperty("x-modularyn-antiforgery", out _));
        Assert.Equal(new[] { "image/png", "application/pdf" }, Keys(upload.GetProperty("requestBody").GetProperty("content")));
        var formUpload = paths.GetProperty("/t/{tenant}/attachments/form").GetProperty("post");
        Assert.Equal("form", formUpload.GetProperty("x-modularyn-upload").GetString());
        Assert.True(formUpload.GetProperty("x-modularyn-antiforgery").GetBoolean());
        Assert.False(formUpload.TryGetProperty("x-modularyn-page", out _));
        Assert.Equal(new[] { "multipart/form-data" }, Keys(formUpload.GetProperty("requestBody").GetProperty("content")));

        // Every operation that declares neither keeps its description, and generated clients, which read JSON, gain no method.
        using var original = JsonDocument.Parse(ApiGenerator.OpenApi(FrameworkApi.Inventory));
        foreach (var path in original.RootElement.GetProperty("paths").EnumerateObject())
            Assert.Equal(path.Value.GetRawText(), paths.GetProperty(path.Name).GetRawText());
        Assert.Equal(ApiGenerator.DotNet(FrameworkApi.Inventory, "Modularyn.Framework.Sdk.Client", "ModularynClient"),
            ApiGenerator.DotNet(inventory, "Modularyn.Framework.Sdk.Client", "ModularynClient"));
        Assert.Equal(ApiGenerator.TypeScript(FrameworkApi.Inventory, "ModularynClient"), ApiGenerator.TypeScript(inventory, "ModularynClient"));
    }

    [Fact]
    public void The_TypeScript_package_uses_only_the_approved_candidates_at_exact_versions()
    {
        var files = RepositoryFiles.Current;
        using var package = JsonDocument.Parse(files[Client + "typescript/package.json"]);
        Assert.True(package.RootElement.GetProperty("private").GetBoolean());
        var dependencies = package.RootElement.GetProperty("devDependencies").EnumerateObject().ToDictionary(value => value.Name, value => value.Value.GetString());
        Assert.Equal(4, dependencies.Count);
        Assert.Equal("7.0.2", dependencies["typescript"]);
        Assert.Equal("5.0.4", dependencies["vitest"]);
        Assert.Equal("8.3.4", dependencies["vite"]);
        Assert.Equal("1.33.0", dependencies["lightningcss"]);
        Assert.False(package.RootElement.TryGetProperty("dependencies", out _));
        Assert.Empty(NamesAndLayoutContract.Validate(files));
    }
}
