using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

public sealed class SampleRow : ISoftDeleted, IConcurrencyTracked, IHostScoped
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDeleted { get; set; }
    public Guid Version { get; set; }
    public string? Note { get; set; }
}

public sealed class SystemSeed : IHostScoped
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class SampleContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    public DbSet<SampleRow> Items => Set<SampleRow>();
    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SampleRow>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Name).IsRequired();
        });
        modelBuilder.Entity<SystemSeed>(entity =>
        {
            entity.ToTable("SystemSeeds");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Name).IsRequired();
        });
    }

    protected override void ConfigureFilters(IQueryFilterRegistry filters) =>
        filters.Add<SampleRow>("Sample.NameVisibility", row => row.Name != "excluded");
}

[DbContext(typeof(SampleContext))]
[Migration(SampleInitial.Id)]
public sealed class SampleInitial : ModuleMigration
{
    public const string Id = "20261026000100_Initial";
    public static Guid InitialVersion { get; } = Guid.Parse("10000000-0000-0000-0000-000000000005");

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable("Items", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: "text", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Items", row => row.Id));
        migrationBuilder.CreateTable("SystemSeeds", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: "text", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_SystemSeeds", row => row.Id));
        migrationBuilder.InsertData("Items", columns: ["Id", "Name", "IsDeleted", "Version"],
            columnTypes: ["text", "text", "boolean", "uuid"], values: new object[,]
            {
                { "item-1", "first", false, InitialVersion },
                { "item-2", "second", false, InitialVersion },
                { "item-3", "deleted", true, InitialVersion }
            }, schema: Schema);
    }
}

[DbContext(typeof(SampleContext))]
[Migration(SampleAddNoteAndSeed.Id)]
public sealed class SampleAddNoteAndSeed : ModuleMigration
{
    public const string Id = "20261026000200_AddNoteAndSeed";
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Note", "Items", type: "text", schema: Schema, nullable: true);
        migrationBuilder.InsertData("SystemSeeds", columns: ["Id", "Name"], columnTypes: ["text", "text"],
            values: new object[] { "core", "required system data" }, schema: Schema);
    }
}
