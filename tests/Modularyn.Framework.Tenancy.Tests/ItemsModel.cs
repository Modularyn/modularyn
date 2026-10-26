using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Tenancy.Tests;

/// <summary>A tenant business record with a per-tenant unique business key, soft delete and a version token.</summary>
public sealed class Item : ITenantScoped, ISoftDeleted, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDeleted { get; set; }
    public Guid Version { get; set; }
}

/// <summary>A tenant record that references an item through the tenant and the item's identity.</summary>
public sealed class ItemNote : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Host data shared by every tenant.</summary>
public sealed class Lookup : IHostScoped
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Queued propagated work as a test fixture: written in the producing tenant's unit, with the propagated context beside
/// the payload. CODE-05 owns the real outbox and job store.
/// </summary>
public sealed class WorkItem : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string Propagated { get; set; } = "";
    public string Payload { get; set; } = "";
    public string State { get; set; } = "queued";
    public int Attempts { get; set; }
    public Guid Version { get; set; }
}

public sealed class ItemsContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemNote> Notes => Set<ItemNote>();
    public DbSet<Lookup> Lookups => Set<Lookup>();
    public DbSet<WorkItem> Work => Set<WorkItem>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Code).IsRequired();
            entity.Property(row => row.Name).IsRequired();
            entity.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
        });
        modelBuilder.Entity<ItemNote>(entity =>
        {
            entity.ToTable("ItemNotes");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Text).IsRequired();
            entity.HasOne<Item>().WithMany().HasForeignKey(row => new { row.TenantId, row.ItemId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Lookup>(entity =>
        {
            entity.ToTable("Lookups");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Name).IsRequired();
        });
        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.ToTable("WorkItems");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Kind).IsRequired();
            entity.Property(row => row.Propagated).IsRequired();
            entity.Property(row => row.Payload).IsRequired();
            entity.Property(row => row.State).IsRequired();
            entity.HasIndex(row => new { row.TenantId, row.State });
        });
    }
}

[DbContext(typeof(ItemsContext))]
[Migration(Id)]
public sealed class ItemsInitial : ModuleMigration
{
    public const string Id = "20261026000100_Items";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable("Items", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Code = table.Column<string>(type: "text", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Items", row => new { row.TenantId, row.Id }));
        migrationBuilder.CreateIndex("IX_Items_TenantId_Code", "Items", ["TenantId", "Code"], schema: Schema, unique: true);
        migrationBuilder.CreateTable("ItemNotes", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            ItemId = table.Column<Guid>(type: "uuid", nullable: false),
            Text = table.Column<string>(type: "text", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ItemNotes", row => new { row.TenantId, row.Id });
            table.ForeignKey("FK_ItemNotes_Items", row => new { row.TenantId, row.ItemId }, principalSchema: Schema, principalTable: "Items",
                principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_ItemNotes_TenantId_ItemId", "ItemNotes", ["TenantId", "ItemId"], schema: Schema);
        migrationBuilder.CreateTable("Lookups", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: "text", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Lookups", row => row.Id));
        migrationBuilder.InsertData("Lookups", ["Id", "Name"], ["text", "text"], new object[] { "core", "shared" }, schema: Schema);
        migrationBuilder.CreateTable("WorkItems", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Kind = table.Column<string>(type: "text", nullable: false),
            Propagated = table.Column<string>(type: "text", nullable: false),
            Payload = table.Column<string>(type: "text", nullable: false),
            State = table.Column<string>(type: "text", nullable: false),
            Attempts = table.Column<int>(type: "integer", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_WorkItems", row => new { row.TenantId, row.Id }));
        migrationBuilder.CreateIndex("IX_WorkItems_TenantId_State", "WorkItems", ["TenantId", "State"], schema: Schema);
    }
}
