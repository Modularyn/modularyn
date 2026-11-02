using Modularyn.Modules.Files.Application.Contracts;
using Xunit;

namespace Modularyn.Modules.Files.Application.Tests;

/// <summary>The opt-in allowlist release policy's construction and decisions over verified bytes, without providers.</summary>
public sealed class AllowlistReleasePolicyTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void TM_G02_042_The_allowlist_policy_is_built_only_from_an_explicit_allowlist_and_maximum()
    {
        var policy = new AllowlistFileReleasePolicy([FileContentTypes.Png, FileContentTypes.Pdf], 4096);
        Assert.True(policy.AllowedContentTypes.SetEquals([FileContentTypes.Png, FileContentTypes.Pdf]));
        Assert.Equal(4096, policy.MaximumLength);
        Assert.Throws<ArgumentNullException>(() => new AllowlistFileReleasePolicy(null!, 4096));
        // No allowlist, a type that detection never returns, and the type it returns for bytes it does not recognize.
        string[][] refused = [[], ["image/svg"], ["IMAGE/PNG"], [FileContentTypes.Binary], [FileContentTypes.Png, FileContentTypes.Binary]];
        foreach (var types in refused)
            Assert.Throws<ArgumentException>(() => new AllowlistFileReleasePolicy(types, 4096));
        foreach (var maximum in new[] { 0L, -1L, long.MinValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => new AllowlistFileReleasePolicy([FileContentTypes.Png], maximum));
    }

    [Fact]
    public async Task TM_G02_042_The_allowlist_policy_releases_only_bytes_that_confirm_an_allowed_type_within_its_maximum()
    {
        var policy = new AllowlistFileReleasePolicy([FileContentTypes.Png, FileContentTypes.PlainText], 4096);
        Assert.Equal(FileReleaseVerdict.Release, await DecideAsync(policy, Png(64)));
        Assert.Equal(FileReleaseVerdict.Release, await DecideAsync(policy, Png(4096)));
        Assert.Equal(FileReleaseVerdict.Release, await DecideAsync(policy, FilesHost.Text(4096)));
        Assert.Equal(FileReleaseVerdict.Release, await DecideAsync(policy, []));
        // The display name is never consulted, either way.
        Assert.Equal(FileReleaseVerdict.Release, await DecideAsync(policy, FilesHost.Text(40), name: "photo.png"));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, Pdf(400), name: "notes.txt"));

        // Over the maximum, and a type off the allowlist.
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, Png(4097)));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, FilesHost.Text(4097)));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, Pdf(400)));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, FilesHost.Bytes(200)));
        // Uncertain detection: a record whose type the bytes contradict, text with a binary data byte past its header or in it
        // behind a byte-order mark, and bytes fewer or more than the verified length.
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, FilesHost.Text(100), FileContentTypes.Png));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, Png(100), FileContentTypes.PlainText));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, [.. FilesHost.Text(FileContentTypes.SniffLength), 0x00, .. FilesHost.Text(10)]));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, [.. FilesHost.Text(3000), 0x1A]));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, [0xFF, 0xFE, 0x41, 0x00, 0x42, 0x00]));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, Png(100), length: 101));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, Png(100), length: 99));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, Png(3000), length: 2000));
        Assert.Equal(FileReleaseVerdict.Reject, await DecideAsync(policy, Png(2000), length: 3000));
    }

    internal static byte[] Png(int length) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. FilesHost.Bytes(length - 8)];
    internal static byte[] Pdf(int length) => [.. "%PDF-1.7\n"u8, .. FilesHost.Text(length - 9)];

    /// <summary>Decides on the bytes as Files would hand them over, with the record's type and length unless a test overrides them.</summary>
    private static async Task<FileReleaseVerdict> DecideAsync(AllowlistFileReleasePolicy policy, byte[] content, string? contentType = null,
        long? length = null, string name = "attachment")
    {
        var candidate = new FileReleaseCandidate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, length ?? content.LongLength,
            FilesHost.Digest(content), contentType ?? FileContentTypes.Detect(content.AsSpan(0, Math.Min(content.Length, FileContentTypes.SniffLength))));
        using var stream = new MemoryStream(content, writable: false);
        return await policy.DecideAsync(candidate, stream, Cancellation);
    }
}
