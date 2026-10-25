using System.Text.Json;
using System.Text.RegularExpressions;
using static Modularyn.Repository.Tests.ContentPackageContract;

namespace Modularyn.Repository.Tests;

internal static class EditionMapContract
{
    internal const string MapPath = "docs/editions/edition-map.json";
    internal const string ReadmePath = "docs/editions/README.md";
    internal const string RegisterPath = "docs/coverage/register.json";
    private const string TableHeader = "| Key | Catalog entry | Home |";

    // Catalog identities are transcribed from the design and edition decisions, independently of the map.
    // T rows and LOW contracts retain individual entries; module variants share their domain home.
    private const string CatalogAnchors = """
        accounts MOD-001 MOD-002
        ai-management MOD-003 JRN-005
        audit MOD-004 MOD-005
        background-jobs MOD-006
        blogging MOD-007
        chat MOD-008
        cms MOD-009 MOD-010 CMS-001 CMS-024 JRN-003
        documentation MOD-011
        workflow MOD-012 JRN-004
        feature-management MOD-013
        files MOD-014
        forms MOD-015
        privacy MOD-016
        identity MOD-017 MOD-018
        identity-migration MOD-019 MOD-020
        localization MOD-021
        operation-limits MOD-022
        authorization-server MOD-023 MOD-024
        payments MOD-025
        permissions MOD-026
        saas-lifecycle MOD-027 JRN-002
        settings MOD-028
        tenancy MOD-029 JRN-006
        text-templates MOD-030
        sms MOD-031
        virtual-files MOD-032
        commerce
        ui-contracts UI-001 UI-014
        tooling-contracts DEV-001 DEV-006 JRN-001
        run-profiles DEV-015
        local-observability DEV-016
        supply-chain DEV-018
        tool-architecture-families T-001 DEV-003
        tool-application-starters T-002
        tool-modern-creation T-003
        tool-classic-creation T-004
        tool-profile-choices T-005 DEV-004
        tool-solution-composition T-006 DEV-002
        tool-version-alignment T-007
        tool-razor-pages T-008
        tool-angular T-009
        tool-blazor-wasm T-010
        tool-blazor-server T-011
        tool-blazor-web-app T-012
        tool-maui-blazor T-013
        tool-blazor-components T-014
        tool-react T-015
        tool-react-admin T-016
        tool-native-clients T-017
        tool-theme-variants T-018
        tool-minimal-theme T-019
        tool-legacy-theme-migration T-020
        tool-theme-appearance T-021
        tool-studio-workspace T-022 DEV-014
        tool-studio-runner T-023
        tool-studio-telemetry T-024
        tool-cluster-workflows T-025 DEV-017
        tool-cli T-026
        tool-cli-composition T-027 DEV-005
        tool-cli-utilities T-028
        tool-entity-generation T-029 DEV-007
        tool-relationship-generation T-030 DEV-008
        tool-database-intake T-031 DEV-009
        tool-generator-customization T-032 DEV-010 DEV-011
        tool-sdk T-033 DEV-012
        tool-react-api T-034 DEV-013
        tool-coding-assistance T-035 DEV-019
        tool-assistance-configuration T-036 DEV-020
        tool-assistance-tools T-037
        tool-assistance-workflows T-038
        tool-change-review T-039 DEV-021
        tool-studio-mcp T-040
        tool-documentation-mcp T-041 DEV-022
        tool-low-code-mcp T-042
        tool-application-ai T-043
        tool-low-code-designer T-044
        tool-low-code-runtime T-045
        tool-low-code-views T-046
        tool-low-code-data T-047
        tool-low-code-page-groups T-048
        tool-low-code-health T-049
        tool-low-code-references T-050
        tool-low-code-scripting T-051
        tool-low-code-hooks T-052
        tool-low-code-endpoints T-053
        tool-low-code-mutations T-054
        tool-low-code-expressions T-055
        tool-low-code-profiles T-056
        tool-contract-tests T-057 DEV-023
        tool-frontend-tests T-058
        tool-samples T-059 DEV-024
        tool-sample-migration T-060
        tool-upgrades T-061 DEV-025 JRN-007
        tool-runtime-upgrades T-062
        tool-deployment-guidance T-063
        low-model-contract LOW-001
        low-revisions LOW-002
        low-drafts LOW-003
        low-designer LOW-004
        low-model-health LOW-005
        low-migration LOW-006
        low-crud-runtime LOW-007
        low-views LOW-008
        low-renderers LOW-009
        low-code-entities LOW-010
        low-data-movement LOW-011
        low-attachments LOW-012
        low-compiled-extensions LOW-013
        low-scripted-extensions LOW-014
        low-business-invariants LOW-015
        low-endpoints LOW-016
        low-runtime-mcp LOW-017
        low-provider-rollout LOW-018
        low-recovery LOW-019
        low-completion LOW-020
        low-calculated-fields LOW-021
        """;

    private static readonly (string Key, string Family)[] FoundationGroups =
    [
        ("product-contracts", "PRD"), ("architecture-contracts", "ARC"), ("foundation-contracts", "FND"),
        ("operations-contracts", "OPS"), ("release-contracts", "REL"), ("acceptance-corpus", "AC"),
        ("foundation-inventory", "F")
    ];

    private static readonly string[] NoLockInRules =
    [
        "1. Each paid service has a self-hosted path in the edition that provides it.",
        "2. Access to your applications and data never requires a Modularyn account.",
        "3. Moving between hosted and self-hosted operation preserves everything in either direction.",
        "4. Paid rights are checked offline and remain valid beyond the life of Modularyn's servers.",
        "5. Ending a subscription ends only the service; it preserves self-hosting of covered releases and every Community right."
    ];

    internal static List<string> Validate(IReadOnlyDictionary<string, string> files)
    {
        try
        {
            foreach (var path in new[] { MapPath, ReadmePath, RegisterPath, NamesAndLayoutContract.CompanionPath })
                Require(files.ContainsKey(path), "edition input is missing: " + path);

            var map = Parse(files[MapPath]);
            Fields(map, "version", "register", "names", "entries", "foundation");
            Require(map.GetProperty("version").ValueKind == JsonValueKind.Number
                && map.GetProperty("version").TryGetInt32(out var version) && version == 1, "edition map version must be 1");
            Require(String(map, "register") == RegisterPath
                && String(map, "names") == NamesAndLayoutContract.CompanionPath, "edition map must name its exact inputs");

            var register = Items(Parse(files[RegisterPath]), "entries")
                .ToDictionary(entry => String(entry, "id"), StringComparer.Ordinal);
            var homes = Homes(Parse(files[NamesAndLayoutContract.CompanionPath]));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var coverage = new Dictionary<string, string>(StringComparer.Ordinal);
            var entries = ReadEntries(map, "entries", keys, coverage, register, homes);
            var foundation = ReadEntries(map, "foundation", keys, coverage, register, homes);
            Require(coverage.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(register.Keys), "every register id must be covered exactly once");
            CheckCatalog(entries, foundation, register);
            CheckReadme(files[ReadmePath], entries, foundation);
            CheckManifests(files, coverage);
            return [];
        }
        catch (ContractViolation error) { return [error.Message]; }
        catch (JsonException) { return ["invalid edition JSON"]; }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            return ["invalid edition document: " + error.GetType().Name];
        }
    }

    private static List<MapEntry> ReadEntries(JsonElement map, string section, HashSet<string> keys,
        Dictionary<string, string> coverage, Dictionary<string, JsonElement> register, Dictionary<string, string> homes)
    {
        var array = map.GetProperty(section);
        Require(array.ValueKind == JsonValueKind.Array, section + " must be an array");
        var entries = new List<MapEntry>();
        foreach (var item in array.EnumerateArray())
        {
            Fields(item, "key", "title", "edition", "home", "coverage", "reason");
            var key = Text(item, "key", 64);
            Require(Matches(key, @"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z"), "malformed edition key");
            Require(keys.Add(key), "duplicate edition key: " + key);
            var title = Text(item, "title", 64);
            var edition = Text(item, "edition", 16);
            Require(edition is "community" or "paid", "edition must be community or paid: " + key);
            var home = Text(item, "home", 80);
            Require(homes.ContainsKey(home), "home must be a declared family or product home: " + key);
            Require(edition != "paid" || home.StartsWith("Modularyn.Pro.", StringComparison.Ordinal), "paid entry requires a Modularyn.Pro home: " + key);
            Require(edition != "community" || !home.StartsWith("Modularyn.Pro.", StringComparison.Ordinal), "Community entry cannot use a Modularyn.Pro home: " + key);
            Require(homes[home] == edition, "home edition must agree with names contract: " + key);
            var reason = Text(item, "reason", 300);
            Require(reason.EndsWith('.') && Regex.Matches(reason, @"[.!?](?:\s|$)", RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)).Count == 1, "reason must be one sentence: " + key);
            var ids = item.GetProperty("coverage");
            Require(ids.ValueKind == JsonValueKind.Array, "coverage must be an array: " + key);
            var references = new List<string>();
            foreach (var value in ids.EnumerateArray())
            {
                Require(value.ValueKind == JsonValueKind.String, "coverage id must be a string: " + key);
                var id = value.GetString()!;
                Require(register.ContainsKey(id), "unknown coverage id: " + id);
                Require(coverage.TryAdd(id, edition), "duplicate coverage id: " + id);
                Require(String(register[id], "edition") == edition, "edition must agree with register: " + id);
                references.Add(id);
            }
            Require(key == "commerce" || references.Count > 0, "coverage must not be empty: " + key);
            entries.Add(new(key, title, edition, home, references));
        }
        return entries;
    }

    private static Dictionary<string, string> Homes(JsonElement names)
    {
        var homes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var family in Items(names, "families"))
        {
            var editions = Items(family, "editions").Select(value => value.GetString()).ToArray();
            Require(editions.SequenceEqual(["community"]) || editions.SequenceEqual(["standard", "pro"]), "names family must identify Community or both paid editions");
            homes.Add(String(family, "prefix"), editions[0] == "community" ? "community" : "paid");
        }
        var families = homes.ToArray();
        foreach (var product in Items(names, "products"))
        {
            var home = String(product, "home");
            if (home == String(names.GetProperty("identifiers"), "toolPackage")) homes[home] = "community";
            else
            {
                var family = families.Single(pair => home.StartsWith(pair.Key[..^1], StringComparison.Ordinal));
                homes[home] = family.Value;
            }
        }
        return homes;
    }

    private static void CheckCatalog(List<MapEntry> entries, List<MapEntry> foundation, Dictionary<string, JsonElement> register)
    {
        var anchors = CatalogAnchors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1..], StringComparer.Ordinal);
        anchors["cms"] = anchors["cms"].Concat(Enumerable.Range(1, 24).Select(number => $"CMS-{number:D3}")).ToArray();
        anchors["ui-contracts"] = Enumerable.Range(1, 14).Select(number => $"UI-{number:D3}").ToArray();
        Require(entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal).SetEquals(anchors.Keys), "exact catalog key set required");
        var commerce = entries.Single(entry => entry.Key == "commerce");
        Require(commerce.Edition == "community" && commerce.Home == "Modularyn.Modules.*" && commerce.Coverage.Count == 0,
            "Commerce must be Community with its explicit unnumbered catalog entry");
        foreach (var entry in entries)
            Require(anchors[entry.Key].All(id => entry.Coverage.Contains(id, StringComparer.Ordinal)), "catalog coverage anchor missing: " + entry.Key);
        Require(foundation.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal).SetEquals(FoundationGroups.Select(group => group.Key)),
            "exact cross-cutting foundation groups required");
        foreach (var (key, family) in FoundationGroups)
        {
            var entry = foundation.Single(entry => entry.Key == key);
            var expected = register.Where(pair => String(pair.Value, "family") == family).Select(pair => pair.Key);
            Require(entry.Edition == "community" && entry.Coverage.ToHashSet(StringComparer.Ordinal).SetEquals(expected),
                "cross-cutting foundation must cover only its complete register family: " + key);
        }
    }

    private static void CheckReadme(string text, List<MapEntry> entries, List<MapEntry> foundation)
    {
        var readme = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var (heading, expected) in new[]
        {
            ("Community catalog", entries.Where(entry => entry.Edition == "community")),
            ("Paid catalog", entries.Where(entry => entry.Edition == "paid")),
            ("Cross-cutting foundation", foundation.AsEnumerable())
        })
        {
            var section = Section(readme, heading);
            var lines = section.Split('\n');
            Require(lines.Count(line => line == TableHeader) == 1, "README needs one catalog table: " + heading);
            var start = Array.IndexOf(lines, TableHeader);
            Require(lines.ElementAtOrDefault(start + 1) == "| --- | --- | --- |", "README catalog table delimiter: " + heading);
            var rows = lines.Skip(start + 2).Where(line => line.StartsWith('|')).ToArray();
            Require(rows.SequenceEqual(expected.Select(entry => $"| `{entry.Key}` | {entry.Title} | `{entry.Home}` |")),
                "README catalog list must agree with map: " + heading);
        }
        var rules = Section(readme, "No-lock-in rules").Split('\n').Where(line => Matches(line, @"\A[0-9]+\. ")).ToArray();
        Require(rules.SequenceEqual(NoLockInRules), "README must state exactly the five no-lock-in rules");
    }

    private static string Section(string text, string heading)
    {
        var marker = "## " + heading + "\n";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Require(start >= 0 && text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal) < 0, "README needs one section: " + heading);
        start += marker.Length;
        var end = text.IndexOf("\n## ", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static void CheckManifests(IReadOnlyDictionary<string, string> files, Dictionary<string, string> coverage)
    {
        var examples = files.Where(file => file.Key.StartsWith(ReleaseManifestContract.ExampleDirectory, StringComparison.Ordinal)
            && file.Key.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        Require(examples.Length > 0, "release-manifest examples are required");
        foreach (var (path, text) in examples)
        {
            var manifest = Parse(text);
            var distribution = String(manifest, "edition");
            Require(distribution is "community" or "standard" or "pro", "manifest distribution edition must be community, standard or pro: " + path);
            var capabilities = manifest.GetProperty("capabilities");
            Require(capabilities.ValueKind == JsonValueKind.Object && capabilities.EnumerateObject().Any(), "manifest needs capabilities: " + path);
            foreach (var capability in capabilities.EnumerateObject())
            {
                Require(coverage.TryGetValue(capability.Name, out var mapped), "manifest capability must occur in map: " + capability.Name);
                Require(mapped != "paid" || distribution != "community", "paid capability cannot appear in a Community manifest: " + capability.Name);
                var expected = mapped == "community" ? "community" : distribution;
                Require(String(capability.Value, "edition") == expected, "manifest capability edition must agree with map: " + capability.Name + " in " + path);
            }
        }
    }

    private static void Fields(JsonElement value, params string[] expected) =>
        Require(value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected),
            "edition fields must be exactly: " + string.Join(", ", expected));

    private static string Text(JsonElement value, string name, int maximum)
    {
        var field = value.GetProperty(name);
        Require(field.ValueKind == JsonValueKind.String, name + " must be a nonempty single-line string");
        var text = field.GetString()!;
        Require(text.Length > 0 && text.Length <= maximum && text.Trim() == text && !text.Any(char.IsControl),
            name + " must be a nonempty single-line string of at most " + maximum + " characters");
        Require(!Conventions.HasMachinePath(text), "edition text must be portable: " + name);
        return text;
    }

    private static bool Matches(string value, string pattern) => Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private sealed record MapEntry(string Key, string Title, string Edition, string Home, List<string> Coverage);
}
