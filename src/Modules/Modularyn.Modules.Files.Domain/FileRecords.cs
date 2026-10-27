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
    public DateTimeOffset CreatedAt { get; set; }
    public string PropagatedContext { get; set; } = "";
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
    UnsafeLocation, StorageUnavailable, Busy
}

/// <summary>Stable storage diagnostics without client values, machine locations or provider exception text.</summary>
public sealed class FileStorageException(FileStorageError code) : Exception("File storage operation refused: " + code + ".")
{
    public FileStorageError Code { get; } = code;
}
