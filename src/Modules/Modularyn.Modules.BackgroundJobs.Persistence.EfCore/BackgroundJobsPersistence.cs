using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Modules.BackgroundJobs.Domain;

namespace Modularyn.Modules.BackgroundJobs.Persistence.EfCore;

public static class BackgroundJobsPersistence
{
    public static PostgreSqlModule Create(PostgreSqlPersistenceOptions options, string schema = "modularyn_jobs") =>
        PostgreSqlModule.Create(BackgroundJobsDefinition.ModuleId, schema, options, module => new BackgroundJobsContext(module));
}

public sealed class BackgroundJobsContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder) => JobModel.Configure(modelBuilder);
}

[DbContext(typeof(BackgroundJobsContext))]
[Migration(BackgroundJobsDefinition.MigrationId)]
public sealed class BackgroundJobsInitial : ModuleMigration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        JobModel.CreateTables(migrationBuilder, Schema);
    }
}
