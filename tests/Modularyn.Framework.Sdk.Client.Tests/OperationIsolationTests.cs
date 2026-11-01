using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Api;
using Modularyn.Framework.Persistence.EfCore;
using Xunit;

namespace Modularyn.Framework.Sdk.Client.Tests;

[Collection(AuthorizationPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class OperationIsolationTests
{
    [Fact]
    public async Task Receipts_never_cross_a_tenant_or_a_subject_and_changed_input_cannot_reuse_them()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = AuthorizationHost.Create(database, clock);
        var first = await host.TenantAsync("first");
        var second = await host.TenantAsync("second");
        var owner = await host.UserAsync("owner", first, second);
        var other = await host.UserAsync("other", first);
        await host.OwnerAsync(owner, first);
        await host.OwnerAsync(owner, second);
        await host.OwnerAsync(other, first);
        var ownerToken = (await host.SignInAsync(owner)).Session.Token.Reveal();
        var otherToken = (await host.SignInAsync(other)).Session.Token.Reveal();
        await using var server = await ClientServer.StartAsync(database, clock);
        using var ownerTransport = server.Transport(ownerToken);
        using var otherTransport = server.Transport(otherToken);
        var client = new FixtureClient(ownerTransport);
        var stranger = new FixtureClient(otherTransport);
        // Even this explicit-credential endpoint cannot exempt a request carrying any cookie from the ingress guard.
        using (var raw = new HttpClient(server.Handler()))
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(server.Origin.Value), "/api/v2/t/" + first.ToString("D") + "/items")))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
            request.Headers.Add("Cookie", "unrelated=present");
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            request.Content = JsonContent.Create(new CreateItemRequest("refused", null));
            using var response = await raw.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var operation = Guid.NewGuid();
        var item = await client.CreateItemAsync(first.ToString("D"), new("first", null), operation, token);
        Assert.Equal("notObserved", (await client.GetOperationAsync(second.ToString("D"), operation.ToString("D"), token)).State);
        Assert.Equal("notObserved", (await stranger.GetOperationAsync(first.ToString("D"), operation.ToString("D"), token)).State);
        await Assert.ThrowsAsync<MissingFailure>(() => stranger.CreateItemAsync(first.ToString("D"), new("first", null), operation, token));
        await Assert.ThrowsAsync<OperationConflictFailure>(() => client.CreateItemAsync(first.ToString("D"), new("changed", null), operation, token));
        Assert.Equal(item, await client.CreateItemAsync(first.ToString("D"), new("first", null), operation, token));
        var independent = await client.CreateItemAsync(second.ToString("D"), new("second", null), operation, token);
        Assert.NotEqual(item.Id, independent.Id);
        foreach (var tenant in new[] { first, second })
        {
            await using var scope = await host.EnterAsync(owner, tenant);
            await using var unit = await EfCoreUnitOfWork.BeginAsync(database.ItemsModule, scope.Context, cancellationToken: token);
            Assert.Equal(1, await unit.Context.Set<Item>().CountAsync(token));
            Assert.Equal(1, await unit.Context.Set<ApiOperation>().CountAsync(token));
        }
    }
}
