using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Api;
using Modularyn.Framework.Events;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Tooling.Samples.TeamWorkspace;

public sealed class WorkspaceProject : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid Version { get; set; }
}

public sealed class WorkspaceNote : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Text { get; set; } = "";
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid Version { get; set; }
}

public sealed class NoteRevision : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid NoteId { get; set; }
    public int Number { get; set; }
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

// The activity projection contains references only; note content is read through the authorized notes service.
public sealed class ActivityItem : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid NoteId { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class DigestSubscription : ITenantScoped
{
    public Guid TenantId { get; set; }
    public string Member { get; set; } = "";
    public Guid ScheduleId { get; set; }
    public string TimeZone { get; set; } = "";
}

public sealed class MemberDigest : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid JobId { get; set; }
    public string Member { get; set; } = "";
    public int ActivityCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkspaceContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder model)
    {
        ApiOperation.Configure(model);
        EventModel.Configure(model);
        JobModel.Configure(model);
        model.Entity<WorkspaceProject>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.Property(row => row.Name).HasMaxLength(120);
        });
        model.Entity<WorkspaceNote>(entity =>
        {
            entity.ToTable("Notes");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.ProjectId });
            entity.HasOne<WorkspaceProject>().WithMany().HasForeignKey(row => new { row.TenantId, row.ProjectId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<NoteRevision>(entity =>
        {
            entity.ToTable("NoteRevisions");
            entity.HasKey(row => new { row.TenantId, row.NoteId, row.Number });
            entity.HasOne<WorkspaceNote>().WithMany().HasForeignKey(row => new { row.TenantId, row.NoteId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ActivityItem>(entity =>
        {
            entity.ToTable("Activity");
            entity.HasKey(row => new { row.TenantId, row.EventId });
            entity.HasIndex(row => new { row.TenantId, row.RecordedAt });
            entity.HasOne<NoteRevision>().WithMany().HasForeignKey(row => new { row.TenantId, row.NoteId, row.Revision }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DigestSubscription>(entity =>
        {
            entity.ToTable("DigestSubscriptions");
            entity.HasKey(row => new { row.TenantId, row.Member });
            entity.HasOne<JobSchedule>().WithMany().HasForeignKey(row => new { row.TenantId, row.ScheduleId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<MemberDigest>(entity =>
        {
            entity.ToTable("MemberDigests");
            entity.HasKey(row => new { row.TenantId, row.JobId });
            entity.HasIndex(row => new { row.TenantId, row.Member, row.CreatedAt });
            entity.HasOne<JobRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.JobId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

[DbContext(typeof(WorkspaceContext))]
[Migration(Id)]
public sealed class WorkspaceInitial : ModuleMigration
{
    public const string Id = "20261102000100_TeamWorkspace";
    protected override void Up(MigrationBuilder migration)
    {
        migration.EnsureSchema(Schema);
        EventModel.CreateTables(migration, Schema);
        JobModel.CreateTables(migration, Schema);
        migration.CreateTable("ApiOperations", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Id = table.Column<Guid>("uuid"), Subject = table.Column<string>("text"),
            Fingerprint = table.Column<string>("text"), Result = table.Column<string>("text")
        }, constraints: table => table.PrimaryKey("PK_ApiOperations", row => new { row.TenantId, row.Id }));
        migration.CreateTable("Projects", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Id = table.Column<Guid>("uuid"), Name = table.Column<string>("character varying(120)", maxLength: 120),
            Version = table.Column<Guid>("uuid")
        }, constraints: table => table.PrimaryKey("PK_Projects", row => new { row.TenantId, row.Id }));
        migration.CreateTable("Notes", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Id = table.Column<Guid>("uuid"), ProjectId = table.Column<Guid>("uuid"), Text = table.Column<string>("text"),
            Revision = table.Column<int>("integer"), UpdatedAt = table.Column<DateTimeOffset>("timestamp with time zone"), Version = table.Column<Guid>("uuid")
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Notes", row => new { row.TenantId, row.Id });
            table.ForeignKey("FK_Notes_Projects", row => new { row.TenantId, row.ProjectId }, "Projects", new[] { "TenantId", "Id" }, principalSchema: Schema);
        });
        migration.CreateIndex("IX_Notes_Project", "Notes", new[] { "TenantId", "ProjectId" }, schema: Schema);
        migration.CreateTable("NoteRevisions", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), NoteId = table.Column<Guid>("uuid"), Number = table.Column<int>("integer"),
            Text = table.Column<string>("text"), Author = table.Column<string>("text"), CreatedAt = table.Column<DateTimeOffset>("timestamp with time zone")
        }, constraints: table =>
        {
            table.PrimaryKey("PK_NoteRevisions", row => new { row.TenantId, row.NoteId, row.Number });
            table.ForeignKey("FK_Revisions_Notes", row => new { row.TenantId, row.NoteId }, "Notes", new[] { "TenantId", "Id" }, principalSchema: Schema);
        });
        migration.CreateTable("Activity", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), EventId = table.Column<Guid>("uuid"), NoteId = table.Column<Guid>("uuid"), Revision = table.Column<int>("integer"),
            RecordedAt = table.Column<DateTimeOffset>("timestamp with time zone")
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Activity", row => new { row.TenantId, row.EventId });
            table.ForeignKey("FK_Activity_Revision", row => new { row.TenantId, row.NoteId, row.Revision }, "NoteRevisions",
                new[] { "TenantId", "NoteId", "Number" }, principalSchema: Schema);
        });
        migration.CreateIndex("IX_Activity_Time", "Activity", new[] { "TenantId", "RecordedAt" }, schema: Schema);
        migration.CreateIndex("IX_Activity_Revision", "Activity", new[] { "TenantId", "NoteId", "Revision" }, schema: Schema);
        migration.CreateTable("DigestSubscriptions", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Member = table.Column<string>("text"), ScheduleId = table.Column<Guid>("uuid"), TimeZone = table.Column<string>("text")
        }, constraints: table =>
        {
            table.PrimaryKey("PK_DigestSubscriptions", row => new { row.TenantId, row.Member });
            table.ForeignKey("FK_Subscription_Schedule", row => new { row.TenantId, row.ScheduleId }, "JobSchedules", new[] { "TenantId", "Id" }, principalSchema: Schema);
        });
        migration.CreateIndex("IX_Subscriptions_Schedule", "DigestSubscriptions", new[] { "TenantId", "ScheduleId" }, schema: Schema);
        migration.CreateTable("MemberDigests", schema: Schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), JobId = table.Column<Guid>("uuid"), Member = table.Column<string>("text"),
            ActivityCount = table.Column<int>("integer"), CreatedAt = table.Column<DateTimeOffset>("timestamp with time zone")
        }, constraints: table =>
        {
            table.PrimaryKey("PK_MemberDigests", row => new { row.TenantId, row.JobId });
            table.ForeignKey("FK_Digests_Job", row => new { row.TenantId, row.JobId }, "Jobs", new[] { "TenantId", "Id" }, principalSchema: Schema);
        });
        migration.CreateIndex("IX_Digests_Member", "MemberDigests", new[] { "TenantId", "Member", "CreatedAt" }, schema: Schema);
    }
}
