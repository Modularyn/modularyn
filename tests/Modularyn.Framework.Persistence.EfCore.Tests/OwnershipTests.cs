using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

public sealed class OwnershipTests
{
    private static PostgreSqlPersistenceOptions Options => new("Host=localhost;Database=modularyn_contract");

    [Fact]
    public void A_second_context_cannot_map_the_first_modules_table()
    {
        var first = PostgreSqlModule.Create("First", "first_module", Options, module => new SampleContext(module));
        using var owned = first.CreateContext();
        Assert.Equal("first_module", owned.Model.FindEntityType(typeof(SampleRow))!.GetSchema());
        var second = PostgreSqlModule.Create("Second", "second_module", Options, module => new ForeignContext(module));
        using var foreign = second.CreateContext();
        Assert.Equal("MPS004", Assert.Throws<PersistenceContractException>(() => foreign.Model).Code);
    }

    [Fact]
    public void Registry_rejects_shared_schemas_contexts_and_module_identities()
    {
        var first = PostgreSqlModule.Create("First", "first_module", Options, module => new SampleContext(module));
        var sharedSchema = PostgreSqlModule.Create("Second", "first_module", Options, module => new ForeignContext(module));
        var sharedContext = PostgreSqlModule.Create("Second", "second_module", Options, module => new SampleContext(module));
        var sharedId = PostgreSqlModule.Create("First", "second_module", Options, module => new ForeignContext(module));
        foreach (var other in new[] { sharedSchema, sharedContext, sharedId })
            Assert.Equal("MPS002", Assert.Throws<PersistenceContractException>(() => new PostgreSqlModuleRegistry([first, other])).Code);
    }

    [Fact]
    public void Context_models_and_history_are_scoped_to_the_registration_even_when_the_context_type_is_shared()
    {
        var first = PostgreSqlModule.Create("Sample", "first_module", Options, module => new SampleContext(module));
        var second = PostgreSqlModule.Create("Sample", "second_module", Options, module => new SampleContext(module));
        using var a = first.CreateContext();
        using var b = second.CreateContext();
        Assert.NotSame(a.Model, b.Model);
        Assert.Equal("first_module", a.Model.FindEntityType(typeof(SampleRow))!.GetSchema());
        Assert.Equal("second_module", b.Model.FindEntityType(typeof(SampleRow))!.GetSchema());
        Assert.Contains("first_module.__EFMigrationsHistory", a.GetService<IHistoryRepository>().GetCreateScript().Replace("\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("second_module.__EFMigrationsHistory", b.GetService<IHistoryRepository>().GetCreateScript().Replace("\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_migrations_sql_and_reserved_tables_are_rejected_before_execution()
    {
        var module = PostgreSqlModule.Create("Sample", "sample", Options, owner => new SampleContext(owner));
        MigrationOperation[] forbidden =
        [
            new EnsureSchemaOperation { Name = "foreign_module" },
            new AddColumnOperation { Schema = "foreign_module", Table = "Items", Name = "Note", ClrType = typeof(string) },
            new InsertDataOperation { Schema = "foreign_module", Table = "Items", Columns = ["Id"], Values = new object[,] { { "one" } } },
            new AddForeignKeyOperation { Schema = "sample", Table = "Items", PrincipalSchema = "foreign_module", PrincipalTable = "Items", Name = "FK_Items", Columns = ["Id"] },
            new RenameTableOperation { Schema = "sample", Name = "Items", NewSchema = "foreign_module", NewName = "Items" },
            new SqlOperation { Sql = "SELECT 1" },
            new CreateTableOperation { Schema = "sample", Name = PostgreSqlModule.HistoryTable },
            new CreateTableOperation { Schema = "sample", Name = PostgreSqlModule.ProgressTable },
            new CreateTableOperation { Schema = "sample", Name = PostgreSqlModule.OwnershipTable },
            new AddColumnOperation { Schema = "sample", Table = "Items", Name = "Note", ClrType = typeof(string), DefaultValueSql = "current_database()" }
        ];
        foreach (var operation in forbidden)
            Assert.Equal("MPS004", Assert.Throws<PersistenceContractException>(() => ModuleOwnership.ValidateMigration([operation], module)).Code);
        using var context = module.CreateContext();
        var assembly = context.GetService<IMigrationsAssembly>();
        Assert.Equal(new[] { SampleInitial.Id, SampleAddNoteAndSeed.Id }, assembly.Migrations.Keys);
        Assert.Equal("MPS005", Assert.Throws<PersistenceContractException>(() => assembly.CreateMigration(
            typeof(DependentInitial).GetTypeInfo(), context.Database.ProviderName!)).Code);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("pg_catalog")]
    [InlineData("information_schema")]
    [InlineData("upperCase")]
    [InlineData("two.schemas")]
    public void Module_schema_names_have_one_unambiguous_PostgreSQL_identity(string schema) =>
        Assert.Throws<ArgumentException>(() => PostgreSqlModule.Create("Sample", schema, Options, module => new SampleContext(module)));

    [Fact]
    public async Task A_manifest_cannot_omit_owned_migrations_even_before_database_access()
    {
        var module = PostgreSqlModule.Create("Sample", "sample", Options, owner => new SampleContext(owner));
        var selection = new CompositionSelection([new("Sample", SemanticVersion.Parse("1.0.0"))], new(SemanticVersion.Parse("10.0.14"), SemanticVersion.Parse("1.0.0")));
        var diagnostics = await new MigrationActivationValidator(new([module])).ValidateAsync(selection, TestContext.Current.CancellationToken);
        Assert.Equal(MigrationActivationValidator.Invalid, Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Filter_context_members_are_parameterized_per_context_despite_model_caching()
    {
        var module = PostgreSqlModule.Create("Filter", "filter_module", Options, owner => new ContextFilterContext(owner));
        using var first = (ContextFilterContext)module.CreateContext();
        using var second = (ContextFilterContext)module.CreateContext();
        first.VisibleName = "first";
        second.VisibleName = "second";
        Assert.Same(first.Model, second.Model);
        var firstSql = first.Set<SampleRow>().ToQueryString();
        var secondSql = second.Set<SampleRow>().ToQueryString();
        Assert.Contains("'first'", firstSql, StringComparison.Ordinal);
        Assert.Contains("'second'", secondSql, StringComparison.Ordinal);
        Assert.DoesNotContain("'first'", secondSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_external_ambient_transaction_is_refused_before_opening_a_database_connection()
    {
        var module = PostgreSqlModule.Create("Sample", "sample", Options, owner => new SampleContext(owner));
        using var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        var refusal = await Assert.ThrowsAsync<PersistenceContractException>(() => EfCoreUnitOfWork.BeginAsync(module, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("MPS023", refusal.Code);
    }

    [Fact]
    public async Task Host_refuses_declared_migrations_when_the_completion_gate_was_not_registered()
    {
        var constructed = 0;
        var manifest = new ModuleManifest("Sample", SemanticVersion.Parse("1.0.0"), migrations: [new(SampleInitial.Id, SemanticVersion.Parse("1.0.0"))]);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynModules(new(SemanticVersion.Parse("10.0.14"), SemanticVersion.Parse("1.0.0")),
            [new(manifest, _ => { constructed++; return new LifecycleModule(); })], new() { AllowUnverifiedComposition = true });
        using var host = builder.Build();
        var refusal = await Assert.ThrowsAsync<ModuleCompositionException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ModuleHostDiagnosticCodes.MigrationsNotConfigured, Assert.Single(refusal.Diagnostics).Code);
        Assert.Equal(0, constructed);
        Assert.False(host.Services.GetRequiredService<ModuleHost>().Report.IsReady);
    }

    private sealed class ForeignContext(PostgreSqlModule module) : ModuleDbContext(module)
    {
        protected override void ConfigureModule(ModelBuilder modelBuilder) => modelBuilder.Entity<SampleRow>().ToTable("Items", "first_module");
    }

    private sealed class ContextFilterContext(PostgreSqlModule module) : ModuleDbContext(module)
    {
        public string VisibleName { get; set; } = "first";
        protected override void ConfigureModule(ModelBuilder modelBuilder) => modelBuilder.Entity<SampleRow>().ToTable("Items");
        protected override void ConfigureFilters(IQueryFilterRegistry filters) =>
            filters.Add<SampleRow>("Sample.VisibleName", row => row.Name == VisibleName);
    }
}

internal sealed class LifecycleModule : IModule
{
    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
