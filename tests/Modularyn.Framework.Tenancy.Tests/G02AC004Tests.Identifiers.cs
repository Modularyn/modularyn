using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Files.Persistence.FileSystem;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// AC-004-N02, SEC-08's: user-a holds every item and file permission in tenant-A and knows the identifiers of tenant-B's
/// item-b1, file-b1 and a download grant on file-b1. Reads, updates, deletes, references, filters, sorts, counts, exports,
/// the direct-SQL report, a bulk update, a batch import and three ways of reaching file-b1 each answer as for an identifier
/// that does not exist, carry the tenant predicate in the SQL they execute, and leave tenant-B's rows and files unchanged.
/// </summary>
public sealed partial class G02AC004Tests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_N02_ForeignIdentifiersCannotReachDataImportsOrFiles_Isolation()
    {
        await using var database = await AdversarialDatabase.CreateAsync();
        await using var host = AdversarialHost.Create(database);
        var seed = await AdversarialTenants.SeedAsync(host);
        await using (var b = await host.EnterAsync(seed.UserB, seed.TenantB)) await NoteAsync(database, b.Context, seed.ItemB1.Id, "tenant-b note");
        await using var a = await host.EnterAsync(seed.UserA, seed.TenantA);
        await NoteAsync(database, a.Context, seed.ItemA1.Id, "tenant-a note");
        var before = await database.TenantInventoryAsync(seed.TenantB);
        using var executed = new ExecutedSql(database.Schemas["items"]);
        var unknown = Guid.NewGuid();

        // 1. Read, update and delete item-b1 by identifier, by a direct update and by a tracked write naming tenant-A: each
        //    finds nothing, exactly as for an identifier that does not exist. A tracked write naming tenant-B is refused outright.
        async Task<string> ByIdentifierAsync(Guid id)
        {
            var outcomes = new List<string>();
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation))
            {
                outcomes.Add("read:" + await ((ItemsContext)work.Context).Items.AsNoTracking().AnyAsync(row => row.Id == id, Cancellation));
                var update = await work.Sql.UpdateAsync<Item, Guid, string>(row => row.Id, id, seed.ItemB1.Version, row => row.Name, "renamed by user-a", Cancellation);
                outcomes.Add("update:" + update.Succeeded + ":" + (await work.CompleteAsync(Cancellation)).State);
            }
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation))
            {
                var delete = await work.Sql.UpdateAsync<Item, Guid, bool>(row => row.Id, id, seed.ItemB1.Version, row => row.IsDeleted, true, Cancellation);
                outcomes.Add("delete:" + delete.Succeeded + ":" + (await work.CompleteAsync(Cancellation)).State);
            }
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation))
            {
                var items = (ItemsContext)work.Context;
                items.Items.Remove(items.Items.Attach(new Item { TenantId = seed.TenantA, Id = id, Code = "x", Name = "x", Version = seed.ItemB1.Version }).Entity);
                outcomes.Add("tracked:" + (await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => work.FlushAsync(Cancellation))).GetType().Name);
            }
            await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation))
            {
                var items = (ItemsContext)work.Context;
                var row = items.Items.Attach(new Item { TenantId = seed.TenantB, Id = id, Code = "x", Name = "x", Version = seed.ItemB1.Version }).Entity;
                row.Name = "renamed by user-a";
                outcomes.Add("tenant-b:" + (await Assert.ThrowsAsync<PersistenceContractException>(() => work.FlushAsync(Cancellation))).Code);
            }
            return string.Join(" ", outcomes);
        }
        var foreign = await ByIdentifierAsync(seed.ItemB1.Id);
        Assert.Equal(await ByIdentifierAsync(unknown), foreign);
        Assert.Equal("read:False update:False:Failed delete:False:Failed tracked:DbUpdateConcurrencyException tenant-b:MPS036", foreign);
        Log($"item-b1 by identifier from tenant-A: {foreign}; the same for an unknown identifier.");

        // A tenant-A note whose reference names item-b1 is refused by the reference itself, which carries the tenant, exactly
        // as one naming no item; the refusal names tenant-A's key alone.
        async Task<string> ReferenceAsync(Guid item)
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation);
            work.Context.Add(new ItemNote { Id = Guid.NewGuid(), ItemId = item, Text = "references " + item });
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => work.FlushAsync(Cancellation));
            var provider = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.DoesNotContain(seed.TenantB.ToString("D"), provider.Message + provider.Detail, StringComparison.OrdinalIgnoreCase);
            return provider.SqlState + ":" + provider.ConstraintName;
        }
        Assert.Equal(await ReferenceAsync(unknown), await ReferenceAsync(seed.ItemB1.Id));
        Assert.Equal("23503:FK_ItemNotes_Items", await ReferenceAsync(seed.ItemB1.Id));

        // Filters, sorts, counts and exports by item-b1's code or identifier find nothing; unfiltered, they hold tenant-A's rows alone.
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
        {
            var items = (ItemsContext)work.Context;
            Assert.Empty(await items.Items.Where(row => row.Code == seed.ItemB1.Code).OrderBy(row => row.Name).Select(row => row.Id).ToArrayAsync(Cancellation));
            Assert.Equal(0, await items.Items.CountAsync(row => row.Id == seed.ItemB1.Id || row.Code == seed.ItemB1.Code, Cancellation));
            Assert.Empty(await work.Sql.Query<Item, string>(row => row.Code, seed.ItemB1.Code).ToArrayAsync(Cancellation));
            Assert.Empty(await work.Sql.Query<Item>().Where(row => row.Id == seed.ItemB1.Id).ToArrayAsync(Cancellation));
            Assert.Equal([seed.TenantA], await items.Items.Select(row => row.TenantId).Distinct().ToArrayAsync(Cancellation));
            Assert.Equal([seed.TenantA], await work.Sql.Query<Item>().Select(row => row.TenantId).Distinct().ToArrayAsync(Cancellation));
        }

        // 2. The direct-SQL report and a bulk update in tenant-A's context reach tenant-A's rows alone; a bulk update cannot
        //    move a row to tenant-B, and a concurrency-tracked entity takes no bulk update at all.
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation))
        {
            var items = (ItemsContext)work.Context;
            var report = await work.Sql.Query<Item>().GroupBy(row => row.TenantId).Select(group => new TenantCount(group.Key, group.Count())).ToArrayAsync(Cancellation);
            Assert.Equal([new TenantCount(seed.TenantA, 1)], report);
            Assert.Equal(1, await items.Notes.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Text, "bulk by user-a"), Cancellation));
            Assert.Equal("MPS036", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                items.Notes.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.TenantId, seed.TenantB), Cancellation))).Code);
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation))
            Assert.Equal("MPS010", (await Assert.ThrowsAsync<PersistenceContractException>(() =>
                ((ItemsContext)work.Context).Items.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "bulk"), Cancellation))).Code);

        // 3. A tenant-A batch whose rows name tenant-B as owner, item-b1 as target, or both, is refused whole.
        var batches = new Dictionary<string, string[]>
        {
            ["owner"] = ["imp-1,first,,", "imp-2,owned by tenant-b," + seed.TenantB.ToString("D") + ","],
            ["target"] = ["imp-3,targets item-b1,," + seed.ItemB1.Id.ToString("D")],
            ["both"] = ["imp-4,owned and targeted," + seed.TenantB.ToString("D") + "," + seed.ItemB1.Id.ToString("D")],
            ["unknown"] = ["imp-5,targets nothing,," + unknown.ToString("D")]
        };
        var imports = new Dictionary<string, string>();
        foreach (var (name, rows) in batches)
        {
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => AdversarialEndpoints.ImportAsync(database.Items, a.Context, rows, Cancellation));
            imports[name] = refused is PersistenceContractException contract ? contract.Code : ((PostgresException)refused.InnerException!).SqlState;
        }
        Assert.Equal(new Dictionary<string, string> { ["owner"] = "MPS036", ["target"] = "23503", ["both"] = "MPS036", ["unknown"] = "23503" }, imports);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("items", "Items") + " WHERE \"Code\" LIKE 'imp-%'"));

        // 4. file-b1 by identifier, with tenant-B's grant, and through the storage location its identity derives, is no file in
        //    tenant-A; an unknown identifier and grant answer the same. The gateway checks tenant, version and purpose.
        var files = host.Files;
        async Task<string> FileAsync(Guid file, Guid version, string token)
        {
            var read = await Assert.ThrowsAsync<FileStorageException>(() => files.ReadAsync(a.Context, a.Principal, file, version, cancellationToken: Cancellation));
            var redeemed = await Assert.ThrowsAsync<FileStorageException>(() => files.RedeemGrantAsync(a.Context, a.Principal, Token(token), FileGrantPurpose.Download,
                cancellationToken: Cancellation));
            var stored = await Assert.ThrowsAsync<FileStorageException>(() => new FileSystemBlobStore(database.Root, AdversarialHost.MaximumFileLength)
                .ReadAsync(a.Context, version, new BlobIntegrity(seed.FileB1.Length, seed.FileB1.Sha256), null, Cancellation));
            return read.Code + " " + redeemed.Code + " " + stored.Code;
        }
        var unknownGrant = FileGrantToken.Create(Guid.NewGuid()).Reveal();
        Assert.Equal(await FileAsync(Guid.NewGuid(), Guid.NewGuid(), unknownGrant), await FileAsync(seed.FileB1.FileId, seed.FileB1.VersionId, seed.GrantB1));
        Assert.Equal("NotFound GrantRefused NotFound", await FileAsync(seed.FileB1.FileId, seed.FileB1.VersionId, seed.GrantB1));
        Assert.Equal(AdversarialTenants.ContentA1, await ReadAllAsync(await files.ReadAsync(a.Context, a.Principal, seed.FileA1.FileId, seed.FileA1.VersionId,
            cancellationToken: Cancellation)));
        await using (var b = await host.EnterAsync(seed.UserB, seed.TenantB))
        {
            Assert.Equal(FileStorageError.GrantRefused, (await Assert.ThrowsAsync<FileStorageException>(() => files.RedeemGrantAsync(b.Context, b.Principal,
                Token(seed.GrantB1), FileGrantPurpose.Preview, cancellationToken: Cancellation))).Code);
            Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() => files.ReadAsync(b.Context, b.Principal, seed.FileB1.FileId,
                seed.FileA1.VersionId, cancellationToken: Cancellation))).Code);
            Assert.Equal(AdversarialTenants.ContentB1, await ReadAllAsync(await files.RedeemGrantAsync(b.Context, b.Principal, Token(seed.GrantB1),
                FileGrantPurpose.Download, cancellationToken: Cancellation)));
        }

        // Every command these attempts executed against the items schema carried tenant-A's predicate and never tenant-B's value.
        Assert.NotEmpty(executed.Commands);
        Assert.All(executed.Commands, command =>
        {
            Assert.Contains("\"TenantId\"", command.Text, StringComparison.Ordinal);
            Assert.Contains(command.Parameters, parameter => parameter.EndsWith("=" + seed.TenantA.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(command.Parameters, parameter => parameter.EndsWith("=" + seed.TenantB.ToString("D"), StringComparison.OrdinalIgnoreCase));
        });
        foreach (var command in executed.Commands.Where(command => command.Text.StartsWith("UPDATE", StringComparison.Ordinal)))
            Log($"Executed: {command.Text.ReplaceLineEndings(" ")} [{string.Join(", ", command.Parameters)}]");
        Assert.Equal(before, await database.TenantInventoryAsync(seed.TenantB));
        Log($"Tenant-B's inventory, {before.Count} rows and files, is unchanged.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_N02_ForeignIdentifiersCannotReachDataImportsOrFiles_Integration()
    {
        await using var database = await AdversarialDatabase.CreateAsync();
        AdversarialTenants seed;
        await using (var setup = AdversarialHost.Create(database))
        {
            seed = await AdversarialTenants.SeedAsync(setup);
            await using var b = await setup.EnterAsync(seed.UserB, seed.TenantB);
            await NoteAsync(database, b.Context, seed.ItemB1.Id, "tenant-b note");
        }
        await using var server = await AdversarialServer.StartAsync(database);
        var client = server.NewClient();
        await client.SignInAsync(seed.UserA);
        var beforeB = await database.TenantInventoryAsync(seed.TenantB);
        var beforeA = await database.TenantInventoryAsync(seed.TenantA);
        using var executed = new ExecutedSql(database.Schemas["items"]);

        // Every request of the case, with item-b1's, file-b1's and its grant's identifiers, end to end through the host.
        var attempts = await ForeignRequestsAsync(client, seed, foreign: true);
        Assert.All(attempts, attempt => Assert.True(attempt.Reply.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest
            || attempt.Reply.Status == HttpStatusCode.OK && attempt.Name is "list" or "count" or "export" or "sorted", attempt.Name + ": " + attempt.Reply.Contract));
        foreach (var attempt in attempts) Log($"{attempt.Name}: {attempt.Reply.Contract}");
        Assert.Equal(0, attempts.Single(attempt => attempt.Name == "count").Reply.Body!.Value.GetProperty("count").GetInt32());
        Assert.Empty(attempts.Single(attempt => attempt.Name == "export").Reply.Body!.Value.GetProperty("rows").EnumerateArray());

        // Unfiltered, the list and the export hold tenant-A's rows alone.
        var list = await client.SendAsync(HttpMethod.Get, Path(seed.TenantA, "/items"));
        Assert.All(list.Body!.Value.GetProperty("items").EnumerateArray(), item => Assert.Equal(seed.TenantA, item.GetProperty("tenant").GetGuid()));
        Assert.Equal([seed.ItemA1.Id + "," + seed.ItemA1.Code + "," + seed.ItemA1.Name],
            (await client.SendAsync(HttpMethod.Post, Path(seed.TenantA, "/items/exports"))).Body!.Value.GetProperty("rows").EnumerateArray().Select(row => row.GetString()));

        // The SQL the handlers executed, with its parameters, carries tenant-A's predicate on every command and never tenant-B's value.
        Assert.NotEmpty(executed.Commands);
        Assert.All(executed.Commands, command =>
        {
            Assert.Contains("\"TenantId\"", command.Text, StringComparison.Ordinal);
            Assert.Contains(command.Parameters, parameter => parameter.EndsWith("=" + seed.TenantA.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(command.Parameters, parameter => parameter.EndsWith("=" + seed.TenantB.ToString("D"), StringComparison.OrdinalIgnoreCase));
        });
        Log($"{executed.Commands.Count} commands executed, each with tenant-A's predicate; for example: "
            + executed.Commands.First(command => command.Text.Contains("WHERE", StringComparison.Ordinal)).Text.ReplaceLineEndings(" "));

        // The direct-SQL report and a bulk update, in tenant-A's context on the running host, reach tenant-A's rows alone.
        await using (var host = AdversarialHost.Create(database))
        await using (var a = await host.EnterAsync(seed.UserA, seed.TenantA))
        {
            await NoteAsync(database, a.Context, seed.ItemA1.Id, "tenant-a note");
            await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation);
            Assert.Equal([new TenantCount(seed.TenantA, 1)],
                await work.Sql.Query<Item>().GroupBy(row => row.TenantId).Select(group => new TenantCount(group.Key, group.Count())).ToArrayAsync(Cancellation));
            Assert.Equal(1, await ((ItemsContext)work.Context).Notes.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Text, "bulk by user-a"), Cancellation));
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        var update = executed.Commands.Last(command => command.Text.StartsWith("UPDATE", StringComparison.Ordinal));
        Assert.Contains("\"TenantId\" = @", update.Text, StringComparison.Ordinal);
        Log($"Bulk update executed: {update.Text.ReplaceLineEndings(" ")} [{string.Join(", ", update.Parameters)}]");

        // Tenant-B's rows and files are exactly as they were; tenant-A's changed only by its own note and its bulk update.
        Assert.Equal(beforeB, await database.TenantInventoryAsync(seed.TenantB));
        Assert.Equal(beforeA.Count + 1, (await database.TenantInventoryAsync(seed.TenantA)).Count);
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("items", "Items") + " WHERE \"Code\" LIKE 'imp-%'"));
        Log($"Tenant-B's inventory before and after: {beforeB.Count} rows and files, unchanged.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_N02_ForeignIdentifiersCannotReachDataImportsOrFiles_Contract()
    {
        await using var database = await AdversarialDatabase.CreateAsync();
        AdversarialTenants seed;
        await using (var setup = AdversarialHost.Create(database)) seed = await AdversarialTenants.SeedAsync(setup);
        await using var server = await AdversarialServer.StartAsync(database);
        var client = server.NewClient();
        await client.SignInAsync(seed.UserA);

        // Each request with tenant-B's identifiers answers exactly as the same request with identifiers that exist nowhere.
        var foreign = await ForeignRequestsAsync(client, seed, foreign: true);
        var unknown = await ForeignRequestsAsync(client, seed, foreign: false);
        Assert.Equal(unknown.Select(attempt => attempt.Name + " " + attempt.Reply.Contract), foreign.Select(attempt => attempt.Name + " " + attempt.Reply.Contract));
        var expected = new Dictionary<string, string>
        {
            ["read"] = ApiCodes.NotFound, ["update"] = ApiCodes.NotFound, ["delete"] = ApiCodes.NotFound, ["link"] = ApiCodes.NotFound,
            ["reference"] = ApiCodes.NotFound, ["import-owner"] = ApiCodes.Validation, ["import-target"] = ApiCodes.Validation,
            ["download"] = ApiCodes.NotFound, ["grant"] = ApiCodes.NotFound
        };
        foreach (var attempt in foreign)
        {
            if (expected.TryGetValue(attempt.Name, out var code))
            {
                Assert.Equal(code, attempt.Reply.Code);
                Assert.Equal("application/problem+json", attempt.Reply.Header("Content-Type")?.Split(';')[0]);
                Assert.DoesNotContain(seed.TenantB.ToString("D"), attempt.Reply.Text, StringComparison.OrdinalIgnoreCase);
            }
            else Assert.Equal(HttpStatusCode.OK, attempt.Reply.Status);
            Assert.Equal("nosniff", attempt.Reply.Header("X-Content-Type-Options"));
            Log($"{attempt.Name}: {attempt.Reply.Contract}");
        }
        Assert.Equal("{\"items\":[],\"total\":0}", foreign.Single(attempt => attempt.Name == "list").Reply.Text);

        // The same requests reach tenant-A's own records, so the refusals are tenant-B's, not the endpoints'.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(HttpMethod.Get, Path(seed.TenantA, "/items/" + seed.ItemA1.Id))).Status);
        var download = await client.SendAsync(HttpMethod.Get, Path(seed.TenantA, "/files/" + seed.FileA1.FileId + "/revisions/" + seed.FileA1.VersionId));
        Assert.Equal((HttpStatusCode.OK, "text/plain"), (download.Status, download.Header("Content-Type")?.Split(';')[0]));
        Assert.Equal(System.Text.Encoding.UTF8.GetString(AdversarialTenants.ContentA1), download.Text);
        Assert.StartsWith("attachment", download.Header("Content-Disposition"), StringComparison.Ordinal);
        var granted = await client.SendAsync(HttpMethod.Get, Path(seed.TenantA, "/downloads"),
            configure: request => request.Headers.Add(AdversarialEndpoints.GrantHeader, seed.GrantA1));
        Assert.Equal(HttpStatusCode.OK, granted.Status);
        // user-b, in tenant-B, reaches file-b1 with the same grant, which a tenant-A request could not use.
        var owner = server.NewClient(AdversarialHost.HostB);
        await owner.SignInAsync(seed.UserB);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(AdversarialTenants.ContentB1), (await owner.SendAsync(HttpMethod.Get, Path(seed.TenantB, "/downloads"),
            configure: request => request.Headers.Add(AdversarialEndpoints.GrantHeader, seed.GrantB1))).Text);
    }

    /// <summary>
    /// The case's requests as user-a on tenant-A's domain, with tenant-B's identifiers or, for comparison, identifiers that
    /// exist nowhere.
    /// </summary>
    private static async Task<List<(string Name, Reply Reply)>> ForeignRequestsAsync(AdversarialClient client, AdversarialTenants seed, bool foreign)
    {
        var item = foreign ? seed.ItemB1.Id : Guid.NewGuid();
        var code = foreign ? seed.ItemB1.Code : "no-such-code";
        var (file, version) = foreign ? (seed.FileB1.FileId, seed.FileB1.VersionId) : (Guid.NewGuid(), Guid.NewGuid());
        var grant = foreign ? seed.GrantB1 : FileGrantToken.Create(Guid.NewGuid()).Reveal();
        var tenant = foreign ? seed.TenantB.ToString("D") : Guid.NewGuid().ToString("D");
        var a = seed.TenantA;
        static StringContent Csv(params string[] rows)
        {
            var content = new StringContent(string.Join("\n", rows));
            content.Headers.ContentType = new MediaTypeHeaderValue(AdversarialEndpoints.ImportType);
            return content;
        }
        return
        [
            ("read", await client.SendAsync(HttpMethod.Get, Path(a, "/items/" + item))),
            ("update", await client.SendAsync(HttpMethod.Put, Path(a, "/items/" + item), new { name = "renamed by user-a" })),
            ("delete", await client.SendAsync(HttpMethod.Delete, Path(a, "/items/" + item))),
            ("link", await client.SendAsync(HttpMethod.Get, Path(a, "/items/" + item + "/link"))),
            ("reference", await client.SendAsync(HttpMethod.Post, Path(a, "/notes"), new { item, text = "references another tenant's item" })),
            ("list", await client.SendAsync(HttpMethod.Get, Path(a, "/items?code=" + code))),
            ("sorted", await client.SendAsync(HttpMethod.Get, Path(a, "/items?sort=name&id=" + item))),
            ("count", await client.SendAsync(HttpMethod.Get, Path(a, "/items/count?code=" + code))),
            ("export", await client.SendAsync(HttpMethod.Post, Path(a, "/items/exports?code=" + code))),
            ("import-owner", await client.SendAsync(HttpMethod.Post, Path(a, "/imports"), Csv("imp-1,first,,", "imp-2,owned elsewhere," + tenant + ","))),
            ("import-target", await client.SendAsync(HttpMethod.Post, Path(a, "/imports"), Csv("imp-3,targets elsewhere,," + item))),
            ("download", await client.SendAsync(HttpMethod.Get, Path(a, "/files/" + file + "/revisions/" + version))),
            ("grant", await client.SendAsync(HttpMethod.Get, Path(a, "/downloads"), configure: request => request.Headers.Add(AdversarialEndpoints.GrantHeader, grant)))
        ];
    }

    private static string Path(Guid tenant, string rest) => "/t/" + TenantIdentifier.Format(tenant) + rest;

    private static FileGrantToken Token(string text) => FileGrantToken.TryParse(text, out var token) ? token : throw new FormatException("A grant token is required.");

    private static async Task<byte[]> ReadAllAsync(FileReadResult read)
    {
        await using (read)
        {
            using var output = new MemoryStream();
            await read.Content.CopyToAsync(output, Cancellation);
            return output.ToArray();
        }
    }

    private static async Task NoteAsync(AdversarialDatabase database, TenantContext context, Guid item, string text)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(database.Items, context, cancellationToken: Cancellation);
        work.Context.Add(new ItemNote { Id = Guid.NewGuid(), ItemId = item, Text = text });
        Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
    }
}
