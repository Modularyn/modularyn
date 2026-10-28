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

[assembly: InternalsVisibleTo("Modularyn.Framework.Authorization.Tests")]

namespace Modularyn.Framework.Authorization;

/// <summary>
/// The authorization module's PostgreSQL module: its own schema, context and migration history through the persistence
/// adapter, and the permissions and host operations it declares itself.
/// </summary>
public static class AuthorizationModule
{
    public const string ModuleId = "Modularyn.Framework.Authorization";
    public const string DefaultSchema = "modularyn_authorization";

    /// <summary>The permission that administers permissions, roles and grants within a tenant, up to the actor's assignment ceiling.</summary>
    public const string ManagePermission = "modularyn.authorization.manage";

    /// <summary>The permission an owner of a resource needs to share it with another member.</summary>
    public const string SharePermission = "modularyn.authorization.share";

    /// <summary>The host operation that grants and revokes host authority, to operations the granting actor holds itself.</summary>
    public static HostOperation HostAuthorityOperation { get; } = new("modularyn.authorization.host-authority",
        [new HostReach(ModuleId, AuthorizationContext.HostAuthorityGrantsTable)]);

    /// <summary>The host operation that names a tenant's owners, the root of authority within that tenant.</summary>
    public static HostOperation TenantOwnersOperation { get; } = new("modularyn.authorization.tenant-owners",
        [new HostReach(ModuleId, AuthorizationContext.TenantOwnersTable)]);

    /// <summary>The version of the cached authority's shape; a change to it makes earlier entries unreachable.</summary>
    internal const string CacheSchemaVersion = "1";

    public static IReadOnlyList<string> MigrationIds { get; } = [AuthorizationInitial.Id];

    /// <summary>Add the result to the host's one <see cref="PostgreSqlModuleRegistry"/>, so its migrations are coordinated and gated.</summary>
    public static PostgreSqlModule Create(PostgreSqlPersistenceOptions options, string schema = DefaultSchema) =>
        PostgreSqlModule.Create(ModuleId, schema, options, module => new AuthorizationContext(module));

    public static IReadOnlyList<PermissionDefinition> Permissions(SemanticVersion version) =>
    [
        new(ModuleId, ManagePermission, version, PermissionFreshness.Current),
        new(ModuleId, SharePermission, version, PermissionFreshness.Current)
    ];

    public static IReadOnlyList<HostOperationDefinition> HostOperations(SemanticVersion version) =>
        [new(ModuleId, version, HostAuthorityOperation), new(ModuleId, version, TenantOwnersOperation)];

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
        if (module.ContextType != typeof(AuthorizationContext))
            throw new ArgumentException("Use the module that AuthorizationModule.Create returns.", nameof(module));
    }
}

/// <summary>
/// The one place authorization reads and normalizes time: the host's <see cref="TimeProvider"/>, floored to whole
/// microseconds, the precision PostgreSQL's <c>timestamptz</c> keeps. A time held more finely in memory would come back
/// from the database up to 0.9 microseconds earlier, and an expiry decided before the round trip could flip after it.
/// </summary>
public sealed class AuthorizationClock(TimeProvider time)
{
    public DateTimeOffset Now => Floor(time.GetUtcNow());

    /// <summary>The time at or before <paramref name="value"/> that is whole microseconds, in UTC.</summary>
    public static DateTimeOffset Floor(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    internal static DateTimeOffset Require(DateTimeOffset value) => value.UtcTicks % TimeSpan.TicksPerMicrosecond == 0 ? value
        : throw new InvalidOperationException("An authorization time must be floored to whole microseconds before it is stored or compared.");
}

/// <summary>The serialization point and version of one tenant's authority: every tenant-level change locks and rotates it.</summary>
internal sealed class TenantAuthorityRow : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public Guid Version { get; set; }
}

/// <summary>A tenant's role: the permissions its members hold and the assignment ceiling within which they may grant.</summary>
internal sealed class RoleRow : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Permissions { get; set; } = "";
    public string Ceiling { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid Version { get; set; }
}

internal sealed class RoleMemberRow : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string Role { get; set; } = "";
    public int MemberKind { get; set; }
    public string Member { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
    public int AddedByKind { get; set; }
    public string AddedBy { get; set; } = "";
}

internal sealed class PermissionGrantRow : ITenantScoped
{
    public Guid TenantId { get; set; }
    public int MemberKind { get; set; }
    public string Member { get; set; } = "";
    public string Permission { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
    public int GrantedByKind { get; set; }
    public string GrantedBy { get; set; } = "";
}

internal sealed class ResourceGrantRow : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int MemberKind { get; set; }
    public string Member { get; set; } = "";
    public string Permission { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public int GrantedByKind { get; set; }
    public string GrantedBy { get; set; } = "";
}

/// <summary>
/// A tenant's owner, who holds every declared permission in the tenant with an unbounded ceiling. Host-scoped: it is named
/// only in an audited host scope, so authority within a tenant originates from separately granted host authority.
/// </summary>
internal sealed class TenantOwnerRow : IHostScoped
{
    public Guid TenantId { get; set; }
    public int OwnerKind { get; set; }
    public string Owner { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
    public int AddedByKind { get; set; }
    public string AddedBy { get; set; } = "";
}

/// <summary>A stored host-authority grant: one actor may establish host scope for one declared operation.</summary>
internal sealed class HostAuthorityGrantRow : IHostScoped
{
    public int ActorKind { get; set; }
    public string Actor { get; set; } = "";
    public string OperationId { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
    public int GrantedByKind { get; set; }
    public string GrantedBy { get; set; } = "";
}

internal sealed class AuthorizationContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    internal const string TenantAuthorityTable = "TenantAuthority";
    internal const string RolesTable = "Roles";
    internal const string RoleMembersTable = "RoleMembers";
    internal const string PermissionGrantsTable = "PermissionGrants";
    internal const string ResourceGrantsTable = "ResourceGrants";
    internal const string TenantOwnersTable = "TenantOwners";
    internal const string HostAuthorityGrantsTable = "HostAuthorityGrants";

    internal DbSet<TenantAuthorityRow> TenantAuthority => Set<TenantAuthorityRow>();
    internal DbSet<RoleRow> Roles => Set<RoleRow>();
    internal DbSet<RoleMemberRow> RoleMembers => Set<RoleMemberRow>();
    internal DbSet<PermissionGrantRow> PermissionGrants => Set<PermissionGrantRow>();
    internal DbSet<ResourceGrantRow> ResourceGrants => Set<ResourceGrantRow>();
    internal DbSet<TenantOwnerRow> TenantOwners => Set<TenantOwnerRow>();
    internal DbSet<HostAuthorityGrantRow> HostAuthorityGrants => Set<HostAuthorityGrantRow>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantAuthorityRow>(entity =>
        {
            entity.ToTable(TenantAuthorityTable);
            entity.HasKey(row => row.TenantId);
        });
        modelBuilder.Entity<RoleRow>(entity =>
        {
            entity.ToTable(RolesTable);
            entity.HasKey(row => new { row.TenantId, row.Name });
            entity.Property(row => row.Name).IsRequired();
            entity.Property(row => row.Permissions).IsRequired();
            entity.Property(row => row.Ceiling).IsRequired();
        });
        modelBuilder.Entity<RoleMemberRow>(entity =>
        {
            entity.ToTable(RoleMembersTable);
            entity.HasKey(row => new { row.TenantId, row.Role, row.MemberKind, row.Member });
            entity.HasIndex(row => new { row.TenantId, row.MemberKind, row.Member });
            entity.HasOne<RoleRow>().WithMany().HasForeignKey(row => new { row.TenantId, row.Role }).OnDelete(DeleteBehavior.Cascade);
            entity.Property(row => row.AddedBy).IsRequired();
        });
        modelBuilder.Entity<PermissionGrantRow>(entity =>
        {
            entity.ToTable(PermissionGrantsTable);
            entity.HasKey(row => new { row.TenantId, row.MemberKind, row.Member, row.Permission });
            entity.Property(row => row.GrantedBy).IsRequired();
        });
        modelBuilder.Entity<ResourceGrantRow>(entity =>
        {
            entity.ToTable(ResourceGrantsTable);
            entity.HasKey(row => new { row.TenantId, row.ResourceType, row.ResourceId, row.MemberKind, row.Member, row.Permission });
            entity.HasIndex(row => new { row.TenantId, row.MemberKind, row.Member, row.ResourceType, row.Permission });
            entity.Property(row => row.GrantedBy).IsRequired();
        });
        modelBuilder.Entity<TenantOwnerRow>(entity =>
        {
            entity.ToTable(TenantOwnersTable);
            entity.HasKey(row => new { row.TenantId, row.OwnerKind, row.Owner });
            entity.Property(row => row.AddedBy).IsRequired();
        });
        modelBuilder.Entity<HostAuthorityGrantRow>(entity =>
        {
            entity.ToTable(HostAuthorityGrantsTable);
            entity.HasKey(row => new { row.ActorKind, row.Actor, row.OperationId });
            entity.Property(row => row.GrantedBy).IsRequired();
        });

        // Every stored time is floored to whole microseconds where it is read (AuthorizationClock); any other is refused
        // here, written or compared, rather than silently truncated by the database.
        var precision = new ValueConverter<DateTimeOffset, DateTimeOffset>(value => AuthorizationClock.Require(value), value => value);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)).ToArray())
            property.SetValueConverter(precision);
    }
}

[DbContext(typeof(AuthorizationContext))]
[Migration(Id)]
internal sealed class AuthorizationInitial : ModuleMigration
{
    public const string Id = "20261028000100_Authorization";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable(AuthorizationContext.TenantAuthorityTable, schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_TenantAuthority", row => row.TenantId));
        migrationBuilder.CreateTable(AuthorizationContext.RolesTable, schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            Permissions = table.Column<string>(type: "text", nullable: false),
            Ceiling = table.Column<string>(type: "text", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Roles", row => new { row.TenantId, row.Name }));
        migrationBuilder.CreateTable(AuthorizationContext.RoleMembersTable, schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Role = table.Column<string>(type: "text", nullable: false),
            MemberKind = table.Column<int>(type: "integer", nullable: false),
            Member = table.Column<string>(type: "text", nullable: false),
            AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            AddedByKind = table.Column<int>(type: "integer", nullable: false),
            AddedBy = table.Column<string>(type: "text", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_RoleMembers", row => new { row.TenantId, row.Role, row.MemberKind, row.Member });
            table.ForeignKey("FK_RoleMembers_Roles_TenantId_Role", row => new { row.TenantId, row.Role }, principalSchema: Schema,
                principalTable: AuthorizationContext.RolesTable, principalColumns: ["TenantId", "Name"], onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_RoleMembers_TenantId_MemberKind_Member", AuthorizationContext.RoleMembersTable,
            ["TenantId", "MemberKind", "Member"], schema: Schema);
        migrationBuilder.CreateTable(AuthorizationContext.PermissionGrantsTable, schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            MemberKind = table.Column<int>(type: "integer", nullable: false),
            Member = table.Column<string>(type: "text", nullable: false),
            Permission = table.Column<string>(type: "text", nullable: false),
            GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            GrantedByKind = table.Column<int>(type: "integer", nullable: false),
            GrantedBy = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_PermissionGrants", row => new { row.TenantId, row.MemberKind, row.Member, row.Permission }));
        migrationBuilder.CreateTable(AuthorizationContext.ResourceGrantsTable, schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            ResourceType = table.Column<string>(type: "text", nullable: false),
            ResourceId = table.Column<string>(type: "text", nullable: false),
            MemberKind = table.Column<int>(type: "integer", nullable: false),
            Member = table.Column<string>(type: "text", nullable: false),
            Permission = table.Column<string>(type: "text", nullable: false),
            ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            GrantedByKind = table.Column<int>(type: "integer", nullable: false),
            GrantedBy = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_ResourceGrants",
            row => new { row.TenantId, row.ResourceType, row.ResourceId, row.MemberKind, row.Member, row.Permission }));
        migrationBuilder.CreateIndex("IX_ResourceGrants_TenantId_MemberKind_Member_ResourceType_Permission", AuthorizationContext.ResourceGrantsTable,
            ["TenantId", "MemberKind", "Member", "ResourceType", "Permission"], schema: Schema);
        migrationBuilder.CreateTable(AuthorizationContext.TenantOwnersTable, schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            OwnerKind = table.Column<int>(type: "integer", nullable: false),
            Owner = table.Column<string>(type: "text", nullable: false),
            AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            AddedByKind = table.Column<int>(type: "integer", nullable: false),
            AddedBy = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_TenantOwners", row => new { row.TenantId, row.OwnerKind, row.Owner }));
        migrationBuilder.CreateTable(AuthorizationContext.HostAuthorityGrantsTable, schema: Schema, columns: table => new
        {
            ActorKind = table.Column<int>(type: "integer", nullable: false),
            Actor = table.Column<string>(type: "text", nullable: false),
            OperationId = table.Column<string>(type: "text", nullable: false),
            GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            GrantedByKind = table.Column<int>(type: "integer", nullable: false),
            GrantedBy = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_HostAuthorityGrants", row => new { row.ActorKind, row.Actor, row.OperationId }));
    }
}
