using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

[assembly: InternalsVisibleTo("Modularyn.Framework.Settings.Tests")]

namespace Modularyn.Framework.Settings;

public static class SettingsModule
{
    public const string ModuleId = "Modularyn.Framework.Settings";
    public const string DefaultSchema = "modularyn_settings";
    public static PostgreSqlModule Create(PostgreSqlPersistenceOptions options, string schema = DefaultSchema) =>
        PostgreSqlModule.Create(ModuleId, schema, options, module => new SettingsContext(module));

    public static ModuleManifest Manifest(SemanticVersion version) => new(ModuleId, version,
        migrations: [new ModuleAsset(SettingsInitial.Id, version)]);

    internal static void Require(PostgreSqlModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.ContextType != typeof(SettingsContext)) throw new ArgumentException("Use the module returned by SettingsModule.Create.", nameof(module));
    }
}

internal sealed class GlobalValue : IHostScoped, IConcurrencyTracked
{
    public string ModuleId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public ValueKind Kind { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class TenantValue : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public string ModuleId { get; set; } = "";
    public string Name { get; set; } = "";
    // Empty identifies a tenant override; a user override uses the validated user actor's subject.
    public string User { get; set; } = "";
    public string Value { get; set; } = "";
    public ValueKind Kind { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class SettingsContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    internal DbSet<GlobalValue> GlobalValues => Set<GlobalValue>();
    internal DbSet<TenantValue> TenantValues => Set<TenantValue>();

    protected override void ConfigureModule(ModelBuilder builder)
    {
        builder.Entity<GlobalValue>(entity =>
        {
            entity.ToTable("GlobalValues");
            entity.HasKey(row => new { row.ModuleId, row.Name });
            entity.Property(row => row.Value).IsRequired();
        });
        builder.Entity<TenantValue>(entity =>
        {
            entity.ToTable("TenantValues");
            entity.HasKey(row => new { row.TenantId, row.ModuleId, row.Name, row.User });
            entity.Property(row => row.Value).IsRequired();
        });
    }
}

[DbContext(typeof(SettingsContext))]
[Migration(Id)]
internal sealed class SettingsInitial : ModuleMigration
{
    public const string Id = "20261027000100_Settings";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable("GlobalValues", schema: Schema, columns: table => new
        {
            ModuleId = table.Column<string>(type: "text", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            Value = table.Column<string>(type: "text", nullable: false),
            Kind = table.Column<int>(type: "integer", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_GlobalValues", row => new { row.ModuleId, row.Name }));
        migrationBuilder.CreateTable("TenantValues", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            ModuleId = table.Column<string>(type: "text", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            User = table.Column<string>(type: "text", nullable: false),
            Value = table.Column<string>(type: "text", nullable: false),
            Kind = table.Column<int>(type: "integer", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_TenantValues", row => new { row.TenantId, row.ModuleId, row.Name, row.User }));
    }
}
