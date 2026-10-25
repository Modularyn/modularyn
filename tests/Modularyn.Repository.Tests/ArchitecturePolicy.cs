using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Modularyn.Repository.Tests.ContentPackageContract;

namespace Modularyn.Repository.Tests;

internal sealed class ArchitecturePolicy
{
    internal const string RulesPath = "docs/architecture/dependency-rules.json";
    internal const string GuidePath = "docs/architecture/dependencies.md";
    internal const string Start = "<!-- dependency-policy:start -->";
    internal const string End = "<!-- dependency-policy:end -->";
    private readonly JsonElement rules;
    private readonly JsonElement names;

    private ArchitecturePolicy(JsonElement rules, JsonElement names)
    {
        this.rules = rules;
        this.names = names;
    }

    internal static ArchitecturePolicy Load(IReadOnlyDictionary<string, string> files)
    {
        var policy = new ArchitecturePolicy(Parse(files[RulesPath]), Parse(files[NamesAndLayoutContract.CompanionPath]));
        policy.CheckShape();
        var guide = files[GuidePath].Replace("\r\n", "\n");
        var start = guide.IndexOf(Start, StringComparison.Ordinal);
        var end = guide.IndexOf(End, StringComparison.Ordinal);
        Require(start >= 0 && end > start && guide.LastIndexOf(Start, StringComparison.Ordinal) == start
            && guide.LastIndexOf(End, StringComparison.Ordinal) == end, "dependency policy section markers");
        Require(guide[(start + Start.Length)..end] == "\n\n" + policy.Render() + "\n", "dependency guide tables differ from the companion");
        return policy;
    }

    internal string Text(string key) => rules.GetProperty(key).GetString()!;
    internal string[] List(string key) => Values(rules.GetProperty(key));
    internal string[] Targets(string table, string source) => Values(rules.GetProperty(table).GetProperty(source));
    internal string[] ReferenceKinds(string sourceEdition, string targetEdition) =>
        Values(rules.GetProperty("editionReferences").GetProperty(sourceEdition).GetProperty(targetEdition));
    internal string Diagnostic(string code, string detail) => code + ": " + rules.GetProperty("diagnostics").GetProperty(code).GetString() + " " + detail;
    internal string TestProject => names.GetProperty("repositoryTestProject").GetString()!;
    internal string Identifier(string key) => names.GetProperty("identifiers").GetProperty(key).GetString()!;

    internal ArchitectureNode Package(string id)
    {
        Require(Regex.IsMatch(id, names.GetProperty("packageIdPattern").GetString()!, RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)), "invalid Modularyn package identifier: " + id);
        if (id == Identifier("frontDoorPackage"))
            return new(id, "FrontDoor", "", "", "community", "modularyn", "", false);
        var tool = id == Identifier("toolPackage");
        var family = names.GetProperty("families").EnumerateArray().Single(value => tool
            ? value.GetProperty("name").GetString() == "Tooling"
            : id.StartsWith(value.GetProperty("prefix").GetString()![..^1], StringComparison.Ordinal));
        var familyName = family.GetProperty("name").GetString()!;
        var module = "";
        var role = "";
        if (List("moduleFamilies").Contains(familyName, StringComparer.Ordinal)
            && !List("nonModulePrefixes").Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var parts = id.Split('.');
            module = string.Join('.', parts.Take(3));
            var suffix = "." + string.Join('.', parts.Skip(3));
            foreach (var candidate in names.GetProperty("moduleSuffixes").EnumerateArray())
            {
                var pattern = Regex.Escape(candidate.GetProperty("suffix").GetString()!)
                    .Replace("\\{Provider}", "(?:UI|(?:[A-Z][a-z0-9]+)+)")
                    .Replace("\\{Adapter}", "(?:UI|(?:[A-Z][a-z0-9]+)+)");
                if (Regex.IsMatch(suffix, "^" + pattern + "(?![\\s\\S])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    role = candidate.GetProperty("role").GetString()!;
            }
            Require(role.Length > 0, "module needs one exact role suffix: " + id);
        }
        return new(id, familyName, module, role, familyName == "Pro" ? "paid" : "community",
            family.GetProperty("repository").GetString()!, "", false);
    }

    internal string SourcePath(ArchitectureNode node)
    {
        if (node.Family == "FrontDoor") return "src/Modularyn/Modularyn.csproj";
        var assembly = node.Id == Identifier("toolPackage") ? Identifier("toolAssembly") : node.Id;
        var directory = names.GetProperty("families").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == node.Family).GetProperty("sourceDirectory").GetString();
        return directory + assembly + "/" + assembly + ".csproj";
    }

    internal bool IsModularyn(string id) => id.Equals(Identifier("frontDoorPackage"), StringComparison.OrdinalIgnoreCase)
        || id.Equals(Identifier("toolPackage"), StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("Modularyn.", StringComparison.OrdinalIgnoreCase);

    internal bool ForbiddenPlatform(string id) => List("domainForbiddenPlatforms").Any(prefix =>
        id.Equals(prefix, StringComparison.OrdinalIgnoreCase) || id.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    private void CheckShape()
    {
        Require(rules.GetProperty("formatVersion").GetInt32() == 1, "dependency format version");
        var fields = new[] { "formatVersion", "namesContract", "moduleFamilies", "nonModulePrefixes", "eventContractRole", "editionReferences",
            "familyReferences", "sameModuleReferences", "otherModuleReferences", "supportFamilyReferences", "domainFrameworkSuffixes",
            "contractsFrameworkSuffixes", "domainForbiddenRoles", "domainForbiddenFamilies", "domainForbiddenSegments",
            "domainForbiddenPlatforms", "domainForbiddenProperties", "domainForbiddenSdks", "paidCommunityVersionPattern", "cycleScopes", "diagnostics" };
        Require(rules.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields), "dependency companion fields");
        Require(Text("namesContract") == NamesAndLayoutContract.CompanionPath, "dependency names contract path");
        Require(names.GetProperty("packageIdPattern").GetString() == NamesAndLayoutContract.PackagePattern, "dependency package grammar");
        Require(List("moduleFamilies").SequenceEqual(new[] { "Modules", "Cms", "Pro" }), "dependency module families");
        var studio = names.GetProperty("products").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "Studio")
            .GetProperty("home").GetString()![..^1];
        Require(List("nonModulePrefixes").SequenceEqual(new[] { studio }), "dependency product subtree exception");
        Require(Text("eventContractRole") == "application-contracts", "event contract role");
        Require(List("cycleScopes").SequenceEqual(new[] { "package", "module" }), "dependency cycle scopes");
        var editions = new[] { "community", "paid" };
        var editionRows = rules.GetProperty("editionReferences").EnumerateObject().ToArray();
        Require(editionRows.Select(row => row.Name).SequenceEqual(editions), "dependency source editions");
        foreach (var row in editionRows)
        {
            Require(row.Value.EnumerateObject().Select(target => target.Name).SequenceEqual(editions), "dependency target editions");
            foreach (var target in row.Value.EnumerateObject())
            {
                var kinds = Values(target.Value);
                Require(kinds.All(kind => kind is "ProjectReference" or "PackageReference")
                    && kinds.Distinct(StringComparer.Ordinal).Count() == kinds.Length, "dependency edition reference kinds");
            }
        }
        var families = names.GetProperty("families").EnumerateArray().Select(family => family.GetProperty("name").GetString()!).Append("FrontDoor").ToArray();
        var roles = names.GetProperty("moduleSuffixes").EnumerateArray().Select(role => role.GetProperty("role").GetString()!).ToArray();
        CheckMap("familyReferences", families, families);
        CheckMap("sameModuleReferences", roles, roles);
        CheckMap("otherModuleReferences", roles, [Text("eventContractRole")]);
        CheckMap("supportFamilyReferences", roles, families.Except(List("moduleFamilies")).ToArray());
        foreach (var key in fields.Where(key => rules.GetProperty(key).ValueKind == JsonValueKind.Array))
        {
            var values = List(key);
            Require(values.All(value => !string.IsNullOrWhiteSpace(value)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length,
                "unique nonempty dependency values: " + key);
        }
        Require(List("domainForbiddenRoles").All(value => roles.Contains(value, StringComparer.Ordinal))
            && List("domainForbiddenFamilies").All(value => families.Contains(value, StringComparer.Ordinal)), "known domain boundaries");
        var diagnostics = rules.GetProperty("diagnostics").EnumerateObject().ToArray();
        Require(diagnostics.Select(property => property.Name).SequenceEqual(Enumerable.Range(1, 13).Select(number => $"DEP{number:000}"))
            && diagnostics.All(property => !string.IsNullOrWhiteSpace(property.Value.GetString())), "complete dependency diagnostics");
        _ = new Regex(Text("paidCommunityVersionPattern"), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private void CheckMap(string key, string[] sources, string[] targets)
    {
        var map = rules.GetProperty(key).EnumerateObject().ToArray();
        Require(map.Select(property => property.Name).SequenceEqual(sources), "dependency table keys: " + key);
        foreach (var row in map)
        {
            var values = Values(row.Value);
            Require(values.All(value => targets.Contains(value, StringComparer.Ordinal))
                && values.Distinct(StringComparer.Ordinal).Count() == values.Length, "dependency table targets: " + key);
        }
    }

    private string Render()
    {
        var output = new StringBuilder();
        output.AppendLine("### Enforced policy tables").AppendLine();
        output.AppendLine("Authority: `dependency-rules.json`, format `1`. Generator: `ArchitecturePolicy`,");
        output.AppendLine("version `1`; template: `dependency-policy`, version `1`. Ownership policy:");
        output.AppendLine("regenerate only this marked section, preview its diff and preserve the authored");
        output.AppendLine("prose outside it. Edit the companion to change these tables.").AppendLine();
        output.AppendLine("An empty target set means no reference is allowed in that category.").AppendLine();
        output.AppendLine("#### Edition references").AppendLine();
        output.AppendLine("| Source edition | Target edition | Allowed reference kinds |").AppendLine("|---|---|---|");
        foreach (var source in rules.GetProperty("editionReferences").EnumerateObject())
        foreach (var target in source.Value.EnumerateObject())
            output.AppendLine("| `" + source.Name + "` | `" + target.Name + "` | " + Display(Values(target.Value)) + " |");
        output.AppendLine();
        foreach (var (key, title, source, target) in new[]
        {
            ("familyReferences", "Family references", "Source family", "Allowed target families"),
            ("sameModuleReferences", "References within one module", "Source role", "Allowed target roles"),
            ("otherModuleReferences", "References to another module", "Source role", "Allowed target roles"),
            ("supportFamilyReferences", "References from a module to support packages", "Source role", "Allowed non-module families")
        })
        {
            output.AppendLine("#### " + title).AppendLine();
            output.AppendLine("| " + source + " | " + target + " |").AppendLine("|---|---|");
            foreach (var row in rules.GetProperty(key).EnumerateObject())
                output.AppendLine("| `" + row.Name + "` | " + Display(Values(row.Value)) + " |");
            output.AppendLine();
        }
        output.AppendLine("#### Classification and boundary values").AppendLine();
        output.AppendLine("| Setting | Value |").AppendLine("|---|---|");
        foreach (var key in new[] { "namesContract", "moduleFamilies", "nonModulePrefixes", "eventContractRole", "domainFrameworkSuffixes",
            "contractsFrameworkSuffixes", "domainForbiddenRoles", "domainForbiddenFamilies", "domainForbiddenSegments",
            "domainForbiddenPlatforms", "domainForbiddenProperties", "domainForbiddenSdks", "paidCommunityVersionPattern", "cycleScopes" })
        {
            var value = rules.GetProperty(key);
            output.AppendLine("| `" + key + "` | " + Display(value.ValueKind == JsonValueKind.Array ? Values(value) : [value.GetString()!]) + " |");
        }
        output.AppendLine().AppendLine("#### Diagnostics").AppendLine();
        output.AppendLine("| Code | Reason |").AppendLine("|---|---|");
        foreach (var diagnostic in rules.GetProperty("diagnostics").EnumerateObject())
            output.AppendLine("| `" + diagnostic.Name + "` | " + diagnostic.Value.GetString() + " |");
        return output.ToString().Replace("\r\n", "\n");
    }

    private static string Display(string[] values) => values.Length == 0 ? "None" : string.Join(", ", values.Select(value => "`" + value.Replace("|", "\\|") + "`"));
    private static string[] Values(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();
}

internal sealed record ArchitectureNode(string Id, string Family, string Module, string Role, string Edition, string Repository, string Path, bool IsTest);
internal sealed record ArchitectureEdge(ArchitectureNode Source, ArchitectureNode Target, string Kind, string? Version = null);
