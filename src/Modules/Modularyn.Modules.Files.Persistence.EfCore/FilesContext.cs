using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.EfCore;

public static class FilesMetadataModule
{
    public const string ModuleId = "Modularyn.Modules.Files";
    public const string DefaultSchema = "modularyn_files";

    public static PostgreSqlModule Create(PostgreSqlPersistenceOptions options, string schema = DefaultSchema) =>
        PostgreSqlModule.Create(ModuleId, schema, options, module => new FilesContext(module));

    public static ModuleManifest Manifest(SemanticVersion version) =>
        new(ModuleId, version, migrations: [new ModuleAsset(FilesInitial.Id, version)]);
}

public sealed class FilesContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    public DbSet<StoredFileAsset> Files => Set<StoredFileAsset>();
    public DbSet<StoredFileRevision> Revisions => Set<StoredFileRevision>();
    public DbSet<StoredFileUpload> Uploads => Set<StoredFileUpload>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFileAsset>(entity =>
        {
            entity.ToTable("Files");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Name).IsRequired();
            entity.Property(row => row.Container).IsRequired();
            entity.Property(row => row.Owner).IsRequired();
            entity.Property(row => row.CreatedAt).HasPrecision(6).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });
        modelBuilder.Entity<StoredFileRevision>(entity =>
        {
            entity.ToTable("FileRevisions");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasOne<StoredFileAsset>().WithMany().HasForeignKey(row => new { row.TenantId, row.FileId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.TenantId, row.FileId });
            entity.Property(row => row.FileId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.Length).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.Sha256).IsRequired().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.CreatedAt).HasPrecision(6).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });
        modelBuilder.Entity<StoredFileUpload>(entity =>
        {
            entity.ToTable("FileUploads");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasOne<StoredFileAsset>().WithMany().HasForeignKey(row => new { row.TenantId, row.FileId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.TenantId, row.FileId });
            entity.Property(row => row.FileId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.Length).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.Sha256).IsRequired().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.CreatedAt).HasPrecision(6).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.PropagatedContext).IsRequired().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });
    }
}
