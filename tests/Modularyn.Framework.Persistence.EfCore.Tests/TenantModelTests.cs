using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

// TM-G02-029 and TH-G02-038 at model and migration time: every root entity declares its scope, and a tenant-scoped
// table carries its tenant in every key, unique constraint, index and reference, so no uniqueness rule spans tenants.
public sealed class TenantModelTests
{
    private static PostgreSqlPersistenceOptions Options => new("Host=localhost;Database=modularyn_contract");

    private static PostgreSqlModule Module(Action<ModelBuilder> configure, Action<IQueryFilterRegistry>? filters = null) =>
        PostgreSqlModule.Create("Probe", "probe_module", Options, module => new ProbeContext(module, configure, filters));

    private static string Refusal(Action<ModelBuilder> configure, Action<IQueryFilterRegistry>? filters = null)
    {
        using var context = Module(configure, filters).CreateContext();
        return Assert.Throws<PersistenceContractException>(() => context.Model).Code;
    }

    [Fact]
    public void TM_G02_029_Every_root_entity_declares_exactly_one_scope()
    {
        Assert.Equal("MPS030", Refusal(builder => builder.Entity<Undeclared>().HasKey(row => row.Id)));
        Assert.Equal("MPS030", Refusal(builder => builder.Entity<DoublyDeclared>().HasKey(row => new { row.TenantId, row.Id })));
        Assert.Equal("MPS030", Refusal(builder => builder.Entity<OwnerItem>(entity =>
        {
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.OwnsOne(row => row.Detail);
        })));
        Assert.Equal("MPS031", Refusal(builder => builder.Entity<HiddenTenant>().HasKey(row => row.Id)));
    }

    [Fact]
    public void TM_G02_029_Keys_indexes_and_references_of_tenant_scoped_data_carry_the_tenant()
    {
        Assert.Equal("MPS032", Refusal(builder => builder.Entity<TenantItem>().HasKey(row => row.Id)));
        Assert.Equal("MPS032", Refusal(builder => builder.Entity<TenantItem>(entity =>
        {
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => row.Code).IsUnique();
        })));
        Assert.Equal("MPS032", Refusal(builder => builder.Entity<TenantItem>(entity =>
        {
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasAlternateKey(row => row.Code);
        })));
        // A reference that does not share the tenant could point at another tenant's row.
        Assert.Equal("MPS032", Refusal(builder =>
        {
            builder.Entity<TenantItem>(entity =>
            {
                entity.HasKey(row => new { row.TenantId, row.Id });
                entity.HasAlternateKey(row => new { row.TenantId, row.Code });
            });
            builder.Entity<TenantNote>(entity =>
            {
                entity.HasKey(row => new { row.TenantId, row.Id });
                entity.HasOne<TenantItem>().WithMany().HasForeignKey(row => new { row.OtherTenantId, row.ItemCode })
                    .HasPrincipalKey(row => new { row.TenantId, row.Code });
            });
        }));
        Assert.Equal("MPS032", Refusal(builder =>
        {
            builder.Entity<TenantItem>().HasKey(row => new { row.TenantId, row.Id });
            builder.Entity<HostLink>(entity =>
            {
                entity.HasKey(row => row.Id);
                entity.HasOne<TenantItem>().WithMany().HasForeignKey(row => new { row.ItemTenantId, row.ItemId });
            });
        }));

        using var valid = Module(ValidModel).CreateContext();
        var item = valid.Model.FindEntityType(typeof(TenantItem))!;
        Assert.Equal([nameof(TenantItem.TenantId), nameof(TenantItem.Id)], item.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(3, item.GetDeclaredQueryFilters().Count);
    }

    [Fact]
    public void TM_G02_029_Adapter_filter_names_are_reserved()
    {
        Assert.Equal("MPS012", Refusal(ValidModel, filters => filters.Add<TenantItem>("Modularyn.Tenant", row => true)));
        Assert.Equal("MPS012", Refusal(ValidModel, filters => filters.Add<TenantItem>("Modularyn.Other", row => true)));
    }

    [Fact]
    public void TM_G02_029_Tenant_scoped_data_without_a_tenant_context_is_refused_rather_than_unfiltered()
    {
        using var context = Module(ValidModel).CreateContext();
        Assert.Null(((ModuleDbContext)context).Tenant);
        Assert.Equal("MPS035", Assert.Throws<PersistenceContractException>(() => context.Set<TenantItem>().ToQueryString()).Code);
        Assert.Equal("MPS035", Assert.Throws<PersistenceContractException>(() => context.Set<TenantNote>().Where(row => row.Text != "").ToQueryString()).Code);
        Assert.Equal("MPS038", Assert.Throws<PersistenceContractException>(() => ((ModuleDbContext)context).AcrossTenants<TenantItem>()).Code);
        // Host-scoped data needs no tenant context.
        Assert.Contains("FROM probe_module.\"HostLookups\"", context.Set<HostLookup>().ToQueryString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TM_G02_029_Migrations_keep_the_tenant_in_tenant_tables_and_cannot_seed_tenant_rows()
    {
        using var context = Module(ValidModel).CreateContext();
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.Contains(TenantModelInitial.Id, assembly.Migrations.Keys);
        Assert.NotNull(assembly.CreateMigration(typeof(TenantModelInitial).GetTypeInfo(), context.Database.ProviderName!));
        foreach (var (type, code) in new[]
        {
            (typeof(SeedsTenantRows), "MPS039"), (typeof(UpdatesTenantRows), "MPS039"), (typeof(DeletesTenantRows), "MPS039"),
            (typeof(IndexWithoutTenant), "MPS032"), (typeof(UniqueWithoutTenant), "MPS032"), (typeof(KeyWithoutTenant), "MPS032"),
            (typeof(ReferenceWithoutTenant), "MPS032"), (typeof(TableKeyWithoutTenant), "MPS032")
        })
            Assert.Equal(code, Assert.Throws<PersistenceContractException>(() => assembly.CreateMigration(type.GetTypeInfo(), context.Database.ProviderName!)).Code);
    }

    [Fact]
    public void TM_G02_030_Bulk_writes_cannot_delete_soft_deleted_rows_or_skip_a_version_token()
    {
        // The guard decides when the query compiles, before any parameter is evaluated or connection opened.
        using var context = Module(ValidModel).CreateContext();
        Assert.Equal("MPS041", Assert.Throws<PersistenceContractException>(() => context.Set<TenantItem>().ExecuteDelete()).Code);
        Assert.Equal("MPS041", Assert.Throws<PersistenceContractException>(() => context.Set<TenantItem>().Where(row => row.Code == "x").ExecuteDelete()).Code);
        Assert.Equal("MPS010", Assert.Throws<PersistenceContractException>(() => context.Set<TenantItem>()
            .ExecuteUpdate(setters => setters.SetProperty(row => row.Code, "bulk"))).Code);
        Assert.Equal("MPS010", Assert.Throws<PersistenceContractException>(() => context.Set<TenantItem>()
            .ExecuteUpdate(setters => setters.SetProperty(row => row.IsDeleted, true))).Code);
        // A type that is neither soft-deleted nor version-tracked passes the guard and meets the tenant filter instead.
        Assert.Equal("MPS035", Assert.Throws<PersistenceContractException>(() => context.Set<TenantNote>().ExecuteDelete()).Code);
        Assert.Equal("MPS035", Assert.Throws<PersistenceContractException>(() => context.Set<TenantNote>()
            .ExecuteUpdate(setters => setters.SetProperty(row => row.Text, "bulk"))).Code);
    }

    internal static void ValidModel(ModelBuilder builder)
    {
        builder.Entity<TenantItem>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.Code }).IsUnique();
        });
        builder.Entity<TenantNote>(entity =>
        {
            entity.ToTable("Notes");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasOne<TenantItem>().WithMany().HasForeignKey(row => new { row.TenantId, row.ItemId });
        });
        builder.Entity<HostLookup>(entity =>
        {
            entity.ToTable("HostLookups");
            entity.HasKey(row => row.Id);
        });
    }
}

public sealed class Undeclared
{
    public string Id { get; set; } = "";
}

public sealed class DoublyDeclared : ITenantScoped, IHostScoped
{
    public Guid TenantId { get; set; }
    public string Id { get; set; } = "";
}

public sealed class HiddenTenant : ITenantScoped
{
    public string Id { get; set; } = "";
    Guid ITenantScoped.TenantId { get; set; }
}

public sealed class TenantItem : ITenantScoped, ISoftDeleted, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public bool IsDeleted { get; set; }
    public Guid Version { get; set; }
}

public sealed class OwnerItem : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string Id { get; set; } = "";
    public ItemDetail Detail { get; set; } = new();
}

/// <summary>Declares a scope although it is owned, which the adapter refuses.</summary>
public sealed class ItemDetail : IHostScoped
{
    public string Text { get; set; } = "";
}

public sealed class TenantNote : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string Id { get; set; } = "";
    public string ItemId { get; set; } = "";
    public Guid OtherTenantId { get; set; }
    public string ItemCode { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class HostLink : IHostScoped
{
    public string Id { get; set; } = "";
    public Guid ItemTenantId { get; set; }
    public string ItemId { get; set; } = "";
}

public sealed class HostLookup : IHostScoped
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ProbeContext(PostgreSqlModule module, Action<ModelBuilder> configure, Action<IQueryFilterRegistry>? filters) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder) => configure(modelBuilder);

    protected override void ConfigureFilters(IQueryFilterRegistry registry) => filters?.Invoke(registry);
}

[DbContext(typeof(ProbeContext))]
[Migration(Id)]
public sealed class TenantModelInitial : ModuleMigration
{
    public const string Id = "20261026000100_TenantModel";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Items", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<string>(type: "text", nullable: false),
            Code = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Items", row => new { row.TenantId, row.Id }));
        migrationBuilder.CreateIndex("IX_Items_TenantId_Code", "Items", ["TenantId", "Code"], schema: Schema, unique: true);
        migrationBuilder.CreateTable("Notes", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<string>(type: "text", nullable: false),
            ItemId = table.Column<string>(type: "text", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Notes", row => new { row.TenantId, row.Id });
            table.ForeignKey("FK_Notes_Items", row => new { row.TenantId, row.ItemId }, principalSchema: Schema, principalTable: "Items",
                principalColumns: ["TenantId", "Id"]);
        });
        migrationBuilder.InsertData("HostLookups", ["Id", "Name"], ["text", "text"], new object[] { "core", "shared" }, schema: Schema);
    }
}

[DbContext(typeof(ProbeContext))]
[Migration("20261026000201_SeedsTenantRows")]
public sealed class SeedsTenantRows : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.InsertData("Items", ["TenantId", "Id", "Code"],
        ["uuid", "text", "text"], new object[] { Guid.Parse("10000000-0000-0000-0000-00000000000a"), "seeded", "code-1" }, schema: Schema);
}

[DbContext(typeof(ProbeContext))]
[Migration("20261026000202_UpdatesTenantRows")]
public sealed class UpdatesTenantRows : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.UpdateData("Items", ["TenantId", "Id"],
        ["uuid", "text"], new object[] { Guid.Parse("10000000-0000-0000-0000-00000000000a"), "seeded" }, ["Code"], ["text"], new object[] { "changed" }, schema: Schema);
}

[DbContext(typeof(ProbeContext))]
[Migration("20261026000203_DeletesTenantRows")]
public sealed class DeletesTenantRows : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.DeleteData("Items", ["TenantId", "Id"],
        ["uuid", "text"], new object[] { Guid.Parse("10000000-0000-0000-0000-00000000000a"), "seeded" }, schema: Schema);
}

[DbContext(typeof(ProbeContext))]
[Migration("20261026000204_IndexWithoutTenant")]
public sealed class IndexWithoutTenant : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex("IX_Items_Code", "Items", "Code", schema: Schema);
}

[DbContext(typeof(ProbeContext))]
[Migration("20261026000205_UniqueWithoutTenant")]
public sealed class UniqueWithoutTenant : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddUniqueConstraint("AK_Items_Code", "Items", "Code", schema: Schema);
}

[DbContext(typeof(ProbeContext))]
[Migration("20261026000206_KeyWithoutTenant")]
public sealed class KeyWithoutTenant : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddPrimaryKey("PK_Notes_Id", "Notes", "Id", schema: Schema);
}

[DbContext(typeof(ProbeContext))]
[Migration("20261026000207_ReferenceWithoutTenant")]
public sealed class ReferenceWithoutTenant : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddForeignKey("FK_Notes_Items_Id", "Notes", "ItemId",
        "Items", schema: Schema, principalSchema: Schema, principalColumn: "Id");
}

[DbContext(typeof(ProbeContext))]
[Migration("20261026000208_TableKeyWithoutTenant")]
public sealed class TableKeyWithoutTenant : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable("Items", schema: Schema, columns: table => new
    {
        TenantId = table.Column<Guid>(type: "uuid", nullable: false),
        Id = table.Column<string>(type: "text", nullable: false)
    }, constraints: table => table.PrimaryKey("PK_Items", row => row.Id));
}
