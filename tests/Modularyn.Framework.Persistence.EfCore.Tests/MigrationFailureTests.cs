using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(PostgreSqlCollection.Name)]
public sealed class MigrationFailureTests : PostgreSqlTestDatabase
{
    [Fact]
    public async Task Failure_after_DDL_rolls_back_the_partial_migration_and_a_fresh_coordinator_retries_once()
    {
        var module = PostgreSqlModule.Create("Recovery", Schema, Options, owner => new RecoveryContext(owner));
        var registry = new PostgreSqlModuleRegistry([module]);
        var plan = await PlanAsync(Manifest(module));
        var failed = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan, cancellationToken: Cancellation);
        var diagnostic = Assert.Single(failed.Diagnostics);
        Assert.Equal("MGP004", diagnostic.Code);
        Assert.Equal(RecoveryExtend.Id, diagnostic.MigrationId);
        Assert.Equal("Failed", await ProgressStateAsync(RecoveryExtend.Id));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table(PostgreSqlModule.HistoryTable)));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = @schema AND table_name = 'SystemSeeds' AND column_name = 'Note'", new NpgsqlParameter("schema", Schema)));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds") + " WHERE \"Id\" = 'core'"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds") + " WHERE \"Id\" = 'existing'"));
        Assert.Contains(await new MigrationActivationValidator(registry).ValidateAsync(plan.Selection, Cancellation),
            item => item.Code == MigrationActivationValidator.Incomplete);

        // The declared forward repair retains the existing row while releasing its conflicting seed name.
        await ScalarAsync<long>("WITH repaired AS (UPDATE " + Table("SystemSeeds")
            + " SET \"Name\" = 'retained' WHERE \"Id\" = 'existing' RETURNING 1) SELECT count(*) FROM repaired");
        var resumed = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan, cancellationToken: Cancellation);
        Assert.Empty(resumed.Diagnostics);
        Assert.True(Assert.Single(resumed.Completed).Recovered);
        Assert.Equal("Complete", await ProgressStateAsync(RecoveryExtend.Id));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds") + " WHERE \"Id\" = 'core'"));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds") + " WHERE \"Id\" = 'existing'"));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM " + Table(PostgreSqlModule.HistoryTable)));
        Assert.Equal("YES:text", await ScalarAsync<string>(
            "SELECT is_nullable || ':' || data_type FROM information_schema.columns WHERE table_schema = @schema AND table_name = 'SystemSeeds' AND column_name = 'Note'", new NpgsqlParameter("schema", Schema)));
        await AssertCompleteAsync(module);
        Assert.Empty((await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan, cancellationToken: Cancellation)).Completed);
    }
}

public sealed class RecoverySeed : IHostScoped
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Note { get; set; }
}

public sealed class RecoveryContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder) => modelBuilder.Entity<RecoverySeed>(entity =>
    {
        entity.ToTable("SystemSeeds");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).ValueGeneratedNever();
        entity.Property(row => row.Name).IsRequired();
        entity.HasIndex(row => row.Name).IsUnique();
    });
}

[DbContext(typeof(RecoveryContext))]
[Migration("20261026000100_RecoveryInitial")]
public sealed class RecoveryInitial : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("SystemSeeds", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: "text", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_SystemSeeds", row => row.Id));
        migrationBuilder.CreateIndex("IX_SystemSeeds_Name", "SystemSeeds", "Name", schema: Schema, unique: true);
        migrationBuilder.InsertData("SystemSeeds", columns: ["Id", "Name"], columnTypes: ["text", "text"],
            values: new object[] { "existing", "required" }, schema: Schema);
    }
}

[DbContext(typeof(RecoveryContext))]
[Migration(RecoveryExtend.Id)]
public sealed class RecoveryExtend : ModuleMigration
{
    public const string Id = "20261026000200_RecoveryExtend";
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Note", "SystemSeeds", type: "text", schema: Schema, nullable: true);
        // PostgreSQL rejects this unique name after executing the column addition, inside the same EF transaction.
        migrationBuilder.InsertData("SystemSeeds", columns: ["Id", "Name"], columnTypes: ["text", "text"],
            values: new object[] { "core", "required" }, schema: Schema);
    }
}
