using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.EfCore;

namespace Modularyn.Modules.Files.Persistence.EfCore;

[DbContext(typeof(FilesContext))]
[Migration(Id)]
public sealed class FilesInitial : ModuleMigration
{
    public const string Id = "20261027000100_Files";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable("Files", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            Container = table.Column<string>(type: "text", nullable: false),
            Owner = table.Column<string>(type: "text", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Files", row => new { row.TenantId, row.Id }));
        migrationBuilder.CreateTable("FileRevisions", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            FileId = table.Column<Guid>(type: "uuid", nullable: false),
            Length = table.Column<long>(type: "bigint", nullable: false),
            Sha256 = table.Column<string>(type: "text", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: false),
            Quarantine = table.Column<int>(type: "integer", nullable: false),
            IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_FileRevisions", row => new { row.TenantId, row.Id });
            table.ForeignKey("FK_FileRevisions_Files", row => new { row.TenantId, row.FileId },
                principalSchema: Schema, principalTable: "Files", principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_FileRevisions_TenantId_FileId", "FileRevisions", ["TenantId", "FileId"], schema: Schema);
        migrationBuilder.CreateTable("FileUploads", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            FileId = table.Column<Guid>(type: "uuid", nullable: false),
            Length = table.Column<long>(type: "bigint", nullable: false),
            Sha256 = table.Column<string>(type: "text", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: false),
            PropagatedContext = table.Column<string>(type: "text", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_FileUploads", row => new { row.TenantId, row.Id });
            table.ForeignKey("FK_FileUploads_Files", row => new { row.TenantId, row.FileId },
                principalSchema: Schema, principalTable: "Files", principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_FileUploads_TenantId_FileId", "FileUploads", ["TenantId", "FileId"], schema: Schema);
    }
}
