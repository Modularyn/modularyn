using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Modules.Files.Domain;
using Xunit;

namespace Modularyn.Modules.Files.Persistence.FileSystem.Tests;

/// <summary>
/// The storage boundary of the filesystem provider without a database: links, the tenant lease, staging, read slots,
/// verification and the locking check, over contexts that tenancy establishes from an in-memory directory. These tests
/// carry no PostgreSQL trait, so every CI platform runs them.
/// </summary>
[Collection(LocalStoreCollection.Name)]
public sealed class FileSystemStoreTests
{
    internal const string ProbeVariable = "MODULARYN_FILES_LOCKING_PROBE";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Linked_roots_tenant_directories_content_staging_slots_and_leases_are_refused_without_touching_their_targets()
    {
        await using var local = new LocalTenants();
        var tenant = await local.EnterAsync(local.TenantA);
        var outside = local.NewRoot();
        var marker = Path.Combine(outside, "marker");
        var bytes = Bytes(64);
        await File.WriteAllBytesAsync(marker, bytes, Cancellation);
        var linkedRoot = Path.Combine(local.Root, "linked");
        Directory.CreateSymbolicLink(linkedRoot, outside);
        Assert.Equal(FileStorageError.UnsafeLocation, Assert.Throws<FileStorageException>(() => new FileSystemBlobStore(linkedRoot, 1024)).Code);
        Directory.Delete(linkedRoot);
        var tenantPath = Path.Combine(local.Root, local.TenantA.ToString("N"));
        Directory.CreateSymbolicLink(tenantPath, outside);
        var store = new FileSystemBlobStore(local.Root, 1024);
        Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(async () =>
            await store.OpenAsync(tenant, Cancellation))).Code);
        Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() =>
            store.ReadAsync(tenant, Guid.NewGuid(), Integrity(bytes), null, Cancellation))).Code);
        Directory.Delete(tenantPath);

        var id = await WriteAsync(store, tenant, bytes);
        var content = local.Content(local.TenantA, id, ".blob");
        var moved = Path.Combine(outside, "original");
        File.Move(content, moved);
        foreach (var target in new[] { moved, Path.Combine(outside, "absent") })
        {
            File.CreateSymbolicLink(content, target);
            Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() =>
                store.ReadAsync(tenant, id, Integrity(bytes), null, Cancellation))).Code);
            Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() =>
                store.VerifyAsync(tenant, id, Integrity(bytes), Cancellation))).Code);
            File.Delete(content);
        }
        File.Move(moved, content);

        var staged = Guid.NewGuid();
        File.CreateSymbolicLink(local.Content(local.TenantA, staged, ".pending"), marker);
        await using (var session = await store.OpenAsync(tenant, Cancellation))
        {
            Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() => session.StageAsync(staged, Cancellation))).Code);
            Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() => session.DiscardAsync(staged, Cancellation))).Code);
        }
        File.Delete(local.Content(local.TenantA, staged, ".pending"));

        var slots = Enumerable.Range(0, FileSystemBlobStore.MaxReadSlots).Select(slot => local.Slot(local.TenantA, slot)).ToArray();
        foreach (var slot in slots.Where(File.Exists)) File.Delete(slot);
        File.CreateSymbolicLink(slots[0], marker);
        var single = new FileSystemBlobStore(local.Root, 1024, readSlots: 1);
        Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() =>
            single.ReadAsync(tenant, id, Integrity(bytes), null, Cancellation))).Code);
        await using (var session = await store.OpenAsync(tenant, Cancellation))
            Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(() =>
                session.RecoverReadSnapshotsAsync(Cancellation))).Code);
        File.Delete(slots[0]);

        var lease = Path.Combine(tenantPath, ".lease");
        File.Delete(lease);
        File.CreateSymbolicLink(lease, marker);
        Assert.Equal(FileStorageError.UnsafeLocation, (await Assert.ThrowsAsync<FileStorageException>(async () =>
            await store.OpenAsync(tenant, Cancellation))).Code);
        File.Delete(lease);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(marker, Cancellation));
        Assert.Equal(new[] { "marker" }, Directory.GetFiles(outside).Select(path => Path.GetFileName(path)).ToArray());
    }

    [Fact]
    public async Task The_tenant_lease_excludes_leased_work_per_tenant_and_reads_and_verification_need_none()
    {
        await using var local = new LocalTenants();
        var tenantA = await local.EnterAsync(local.TenantA);
        var tenantB = await local.EnterAsync(local.TenantB);
        var store = new FileSystemBlobStore(local.Root, 1024);
        var bytes = Bytes(100);
        var id = await WriteAsync(store, tenantA, bytes);
        await using (var held = await store.OpenAsync(tenantA, Cancellation))
        {
            Assert.Equal(FileStorageError.Busy, (await Assert.ThrowsAsync<FileStorageException>(async () =>
                await new FileSystemBlobStore(local.Root, 1024).OpenAsync(tenantA, Cancellation))).Code);
            await using (await store.OpenAsync(tenantB, Cancellation)) { }
            // Reads and verification of the same tenant proceed while its lease is held.
            await store.VerifyAsync(tenantA, id, Integrity(bytes), Cancellation);
            Assert.Equal(bytes, await ReadAllAsync(await new FileSystemBlobStore(local.Root, 1024).ReadAsync(tenantA, id, Integrity(bytes), null, Cancellation)));
        }
        await using (await store.OpenAsync(tenantA, Cancellation)) { }
    }

    [Fact]
    public async Task Reads_run_concurrently_within_the_tenant_s_read_slots_and_are_refused_beyond_them()
    {
        await using var local = new LocalTenants();
        var tenantA = await local.EnterAsync(local.TenantA);
        var tenantB = await local.EnterAsync(local.TenantB);
        var store = new FileSystemBlobStore(local.Root, 4096, readSlots: 3);
        var bytes = Bytes(3000);
        var id = await WriteAsync(store, tenantA, bytes);
        var other = await WriteAsync(store, tenantB, bytes);
        var reads = new List<Stream>();
        for (var index = 0; index < 3; index++) reads.Add(await store.ReadAsync(tenantA, id, Integrity(bytes), new(index, 10), Cancellation));
        Assert.Equal(FileStorageError.Busy, (await Assert.ThrowsAsync<FileStorageException>(() =>
            store.ReadAsync(tenantA, id, Integrity(bytes), null, Cancellation))).Code);
        // The slots bound each tenant's snapshot bytes: three of at most the maximum length, and another tenant has its own.
        Assert.Equal(3, local.SlotLengths(local.TenantA).Count(length => length == bytes.Length));
        Assert.Equal(bytes, await ReadAllAsync(await store.ReadAsync(tenantB, other, Integrity(bytes), null, Cancellation)));
        for (var index = 0; index < 3; index++) Assert.Equal(bytes.Skip(index).Take(10).ToArray(), await ReadAllAsync(reads[index]));
        Assert.All(local.SlotLengths(local.TenantA), length => Assert.Equal(0, length));
        Assert.Equal(3, local.SlotLengths(local.TenantA).Length);
        Assert.Equal(bytes, await ReadAllAsync(await store.ReadAsync(tenantA, id, Integrity(bytes), null, Cancellation)));
    }

    [Fact]
    public async Task Abandoned_read_slots_are_reclaimed_while_live_readers_keep_their_bytes()
    {
        await using var local = new LocalTenants();
        var tenant = await local.EnterAsync(local.TenantA);
        var store = new FileSystemBlobStore(local.Root, 1024, readSlots: 4);
        var bytes = Bytes(500);
        var id = await WriteAsync(store, tenant, bytes);
        var live = await store.ReadAsync(tenant, id, Integrity(bytes), null, Cancellation);
        var held = Enumerable.Range(0, 4).Single(slot => FileDatabase.IsHeld(local.Slot(local.TenantA, slot)));
        // Every other slot, and one beyond the configured count, is left with bytes and no holder, as an ended reader leaves it.
        foreach (var slot in Enumerable.Range(0, 5).Where(slot => slot != held)) await File.WriteAllBytesAsync(local.Slot(local.TenantA, slot), bytes, Cancellation);
        await using (var session = await store.OpenAsync(tenant, Cancellation)) await session.RecoverReadSnapshotsAsync(Cancellation);
        Assert.Equal(bytes.Length, new FileInfo(local.Slot(local.TenantA, held)).Length);
        Assert.Equal(4, local.SlotLengths(local.TenantA).Count(length => length == 0));
        Assert.Equal(bytes, await ReadAllAsync(live));
        // A slot that still holds an ended reader's bytes is truncated before its next reader uses it.
        await File.WriteAllBytesAsync(local.Slot(local.TenantA, 0), Bytes(900, 5), Cancellation);
        var reads = new List<Stream>();
        for (var index = 0; index < 4; index++) reads.Add(await store.ReadAsync(tenant, id, Integrity(bytes), null, Cancellation));
        foreach (var read in reads) Assert.Equal(bytes, await ReadAllAsync(read));
        Assert.All(local.SlotLengths(local.TenantA), length => Assert.Equal(0, length));
    }

    [Fact]
    public async Task A_held_staging_file_is_never_discarded_and_an_abandoned_one_is_reclaimed()
    {
        await using var local = new LocalTenants();
        var tenant = await local.EnterAsync(local.TenantA);
        var store = new FileSystemBlobStore(local.Root, 1024);
        var bytes = Bytes(200);
        var id = Guid.NewGuid();
        IFileBlobStaging staging;
        await using (var session = await store.OpenAsync(tenant, Cancellation)) staging = await session.StageAsync(id, Cancellation);
        // The upload streams after releasing the lease; recovery under a new lease leaves the held staging file alone.
        await using (var recovery = await store.OpenAsync(tenant, Cancellation))
        {
            Assert.False(await recovery.DiscardAsync(id, Cancellation));
            Assert.True(File.Exists(local.Content(local.TenantA, id, ".pending")));
        }
        await staging.WriteAsync(new MemoryStream(bytes), Integrity(bytes), Cancellation);
        await using (var recovery = await store.OpenAsync(tenant, Cancellation)) Assert.False(await recovery.DiscardAsync(id, Cancellation));
        await using (var publication = await store.OpenAsync(tenant, Cancellation)) await publication.PublishAsync(staging, Cancellation);
        await staging.DisposeAsync();
        Assert.False(File.Exists(local.Content(local.TenantA, id, ".pending")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(local.Content(local.TenantA, id, ".blob"), Cancellation));

        var abandoned = Guid.NewGuid();
        await using (var session = await store.OpenAsync(tenant, Cancellation))
        {
            await using var left = await session.StageAsync(abandoned, Cancellation);
            await Assert.ThrowsAsync<FileStorageException>(() => left.WriteAsync(new MemoryStream(Bytes(150)), Integrity(bytes), Cancellation));
        }
        Assert.True(File.Exists(local.Content(local.TenantA, abandoned, ".pending")));
        await using (var recovery = await store.OpenAsync(tenant, Cancellation)) Assert.True(await recovery.DiscardAsync(abandoned, Cancellation));
        Assert.False(File.Exists(local.Content(local.TenantA, abandoned, ".pending")));
    }

    [Fact]
    public async Task Staged_bytes_are_bounded_while_read_and_existing_content_is_never_replaced()
    {
        await using var local = new LocalTenants();
        var tenant = await local.EnterAsync(local.TenantA);
        var store = new FileSystemBlobStore(local.Root, 64);
        var bytes = Bytes(64);
        var id = await WriteAsync(store, tenant, bytes);
        await using (var session = await store.OpenAsync(tenant, Cancellation))
        {
            var replacement = Bytes(12, 9);
            Assert.Equal(FileStorageError.Conflict, (await Assert.ThrowsAsync<FileStorageException>(() =>
                session.WriteAsync(id, new MemoryStream(replacement), Integrity(replacement), Cancellation))).Code);
            Assert.Equal(FileStorageError.Conflict, (await Assert.ThrowsAsync<FileStorageException>(() => session.StageAsync(id, Cancellation))).Code);
            // More bytes than declared are refused as they arrive, before they are written.
            var longer = Guid.NewGuid();
            await using (var staging = await session.StageAsync(longer, Cancellation))
            {
                var declared = bytes[..16];
                Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
                    staging.WriteAsync(new MemoryStream(bytes), Integrity(declared), Cancellation))).Code);
                await Assert.ThrowsAsync<InvalidOperationException>(() => staging.WriteAsync(new MemoryStream(declared), Integrity(declared), Cancellation));
                await Assert.ThrowsAsync<InvalidOperationException>(() => session.PublishAsync(staging, Cancellation));
            }
            Assert.True(new FileInfo(local.Content(local.TenantA, longer, ".pending")).Length <= 16);
            await using (var staging = await session.StageAsync(Guid.NewGuid(), Cancellation))
                Assert.Equal(FileStorageError.InvalidLength, (await Assert.ThrowsAsync<FileStorageException>(() =>
                    staging.WriteAsync(new MemoryStream(Bytes(65)), Integrity(Bytes(65)), Cancellation))).Code);
        }
        Assert.Equal(64, store.MaximumLength);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(local.Content(local.TenantA, id, ".blob"), Cancellation));
    }

    [Fact]
    public async Task Verification_hashes_the_stored_bytes_where_they_are_without_a_copy()
    {
        await using var local = new LocalTenants();
        var tenant = await local.EnterAsync(local.TenantA);
        var store = new FileSystemBlobStore(local.Root, 1024, readSlots: 1);
        var bytes = Bytes(800);
        var id = await WriteAsync(store, tenant, bytes);
        await store.VerifyAsync(tenant, id, Integrity(bytes), Cancellation);
        // Verification takes no read slot, so it proceeds while the tenant's only slot is held.
        await using (var held = await store.ReadAsync(tenant, id, Integrity(bytes), new(0, 1), Cancellation))
            await store.VerifyAsync(tenant, id, Integrity(bytes), Cancellation);
        Assert.Equal(FileStorageError.NotFound, (await Assert.ThrowsAsync<FileStorageException>(() =>
            store.VerifyAsync(tenant, Guid.NewGuid(), Integrity(bytes), Cancellation))).Code);
        var damaged = bytes.ToArray();
        damaged[^1] ^= 1;
        await File.WriteAllBytesAsync(local.Content(local.TenantA, id, ".blob"), damaged, Cancellation);
        Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
            store.VerifyAsync(tenant, id, Integrity(bytes), Cancellation))).Code);
        await File.WriteAllBytesAsync(local.Content(local.TenantA, id, ".blob"), bytes[..^1], Cancellation);
        Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
            store.VerifyAsync(tenant, id, Integrity(bytes), Cancellation))).Code);
        Assert.Equal(FileStorageError.IntegrityMismatch, (await Assert.ThrowsAsync<FileStorageException>(() =>
            store.ReadAsync(tenant, id, Integrity(bytes), null, Cancellation))).Code);
        Assert.All(local.SlotLengths(local.TenantA), length => Assert.Equal(0, length));
    }

    [Fact]
    public async Task Ending_a_context_refuses_the_store_s_reads_streams_and_staging()
    {
        await using var local = new LocalTenants();
        var tenant = await local.EnterAsync(local.TenantA);
        var store = new FileSystemBlobStore(local.Root, 1024);
        var bytes = Bytes(20);
        var id = await WriteAsync(store, tenant, bytes);
        var read = await store.ReadAsync(tenant, id, Integrity(bytes), null, Cancellation);
        IFileBlobStaging staging;
        await using (var session = await store.OpenAsync(tenant, Cancellation)) staging = await session.StageAsync(Guid.NewGuid(), Cancellation);
        await local.EndAsync(tenant);
        Assert.Equal(FileStorageError.InvalidScope, (await Assert.ThrowsAsync<FileStorageException>(async () =>
            await read.ReadExactlyAsync(new byte[1], Cancellation))).Code);
        Assert.Equal(FileStorageError.InvalidScope, (await Assert.ThrowsAsync<FileStorageException>(() =>
            staging.WriteAsync(new MemoryStream(bytes), Integrity(bytes), Cancellation))).Code);
        Assert.Equal(FileStorageError.InvalidScope, (await Assert.ThrowsAsync<FileStorageException>(() =>
            store.ReadAsync(tenant, id, Integrity(bytes), null, Cancellation))).Code);
        Assert.Equal(FileStorageError.InvalidScope, (await Assert.ThrowsAsync<FileStorageException>(() =>
            store.VerifyAsync(tenant, id, Integrity(bytes), Cancellation))).Code);
        await read.DisposeAsync();
        await staging.DisposeAsync();
    }

    /// <summary>
    /// The store starts only where a second exclusive open of a file fails. Run directly, this checks the store starts here
    /// and leaves no probe behind; run by <see cref="The_store_refuses_to_start_where_the_runtime_disables_file_locking"/>
    /// in a process with file locking disabled, it reports what the store did.
    /// </summary>
    [Fact]
    public void The_store_starts_only_where_exclusive_opens_are_enforced()
    {
        var root = LocalTenants.CreateRoot();
        try
        {
            if (Environment.GetEnvironmentVariable(ProbeVariable) is { Length: > 0 } report)
            {
                var outcome = "started";
                try { _ = new FileSystemBlobStore(root, 1024); }
                catch (FileStorageException exception) when (exception.Code == FileStorageError.LockingUnavailable) { outcome = "refused"; }
                File.WriteAllText(report, outcome);
                return;
            }
            _ = new FileSystemBlobStore(root, 1024);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task The_store_refuses_to_start_where_the_runtime_disables_file_locking()
    {
        var report = Path.Combine(LocalTenants.CreateRoot(), "outcome");
        try
        {
            var assembly = typeof(FileSystemStoreTests).Assembly.Location;
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } configured && File.Exists(configured) ? configured
                : Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath! : "dotnet";
            var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { assembly, "-noLogo", "-noColor", "-method",
                         typeof(FileSystemStoreTests).FullName + "." + nameof(The_store_starts_only_where_exclusive_opens_are_enforced) })
                start.ArgumentList.Add(argument);
            start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
            start.Environment[ProbeVariable] = report;
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync(Cancellation);
            var errors = child.StandardError.ReadToEndAsync(Cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { child.Kill(entireProcessTree: true); throw; }
            Assert.True(child.ExitCode == 0, await output + await errors);
            // Windows enforces share modes itself, so the switch cannot remove the exclusion there; elsewhere the store refuses.
            Assert.Equal(OperatingSystem.IsWindows() ? "started" : "refused", await File.ReadAllTextAsync(report, Cancellation));
        }
        finally { Directory.Delete(Path.GetDirectoryName(report)!, recursive: true); }
    }

    private static async Task<Guid> WriteAsync(FileSystemBlobStore store, TenantContext tenant, byte[] bytes)
    {
        var id = Guid.NewGuid();
        await using var session = await store.OpenAsync(tenant, Cancellation);
        await session.WriteAsync(id, new MemoryStream(bytes), Integrity(bytes), Cancellation);
        return id;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, Cancellation);
            return output.ToArray();
        }
    }

    private static BlobIntegrity Integrity(byte[] bytes) => new(bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    private static byte[] Bytes(int count, int seed = 0) => Enumerable.Range(0, count).Select(index => (byte)((index + seed) % 251)).ToArray();
}

[CollectionDefinition(Name)]
public sealed class LocalStoreCollection
{
    public const string Name = "Local file stores";
}

/// <summary>Two tenants in tenancy's in-memory directory, a member of both, and isolated filesystem roots.</summary>
internal sealed class LocalTenants : IAsyncDisposable
{
    private const string Member = "file-owner";
    private readonly ServiceProvider services;
    private readonly List<AsyncServiceScope> scopes = [];
    private readonly Dictionary<TenantContext, AsyncServiceScope> units = [];
    private readonly List<string> roots = [];

    internal LocalTenants()
    {
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        TenantA = directory.Add("Local tenant A");
        TenantB = directory.Add("Local tenant B");
        seams.Membership.Add(Member, TenantA);
        seams.Membership.Add(Member, TenantB);
        services = UnitServices.Build(directory, seams);
        Root = NewRoot();
    }

    internal Guid TenantA { get; }
    internal Guid TenantB { get; }
    internal string Root { get; }

    internal async Task<TenantContext> EnterAsync(Guid tenant)
    {
        var (scope, context) = await Units.EnterAsync(services, Member, tenant);
        scopes.Add(scope);
        units[context] = scope;
        return context;
    }

    /// <summary>Ends the context's unit, as a finished request does.</summary>
    internal ValueTask EndAsync(TenantContext context) => units[context].DisposeAsync();

    internal string Content(Guid tenant, Guid id, string extension) => Path.Combine(Root, tenant.ToString("N"), id.ToString("N") + extension);
    internal string Slot(Guid tenant, int slot) => Path.Combine(Root, tenant.ToString("N"), ".read-" + slot);

    internal long[] SlotLengths(Guid tenant) => Enumerable.Range(0, FileSystemBlobStore.MaxReadSlots).Select(slot => Slot(tenant, slot))
        .Where(File.Exists).Select(path => new FileInfo(path).Length).ToArray();

    internal string NewRoot()
    {
        var root = CreateRoot();
        roots.Add(root);
        return root;
    }

    /// <summary>A new directory under the platform's temporary directory, with its link aliases resolved, as the store requires.</summary>
    internal static string CreateRoot()
    {
        var path = Path.GetFullPath(Path.GetTempPath());
        var current = Path.GetPathRoot(path)!;
        foreach (var segment in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var info = new DirectoryInfo(Path.Combine(current, segment));
            current = info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(true)!.FullName;
        }
        var root = Path.Combine(current, "modularyn-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes) await scope.DisposeAsync();
        await services.DisposeAsync();
        foreach (var root in roots) if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
