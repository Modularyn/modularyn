using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Events.Tests;

public sealed class DurableEffect : ITenantScoped, IConcurrencyTracked
{
    public Guid TenantId { get; set; }
    public string Key { get; set; } = "";
    public int Count { get; set; }
    public long Position { get; set; }
    public Guid Version { get; set; }
}

/// <summary>A tenant's note that is soft-deleted rather than removed, so a read can bypass the soft-delete predicate.</summary>
public sealed class DurableNote : ITenantScoped, ISoftDeleted
{
    public Guid TenantId { get; set; }
    public string Key { get; set; } = "";
    public bool IsDeleted { get; set; }
}

internal static class DurableModel
{
    internal const string ProducerId = "Test.DurableProducer";
    internal const string ConsumerId = "Test.DurableConsumer";
    internal const string Apply = "durable.apply";
    internal const string Consumer = "projection";
    internal static readonly SemanticVersion Version = SemanticVersion.Parse("1.0.0");
    internal static readonly HostOperation Dispatch = new("test.durable.dispatch",
        new[] { ProducerId, ConsumerId }.SelectMany(module => new[] { "OutboxEvents", "InboxEvents", "Jobs", "JobAttempts", "JobSchedules", "Notes" }
            .Select(table => new HostReach(module, table))));
    internal static PermissionDefinition[] Permissions => [new(ProducerId, Apply, Version, PermissionFreshness.Current),
        new(ProducerId, EventStore.ReplayPermission, Version, PermissionFreshness.Current)];
    internal static HostOperationDefinition DispatchDefinition => new(ProducerId, Version, Dispatch);

    internal static void Configure(ModelBuilder model)
    {
        EventModel.Configure(model);
        JobModel.Configure(model);
        model.Entity<DurableEffect>(entity =>
        {
            entity.ToTable("Effects");
            entity.HasKey(row => new { row.TenantId, row.Key });
        });
        model.Entity<DurableNote>(entity =>
        {
            entity.ToTable("Notes");
            entity.HasKey(row => new { row.TenantId, row.Key });
        });
    }

    internal static void Migrate(MigrationBuilder migration, string schema)
    {
        migration.EnsureSchema(schema);
        EventModel.CreateTables(migration, schema);
        JobModel.CreateTables(migration, schema);
        migration.CreateTable("Effects", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Key = table.Column<string>("text"), Count = table.Column<int>("integer"),
            Position = table.Column<long>("bigint"), Version = table.Column<Guid>("uuid")
        }, constraints: table => table.PrimaryKey("PK_Effects", row => new { row.TenantId, row.Key }));
        migration.CreateTable("Notes", schema: schema, columns: table => new
        {
            TenantId = table.Column<Guid>("uuid"), Key = table.Column<string>("text"), IsDeleted = table.Column<bool>("boolean")
        }, constraints: table => table.PrimaryKey("PK_Notes", row => new { row.TenantId, row.Key }));
    }

    internal static async Task EffectAsync(EfCoreUnitOfWork work, string key, long position, CancellationToken token)
    {
        var row = await work.Context.Set<DurableEffect>().SingleOrDefaultAsync(row => row.Key == key, token);
        row ??= work.Context.Add(new DurableEffect { Key = key }).Entity;
        row.Count++;
        row.Position = position;
    }
}

public sealed class DurableProducerContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder) => DurableModel.Configure(modelBuilder);
}
public sealed class DurableConsumerContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder) => DurableModel.Configure(modelBuilder);
}
[DbContext(typeof(DurableProducerContext))]
[Migration(Id)]
public sealed class DurableProducerInitial : ModuleMigration
{
    public const string Id = "20261028002000_DurableProducer";
    protected override void Up(MigrationBuilder migrationBuilder) => DurableModel.Migrate(migrationBuilder, Schema);
}
[DbContext(typeof(DurableConsumerContext))]
[Migration(Id)]
public sealed class DurableConsumerInitial : ModuleMigration
{
    public const string Id = "20261028002100_DurableConsumer";
    protected override void Up(MigrationBuilder migrationBuilder) => DurableModel.Migrate(migrationBuilder, Schema);
}
