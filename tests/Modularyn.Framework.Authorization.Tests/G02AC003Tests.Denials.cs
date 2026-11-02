using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// AC-003-N01: another owner's resource and hidden rows. In tenant A user-1 owns item-1, item-2 and item-4, user-2 owns item-3
/// and holds a read grant on item-4, and both hold item.read, item.update, item.delete and item.export, so every refusal
/// below is the resource decision's and not a missing permission's. The generated .NET and TypeScript clients are CODE-06's
/// AC-008 evidence: a production host accepts only the same-origin session cookie until G-03 issues explicit credentials, so
/// a client's call reaches exactly the endpoint wrapper and decision exercised here.
/// </summary>
public sealed partial class G02AC003Tests
{
    private const string Missing = "item-9";
    private static readonly string[] Hidden = ["item-1", Missing];

    private sealed record Owned(Guid Tenant, User Owner, User User1, User User2);

    /// <summary>The AC-003-N01 preconditions, committed through permission administration and the items module.</summary>
    private static async Task<Owned> OwnedItemsAsync(AuthorizationHost host)
    {
        var tenant = await host.TenantAsync("Tenant A");
        var owner = await host.UserAsync("owner-a", tenant);
        var user1 = await host.UserAsync("user-1", tenant);
        var user2 = await host.UserAsync("user-2", tenant);
        await host.OwnerAsync(owner, tenant);
        await using var unit = await host.EnterAsync(owner, tenant);
        var administration = host.Get<AuthorityAdministration>();
        foreach (var user in new[] { user1, user2 })
        foreach (var permission in new[] { Items.Read, Items.Update, Items.Delete, Items.Export })
            Assert.True(await administration.GrantPermissionAsync(unit.Context, unit.Principal, user.Actor, permission, Cancellation));
        await ItemWork.AddAsync(host.Database.ItemsModule, unit.Context, user1.Id.ToString("D"), "item-1", "item-2", "item-4");
        await ItemWork.AddAsync(host.Database.ItemsModule, unit.Context, user2.Id.ToString("D"), "item-3");
        var shared = await ItemWork.FindAsync(host.Database.ItemsModule, unit.Context, "item-4", Cancellation);
        Assert.True(await administration.GrantResourceAsync(unit.Context, unit.Principal, user2.Actor, Items.Reference(shared!), Items.Read, null, Cancellation));
        return new(tenant, owner, user1, user2);
    }

    private static async Task<(string Title, int Revision, Guid Version, string Owner)?> StoredAsync(AuthorizationDatabase database, Guid tenant, string code)
    {
        await using var connection = new NpgsqlConnection(database.Options.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT \"Title\", \"Revision\", \"Version\", \"Owner\" FROM " + database.Table(database.ItemsSchema, "Items")
            + " WHERE \"TenantId\" = @tenant AND \"Code\" = @code", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        return await reader.ReadAsync(Cancellation) ? (reader.GetString(0), reader.GetInt32(1), reader.GetGuid(2), reader.GetString(3)) : null;
    }

    // A problem body without its correlation, which differs per request by design.
    private static string Uncorrelated(Reply reply)
    {
        var body = reply.Body!.Value;
        return string.Join(";", body.EnumerateObject().Where(property => property.Name != "correlationId").Select(property => property.Name + "=" + property.Value));
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_N01_ResourceDenialsHideRowsAndProtectServerFields_Isolation()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var setup = AuthorizationHost.Create(database, clock);
        var owned = await OwnedItemsAsync(setup);
        await using var server = await DeclaredServer.StartAsync(database, clock);
        var user2 = server.NewClient();
        await user2.SignInAsync(owned.User2);
        string Path(string rest) => "/t/" + TenantIdentifier.Format(owned.Tenant) + rest;
        var before = await StoredAsync(database, owned.Tenant, "item-1");

        // 1. item-1, another owner's, read, updated and deleted by identifier: refused after resolution exactly as item-9, which
        //    does not exist, and nothing changes.
        foreach (var (method, body) in new[] { (HttpMethod.Get, (object?)null), (HttpMethod.Put, new { title = "Taken" }), (HttpMethod.Delete, null) })
        {
            var replies = new List<Reply>();
            foreach (var code in Hidden) replies.Add(await user2.SendAsync(method, Path("/items/" + code), body));
            Assert.All(replies, reply => Assert.Equal((HttpStatusCode.NotFound, ApiCodes.NotFound), (reply.Status, reply.Code)));
            Assert.Equal(Uncorrelated(replies[1]), Uncorrelated(replies[0]));
            output.WriteLine($"{method} item-1 and {Missing}: {replies[0].Status} {replies[0].Code} alike.");
        }
        Assert.Equal(before, await StoredAsync(database, owned.Tenant, "item-1"));

        // item-4 is shared with user-2 for reading only: the grant reaches that operation and no other.
        Assert.Equal(HttpStatusCode.OK, (await user2.SendAsync(HttpMethod.Get, Path("/items/item-4"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await user2.SendAsync(HttpMethod.Put, Path("/items/item-4"), new { title = "Shared" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await user2.SendAsync(HttpMethod.Delete, Path("/items/item-4"))).Status);
        Assert.Equal("Title of item-4", (await StoredAsync(database, owned.Tenant, "item-4"))!.Value.Title);

        // 2. A filter that matches all four, its total, every page of it and an export: only item-3 and item-4, and the hidden
        //    rows count in no total and shift no page.
        var all = await user2.SendAsync(HttpMethod.Get, Path("/items?prefix=item-"));
        Assert.Equal(["item-3", "item-4"], all.Body!.Value.GetProperty("items").EnumerateArray().Select(code => code.GetString()));
        Assert.Equal(2, all.Body!.Value.GetProperty("total").GetInt32());
        for (var offset = 0; offset < 3; offset++)
        {
            var page = await user2.SendAsync(HttpMethod.Get, Path($"/items?prefix=item-&offset={offset}&limit=1"));
            Assert.Equal(offset < 2 ? [offset == 0 ? "item-3" : "item-4"] : Array.Empty<string>(),
                page.Body!.Value.GetProperty("items").EnumerateArray().Select(code => code.GetString()!).ToArray());
            Assert.Equal(2, page.Body!.Value.GetProperty("total").GetInt32());
        }
        var export = await user2.SendAsync(HttpMethod.Post, Path("/exports"));
        Assert.Equal("completed", export.Body!.Value.GetProperty("state").GetString());
        var content = await database.ScalarAsync<string>("SELECT \"Content\" FROM " + database.Table(database.ItemsSchema, "ItemExports") + " WHERE \"Id\" = @id",
            new NpgsqlParameter("id", Guid.Parse(export.Body!.Value.GetProperty("operation").GetString()!)));
        Assert.Equal("item-3\nitem-4", content);
        output.WriteLine($"List, every page and the export held only item-3 and item-4, with a total of 2; user-1's own list holds three.");

        // 3. item-3, user-2's own, updated with a body that also sets its owner, tenant, version, revision and audit fields:
        //    refused as the contract declares, with nothing stored; the same update with its one declared field succeeds, and
        //    the server derives every other field.
        var three = await StoredAsync(database, owned.Tenant, "item-3");
        var smuggled = await user2.SendAsync(HttpMethod.Put, Path("/items/item-3"), new
        {
            title = "Claimed", owner = owned.User1.Id.ToString("D"), tenantId = Guid.NewGuid(), version = Guid.NewGuid(), revision = 99,
            createdBy = owned.Owner.Id.ToString("D"), updatedAt = clock.Now
        });
        Assert.Equal((HttpStatusCode.BadRequest, ApiCodes.Validation), (smuggled.Status, smuggled.Code));
        Assert.Equal(three, await StoredAsync(database, owned.Tenant, "item-3"));
        var updated = await user2.SendAsync(HttpMethod.Put, Path("/items/item-3"), new { title = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        var after = (await StoredAsync(database, owned.Tenant, "item-3"))!.Value;
        Assert.Equal(("Renamed", three!.Value.Revision + 1, owned.User2.Id.ToString("D")), (after.Title, after.Revision, after.Owner));
        Assert.NotEqual(three.Value.Version, after.Version);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table(database.ItemsSchema, "Items")
            + " WHERE \"Code\" = 'item-3' AND \"TenantId\" = @tenant", new NpgsqlParameter("tenant", owned.Tenant)));
        output.WriteLine($"Smuggled fields were refused with {smuggled.Code} and nothing was stored; the title alone updated, revision {after.Revision}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_N01_ResourceDenialsHideRowsAndProtectServerFields_Contract()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var setup = AuthorizationHost.Create(database, clock);
        var owned = await OwnedItemsAsync(setup);
        await using var server = await DeclaredServer.StartAsync(database, clock);
        var user2 = server.NewClient();
        await user2.SignInAsync(owned.User2);
        string Path(string rest) => "/t/" + TenantIdentifier.Format(owned.Tenant) + rest;

        // Every item endpoint is declared with its permission, so none answers by omission and none relies on interception.
        var inventory = ApiEndpoints.Describe(server.Get<Microsoft.AspNetCore.Routing.EndpointDataSource>());
        Assert.All(DeclaredItems.Inventory.Endpoints.Where(endpoint => endpoint.Permission is not null),
            endpoint => Assert.Contains(inventory.Endpoints, declared => declared == endpoint));

        // The hidden and the missing item answer with one problem contract: status, media type, stable code, type, title, no
        // permission or existence detail, a correlation that matches its header, and no-store.
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            var replies = new List<Reply>();
            foreach (var code in Hidden) replies.Add(await user2.SendAsync(method, Path("/items/" + code), method == HttpMethod.Put ? new { title = "x" } : null));
            foreach (var reply in replies)
            {
                Assert.Equal((HttpStatusCode.NotFound, "application/problem+json"), (reply.Status, reply.MediaType));
                var body = reply.Body!.Value;
                Assert.Equal(("urn:modularyn:problem:" + ApiCodes.NotFound, ApiCodes.NotFound, 404),
                    (body.GetProperty("type").GetString(), body.GetProperty("code").GetString(), body.GetProperty("status").GetInt32()));
                Assert.Equal(reply.Header(FrameworkApi.CorrelationHeader), body.GetProperty("correlationId").GetString());
                Assert.Equal("no-store", reply.Header("Cache-Control"));
                Assert.Equal("nosniff", reply.Header("X-Content-Type-Options"));
                Assert.DoesNotContain(AuthorizationCodes.ResourceNotFound, reply.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("item-1", reply.Text, StringComparison.Ordinal);
            }
            Assert.Equal(Uncorrelated(replies[1]), Uncorrelated(replies[0]));
            Assert.Equal(replies[1].Text.Length - replies[1].Header(FrameworkApi.CorrelationHeader)!.Length,
                replies[0].Text.Length - replies[0].Header(FrameworkApi.CorrelationHeader)!.Length);
        }

        // Lists, totals and pages carry only the visible rows, in a JSON body the declaration allows.
        var listed = await user2.SendAsync(HttpMethod.Get, Path("/items?prefix=item-&limit=10"));
        Assert.Equal((HttpStatusCode.OK, "application/json"), (listed.Status, listed.MediaType));
        Assert.Equal("{\"items\":[\"item-3\",\"item-4\"],\"total\":2}", listed.Text);

        // The input contract lists the writable fields: each field outside it, alone or together, is refused with MAP001 before
        // anything is read, whether or not the item exists or is visible, and an unparseable or non-JSON body is refused too.
        foreach (var field in new[] { "owner", "tenantId", "version", "revision", "createdBy", "updatedAt" })
            foreach (var code in new[] { "item-3", "item-1", Missing })
            {
                var reply = await user2.SendAsync(HttpMethod.Put, Path("/items/" + code), "{\"title\":\"x\",\"" + field + "\":\"y\"}");
                Assert.Equal((HttpStatusCode.BadRequest, ApiCodes.Validation), (reply.Status, reply.Code));
                Assert.False(reply.Body!.Value.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null);
            }
        Assert.Equal(ApiCodes.Validation, (await user2.SendAsync(HttpMethod.Put, Path("/items/item-3"), "{\"title\":")).Code);
        var plain = await user2.SendAsync(HttpMethod.Put, Path("/items/item-3"), configure: request =>
            request.Content = new StringContent("{\"title\":\"x\"}", Encoding.UTF8, "text/plain"));
        Assert.Equal((HttpStatusCode.UnsupportedMediaType, ApiCodes.Validation), (plain.Status, plain.Code));
        Assert.Equal("Title of item-3", (await StoredAsync(database, owned.Tenant, "item-3"))!.Value.Title);
        output.WriteLine("Hidden and missing items shared one MAP004 contract; every field outside the input contract was refused with MAP001.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_N01_ResourceDenialsHideRowsAndProtectServerFields_Integration()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = AuthorizationHost.Create(database, clock);
        var owned = await OwnedItemsAsync(host);
        await using var unit = await host.EnterAsync(owned.User2, owned.Tenant);
        var evaluator = host.Get<IPermissionEvaluator>();

        // Per-call decisions, made on the item as its module resolved it in the tenant: item-1 is refused as missing for every
        // operation, item-3 is user-2's, and item-4's grant reaches reading only.
        var decisions = new List<string>();
        foreach (var code in new[] { "item-1", "item-2", "item-3", "item-4" })
        {
            var item = await ItemWork.FindAsync(database.ItemsModule, unit.Context, code, Cancellation);
            foreach (var permission in new[] { Items.Read, Items.Update, Items.Delete })
            {
                var decision = await evaluator.AuthorizeAsync(new AuthorizationRequest(unit.Context, unit.Principal, permission) { Resource = Items.Reference(item!) },
                    Cancellation);
                decisions.Add($"{code} {permission} {decision.Code ?? "permitted"}");
            }
        }
        output.WriteLine(string.Join("; ", decisions));
        Assert.Equal(
        [
            "item-1 item.read MAZ007", "item-1 item.update MAZ007", "item-1 item.delete MAZ007",
            "item-2 item.read MAZ007", "item-2 item.update MAZ007", "item-2 item.delete MAZ007",
            "item-3 item.read permitted", "item-3 item.update permitted", "item-3 item.delete permitted",
            "item-4 item.read permitted", "item-4 item.update MAZ007", "item-4 item.delete MAZ007"
        ], decisions);

        // The authoritative visible set, computed apart from the evaluator from the stored rows and grants: owned by user-2, or
        // granted to user-2 for reading.
        var member = owned.User2.Id.ToString("D");
        var authoritative = new List<string>();
        await using (var connection = new NpgsqlConnection(database.Options.ConnectionString))
        {
            await connection.OpenAsync(Cancellation);
            await using var command = new NpgsqlCommand("SELECT i.\"Code\" FROM " + database.Table(database.ItemsSchema, "Items") + " i WHERE i.\"TenantId\" = @tenant AND "
                + "(i.\"Owner\" = @member OR EXISTS (SELECT 1 FROM " + database.Table(database.AuthorizationSchema, "ResourceGrants") + " g WHERE g.\"TenantId\" = i.\"TenantId\" "
                + "AND g.\"ResourceType\" = 'item' AND g.\"ResourceId\" = i.\"Code\" AND g.\"Member\" = @member AND g.\"Permission\" = 'item.read')) ORDER BY 1", connection);
            command.Parameters.AddWithValue("tenant", owned.Tenant);
            command.Parameters.AddWithValue("member", member);
            await using var reader = await command.ExecuteReaderAsync(Cancellation);
            while (await reader.ReadAsync(Cancellation)) authoritative.Add(reader.GetString(0));
        }
        Assert.Equal(["item-3", "item-4"], authoritative);

        // List, count and export through the module's own filter equal it exactly; the filter is composed into the query with
        // its values bound as parameters, never written into the SQL text.
        var (codes, count) = await ItemWork.ListAsync(evaluator, database.ItemsModule, unit.Context, unit.Principal, Cancellation);
        Assert.Equal(authoritative, codes);
        Assert.Equal(authoritative.Count, count);
        var visibility = await evaluator.VisibleAsync(new AuthorizationRequest(unit.Context, unit.Principal, Items.Read), Items.ResourceType, Cancellation);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.ItemsModule, unit.Context, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
        {
            var sql = visibility.Apply(((ItemsContext)work.Context).Items.AsNoTracking(), row => row.Owner, row => row.Code).ToQueryString();
            output.WriteLine(sql);
            var statement = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];
            Assert.DoesNotContain(member, statement, StringComparison.Ordinal);
            Assert.DoesNotContain("'item-4'", statement, StringComparison.Ordinal);
            Assert.Contains("\"TenantId\"", statement, StringComparison.Ordinal);
        }
        var export = await ItemWork.ExportAsync(host.Services, database.ItemsModule, unit.Context, unit.Principal, null, Cancellation);
        Assert.Equal(string.Join("\n", authoritative), await database.ScalarAsync<string>("SELECT \"Content\" FROM " + database.Table(database.ItemsSchema, "ItemExports")
            + " WHERE \"Id\" = @id", new NpgsqlParameter("id", export.Id)));

        // Stored item-3: the strict binder refuses every field outside the update's input contract before anything is read, and
        // the stored owner, tenant, revision and version are those the server wrote.
        foreach (var field in new[] { "owner", "tenantId", "version", "revision", "createdBy" })
        {
            var http = new DefaultHttpContext();
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"title\":\"x\",\"" + field + "\":\"y\"}"));
            await Assert.ThrowsAsync<JsonException>(() => ApiEndpoints.ReadRequestAsync<DeclaredItems.TitleChange>(http));
        }
        var stored = await StoredAsync(database, owned.Tenant, "item-3");
        Assert.Equal(("Title of item-3", 0, member), (stored!.Value.Title, stored.Value.Revision, stored.Value.Owner));
        output.WriteLine($"Visible set {string.Join(",", authoritative)} matched the list, the count {count} and the export; item-3 kept its server fields.");
    }
}
