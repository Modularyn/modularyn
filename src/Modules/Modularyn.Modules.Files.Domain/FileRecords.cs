using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Files.Domain;

public enum QuarantineState { Pending = 1, Released = 2, Rejected = 3 }

/// <summary>A durable logical identity. Names, containers and owners are metadata, never storage paths.</summary>
public class FileAsset : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Container { get; set; } = "";
    public string Owner { get; set; } = "";

    /// <summary>The actor that created the file, derived on the server from the uploading principal: the resource's owner.</summary>
    public ActorKind CreatedByKind { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid Version { get; set; }
}

/// <summary>Immutable content metadata; deletion and quarantine are separate, versioned state.</summary>
public class FileRevision : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";

    /// <summary>The type detected from the content's own bytes, never the client's claim.</summary>
    public string ContentType { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public QuarantineState Quarantine { get; set; } = QuarantineState.Pending;
    public bool IsDeleted { get; set; }
    public Guid Version { get; set; }
}

/// <summary>Committed before any bytes are written; removed in the transaction that publishes the revision.</summary>
public class FileUpload : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
    public string ContentType { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string PropagatedContext { get; set; } = "";
    public Guid Version { get; set; }
}

public enum GrantPurpose { Download = 1, Preview = 2, SingleDownload = 3 }

/// <summary>
/// A download grant for one file version and purpose until its expiry. Only the verifier of its token is stored; the token
/// itself is returned once, to its issuer. Revocation and single use are recorded and never undone.
/// </summary>
public class FileGrant : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public Guid VersionId { get; set; }
    public GrantPurpose Purpose { get; set; }
    public byte[] Verifier { get; set; } = [];
    public ActorKind CreatedByKind { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid Version { get; set; }
}

/// <summary>The single clock boundary for values persisted in PostgreSQL.</summary>
public static class FileTime
{
    public static DateTimeOffset UtcNow(TimeProvider time) => Normalize(time.GetUtcNow());

    public static DateTimeOffset Normalize(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
}

public static class FileTenant
{
    public static Guid Require(TenantContext context) => context is { IsActive: true, Scope: TenantScope.Tenant, TenantId: { } id }
        && id != Guid.Empty ? id : throw new FileStorageException(FileStorageError.InvalidScope);
}

public enum FileStorageError
{
    InvalidScope, NotFound, Conflict, IntegrityMismatch, InvalidLength, InvalidRange,
    UnsafeLocation, StorageUnavailable, Busy,
    InvalidName, UnsupportedContent, QuotaExceeded, Quarantined, GrantRefused, LockingUnavailable
}

/// <summary>Stable storage diagnostics without client values, machine locations or provider exception text.</summary>
public sealed class FileStorageException(FileStorageError code) : Exception("File storage operation refused: " + code + ".")
{
    public FileStorageError Code { get; } = code;
}
