using Modularyn.Modules.Files.Application.Contracts;

namespace Modularyn.Modules.Files.Application;

/// <summary>
/// An opt-in release policy for a host that accepts releasing content nobody has scanned. It releases a pending version only
/// when the type detected from its verified bytes is on the host's allowlist and its length is at most the host's maximum,
/// and rejects every other version, including one whose type its bytes do not confirm. It does no malware or content
/// scanning and makes no file safe: it reads the bytes only to detect their type and count them, and never consults the
/// display name. Files never installs it; a host constructs it with its allowlist and maximum and passes it to
/// <see cref="FileService"/>.
/// </summary>
public sealed class AllowlistFileReleasePolicy : IFileReleasePolicy
{
    private const int BufferLength = 81920;

    /// <param name="allowedContentTypes">The detected types to release, each one that detection returns and recognizes.</param>
    /// <param name="maximumLength">The largest version to release, in bytes.</param>
    public AllowlistFileReleasePolicy(IEnumerable<string> allowedContentTypes, long maximumLength)
    {
        ArgumentNullException.ThrowIfNull(allowedContentTypes);
        var allowed = allowedContentTypes.ToHashSet(StringComparer.Ordinal);
        // Detection returns the generic binary type for bytes it does not recognize, so that type is never on an allowlist.
        if (allowed.Count == 0 || !allowed.IsSubsetOf(FileContentTypes.Detectable) || allowed.Contains(FileContentTypes.Binary))
            throw new ArgumentException("Allow only types that detection recognizes.", nameof(allowedContentTypes));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        AllowedContentTypes = allowed;
        MaximumLength = maximumLength;
    }

    public IReadOnlySet<string> AllowedContentTypes { get; }
    public long MaximumLength { get; }

    public async ValueTask<FileReleaseVerdict> DecideAsync(FileReleaseCandidate candidate, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(content);
        if (candidate.Length < 0 || candidate.Length > MaximumLength || !AllowedContentTypes.Contains(candidate.ContentType))
            return FileReleaseVerdict.Reject;
        // The type is detected again from the verified bytes rather than taken from the record; a record and bytes that
        // disagree leave the type uncertain.
        var head = new byte[(int)Math.Min(candidate.Length, FileContentTypes.SniffLength)];
        if (await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) != head.Length
            || FileContentTypes.Detect(head) != candidate.ContentType)
            return FileReleaseVerdict.Reject;
        // Detection calls a header text when it holds no binary data byte, or when it starts with a byte-order mark; text is
        // confirmed only when no byte of the whole content is one, so UTF-16 text, whose bytes include them, is not released.
        var text = candidate.ContentType == FileContentTypes.PlainText;
        if (text && FileContentTypes.HasBinaryData(head)) return FileReleaseVerdict.Reject;
        var length = (long)head.Length;
        var buffer = new byte[BufferLength];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            length += read;
            if (length > candidate.Length || text && FileContentTypes.HasBinaryData(buffer.AsSpan(0, read))) return FileReleaseVerdict.Reject;
        }
        // The bytes must also number exactly the verified length.
        return length == candidate.Length ? FileReleaseVerdict.Release : FileReleaseVerdict.Reject;
    }
}
