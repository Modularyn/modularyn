using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Api;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Sdk.Client.Tests;

internal static class Items
{
    public const string ModuleId = "Test.Items";
    public const string Read = "item.read";
    public const string Update = "item.update";
    public static readonly SemanticVersion Version = SemanticVersion.Parse("1.0.0");
    public static IReadOnlyList<PermissionDefinition> Permissions { get; } = [new(ModuleId, Read, Version), new(ModuleId, Update, Version)];
    public static FeatureDefinition ExportEnabled { get; } = new(ModuleId, "items.export-enabled", Version, false, TimeSpan.FromMinutes(5));
    public static FeatureDefinition ClientFeature { get; } = new(ModuleId, "items.client-feature", Version, true, TimeSpan.FromMinutes(5), clientVisible: true);
    public static SettingDefinition Secret { get; } = new(ModuleId, "items.secret", Version, ValueKind.SecretReference, null, TimeSpan.FromMinutes(5));
    public static SettingDefinition PageSize { get; } = new(ModuleId, "items.page-size", Version, ValueKind.Integer, "20", TimeSpan.FromMinutes(5),
        new ValueValidation(minimum: 1, maximum: 100), clientVisible: true);

    public static ModuleManifest Manifest(PostgreSqlModule module)
    {
        using var context = module.CreateContext();
        return new(ModuleId, Version,
            definitions: Permissions.Select(definition => definition.Declaration).Append(ExportEnabled.Declaration).Append(PageSize.Declaration)
                .Append(ClientFeature.Declaration).Append(Secret.Declaration),
            migrations: context.GetService<IMigrationsAssembly>().Migrations.Keys.Select(id => new ModuleAsset(id, Version)));
    }
}

public sealed class Item : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Note { get; set; }
    public Guid Version { get; set; }
}

public sealed class ItemsContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        ApiOperation.Configure(modelBuilder);
        modelBuilder.Entity<Item>(entity =>
        {
            entity.ToTable("Items");
            entity.HasKey(row => new { row.TenantId, row.Code });
            entity.Property(row => row.Owner).IsRequired();
            entity.Property(row => row.Title).IsRequired();
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
        migrationBuilder.CreateTable("ApiOperations", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Subject = table.Column<string>(type: "text", nullable: false),
            Fingerprint = table.Column<string>(type: "text", nullable: false),
            Result = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_ApiOperations", row => new { row.TenantId, row.Id }));
        migrationBuilder.CreateTable("Items", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Code = table.Column<string>(type: "text", nullable: false),
            Owner = table.Column<string>(type: "text", nullable: false),
            Title = table.Column<string>(type: "text", nullable: false),
            Note = table.Column<string>(type: "text", nullable: true),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Items", row => new { row.TenantId, row.Code }));
    }
}
