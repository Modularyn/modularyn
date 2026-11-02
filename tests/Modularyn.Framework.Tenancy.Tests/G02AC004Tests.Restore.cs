using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;
using Modularyn.Modules.Files.Persistence.FileSystem;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>
/// AC-004-R02, SEC-08's: a coordinated backup of both tenants' records, files, download grants and settings is restored into
/// an isolated recovery deployment of the same release. Restored rows, files and constraints keep their tenants; tenant-A's
/// identifier, list, export and download requests against tenant-B's restored records are refused as they were before the
/// backup; and the rebuilt caches carry tenant keys and serve nothing from before the restore.
/// </summary>
public sealed partial class G02AC004Tests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_R02_RestorePreservesTenantOwnershipAndRefusals_Isolation()
    {
        await using var source = await AdversarialDatabase.CreateAsync();
        AdversarialTenants seed;
        string before;
        await using (var host = AdversarialHost.Create(source))
        {
            seed = await BackedUpAsync(host);
            before = await ForeignInProcessAsync(host, seed);
        }
        var recovery = await source.RecoveryAsync();
        await source.RestoreIntoAsync(recovery);

        // Restored rows and files keep their tenants exactly: each tenant's inventory is the one the backup took.
        foreach (var tenant in new[] { seed.TenantA, seed.TenantB })
            Assert.Equal(await source.TenantInventoryAsync(tenant), await recovery.TenantInventoryAsync(tenant));
        Assert.Equal(await ConstraintsAsync(source), await ConstraintsAsync(recovery));
        Assert.All(await ConstraintsAsync(recovery), constraint => Assert.Contains("\"TenantId\"", constraint, StringComparison.Ordinal));

        // The same tenant-A attempts against tenant-B's restored records are refused as before the backup.
        await using var restored = AdversarialHost.Create(recovery);
        Assert.Equal(before, await ForeignInProcessAsync(restored, seed));
        Log($"Before the backup and after the restore: {before}.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_R02_RestorePreservesTenantOwnershipAndRefusals_Recovery()
    {
        await using var source = await AdversarialDatabase.CreateAsync();
        AdversarialTenants seed;
        string staleKey;
        await using (var host = AdversarialHost.Create(source))
        {
            seed = await BackedUpAsync(host);
            // An entry cached before the backup, holding a value the backup never had.
            await using var a = await host.EnterAsync(seed.UserA, seed.TenantA);
            var entry = new TenantCacheEntry(AdversarialItems.ModuleId, "1", "item-codes", TimeSpan.FromMinutes(5), principalScoped: true);
            staleKey = host.Get<TenantCache>().KeyFor(a.Context, entry);
            Assert.Equal("cached before the backup", await host.Get<TenantCache>().GetOrCreateAsync(a.Context, entry, _ => Task.FromResult("cached before the backup"),
                Cancellation));
        }

        // 1. The coordinated backup is restored into an isolated recovery deployment of the same release, migrated empty, with
        //    its own schemas and file root; the declared key ring is restored from its own backup.
        var recovery = await source.RecoveryAsync();
        Assert.Empty(await recovery.TenantInventoryAsync(seed.TenantA));
        await source.RestoreIntoAsync(recovery);
        Assert.DoesNotContain(recovery.Schemas.Values, schema => source.Schemas.Values.Contains(schema));
        Assert.NotEqual(source.Root, recovery.Root);
        Log($"Restored into schemas {string.Join(", ", recovery.Schemas.Values)}.");

        await using var restored = AdversarialHost.Create(recovery);
        // Every restored file version still matches the digest its metadata records, in its own tenant only.
        var store = new FileSystemBlobStore(recovery.Root, AdversarialHost.MaximumFileLength);
        foreach (var (tenant, version, other) in new[] { (seed.TenantA, seed.FileA1, seed.TenantB), (seed.TenantB, seed.FileB1, seed.TenantA) })
        {
            await using var owner = await restored.EnterServiceAsync(tenant);
            await store.VerifyAsync(owner.Context, version.VersionId, new BlobIntegrity(version.Length, version.Sha256), Cancellation);
            await using var stranger = await restored.EnterServiceAsync(other);
            Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() => store.VerifyAsync(stranger.Context, version.VersionId,
                new BlobIntegrity(version.Length, version.Sha256), Cancellation))).Code);
        }

        // 2. Caches are rebuilt: the recovery host starts with none, the entry from before the restore is not served, and the
        //    rebuilt entry's key carries the tenant and the principal.
        await using (var a = await restored.EnterAsync(seed.UserA, seed.TenantA))
        {
            var entry = new TenantCacheEntry(AdversarialItems.ModuleId, "1", "item-codes", TimeSpan.FromMinutes(5), principalScoped: true);
            var cache = restored.Get<TenantCache>();
            var key = cache.KeyFor(a.Context, entry);
            var rebuilt = false;
            var codes = await cache.GetOrCreateAsync(a.Context, entry, async token =>
            {
                rebuilt = true;
                await using var work = await EfCoreUnitOfWork.BeginAsync(recovery.Items, a.Context, UnitOfWorkMode.Suppress, cancellationToken: token);
                return string.Join(",", await ((ItemsContext)work.Context).Items.OrderBy(row => row.Code).Select(row => row.Code).ToArrayAsync(token));
            }, Cancellation);
            Assert.True(rebuilt);
            Assert.Equal(seed.ItemA1.Code, codes);
            Assert.Contains(TenantIdentifier.Format(seed.TenantA), key, StringComparison.Ordinal);
            Log($"Before the backup the entry was {staleKey}; after the restore it was rebuilt under {key}.");
            // Restored settings resolve per tenant and principal through the rebuilt settings cache.
            Assert.Equal("Banner of tenant A", (await restored.Get<SettingsService>().ResolveAsync(a.Context, AdversarialItems.Banner, Cancellation)).Get<string>());
        }
        await using (var b = await restored.EnterAsync(seed.UserB, seed.TenantB))
            Assert.Equal("Banner of tenant B", (await restored.Get<SettingsService>().ResolveAsync(b.Context, AdversarialItems.Banner, Cancellation)).Get<string>());

        // 3. Restored tenant columns and uniqueness constraints: code-1 is unique per tenant in the recovery deployment as well,
        //    and a restored domain still resolves only to its own tenant's members.
        await using (var a = await restored.EnterAsync(seed.UserA, seed.TenantA))
        await using (var work = await EfCoreUnitOfWork.BeginAsync(recovery.Items, a.Context, cancellationToken: Cancellation))
        {
            work.Context.Add(new Item { Id = Guid.NewGuid(), Code = seed.ItemA1.Code, Name = "A duplicate after the restore" });
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>((await Assert.ThrowsAsync<DbUpdateException>(() =>
                work.FlushAsync(Cancellation))).InnerException).SqlState);
        }
        await using (var a = await restored.EnterAsync(seed.UserA, seed.TenantA))
        await using (var work = await EfCoreUnitOfWork.BeginAsync(recovery.Items, a.Context, cancellationToken: Cancellation))
        {
            work.Context.Add(new Item { Id = Guid.NewGuid(), Code = seed.ItemB1.Code, Name = "Tenant-B's code, now in tenant-A too" });
            Assert.Equal(UnitOfWorkState.Committed, (await work.CompleteAsync(Cancellation)).State);
        }
        var (onB, unit) = await restored.TryEnterAsync(seed.UserA, claims => TenantResolutionRequest.ForMember(claims,
            [new TenantCandidate(TenantCandidateSource.Host, AdversarialHost.HostB)]));
        Assert.Equal((false, TenancyCodes.NotMember), (onB.Succeeded, onB.Code));
        Assert.Null(unit);
        Assert.Equal(await ConstraintsAsync(source), await ConstraintsAsync(recovery));
        foreach (var constraint in await ConstraintsAsync(recovery)) Log($"Restored constraint: {constraint}");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_004_R02_RestorePreservesTenantOwnershipAndRefusals_Integration()
    {
        await using var source = await AdversarialDatabase.CreateAsync();
        AdversarialTenants seed;
        await using (var host = AdversarialHost.Create(source)) seed = await BackedUpAsync(host);

        // Before the backup: tenant-A's requests against tenant-B's records, and tenant-A's cached view.
        List<(string Name, Reply Reply)> before;
        CachedCodes cachedBefore;
        await using (var server = await AdversarialServer.StartAsync(source))
        {
            var client = server.NewClient();
            await client.SignInAsync(seed.UserA);
            before = await ForeignRequestsAsync(client, seed, foreign: true);
            cachedBefore = JsonSerializer.Deserialize<CachedCodes>((await client.SendAsync(HttpMethod.Get, Path(seed.TenantA, "/cached/items?mine=true"))).Text,
                JsonSerializerOptions.Web)!;
        }

        var recovery = await source.RecoveryAsync();
        await source.RestoreIntoAsync(recovery);
        await using var restored = await AdversarialServer.StartAsync(recovery);
        var a = restored.NewClient();
        await a.SignInAsync(seed.UserA);

        // Every cross-tenant request is refused exactly as before the backup, and tenant-A's own records are served.
        var after = await ForeignRequestsAsync(a, seed, foreign: true);
        Assert.Equal(before.Select(attempt => attempt.Name + " " + attempt.Reply.Contract), after.Select(attempt => attempt.Name + " " + attempt.Reply.Contract));
        foreach (var attempt in after) Log($"After the restore, {attempt.Name}: {attempt.Reply.Contract}");
        Assert.Equal(HttpStatusCode.OK, (await a.SendAsync(HttpMethod.Get, Path(seed.TenantA, "/items/" + seed.ItemA1.Id))).Status);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(AdversarialTenants.ContentA1),
            (await a.SendAsync(HttpMethod.Get, Path(seed.TenantA, "/downloads"), configure: request => request.Headers.Add(AdversarialEndpoints.GrantHeader, seed.GrantA1))).Text);
        var b = restored.NewClient(AdversarialHost.HostB);
        await b.SignInAsync(seed.UserB);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(AdversarialTenants.ContentB1),
            (await b.SendAsync(HttpMethod.Get, Path(seed.TenantB, "/downloads"), configure: request => request.Headers.Add(AdversarialEndpoints.GrantHeader, seed.GrantB1))).Text);

        // The rebuilt cache entry is built afresh in the recovery host, under a key that carries tenant-A.
        var cachedAfter = JsonSerializer.Deserialize<CachedCodes>((await a.SendAsync(HttpMethod.Get, Path(seed.TenantA, "/cached/items?mine=true"))).Text,
            JsonSerializerOptions.Web)!;
        Assert.True(cachedBefore.Built);
        Assert.True(cachedAfter.Built);
        Assert.Contains(TenantIdentifier.Format(seed.TenantA), cachedAfter.Key, StringComparison.Ordinal);
        Assert.Equal(cachedBefore.Codes, cachedAfter.Codes);
        Log($"Cache before the backup {cachedBefore.Key}; rebuilt after the restore {cachedAfter.Key}.");
    }

    /// <summary>
    /// The backup's content: both tenants' items, notes, files and download grants from the seed, and their settings.
    /// </summary>
    private static async Task<AdversarialTenants> BackedUpAsync(AdversarialHost host)
    {
        var seed = await AdversarialTenants.SeedAsync(host);
        await ValuesAsync(host, seed);
        await using (var a = await host.EnterAsync(seed.UserA, seed.TenantA)) await NoteAsync(host.Database, a.Context, seed.ItemA1.Id, "tenant-a note");
        await using (var b = await host.EnterAsync(seed.UserB, seed.TenantB)) await NoteAsync(host.Database, b.Context, seed.ItemB1.Id, "tenant-b note");
        return seed;
    }

    /// <summary>tenant-A's in-process attempts on tenant-B's item, note reference, list, export, file, grant and stored content.</summary>
    private static async Task<string> ForeignInProcessAsync(AdversarialHost host, AdversarialTenants seed)
    {
        var database = host.Database;
        var outcomes = new List<string>();
        await using var a = await host.EnterAsync(seed.UserA, seed.TenantA);
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, UnitOfWorkMode.Suppress, cancellationToken: Cancellation))
        {
            var items = (ItemsContext)work.Context;
            outcomes.Add("read:" + await items.Items.AnyAsync(row => row.Id == seed.ItemB1.Id, Cancellation));
            outcomes.Add("list:" + string.Join("|", await items.Items.OrderBy(row => row.Code).Select(row => row.TenantId + "/" + row.Code).ToArrayAsync(Cancellation)));
            outcomes.Add("notes:" + await items.Notes.CountAsync(Cancellation));
            outcomes.Add("export:" + await work.Sql.Query<Item, string>(row => row.Code, seed.ItemB1.Code).CountAsync(Cancellation));
        }
        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Items, a.Context, cancellationToken: Cancellation))
        {
            work.Context.Add(new ItemNote { Id = Guid.NewGuid(), ItemId = seed.ItemB1.Id, Text = "references item-b1" });
            outcomes.Add("reference:" + ((PostgresException)(await Assert.ThrowsAsync<DbUpdateException>(() => work.FlushAsync(Cancellation))).InnerException!).SqlState);
        }
        outcomes.Add("download:" + (await Assert.ThrowsAsync<FileStorageException>(() => host.Files.ReadAsync(a.Context, a.Principal, seed.FileB1.FileId,
            seed.FileB1.VersionId, cancellationToken: Cancellation))).Code);
        outcomes.Add("grant:" + (await Assert.ThrowsAsync<FileStorageException>(() => host.Files.RedeemGrantAsync(a.Context, a.Principal, Token(seed.GrantB1),
            FileGrantPurpose.Download, cancellationToken: Cancellation))).Code);
        outcomes.Add("stored:" + (await Assert.ThrowsAsync<FileStorageException>(() => new FileSystemBlobStore(database.Root, AdversarialHost.MaximumFileLength)
            .ReadAsync(a.Context, seed.FileB1.VersionId, new BlobIntegrity(seed.FileB1.Length, seed.FileB1.Sha256), null, Cancellation))).Code);
        outcomes.Add("own:" + (await ReadAllAsync(await host.Files.ReadAsync(a.Context, a.Principal, seed.FileA1.FileId, seed.FileA1.VersionId,
            cancellationToken: Cancellation))).Length);
        return string.Join(" ", outcomes);
    }

    /// <summary>Every primary key, unique constraint, foreign key and index of the items and Files schemas, without schema names.</summary>
    private static async Task<List<string>> ConstraintsAsync(AdversarialDatabase database)
    {
        var listing = new List<string>();
        foreach (var module in new[] { "items", "files" })
        {
            var schema = database.Schemas[module];
            foreach (var row in await database.RowsAsync("SELECT c.relname || ': ' || pg_get_constraintdef(k.oid) FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid "
                + "JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema AND k.contype IN ('p', 'u', 'f') AND left(c.relname, 2) <> '__' "
                + "AND c.relname <> 'Lookups' ORDER BY 1", new NpgsqlParameter("schema", schema)))
                listing.Add(module + "." + row[0].Replace(schema, "<schema>", StringComparison.Ordinal));
            foreach (var row in await database.RowsAsync("SELECT tablename || ': ' || indexdef FROM pg_indexes WHERE schemaname = @schema AND left(tablename, 2) <> '__' "
                + "AND tablename <> 'Lookups' ORDER BY 1", new NpgsqlParameter("schema", schema)))
                listing.Add(module + "." + row[0].Replace(schema, "<schema>", StringComparison.Ordinal));
        }
        return listing;
    }
}
