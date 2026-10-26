using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Npgsql;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(PostgreSqlCollection.Name)]
public sealed class MigrationCoordinationTests : PostgreSqlTestDatabase
{
    [Fact]
    public async Task Activation_is_refused_before_construction_for_pending_and_failed_migrations_then_recovers()
    {
        var module = SampleModule();
        var registry = new PostgreSqlModuleRegistry([module]);
        var manifest = Manifest(module);
        var constructed = 0;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynPostgreSqlPersistence(registry);
        builder.Services.AddModularynModules(EnvironmentVersion,
            [new(manifest, _ => { constructed++; return new LifecycleModule(); })], new() { AllowUnverifiedComposition = true });
        using var host = builder.Build();
        var pending = await Assert.ThrowsAsync<ModuleCompositionException>(() => host.StartAsync(Cancellation));
        Assert.Equal(MigrationActivationValidator.Incomplete, Assert.Single(pending.Diagnostics).Code);
        Assert.Equal(0, constructed);
        Assert.False(host.Services.GetRequiredService<ModuleHost>().Report.IsReady);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @schema", new NpgsqlParameter("schema", Schema)));

        var plan = await PlanAsync(manifest);
        var failure = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan,
            new CheckpointObserver((_, _) => throw new IOException("Interrupted checkpoint.")), Cancellation);
        Assert.False(failure.Succeeded);
        var failed = await Assert.ThrowsAsync<ModuleCompositionException>(() => host.StartAsync(Cancellation));
        Assert.Equal(MigrationActivationValidator.Incomplete, Assert.Single(failed.Diagnostics).Code);
        Assert.Equal(0, constructed);
        Assert.False(host.Services.GetRequiredService<ModuleHost>().Report.IsReady);

        Assert.True((await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan, cancellationToken: Cancellation)).Succeeded);
        await host.StartAsync(Cancellation);
        Assert.Equal(1, constructed);
        Assert.True(host.Services.GetRequiredService<ModuleHost>().Report.IsReady);
        await host.StopAsync(Cancellation);
    }

    [Fact]
    public async Task Another_coordinator_and_activation_are_refused_while_the_database_lock_is_held()
    {
        var module = SampleModule();
        var registry = new PostgreSqlModuleRegistry([module]);
        var plan = await PlanAsync(Manifest(module));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new CheckpointObserver(async (checkpoint, token) =>
        {
            if (checkpoint.MigrationId != SampleInitial.Id) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var first = new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan, observer, Cancellation);
        try
        {
            var ready = await Task.WhenAny(first, entered.Task).WaitAsync(Cancellation);
            Assert.Same(entered.Task, ready);
            var second = await new PostgreSqlMigrationCoordinator(registry).ApplyAsync(plan, cancellationToken: Cancellation);
            Assert.Equal("MGP002", Assert.Single(second.Diagnostics).Code);
            Assert.Empty(second.Completed);
            var gate = await new MigrationActivationValidator(registry).ValidateAsync(plan.Selection, Cancellation);
            Assert.Equal(MigrationActivationValidator.Busy, Assert.Single(gate).Code);
        }
        finally { release.TrySetResult(); }
        Assert.True((await first).Succeeded);
        await AssertCompleteAsync(module);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds")));
    }

    [Fact]
    public async Task Module_migrations_follow_the_composers_dependency_order_and_keep_separate_histories()
    {
        var primary = SampleModule();
        var dependentSchema = Schema + "_dependent";
        await CreateSchemaAsync(dependentSchema);
        var dependent = PostgreSqlModule.Create("Dependent", dependentSchema, Options, module => new DependentContext(module));
        var plan = await PlanAsync(Manifest(dependent, [new(primary.ModuleId, VersionRange.Any)]), Manifest(primary));
        Assert.Equal(new[] { "Sample", "Dependent" }, plan.Modules.Select(module => module.Id));
        var checkpoints = new List<MigrationCheckpoint>();
        var result = await new PostgreSqlMigrationCoordinator(new([dependent, primary])).ApplyAsync(plan,
            new CheckpointObserver((checkpoint, _) => { checkpoints.Add(checkpoint); return ValueTask.CompletedTask; }), Cancellation);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "Sample", "Sample", "Dependent" }, checkpoints.Select(checkpoint => checkpoint.ModuleId));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM " + Table(PostgreSqlModule.HistoryTable)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Quote(dependentSchema) + "." + Quote(PostgreSqlModule.HistoryTable)));
        Assert.Equal("Dependent", await ScalarAsync<string>("SELECT \"ModuleId\" FROM " + Quote(dependentSchema) + "." + Quote(PostgreSqlModule.OwnershipTable)));
        Assert.Equal("Sample", await ScalarAsync<string>("SELECT \"ModuleId\" FROM " + Table(PostgreSqlModule.OwnershipTable)));
    }

    [Fact]
    public async Task A_different_registry_cannot_reassign_persisted_table_and_history_ownership()
    {
        var primary = SampleModule();
        await MigrateAsync(primary);
        var foreign = PostgreSqlModule.Create("Foreign", Schema, Options, module => new DependentContext(module));
        var result = await new PostgreSqlMigrationCoordinator(new([foreign])).ApplyAsync(await PlanAsync(Manifest(foreign)), cancellationToken: Cancellation);
        Assert.Equal("MPS022", Assert.Single(result.Diagnostics).Code);
        Assert.Equal("MPS022", (await Assert.ThrowsAsync<PersistenceContractException>(() => EfCoreUnitOfWork.BeginAsync(foreign, cancellationToken: Cancellation))).Code);
        Assert.Equal(2L, await ScalarAsync<long>("SELECT count(*) FROM " + Table(PostgreSqlModule.HistoryTable)));
        Assert.Equal("Sample", await ScalarAsync<string>("SELECT \"ModuleId\" FROM " + Table(PostgreSqlModule.OwnershipTable)));
    }

    [Fact]
    public async Task A_history_ahead_of_the_selected_release_is_refused_before_any_migration_effect()
    {
        var module = SampleModule();
        var plan = await MigrateAsync(module);
        await ScalarAsync<long>("WITH inserted AS (INSERT INTO " + Table(PostgreSqlModule.HistoryTable)
            + " (\"MigrationId\", \"ProductVersion\") VALUES ('20261026000300_Future', '10.0.14') RETURNING 1) SELECT count(*) FROM inserted");
        var result = await new PostgreSqlMigrationCoordinator(new([module])).ApplyAsync(plan, cancellationToken: Cancellation);
        Assert.Equal("MGP003", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Completed);
        Assert.NotEmpty(await new MigrationActivationValidator(new([module])).ValidateAsync(plan.Selection, Cancellation));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM " + Table(PostgreSqlModule.HistoryTable)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM " + Table("SystemSeeds")));
    }
}

public sealed class DependentContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SystemSeed>(entity =>
        {
            entity.ToTable("SystemSeeds");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Name).IsRequired();
        });
    }
}

[DbContext(typeof(DependentContext))]
[Migration("20261026000100_DependentInitial")]
public sealed class DependentInitial : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("SystemSeeds", schema: Schema, columns: table => new
        {
            Id = table.Column<string>(type: "text", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_SystemSeeds", row => row.Id));
    }
}
