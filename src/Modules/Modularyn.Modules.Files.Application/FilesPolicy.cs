namespace Modularyn.Modules.Files.Application;

/// <summary>
/// The deployment's Files limits. Each tenant may hold at most <see cref="MaximumPendingUploads"/> outstanding upload
/// intents declaring at most <see cref="MaximumPendingBytes"/> bytes, reserved under the tenant's lease before any byte is
/// accepted; an upload streams for at most <see cref="UploadTimeout"/>. Only <see cref="AllowedContentTypes"/> are accepted,
/// and only <see cref="InlineContentTypes"/> are delivered inline, to a preview. Every value is checked here.
/// </summary>
public sealed class FilesPolicy
{
    public const int DefaultMaximumPendingUploads = 16;
    public const int PendingBytesPerMaximumLength = 4;
    public static readonly TimeSpan DefaultUploadTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultMaximumGrantLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultLeaseWait = TimeSpan.FromSeconds(5);

    public static IReadOnlySet<string> DefaultAllowedContentTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FileContentTypes.Png, FileContentTypes.Jpeg, FileContentTypes.Gif, FileContentTypes.Webp, FileContentTypes.Pdf, FileContentTypes.PlainText
    };

    public static IReadOnlySet<string> DefaultInlineContentTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FileContentTypes.Png, FileContentTypes.Jpeg, FileContentTypes.Gif, FileContentTypes.Webp
    };

    /// <param name="maximumPendingBytes">Null for <see cref="PendingBytesPerMaximumLength"/> times the store's maximum length.</param>
    public FilesPolicy(int maximumPendingUploads = DefaultMaximumPendingUploads, long? maximumPendingBytes = null,
        IEnumerable<string>? allowedContentTypes = null, IEnumerable<string>? inlineContentTypes = null, TimeSpan? uploadTimeout = null,
        TimeSpan? maximumGrantLifetime = null, TimeSpan? leaseWait = null)
    {
        if (maximumPendingUploads is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumPendingUploads));
        if (maximumPendingBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumPendingBytes));
        var allowed = (allowedContentTypes ?? DefaultAllowedContentTypes).ToHashSet(StringComparer.Ordinal);
        if (allowed.Count == 0 || !allowed.IsSubsetOf(FileContentTypes.Detectable))
            throw new ArgumentException("Allow only types that detection can return.", nameof(allowedContentTypes));
        var inline = (inlineContentTypes ?? DefaultInlineContentTypes.Where(allowed.Contains)).ToHashSet(StringComparer.Ordinal);
        if (!inline.IsSubsetOf(allowed) || !inline.IsSubsetOf(FileContentTypes.InlineCapable))
            throw new ArgumentException("Approve for inline display only allowed raster image types.", nameof(inlineContentTypes));
        MaximumPendingUploads = maximumPendingUploads;
        MaximumPendingBytes = maximumPendingBytes;
        AllowedContentTypes = allowed;
        InlineContentTypes = inline;
        UploadTimeout = Within(uploadTimeout ?? DefaultUploadTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromHours(24), nameof(uploadTimeout));
        MaximumGrantLifetime = Within(maximumGrantLifetime ?? DefaultMaximumGrantLifetime, TimeSpan.FromSeconds(1), TimeSpan.FromDays(7),
            nameof(maximumGrantLifetime));
        LeaseWait = Within(leaseWait ?? DefaultLeaseWait, TimeSpan.Zero, TimeSpan.FromMinutes(1), nameof(leaseWait));
    }

    public int MaximumPendingUploads { get; }
    public long? MaximumPendingBytes { get; }
    public IReadOnlySet<string> AllowedContentTypes { get; }
    public IReadOnlySet<string> InlineContentTypes { get; }
    public TimeSpan UploadTimeout { get; }
    public TimeSpan MaximumGrantLifetime { get; }

    /// <summary>How long an upload's short intent or publication step waits for the tenant's lease before reporting Busy.</summary>
    public TimeSpan LeaseWait { get; }

    private static TimeSpan Within(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string name) =>
        value >= minimum && value <= maximum ? value : throw new ArgumentOutOfRangeException(name);
}
