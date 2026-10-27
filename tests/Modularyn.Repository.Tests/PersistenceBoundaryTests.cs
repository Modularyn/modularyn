using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

// TM-G02-029: EF Core's filter bypass and raw SQL appear only inside the persistence adapter, whose query guard refuses
// the general bypass at run time and whose ModuleSql composes every filter, the tenant filter included, over its own
// SQL. Product code elsewhere, module persistence adapters included, reaches data through module contexts and ModuleSql.
// TM-G02-028: no product code outside the adapter opens a deployment scope, so host-scoped data changes in product code
// only under an audited host scope or a module's owner-bound unit. A module opens an owner-bound unit only for its own
// data: BeginOwnedAsync and ModuleOwnerScope appear in product code only in the adapter and in a module's own
// .Application and .Persistence.* projects, and each ModuleOwnerScope.For names a context type that the same module
// declares; the adapter accepts the scope only for the module whose context type it names. Tests may inspect the database
// directly and open deployment scopes and owner-bound units, and are outside this check.
public sealed class PersistenceBoundaryTests
{
    internal const string Adapter = "src/Framework/Modularyn.Framework.Persistence.EfCore/";

    private static readonly Regex Forbidden = new(
        @"\b(?:IgnoreQueryFilters|FromSql\w*|SqlQuery\w*|ExecuteSql\w*|GetDbConnection|Npgsql(?:Command|Connection|DataSource|Batch)\w*|DeploymentScope)\b",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex OwnerBound = new(@"\b(?:BeginOwnedAsync|ModuleOwnerScope)\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex OwnerFor = new(
        @"\bModuleOwnerScope\s*\.\s*For\s*<\s*(?<type>(?:global::)?[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*>",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex OwningProject = new(
        @"\Asrc/Modules/(?<module>Modularyn\.Modules\.[A-Za-z0-9]+)\.(?:Application|Persistence(?:\.[A-Za-z0-9]+)+)/",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool IsProductCode(string path) =>
        path.StartsWith("src/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal) && !path.StartsWith(Adapter, StringComparison.Ordinal);

    internal static IReadOnlyList<string> Violations(IReadOnlyDictionary<string, string> files) => files
        .Where(file => IsProductCode(file.Key) && Forbidden.IsMatch(file.Value))
        .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<string> OwnerBoundViolations(IReadOnlyDictionary<string, string> files)
    {
        var violations = new List<string>();
        foreach (var (path, text) in files.Where(file => IsProductCode(file.Key) && OwnerBound.IsMatch(file.Value)))
        {
            var project = OwningProject.Match(path);
            if (!project.Success || OwnerFor.Matches(text).Any(scope => !DeclaredInModule(files, project.Groups["module"].Value,
                    Regex.Replace(scope.Groups["type"].Value, @"\s+", "", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))))
                violations.Add(path);
        }
        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    // A qualified name must be in the module's own namespace, and the type must be declared in one of the module's projects.
    private static bool DeclaredInModule(IReadOnlyDictionary<string, string> files, string module, string type)
    {
        if (type.StartsWith("global::", StringComparison.Ordinal)) type = type["global::".Length..];
        var separator = type.LastIndexOf('.');
        if (separator >= 0 && !type.StartsWith(module + ".", StringComparison.Ordinal)) return false;
        var declaration = new Regex(@"\b(?:class|record)\s+" + Regex.Escape(type[(separator + 1)..]) + @"\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return files.Any(file => file.Key.StartsWith("src/Modules/" + module + ".", StringComparison.Ordinal)
            && file.Key.EndsWith(".cs", StringComparison.Ordinal) && declaration.IsMatch(file.Value));
    }

    [Fact]
    public void TM_G02_029_Only_the_persistence_adapter_uses_filter_bypasses_and_raw_SQL() =>
        Assert.Empty(Violations(RepositoryFiles.Current));

    [Theory]
    [InlineData("context.Items.IgnoreQueryFilters()")]
    [InlineData("context.Items.IgnoreQueryFilters([\"Modularyn.Tenant\"])")]
    [InlineData("context.Items.FromSqlRaw(text)")]
    [InlineData("context.Items.FromSql($\"SELECT 1\")")]
    [InlineData("context.Items.FromSqlInterpolated($\"SELECT 1\")")]
    [InlineData("context.Database.SqlQueryRaw<int>(text)")]
    [InlineData("context.Database.ExecuteSqlRawAsync(text)")]
    [InlineData("context.Database.ExecuteSql($\"SELECT 1\")")]
    [InlineData("context.Database.GetDbConnection()")]
    [InlineData("new NpgsqlCommand(text)")]
    [InlineData("new NpgsqlConnection(text)")]
    [InlineData("NpgsqlDataSource.Create(text)")]
    [InlineData("new NpgsqlBatch()")]
    [InlineData("new DeploymentScope(\"seed\", \"Seed host data.\")")]
    public void TM_G02_029_Each_bypass_and_raw_SQL_form_is_found_outside_the_adapter(string code)
    {
        const string module = "src/Modules/Modularyn.Modules.Sample.Persistence.EfCore/Reader.cs";
        var files = new Dictionary<string, string>
        {
            [module] = "internal sealed class Reader { void Read() { _ = " + code + "; } }",
            [Adapter + "ModuleSql.cs"] = code,
            ["tests/Modularyn.Modules.Sample.Persistence.EfCore.Tests/Inspection.cs"] = code,
            ["src/Modules/Modularyn.Modules.Sample.Domain/Notes.md"] = code
        };
        Assert.Equal([module], Violations(files));
    }

    [Fact]
    public void TM_G02_028_Only_a_module_s_own_application_and_persistence_projects_open_owner_bound_units_for_its_own_context() =>
        Assert.Empty(OwnerBoundViolations(RepositoryFiles.Current));

    [Fact]
    public void TM_G02_028_Owner_bound_units_elsewhere_or_for_another_module_are_found()
    {
        const string open = "await EfCoreUnitOfWork.BeginOwnedAsync(module, ModuleOwnerScope.For<SampleContext>(\"sample.items\"), cancellationToken: token);";
        var files = new Dictionary<string, string>
        {
            ["src/Modules/Modularyn.Modules.Sample.Persistence.EfCore/SampleContext.cs"] = "internal sealed class SampleContext(PostgreSqlModule module) : ModuleDbContext(module) { }",
            ["src/Modules/Modularyn.Modules.Sample.Persistence.EfCore/Store.cs"] = open,
            ["src/Modules/Modularyn.Modules.Sample.Persistence.EfCore/Qualified.cs"] =
                "_ = ModuleOwnerScope.For< Modularyn.Modules.Sample.Persistence.EfCore.SampleContext >(\"sample.items\");",
            ["src/Modules/Modularyn.Modules.Sample.Application/Scopes.cs"] = "_ = ModuleOwnerScope.For<SampleContext>(\"sample.items\");",
            ["src/Modules/Modularyn.Modules.Sample.Application.Contracts/Scopes.cs"] = "_ = ModuleOwnerScope.For<SampleContext>(\"sample.items\");",
            ["src/Modules/Modularyn.Modules.Sample.HttpApi/Endpoint.cs"] = open,
            ["src/Modules/Modularyn.Modules.Other.Persistence.EfCore/OtherContext.cs"] = "internal sealed class SampleContext { } internal sealed class OtherContext { }",
            ["src/Modules/Modularyn.Modules.Other.Persistence.EfCore/Own.cs"] = "_ = ModuleOwnerScope.For<OtherContext>(\"other.items\");",
            ["src/Modules/Modularyn.Modules.Other.Persistence.EfCore/Borrowed.cs"] = "_ = ModuleOwnerScope.For<NotDeclaredHere>(\"other.items\");",
            ["src/Modules/Modularyn.Modules.Other.Persistence.EfCore/Foreign.cs"] =
                "_ = ModuleOwnerScope.For<global::Modularyn.Modules.Sample.Persistence.EfCore.SampleContext>(\"other.items\");",
            ["src/Framework/Modularyn.Framework.Tenancy/Directory.cs"] = open,
            [Adapter + "EfCoreUnitOfWork.cs"] = open,
            ["tests/Modularyn.Modules.Sample.Persistence.EfCore.Tests/Fixture.cs"] = open,
            ["src/Modules/Modularyn.Modules.Sample.Domain/Notes.md"] = open
        };
        Assert.Equal(
        [
            "src/Framework/Modularyn.Framework.Tenancy/Directory.cs",
            "src/Modules/Modularyn.Modules.Other.Persistence.EfCore/Borrowed.cs",
            "src/Modules/Modularyn.Modules.Other.Persistence.EfCore/Foreign.cs",
            "src/Modules/Modularyn.Modules.Sample.Application.Contracts/Scopes.cs",
            "src/Modules/Modularyn.Modules.Sample.HttpApi/Endpoint.cs"
        ], OwnerBoundViolations(files));
    }

    [Fact]
    public void TM_G02_029_Ordinary_queries_and_ModuleSql_are_not_flagged()
    {
        var files = new Dictionary<string, string>
        {
            ["src/Modules/Modularyn.Modules.Sample.Application/Items.cs"] =
                "var rows = await work.Sql.Query<Item>().Where(row => row.Code == code).ToListAsync(token); context.Items.AsNoTracking();"
        };
        Assert.Empty(Violations(files));
    }
}
