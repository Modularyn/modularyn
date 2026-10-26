using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

// TM-G02-029: EF Core's filter bypass and raw SQL appear only inside the persistence adapter, whose query guard refuses
// the general bypass at run time and whose ModuleSql composes every filter, the tenant filter included, over its own
// SQL. Product code elsewhere, module persistence adapters included, reaches data through module contexts and ModuleSql.
// Tests may inspect the database directly and are outside this check.
public sealed class PersistenceBoundaryTests
{
    internal const string Adapter = "src/Framework/Modularyn.Framework.Persistence.EfCore/";

    private static readonly Regex Forbidden = new(
        @"\b(?:IgnoreQueryFilters|FromSql\w*|SqlQuery\w*|ExecuteSql\w*|GetDbConnection|Npgsql(?:Command|Connection|DataSource|Batch)\w*)\b",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static IReadOnlyList<string> Violations(IReadOnlyDictionary<string, string> files) => files
        .Where(file => file.Key.StartsWith("src/", StringComparison.Ordinal) && file.Key.EndsWith(".cs", StringComparison.Ordinal)
            && !file.Key.StartsWith(Adapter, StringComparison.Ordinal) && Forbidden.IsMatch(file.Value))
        .Select(file => file.Key).Order(StringComparer.Ordinal).ToArray();

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
