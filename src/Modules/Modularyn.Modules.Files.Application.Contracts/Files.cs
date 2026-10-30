using System.Text;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Files.Application.Contracts;

public enum FileQuarantineState { Pending = 1, Released = 2, Rejected = 3 }

public sealed record FileUploadRequest(Guid? FileId, string Name, string Container, string Owner, long Length, string Sha256);
public sealed record FileVersionInfo(Guid FileId, Guid VersionId, long Length, string Sha256, DateTimeOffset CreatedAt,
    FileQuarantineState Quarantine, bool IsDeleted, Guid ConcurrencyVersion, string ContentType);

/// <summary>A file as a list shows it. <see cref="CreatedBy"/> is the server-derived owner; <see cref="Owner"/> is the client's reference.</summary>
public sealed record FileSummary(Guid FileId, string Name, string Container, string Owner, ActorReference? CreatedBy, DateTimeOffset CreatedAt);
public sealed record FileReadRange(long Offset, long Length);

public enum FileDisposition { Attachment = 1, Inline = 2 }

/// <summary>
/// How an endpoint delivers a read: the detected type, sent with sniffing disabled, and attachment disposition unless
/// the read's purpose is a preview and the deployment approved the type for inline display. The endpoint sends
/// <see cref="Headers"/> as they are.
/// </summary>
public sealed record FileDelivery(string ContentType, FileDisposition Disposition, string FileName)
{
    public IReadOnlyList<KeyValuePair<string, string>> Headers() =>
    [
        new("Content-Type", ContentType),
        new("X-Content-Type-Options", "nosniff"),
        new("Content-Disposition", ContentDisposition()),
        new("Content-Security-Policy", "default-src 'none'; sandbox"),
        new("Cross-Origin-Resource-Policy", "same-origin"),
        new("Cache-Control", "private, no-store")
    ];

    // RFC 6266 with an RFC 8187 extended value; the quoted fallback keeps only printable ASCII other than quote, backslash and percent.
    private string ContentDisposition()
    {
        var fallback = new StringBuilder(FileName.Length);
        foreach (var character in FileName) fallback.Append(character is >= ' ' and <= '~' and not '"' and not '\\' and not '%' ? character : '_');
        var encoded = new StringBuilder();
        foreach (var value in Encoding.UTF8.GetBytes(FileName))
        {
            if (value is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
                || "!#$&+-.^_`|~".Contains((char)value, StringComparison.Ordinal))
                encoded.Append((char)value);
            else encoded.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return (Disposition == FileDisposition.Inline ? "inline" : "attachment") + "; filename=\"" + fallback + "\"; filename*=UTF-8''" + encoded;
    }
}

public sealed record FileReadResult(FileVersionInfo Version, Stream Content, FileDelivery Delivery) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// The Files use cases for a validated tenant and the unit's principal. Every action asks SEC-05's evaluator, with the
/// file resolved in the effective tenant before resource authorization; a file the subject may not use is answered as
/// missing. Client names, sizes and bytes are validated before an intent exists; new versions stay pending, and
/// unreadable, until the deployment's release policy releases them. No HTTP endpoint is installed here.
/// </summary>
public interface IFiles
{
    Task<FileVersionInfo> UploadAsync(TenantContext tenant, ExecutionPrincipal? principal, FileUploadRequest request, Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>The files of one container that the subject may read, filtered in the query, bounded to 100 per page.</summary>
    Task<IReadOnlyList<FileSummary>> ListFilesAsync(TenantContext tenant, ExecutionPrincipal? principal, string container, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileVersionInfo>> ListAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileVersionInfo>> ListDeletedAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>A released version's verified bytes, for download; a pending or rejected version is refused as quarantined.</summary>
    Task<FileReadResult> ReadAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId, FileReadRange? range = null,
        CancellationToken cancellationToken = default);

    Task<FileVersionInfo> DeleteAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId, Guid expectedVersion,
        CancellationToken cancellationToken = default);

    Task<FileVersionInfo> RestoreAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId, Guid expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the deployment's release policy over a pending version's verified bytes and records its verdict. Without a
    /// policy, or when it defers, the version stays pending; released and rejected versions are final.
    /// </summary>
    Task<FileVersionInfo> ReleaseAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId,
        CancellationToken cancellationToken = default);

    /// <summary>Issues a download grant for a released version and one purpose, returning its token once.</summary>
    Task<IssuedFileGrant> IssueGrantAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId, FileGrantPurpose purpose,
        TimeSpan lifetime, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FileGrantInfo>> ListGrantsAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, CancellationToken cancellationToken = default);
    Task<FileGrantInfo> RevokeGrantAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid grantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers the granted version for the grant's purpose, after the principal's current authorization to read the file.
    /// An unknown, mistyped, expired, revoked, consumed or foreign grant is refused alike.
    /// </summary>
    Task<FileReadResult> RedeemGrantAsync(TenantContext tenant, ExecutionPrincipal? principal, FileGrantToken token, FileGrantPurpose purpose,
        FileReadRange? range = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// A file action refused by authorization, with the evaluator's stable code. A file the subject may not use is not
/// reported this way: it is answered as missing.
/// </summary>
public sealed class FileAccessDeniedException(string code) : Exception("File operation refused by authorization: " + code + ".")
{
    public string Code { get; } = code;
}
