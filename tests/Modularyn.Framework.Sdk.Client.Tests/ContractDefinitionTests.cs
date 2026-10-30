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
    public void TM_G02_014_Only_explicit_nonsecret_definitions_can_be_client_visible()
    {
        var version = SemanticVersion.Parse("1.0.0");
        Assert.False(new SettingDefinition("Test.Values", "hidden", version, ValueKind.Text, "value", TimeSpan.FromMinutes(1)).ClientVisible);
        Assert.True(new SettingDefinition("Test.Values", "visible", version, ValueKind.Text, "value", TimeSpan.FromMinutes(1), clientVisible: true).ClientVisible);
        Assert.Throws<ValueValidationException>(() => new SettingDefinition("Test.Values", "secret", version, ValueKind.SecretReference, null,
            TimeSpan.FromMinutes(1), clientVisible: true));
    }

    private static EndpointDataSource Source(WebApplication app) => new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources);
}
