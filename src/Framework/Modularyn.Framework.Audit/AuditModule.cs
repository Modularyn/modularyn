using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;

[assembly: InternalsVisibleTo("Modularyn.Framework.Audit.Tests")]

namespace Modularyn.Framework.Audit;

/// <summary>
/// The audit store's PostgreSQL module: its own schema, context and migration history through the persistence adapter,
/// and the permissions and host operations that read audit and diagnostics. Its records are append-only: the adapter
/// inserts them only through its journal append, in the transaction of the change each one records, and no API changes
/// or deletes one.
/// </summary>
public static class AuditModule
{
    public const string ModuleId = "Modularyn.Framework.Audit";
    public const string DefaultSchema = "modularyn_audit";

    /// <summary>The permission that reads a tenant's audit records. Separate from every administration permission.</summary>
    public const string ReadPermission = "modularyn.audit.read";

    /// <summary>The permission that reads a tenant's operational diagnostics, such as quarantined durable work.</summary>
    public const string DiagnosticsPermission = "modularyn.diagnostics.read";

    /// <summary>The host operation that reads every tenant's audit records and the host's own. It reaches no entity in host scope.</summary>
    public static HostOperation HostReadOperation { get; } = new("modularyn.audit.host-read", []);

    /// <summary>The host operation that reads the host's detailed diagnostics: modules, versions, states and topology. It reaches no entity.</summary>
    public static HostOperation DiagnosticsHostReadOperation { get; } = new("modularyn.diagnostics.host-read", []);

    public static IReadOnlyList<string> MigrationIds { get; } = [AuditInitial.Id];

    /// <summary>Add the result to the host's one <see cref="PostgreSqlModuleRegistry"/>, so its migrations are coordinated and gated.</summary>
    public static PostgreSqlModule Create(PostgreSqlPersistenceOptions options, string schema = DefaultSchema) =>
        PostgreSqlModule.Create(ModuleId, schema, options, module => new AuditContext(module));

    public static IReadOnlyList<PermissionDefinition> Permissions(SemanticVersion version) =>
    [
        new(ModuleId, ReadPermission, version, PermissionFreshness.Current),
        new(ModuleId, DiagnosticsPermission, version, PermissionFreshness.Current)
    ];

    public static IReadOnlyList<HostOperationDefinition> HostOperations(SemanticVersion version) =>
        [new(ModuleId, version, HostReadOperation), new(ModuleId, version, DiagnosticsHostReadOperation)];

    /// <summary>The module's manifest, declaring its permissions, host operations and migration for composition and the activation gate.</summary>
    public static ModuleManifest Manifest(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(ModuleId, version,
            definitions: Permissions(version).Select(definition => definition.Declaration)
                .Concat(HostOperations(version).Select(definition => definition.Declaration)),
            migrations: MigrationIds.Select(id => new ModuleAsset(id, version)));
    }

    internal static void Require(PostgreSqlModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.ContextType != typeof(AuditContext))
            throw new ArgumentException("Use the module that AuditModule.Create returns.", nameof(module));
    }
}

/// <summary>
/// The one place audit reads and normalizes time: the host's <see cref="TimeProvider"/>, floored to whole microseconds,
/// the precision PostgreSQL's <c>timestamptz</c> keeps, so a record's time reads back exactly as it was written.
/// </summary>
public sealed class AuditClock(TimeProvider time)
{
    public DateTimeOffset Now => UtcMicroseconds.Now(time);

    internal static DateTimeOffset Require(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.UtcTicks % TimeSpan.TicksPerMicrosecond == 0 ? value
        : throw new AuditRecordException("An audit time must be UTC and floored to whole microseconds before it is stored.");
}

/// <summary>
/// One audit record as stored: who acted and for whom, in which tenant, on what, what they did, the outcome, when, in
/// which unit and under which operation and reason, and the correlation. References and codes only, never a secret, a
/// credential, a token, a session identifier or a payload value. Host-scoped, since one store holds every tenant's records
/// and the host's own; reads select by tenant.
/// </summary>
internal sealed class AuditRecordRow : IHostScoped, IAppendOnly
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Action { get; set; } = "";
    public int Outcome { get; set; }
    public Guid? Tenant { get; set; }
    public int ActorKind { get; set; }
    public string Actor { get; set; } = "";
    public int? SubjectKind { get; set; }
    public string? Subject { get; set; }
    public string? ResourceType { get; set; }
    public string? Resource { get; set; }
    public string? Detail { get; set; }
    public Guid? Unit { get; set; }
    public string? Operation { get; set; }
    public string? Reason { get; set; }
    public string? Correlation { get; set; }
}

internal sealed class AuditContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    internal const string RecordsTable = "AuditRecords";

    internal DbSet<AuditRecordRow> Records => Set<AuditRecordRow>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditRecordRow>(entity =>
        {
            entity.ToTable(RecordsTable);
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Action).IsRequired();
            entity.Property(row => row.Actor).IsRequired();
            entity.HasIndex(row => new { row.Tenant, row.At });
            entity.HasIndex(row => row.At);
            // A stored time is floored to whole microseconds where it is read (AuditClock); any other is refused here.
            entity.Property(row => row.At).HasConversion(new ValueConverter<DateTimeOffset, DateTimeOffset>(value => AuditClock.Require(value), value => value));
        });
    }
}

[DbContext(typeof(AuditContext))]
[Migration(Id)]
internal sealed class AuditInitial : ModuleMigration
{
    public const string Id = "20261101000100_Audit";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable(AuditContext.RecordsTable, schema: Schema, columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Action = table.Column<string>(type: "text", nullable: false),
            Outcome = table.Column<int>(type: "integer", nullable: false),
            Tenant = table.Column<Guid>(type: "uuid", nullable: true),
            ActorKind = table.Column<int>(type: "integer", nullable: false),
            Actor = table.Column<string>(type: "text", nullable: false),
            SubjectKind = table.Column<int>(type: "integer", nullable: true),
            Subject = table.Column<string>(type: "text", nullable: true),
            ResourceType = table.Column<string>(type: "text", nullable: true),
            Resource = table.Column<string>(type: "text", nullable: true),
            Detail = table.Column<string>(type: "text", nullable: true),
            Unit = table.Column<Guid>(type: "uuid", nullable: true),
            Operation = table.Column<string>(type: "text", nullable: true),
            Reason = table.Column<string>(type: "text", nullable: true),
            Correlation = table.Column<string>(type: "text", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_AuditRecords", row => row.Id));
        migrationBuilder.CreateIndex("IX_AuditRecords_Tenant_At", AuditContext.RecordsTable, ["Tenant", "At"], schema: Schema);
        migrationBuilder.CreateIndex("IX_AuditRecords_At", AuditContext.RecordsTable, "At", schema: Schema);
    }
}
