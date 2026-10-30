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
