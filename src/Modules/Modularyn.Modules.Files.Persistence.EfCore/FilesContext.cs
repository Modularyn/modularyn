using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.EfCore;

public static class FilesMetadataModule
{
    public const string ModuleId = "Modularyn.Modules.Files";
    public const string DefaultSchema = "modularyn_files";

    /// <summary>The module's migrations, in order, as its manifest declares them.</summary>
    public static IReadOnlyList<string> MigrationIds { get; } = [FilesInitial.Id, FileSecurity.Id];

    public static PostgreSqlModule Create(PostgreSqlPersistenceOptions options, string schema = DefaultSchema) =>
        PostgreSqlModule.Create(ModuleId, schema, options, module => new FilesContext(module));

    /// <summary>
    /// The module's manifest with its migrations. Pass the permission declarations of <c>FilePermissions.Definitions</c>,
    /// which the application contracts own, so that authorization's registry and the manifest agree before activation.
    /// </summary>
    public static ModuleManifest Manifest(SemanticVersion version, IEnumerable<ModuleAsset>? definitions = null) =>
        new(ModuleId, version, definitions: definitions ?? [], migrations: MigrationIds.Select(id => new ModuleAsset(id, version)));
}

public sealed class FilesContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    public DbSet<StoredFileAsset> Files => Set<StoredFileAsset>();
    public DbSet<StoredFileRevision> Revisions => Set<StoredFileRevision>();
    public DbSet<StoredFileUpload> Uploads => Set<StoredFileUpload>();
    public DbSet<StoredFileGrant> Grants => Set<StoredFileGrant>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFileAsset>(entity =>
        {
            entity.ToTable("Files");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Name).IsRequired().HasMaxLength(FileMetadataRules.MaxNameLength);
            entity.Property(row => row.Container).IsRequired().HasMaxLength(FileMetadataRules.MaxContainerLength);
            entity.Property(row => row.Owner).IsRequired().HasMaxLength(FileMetadataRules.MaxOwnerLength);
            entity.Property(row => row.CreatedByKind).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.CreatedBy).IsRequired().HasMaxLength(ActorReference.MaxSubjectLength)
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
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
            entity.Property(row => row.ContentType).IsRequired().HasMaxLength(FileMetadataRules.MaxContentTypeLength)
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
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
            entity.Property(row => row.ContentType).IsRequired().HasMaxLength(FileMetadataRules.MaxContentTypeLength)
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.CreatedAt).HasPrecision(6).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.PropagatedContext).IsRequired().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });
        modelBuilder.Entity<StoredFileGrant>(entity =>
        {
            entity.ToTable("FileGrants");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasOne<StoredFileAsset>().WithMany().HasForeignKey(row => new { row.TenantId, row.FileId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<StoredFileRevision>().WithMany().HasForeignKey(row => new { row.TenantId, row.VersionId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.TenantId, row.FileId });
            entity.HasIndex(row => new { row.TenantId, row.VersionId });
            foreach (var immutable in new[] { nameof(FileGrant.FileId), nameof(FileGrant.VersionId), nameof(FileGrant.Purpose), nameof(FileGrant.CreatedByKind) })
                entity.Property(immutable).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.Verifier).IsRequired().Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.CreatedBy).IsRequired().HasMaxLength(ActorReference.MaxSubjectLength)
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.CreatedAt).HasPrecision(6).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.ExpiresAt).HasPrecision(6).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(row => row.RevokedAt).HasPrecision(6);
            entity.Property(row => row.ConsumedAt).HasPrecision(6);
        });
    }
}
