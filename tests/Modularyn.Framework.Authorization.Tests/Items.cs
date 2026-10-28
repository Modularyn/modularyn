using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// The fixture module whose operations the tests authorize: items that members own, with the permissions, the host
/// operation, the feature and the setting the module declares. It stands in for a business module such as CODE-09's.
/// </summary>
internal static class Items
{
    public const string ModuleId = "Test.Items";
    public const string ResourceType = "item";
    public const string Read = "item.read";
    public const string Update = "item.update";
    public const string Delete = "item.delete";
    public const string Export = "item.export";
    public const string BulkUpdate = "item.bulk-update";
    public const string Reindex = "item.reindex";
    public const string Service = "svc-items";
    public static readonly SemanticVersion Version = SemanticVersion.Parse("1.0.0");

    /// <summary>Export and bulk update are security-sensitive: every check reads the current authority.</summary>
    public static IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(ModuleId, Read, Version), new(ModuleId, Update, Version), new(ModuleId, Delete, Version),
        new(ModuleId, Export, Version, PermissionFreshness.Current), new(ModuleId, BulkUpdate, Version, PermissionFreshness.Current),
        new(ModuleId, Reindex, Version)
    ];

    /// <summary>A host report that reaches the items table and nothing else.</summary>
    public static HostOperation Report { get; } = new("test.items.report", [new HostReach(ModuleId, "Items")]);

    /// <summary>The job dispatcher's host operation: it reads queued jobs across tenants and records a denied disposition on them.</summary>
    public static HostOperation Dispatch { get; } = new("test.items.dispatch", [new HostReach(ModuleId, "ItemJobs")]);

    public static IReadOnlyList<HostOperationDefinition> HostOperations { get; } = [new(ModuleId, Version, Report), new(ModuleId, Version, Dispatch)];

    public static FeatureDefinition ExportEnabled { get; } = new(ModuleId, "items.export-enabled", Version, false, TimeSpan.FromMinutes(5));

    public static SettingDefinition PageSize { get; } = new(ModuleId, "items.page-size", Version, ValueKind.Integer, "20", TimeSpan.FromMinutes(5),
        new ValueValidation(minimum: 1, maximum: 100));

    public static ModuleManifest Manifest(PostgreSqlModule module)
    {
        using var context = module.CreateContext();
        return new(ModuleId, Version,
            definitions: Permissions.Select(definition => definition.Declaration).Concat(HostOperations.Select(definition => definition.Declaration))
                .Append(ExportEnabled.Declaration).Append(PageSize.Declaration),
            migrations: context.GetService<IMigrationsAssembly>().Migrations.Keys.Select(id => new ModuleAsset(id, Version)));
    }

    public static ResourceReference Reference(Item item) =>
        new(item.TenantId, ResourceType, item.Code, new ActorReference(ActorKind.User, item.Owner));
}

/// <summary>A member's item. Its code is the resource identifier authorization decides on; its owner is a user subject.</summary>
public sealed class Item : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Title { get; set; } = "";
    public int Revision { get; set; }
    public Guid Version { get; set; }
}

/// <summary>A long-running operation's record: its state, the batches it committed, and why it stopped.</summary>
public sealed class ItemOperation : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public int Batches { get; set; }
    public string Disposition { get; set; } = "";
    public Guid Version { get; set; }
}

/// <summary>Queued work for the item job, written in its producer's tenant unit with the propagated context beside it.</summary>
public sealed class ItemJob : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid Operation { get; set; }
    public string Propagated { get; set; } = "";
    public string State { get; set; } = "queued";
    public string Disposition { get; set; } = "";
    public Guid Version { get; set; }
}

/// <summary>A finished export, the only form in which an export's content can be downloaded.</summary>
public sealed class ItemExport : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Content { get; set; } = "";
}

public sealed class ItemsContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemOperation> Operations => Set<ItemOperation>();
    public DbSet<ItemJob> Jobs => Set<ItemJob>();
    public DbSet<ItemExport> Exports => Set<ItemExport>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(row => new { row.TenantId, row.Code });
            entity.Property(row => row.Owner).IsRequired();
            entity.Property(row => row.Title).IsRequired();
        });
        modelBuilder.Entity<ItemOperation>(entity =>
        {
            entity.ToTable("ItemOperations");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Kind).IsRequired();
            entity.Property(row => row.State).IsRequired();
            entity.Property(row => row.Disposition).IsRequired();
        });
        modelBuilder.Entity<ItemJob>(entity =>
        {
            entity.ToTable("ItemJobs");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Propagated).IsRequired();
            entity.Property(row => row.State).IsRequired();
            entity.Property(row => row.Disposition).IsRequired();
        });
        modelBuilder.Entity<ItemExport>(entity =>
        {
            entity.ToTable("ItemExports");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Content).IsRequired();
        });
    }
}

[DbContext(typeof(ItemsContext))]
[Migration(Id)]
public sealed class ItemsInitial : ModuleMigration
{
    public const string Id = "20261028000200_TestItems";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable("Items", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Code = table.Column<string>(type: "text", nullable: false),
            Owner = table.Column<string>(type: "text", nullable: false),
            Title = table.Column<string>(type: "text", nullable: false),
            Revision = table.Column<int>(type: "integer", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Items", row => new { row.TenantId, row.Code }));
        migrationBuilder.CreateTable("ItemOperations", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Kind = table.Column<string>(type: "text", nullable: false),
            State = table.Column<string>(type: "text", nullable: false),
            Batches = table.Column<int>(type: "integer", nullable: false),
            Disposition = table.Column<string>(type: "text", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_ItemOperations", row => new { row.TenantId, row.Id }));
        migrationBuilder.CreateTable("ItemJobs", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Operation = table.Column<Guid>(type: "uuid", nullable: false),
            Propagated = table.Column<string>(type: "text", nullable: false),
            State = table.Column<string>(type: "text", nullable: false),
            Disposition = table.Column<string>(type: "text", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_ItemJobs", row => new { row.TenantId, row.Id }));
        migrationBuilder.CreateTable("ItemExports", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Content = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_ItemExports", row => new { row.TenantId, row.Id }));
    }
}
