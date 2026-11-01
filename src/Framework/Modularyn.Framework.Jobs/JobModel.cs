using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Modularyn.Framework.Jobs;

public static class JobModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<JobSchedule>(entity =>
        {
            entity.ToTable("JobSchedules");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.Enabled, row.NextCheckAt });
        });
        model.Entity<JobRecord>(entity =>
        {
            entity.ToTable("Jobs");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.State, row.AvailableAt, row.LeaseUntil });
            entity.HasIndex(row => new { row.TenantId, row.ScheduleId, row.DueAt }).IsUnique();
            entity.HasOne<JobSchedule>().WithMany().HasForeignKey(row => new { row.TenantId, row.ScheduleId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<JobAttempt>(entity =>
        {
            entity.ToTable("JobAttempts");
            entity.HasKey(row => new { row.TenantId, row.Lease });
            entity.HasIndex(row => new { row.TenantId, row.JobId, row.Number }).IsUnique();
            entity.HasOne<JobRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.JobId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ScheduleOccurrence>(entity =>
        {
            entity.ToTable("ScheduleOccurrences");
            entity.HasKey(row => new { row.TenantId, row.ScheduleId, row.LocalDate });
            entity.HasOne<JobSchedule>().WithMany().HasForeignKey(row => new { row.TenantId, row.ScheduleId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<JobRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.JobId }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static void CreateTables(MigrationBuilder migration, string schema)
    {
        migration.CreateTable("JobSchedules", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"),
            Id = table.Column<Guid>("uuid"),
            Kind = table.Column<string>("text"),
            Payload = table.Column<string>("text"),
            Propagated = table.Column<string>("text"),
            TimeZone = table.Column<string>("text"),
            LocalTime = table.Column<TimeOnly>("time without time zone"),
            NextDate = table.Column<DateOnly>("date"),
            NextCheckAt = table.Column<DateTimeOffset>("timestamp with time zone"),
            Deferrals = table.Column<int>("integer"),
            Misfire = table.Column<int>("integer"),
            Enabled = table.Column<bool>("boolean"),
            Code = table.Column<string>("text"),
            Version = table.Column<Guid>("uuid")
        }, constraints: table =>
        {
            table.PrimaryKey("PK_JobSchedules", row => new { row.TenantId, row.Id });
        });
        migration.CreateTable("Jobs", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"),
            Id = table.Column<Guid>("uuid"),
            Kind = table.Column<string>("text"),
            Payload = table.Column<string>("text"),
            Propagated = table.Column<string>("text"),
            CreatedAt = table.Column<DateTimeOffset>("timestamp with time zone"),
            DueAt = table.Column<DateTimeOffset>("timestamp with time zone"),
            AvailableAt = table.Column<DateTimeOffset>("timestamp with time zone"),
            State = table.Column<int>("integer"),
            Attempts = table.Column<int>("integer"),
            Deferrals = table.Column<int>("integer"),
            Lease = table.Column<Guid>("uuid", nullable: true),
            LeaseUntil = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
            CancellationRequested = table.Column<bool>("boolean"),
            Code = table.Column<string>("text"),
            ScheduleId = table.Column<Guid>("uuid", nullable: true),
            Version = table.Column<Guid>("uuid")
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Jobs", row => new { row.TenantId, row.Id });
            table.ForeignKey("FK_Jobs_ScheduleId", row => new { row.TenantId, row.ScheduleId },
                principalSchema: schema, principalTable: "JobSchedules", principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
        });
        migration.CreateTable("JobAttempts", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"),
            JobId = table.Column<Guid>("uuid"),
            Lease = table.Column<Guid>("uuid"),
            Number = table.Column<int>("integer"),
            StartedAt = table.Column<DateTimeOffset>("timestamp with time zone"),
            EndedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
            State = table.Column<int>("integer"),
            Code = table.Column<string>("text"),
            Version = table.Column<Guid>("uuid")
        }, constraints: table =>
        {
            table.PrimaryKey("PK_JobAttempts", row => new { row.TenantId, row.Lease });
            table.ForeignKey("FK_JobAttempts_JobId", row => new { row.TenantId, row.JobId },
                principalSchema: schema, principalTable: "Jobs", principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
        });
        migration.CreateTable("ScheduleOccurrences", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"),
            ScheduleId = table.Column<Guid>("uuid"),
            LocalDate = table.Column<DateOnly>("date"),
            DueAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
            State = table.Column<int>("integer"),
            JobId = table.Column<Guid>("uuid", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_ScheduleOccurrences", row => new { row.TenantId, row.ScheduleId, row.LocalDate });
            table.ForeignKey("FK_ScheduleOccurrences_ScheduleId", row => new { row.TenantId, row.ScheduleId },
                principalSchema: schema, principalTable: "JobSchedules", principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
            table.ForeignKey("FK_ScheduleOccurrences_JobId", row => new { row.TenantId, row.JobId },
                principalSchema: schema, principalTable: "Jobs", principalColumns: ["TenantId", "Id"], onDelete: ReferentialAction.Restrict);
        });
        migration.CreateIndex("IX_JobSchedules_Due", "JobSchedules", ["TenantId", "Enabled", "NextCheckAt"], schema: schema);
        migration.CreateIndex("IX_Jobs_Due", "Jobs", ["TenantId", "State", "AvailableAt", "LeaseUntil"], schema: schema);
        migration.CreateIndex("IX_Jobs_Occurrence", "Jobs", ["TenantId", "ScheduleId", "DueAt"], schema: schema, unique: true);
        migration.CreateIndex("IX_JobAttempts_Number", "JobAttempts", ["TenantId", "JobId", "Number"], schema: schema, unique: true);
    }
}
