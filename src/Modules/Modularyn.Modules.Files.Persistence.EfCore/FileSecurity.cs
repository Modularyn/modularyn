using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.EfCore;

namespace Modularyn.Modules.Files.Persistence.EfCore;

/// <summary>
/// SEC-06: bounded metadata columns, the server-derived creator that authorization treats as the file's owner, the
/// content type detected from the bytes, and download grants that keep only their token's verifier.
/// </summary>
[DbContext(typeof(FilesContext))]
[Migration(Id)]
public sealed class FileSecurity : ModuleMigration
{
    public const string Id = "20261028000100_FileSecurity";
    private const string DetectedDefault = "application/octet-stream";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>("Name", "Files", type: "character varying(255)", maxLength: 255, nullable: false,
            oldClrType: typeof(string), oldType: "text", schema: Schema);
        migrationBuilder.AlterColumn<string>("Container", "Files", type: "character varying(200)", maxLength: 200, nullable: false,
            oldClrType: typeof(string), oldType: "text", schema: Schema);
        migrationBuilder.AlterColumn<string>("Owner", "Files", type: "character varying(200)", maxLength: 200, nullable: false,
            oldClrType: typeof(string), oldType: "text", schema: Schema);
        migrationBuilder.AddColumn<int>("CreatedByKind", "Files", type: "integer", nullable: false, defaultValue: 0, schema: Schema);
        migrationBuilder.AddColumn<string>("CreatedBy", "Files", type: "character varying(256)", maxLength: 256, nullable: false,
            defaultValue: "", schema: Schema);
        migrationBuilder.AddColumn<string>("ContentType", "FileRevisions", type: "character varying(100)", maxLength: 100, nullable: false,
            defaultValue: DetectedDefault, schema: Schema);
        migrationBuilder.AddColumn<string>("ContentType", "FileUploads", type: "character varying(100)", maxLength: 100, nullable: false,
            defaultValue: DetectedDefault, schema: Schema);
        migrationBuilder.CreateTable("FileGrants", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            FileId = table.Column<Guid>(type: "uuid", nullable: false),
            VersionId = table.Column<Guid>(type: "uuid", nullable: false),
            Purpose = table.Column<int>(type: "integer", nullable: false),
            Verifier = table.Column<byte[]>(type: "bytea", nullable: false),
            CreatedByKind = table.Column<int>(type: "integer", nullable: false),
            CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: false),
            ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: false),
            RevokedAt = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: true),
            ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp(6) with time zone", nullable: true),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_FileGrants", row => new { row.TenantId, row.Id });
            table.ForeignKey("FK_FileGrants_Files", row => new { row.TenantId, row.FileId },
                principalSchema: Schema, principalTable: "Files", principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_FileGrants_FileRevisions", row => new { row.TenantId, row.VersionId },
                principalSchema: Schema, principalTable: "FileRevisions", principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_FileGrants_TenantId_FileId", "FileGrants", ["TenantId", "FileId"], schema: Schema);
        migrationBuilder.CreateIndex("IX_FileGrants_TenantId_VersionId", "FileGrants", ["TenantId", "VersionId"], schema: Schema);
    }
}
