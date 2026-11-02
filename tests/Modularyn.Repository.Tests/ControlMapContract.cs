using System.Text.Json;
using System.Text.RegularExpressions;
using static Modularyn.Repository.Tests.ContentPackageContract;
using static Modularyn.Repository.Tests.G02EvidencePlanContract;

namespace Modularyn.Repository.Tests;

/// <summary>
/// The G-02 control map: every TM-G02 control of the threat model's control map is mapped, in order and once, to the tests
/// that evidence it, to the named residual risks that remain, or to both, and never to neither. Every cited test is a
/// declared test method of its project and class, every residual is defined once, names only model controls and security
/// orders, cites sources that exist, and is listed in the map's README for the operator's acceptance, and every security
/// order from SEC-02 to SEC-09 states at least one residual. It cannot show that a test is sufficient evidence; review does.
/// </summary>
internal static class ControlMapContract
{
    internal const string MapPath = "docs/security/control-map/g02-foundation.json";
    internal const string ReadmePath = "docs/security/control-map/README.md";
    internal static readonly string[] Orders = ["SEC-02", "SEC-03", "SEC-04", "SEC-05", "SEC-06", "SEC-07", "SEC-08", "SEC-09"];
    private static readonly Regex ResidualId = new(@"^RR-G02-[0-9]{3}\z", RegexOptions.CultureInvariant);
    private static readonly Regex RecordId = new(@"^[0-9A-HJKMNP-TV-Z]{26}\z", RegexOptions.CultureInvariant);

    internal static List<string> Validate(string text, IReadOnlyDictionary<string, string> files) => Capture(() =>
    {
        var map = Parse(text);
        Fields(map, "format", "formatVersion", "model", "controls", "orders", "entries", "residuals");
        Require(String(map, "format") == "modularyn.control-map" && map.GetProperty("formatVersion").GetInt32() == 1, "control map format and version");
        Require(String(map, "model") == "g02-foundation" && String(map, "controls") == ThreatModelContract.MapPath, "control map model and source");
        EqualStrings(map, "orders", Orders, "control map orders must be SEC-02 to SEC-09");
        var controls = Items(Parse(files[ThreatModelContract.MapPath]), "controls").Select(control => String(control, "id"))
            .Where(id => id.StartsWith("TM-G02-", StringComparison.Ordinal)).ToArray();

        var residuals = Items(map, "residuals").ToArray();
        var named = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var previous = "";
        foreach (var residual in residuals)
        {
            Fields(residual, "id", "title", "statement", "controls", "orders", "owner", "tests", "sources", "acceptance");
            var id = String(residual, "id");
            Require(ResidualId.IsMatch(id) && string.CompareOrdinal(id, previous) > 0, "residual ids must be unique and ascending: " + id);
            previous = id;
            foreach (var field in new[] { "title", "statement", "owner" }) Nonblank(residual, field);
            var names = Strings(residual, "controls");
            Require(names.Distinct(StringComparer.Ordinal).Count() == names.Length && names.All(controls.Contains), id + ": residual names an unknown control");
            foreach (var control in names) (named.TryGetValue(control, out var set) ? set : named[control] = new(StringComparer.Ordinal)).Add(id);
            var orders = Strings(residual, "orders");
            Require(orders.Distinct(StringComparer.Ordinal).Count() == orders.Length && orders.All(Orders.Contains), id + ": residual names an unknown order");
            Require(names.Length > 0 || orders.Length > 0, id + ": residual names neither a control nor an order");
            Tests(residual, files);
            var sources = Strings(residual, "sources");
            Require(sources.Length > 0 && sources.All(source => RecordId.IsMatch(source) || files.ContainsKey(source)), id + ": residual source must exist");
            var acceptance = String(residual, "acceptance");
            Require(acceptance == "pending" || acceptance.StartsWith("accepted:", StringComparison.Ordinal) && RecordId.IsMatch(acceptance["accepted:".Length..]),
                id + ": acceptance must be pending or name its acceptance record");
        }
        foreach (var order in Orders)
            Require(residuals.Any(residual => Strings(residual, "orders").Contains(order, StringComparer.Ordinal)), order + ": every security order states its residuals");

        var entries = Items(map, "entries").ToArray();
        Require(entries.Select(entry => String(entry, "control")).SequenceEqual(controls, StringComparer.Ordinal),
            "the map must name every TM-G02 control once, in the model's order");
        foreach (var entry in entries)
        {
            Fields(entry, "control", "tests", "residuals");
            var control = String(entry, "control");
            var tests = Tests(entry, files);
            var stated = Strings(entry, "residuals");
            Require(stated.SequenceEqual(named.GetValueOrDefault(control) ?? [], StringComparer.Ordinal),
                control + ": the entry's residuals must be exactly those that name the control, in order");
            Require(tests > 0 || stated.Length > 0, control + ": a control needs evidence or a residual");
        }

        var readme = files[ReadmePath];
        foreach (var residual in residuals)
            Require(readme.Contains("`" + String(residual, "id") + "`", StringComparison.Ordinal), String(residual, "id") + ": the README must list every residual");
    });

    // Each cited test names a declared test method of its project and class, once per list.
    private static int Tests(JsonElement owner, IReadOnlyDictionary<string, string> files)
    {
        var tests = Items(owner, "tests").ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            Fields(test, "project", "name");
            var name = String(test, "name");
            Require(seen.Add(name), "duplicate cited test: " + name);
            Require(name.Count(character => character == '.') >= 2, "a cited test is fully qualified: " + name);
            try { CheckExistingTest(String(test, "project"), name, files); }
            catch (ContractViolation violation) { throw new ContractViolation(violation.Message + ": " + name); }
        }
        return tests.Length;
    }
}
