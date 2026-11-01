using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Modularyn.Framework.Events;

/// <summary>Included by each producing and consuming module in its own model and migration.</summary>
public static class EventModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<OutboxEvent>(entity =>
        {
            entity.ToTable("OutboxEvents");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.Consumer, row.State, row.DueAt });
            entity.HasIndex(row => new { row.TenantId, row.Consumer, row.OrderingKey, row.Position }).IsUnique();
        });
        model.Entity<InboxEvent>(entity =>
        {
            entity.ToTable("InboxEvents");
            entity.HasKey(row => new { row.TenantId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.Consumer, row.State, row.DueAt });
            entity.HasIndex(row => new { row.TenantId, row.Consumer, row.OrderingKey, row.Position }).IsUnique();
        });
        model.Entity<EventPosition>(entity =>
        {
            entity.ToTable("EventPositions");
            entity.HasKey(row => new { row.TenantId, row.Consumer, row.OrderingKey });
        });
    }

    public static void CreateTables(MigrationBuilder migration, string schema)
    {
        migration.CreateTable("OutboxEvents", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Id = table.Column<Guid>("uuid"),
            Consumer = table.Column<string>("text"), Kind = table.Column<string>("text"), SchemaVersion = table.Column<int>("integer"),
            OrderingKey = table.Column<string>("text"), Position = table.Column<long>("bigint"), Payload = table.Column<string>("text"),
            Propagated = table.Column<string>("text"), CreatedAt = table.Column<DateTimeOffset>("timestamp with time zone"),
            DueAt = table.Column<DateTimeOffset>("timestamp with time zone"), State = table.Column<int>("integer"),
            Attempts = table.Column<int>("integer"), Code = table.Column<string>("text"), Version = table.Column<Guid>("uuid")
        }, constraints: table => table.PrimaryKey("PK_OutboxEvents", row => new { row.TenantId, row.Id }));
        migration.CreateTable("InboxEvents", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Id = table.Column<Guid>("uuid"),
            Consumer = table.Column<string>("text"), Kind = table.Column<string>("text"), SchemaVersion = table.Column<int>("integer"),
            OrderingKey = table.Column<string>("text"), Position = table.Column<long>("bigint"), Payload = table.Column<string>("text"),
            Propagated = table.Column<string>("text"), ReceivedAt = table.Column<DateTimeOffset>("timestamp with time zone"),
            DueAt = table.Column<DateTimeOffset>("timestamp with time zone"), CompletedAt = table.Column<DateTimeOffset>("timestamp with time zone", nullable: true),
            State = table.Column<int>("integer"), Attempts = table.Column<int>("integer"), GapWaits = table.Column<int>("integer"),
            Code = table.Column<string>("text"), Version = table.Column<Guid>("uuid")
        }, constraints: table => table.PrimaryKey("PK_InboxEvents", row => new { row.TenantId, row.Id }));
        migration.CreateTable("EventPositions", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Consumer = table.Column<string>("text"), OrderingKey = table.Column<string>("text"),
            Position = table.Column<long>("bigint"), Version = table.Column<Guid>("uuid")
        }, constraints: table => table.PrimaryKey("PK_EventPositions", row => new { row.TenantId, row.Consumer, row.OrderingKey }));
        foreach (var table in new[] { "OutboxEvents", "InboxEvents" })
        {
            migration.CreateIndex("IX_" + table + "_Due", table, ["TenantId", "Consumer", "State", "DueAt"], schema: schema);
            migration.CreateIndex("IX_" + table + "_Position", table, ["TenantId", "Consumer", "OrderingKey", "Position"], schema: schema, unique: true);
        }
    }
}
