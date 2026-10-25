using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class CoverageRegisterTests
{
    private const string RegisterPath = "docs/coverage/register.json";

    [Fact]
    public void Register_covers_the_pinned_design()
    {
        Assert.True(RepositoryFiles.Current.TryGetValue(RegisterPath, out var text), "The coverage register must exist.");
        Assert.Empty(CoverageRegister.Validate(text!));
    }

    [Fact]
    public void Register_accepts_platform_line_endings()
    {
        var text = RepositoryFiles.Current[RegisterPath].Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.Empty(CoverageRegister.Validate(text));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("family")]
    [InlineData("title")]
    [InlineData("owner")]
    [InlineData("state")]
    [InlineData("edition")]
    [InlineData("gate")]
    [InlineData("acceptance")]
    [InlineData("requirements")]
    [InlineData("evidence")]
    public void Missing_entry_fields_are_rejected(string field)
    {
        var register = ReadRegister();
        register["entries"]![0]!.AsObject().Remove(field);
        Assert.Contains(CoverageRegister.Validate(register.ToJsonString()), error => error.Contains("fields must be exactly", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing-entry", "exact id set")]
    [InlineData("duplicate-entry", "duplicate id")]
    [InlineData("extra-entry", "exact id set")]
    [InlineData("same-count-substitution", "exact id set")]
    [InlineData("entry-order", "design order")]
    [InlineData("wrong-kind", "kind must match")]
    [InlineData("wrong-family", "family must match")]
    [InlineData("empty-owner", "nonempty string")]
    [InlineData("unknown-owner", "owner must name")]
    [InlineData("wrong-state", "state must be Intended")]
    [InlineData("unknown-edition", "edition must be")]
    [InlineData("payments-edition", "edition decision")]
    [InlineData("subscription-edition", "edition decision")]
    [InlineData("acceptance-edition", "edition decision")]
    [InlineData("unknown-gate", "gate must be")]
    [InlineData("long-title", "short title")]
    [InlineData("empty-evidence", "nonempty string")]
    [InlineData("multiline-evidence", "one planning sentence")]
    [InlineData("multiple-sentences", "one planning sentence")]
    [InlineData("unmapped-requirement", "requirement acceptance mapping")]
    [InlineData("unknown-acceptance", "unknown acceptance reference")]
    [InlineData("duplicate-acceptance", "duplicate acceptance reference")]
    [InlineData("incorrect-acceptance", "requirement acceptance mapping")]
    [InlineData("unknown-requirement", "unknown requirements reference")]
    [InlineData("empty-inventory-mapping", "normative mapping")]
    [InlineData("changed-inventory-mapping", "normative mapping")]
    [InlineData("duplicate-requirement", "duplicate requirements reference")]
    [InlineData("incorrect-inventory-acceptance", "inventory acceptance mapping")]
    [InlineData("missing-inventory-audit", "inventory acceptance mapping")]
    [InlineData("noninventory-requirements", "requirements must be empty")]
    [InlineData("acceptance-family-mapping", "acceptance must be empty")]
    [InlineData("null-entry", "entry must be an object")]
    [InlineData("null-entries", "entries must be an array")]
    [InlineData("numeric-id", "nonempty string")]
    [InlineData("nonarray-acceptance", "acceptance must be an array")]
    [InlineData("null-reference", "reference must be a string")]
    [InlineData("extra-field", "fields must be exactly")]
    [InlineData("wrong-version", "version must be 1")]
    [InlineData("string-version", "version must be 1")]
    [InlineData("wrong-design", "pinned design")]
    [InlineData("wrong-hash", "pinned design")]
    [InlineData("wrong-date", "date must be")]
    [InlineData("early-date", "date must be")]
    [InlineData("extra-root-field", "fields must be exactly")]
    public void A_single_register_violation_is_rejected(string mutation, string expectedDiagnostic)
    {
        var register = ReadRegister();
        var entries = register["entries"]!.AsArray();
        var first = entries[0]!;
        var inventory = Entry(entries, "F-001");
        switch (mutation)
        {
            case "missing-entry":
                entries.RemoveAt(entries.Count - 1);
                break;
            case "duplicate-entry":
                entries.Add(first.DeepClone());
                break;
            case "extra-entry":
                var extra = first.DeepClone();
                extra["id"] = "PRD-009";
                entries.Add(extra);
                break;
            case "same-count-substitution":
                entries[^1]!["id"] = "T-064";
                break;
            case "entry-order":
                var moved = entries[0];
                entries.RemoveAt(0);
                entries.Insert(1, moved);
                break;
            case "wrong-kind": first["kind"] = "foundation-row"; break;
            case "wrong-family": first["family"] = "ARC"; break;
            case "empty-owner": first["owner"] = ""; break;
            case "unknown-owner": first["owner"] = "External subsystem"; break;
            case "wrong-state": first["state"] = "Qualified"; break;
            case "unknown-edition": first["edition"] = "enterprise"; break;
            case "payments-edition": Entry(entries, "MOD-025")["edition"] = "paid"; break;
            case "subscription-edition": Entry(entries, "MOD-027")["edition"] = "community"; break;
            case "acceptance-edition": Entry(entries, "AC-022")["edition"] = "paid"; break;
            case "unknown-gate": first["gate"] = "G-10"; break;
            case "long-title": first["title"] = new string('a', 65); break;
            case "empty-evidence": first["evidence"] = ""; break;
            case "multiline-evidence": first["evidence"] = "Plan contract checks across profiles.\nRetain the results."; break;
            case "multiple-sentences": first["evidence"] = "Plan contract checks across profiles. Retain the results."; break;
            case "unmapped-requirement": first["acceptance"] = new JsonArray(); break;
            case "unknown-acceptance": first["acceptance"]![0] = "AC-033"; break;
            case "duplicate-acceptance": first["acceptance"]!.AsArray().Add("AC-001"); break;
            case "incorrect-acceptance": first["acceptance"]![0] = "AC-002"; break;
            case "unknown-requirement": inventory["requirements"]![0] = "MOD-033"; break;
            case "empty-inventory-mapping": inventory["requirements"] = new JsonArray(); break;
            case "changed-inventory-mapping": inventory["requirements"]![0] = "FND-001"; break;
            case "duplicate-requirement": inventory["requirements"]!.AsArray().Add("FND-002"); break;
            case "incorrect-inventory-acceptance": inventory["acceptance"]!.AsArray().Add("AC-002"); break;
            case "missing-inventory-audit":
                var families = inventory["acceptance"]!.AsArray();
                families.Remove(families.Single(value => (string?)value == "AC-032"));
                break;
            case "noninventory-requirements": first["requirements"]!.AsArray().Add("FND-001"); break;
            case "acceptance-family-mapping": Entry(entries, "AC-001")["acceptance"]!.AsArray().Add("AC-001"); break;
            case "null-entry": entries[0] = null; break;
            case "null-entries": register["entries"] = null; break;
            case "numeric-id": first["id"] = 1; break;
            case "nonarray-acceptance": first["acceptance"] = "AC-001"; break;
            case "null-reference": first["acceptance"]![0] = null; break;
            case "extra-field": first["description"] = "Additional prose"; break;
            case "wrong-version": register["version"] = 2; break;
            case "string-version": register["version"] = "1"; break;
            case "wrong-design": register["design"]!["version"] = "2.0"; break;
            case "wrong-hash": register["design"]!["sha256"] = new string('0', 64); break;
            case "wrong-date": register["date"] = "2026-02-30"; break;
            case "early-date": register["date"] = "2026-10-20"; break;
            case "extra-root-field": register["description"] = "Additional prose"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        Assert.Contains(CoverageRegister.Validate(register.ToJsonString()),
            error => error.Contains(expectedDiagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"entries\":{}}")]
    public void Invalid_documents_are_rejected(string text)
    {
        Assert.NotEmpty(CoverageRegister.Validate(text));
    }

    [Fact]
    public void Duplicate_json_properties_are_rejected()
    {
        var text = RepositoryFiles.Current[RegisterPath].Replace("\"state\": \"Intended\"", "\"state\": \"Intended\", \"state\": \"Intended\"");
        Assert.Contains(CoverageRegister.Validate(text), error => error.Contains("duplicate field", StringComparison.Ordinal));
    }

    private static JsonNode ReadRegister() => JsonNode.Parse(RepositoryFiles.Current[RegisterPath])!;

    private static JsonNode Entry(JsonArray entries, string id) => entries.Single(entry => (string?)entry!["id"] == id)!;
}

internal static class CoverageRegister
{
    // Identifier and mapping expectations are transcribed from design 1.0, not inferred from register contents.
    private static readonly (string Prefix, int Count, string Kind)[] Families =
    [
        ("PRD", 8, "requirement"), ("ARC", 18, "requirement"), ("FND", 40, "requirement"),
        ("MOD", 32, "requirement"), ("CMS", 24, "requirement"), ("UI", 14, "requirement"),
        ("DEV", 25, "requirement"), ("LOW", 21, "requirement"), ("OPS", 21, "requirement"),
        ("JRN", 7, "requirement"), ("REL", 8, "requirement"),
        ("F", 106, "foundation-row"), ("T", 63, "tooling-row"), ("AC", 32, "acceptance-family")
    ];

    private static readonly Dictionary<string, string> ExpectedKinds = Families
        .SelectMany(family => Ids(family.Prefix, 1, family.Count).Select(id => (Id: id, family.Kind)))
        .ToDictionary(entry => entry.Id, entry => entry.Kind, StringComparer.Ordinal);
    private static readonly HashSet<string> RequirementIds = ExpectedKinds.Where(entry => entry.Value == "requirement")
        .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> AcceptanceIds = Ids("AC", 1, 32).ToHashSet(StringComparer.Ordinal);
    private static readonly string[] DesignOrder = Families.Where(family => family.Kind == "requirement" && family.Prefix != "REL")
        .SelectMany(family => Ids(family.Prefix, 1, family.Count))
        .Concat(Ids("REL", 1, 4)).Concat(Ids("AC", 1, 32)).Concat(Ids("REL", 5, 8))
        .Concat(Ids("F", 1, 106)).Concat(Ids("T", 1, 63)).ToArray();
    private static readonly HashSet<string> Owners = new(StringComparer.Ordinal)
    {
        "AI", "API", "Accounts", "Admin", "Audit", "Authorization", "Authorization Server", "Automation",
        "Caching", "Capabilities", "Communications", "Configuration", "Content", "Content and Engagement",
        "Diagnostics", "Documentation", "Events", "Files", "Forms", "Framework composition", "Generator",
        "Identity", "Identity Migration", "Jobs", "Limits", "Localization", "Low-code", "Messaging",
        "Operations", "Payments", "Persistence", "Privacy", "Publishing", "Release governance", "Runtime",
        "SDK toolchain", "Search", "Security", "Studio", "Templates", "Tenancy", "Tenancy and Subscriptions", "Tooling"
    };
    private static readonly HashSet<string> Gates = Enumerable.Range(1, 9)
        .Select(number => $"G-{number:D2}").Append("W").ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> PaidIds = Ids("LOW", 1, 21)
        .Concat(new[] { "MOD-003", "MOD-012", "MOD-027", "DEV-014", "DEV-019", "DEV-020", "DEV-021", "JRN-002", "JRN-004", "JRN-005" })
        .Concat(Ids("T", 22, 24)).Concat(Ids("T", 35, 40)).Concat(Ids("T", 42, 56))
        .ToHashSet(StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> RequirementAcceptance = BuildRequirementAcceptance();
    private static readonly Dictionary<string, string[]> InventoryRequirements = InventoryMappings
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToDictionary(parts => parts[0], parts => parts[1..], StringComparer.Ordinal);
    private static readonly string[] EntryFields =
        ["id", "kind", "family", "title", "owner", "state", "edition", "gate", "acceptance", "requirements", "evidence"];
    private static readonly DateOnly DesignDate = new(2026, 10, 21);

    public static List<string> Validate(string text)
    {
        var errors = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Register must be an object.");
                return errors;
            }

            Fields(root, ["version", "design", "date", "entries"], "register", errors);
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 1)
            {
                errors.Add("Register version must be 1.");
            }

            if (!root.TryGetProperty("design", out var design) || design.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Register must identify the pinned design.");
            }
            else
            {
                Fields(design, ["title", "version", "date", "sha256"], "design", errors);
                foreach (var (field, expected) in new[]
                {
                    ("title", "Modularyn Product Design"), ("version", "1.0"), ("date", "2026-10-21"),
                    ("sha256", "290c432a8eb9bfaac45c46ce948c2221bddde4248f9ff05552847a87c1db8f8d")
                })
                {
                    if (String(design, field, "design", errors) != expected)
                    {
                        errors.Add($"The pinned design {field} must be {expected}.");
                    }
                }
            }

            var date = String(root, "date", "register", errors);
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                || parsedDate < DesignDate)
            {
                errors.Add("Register date must be an ISO date on or after the design date.");
            }

            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                errors.Add("Register entries must be an array.");
                return errors;
            }

            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Each entry must be an object.");
                    continue;
                }

                Fields(entry, EntryFields, "entry", errors);
                var id = String(entry, "id", "entry", errors);
                ids.Add(id);
                if (!seen.Add(id)) errors.Add($"{id}: duplicate id.");
                var kind = String(entry, "kind", id, errors);
                var family = String(entry, "family", id, errors);
                if (!ExpectedKinds.TryGetValue(id, out var expectedKind) || kind != expectedKind)
                {
                    errors.Add($"{id}: kind must match a design identifier.");
                }
                if (!ExpectedKinds.ContainsKey(id) || family != id.Split('-')[0])
                {
                    errors.Add($"{id}: family must match its identifier prefix.");
                }

                var title = String(entry, "title", id, errors);
                if (title.Length > 64 || Words(title) > 6 || title.Any(char.IsControl))
                {
                    errors.Add($"{id}: require a short title of at most six words and 64 characters.");
                }
                if (!Owners.Contains(String(entry, "owner", id, errors)))
                {
                    errors.Add($"{id}: owner must name an allowed Modularyn subsystem.");
                }
                if (String(entry, "state", id, errors) != "Intended") errors.Add($"{id}: state must be Intended.");
                var edition = String(entry, "edition", id, errors);
                if (edition is not ("community" or "paid")) errors.Add($"{id}: edition must be community or paid.");
                if (edition != (PaidIds.Contains(id) ? "paid" : "community")) errors.Add($"{id}: edition decision does not match.");
                if (!Gates.Contains(String(entry, "gate", id, errors))) errors.Add($"{id}: gate must be G-01 through G-09 or W.");

                var evidence = String(entry, "evidence", id, errors);
                if (Words(evidence) < 8 || Words(evidence) > 60 || evidence.Length > 400 || evidence.Any(char.IsControl)
                    || !evidence.EndsWith('.') || Regex.Matches(evidence, @"[.!?](?:\s|$)").Count != 1)
                {
                    errors.Add($"{id}: evidence must be one planning sentence of 8 to 60 words and at most 400 characters.");
                }
                if (Conventions.HasMachinePath(title) || Conventions.HasMachinePath(evidence))
                {
                    errors.Add($"{id}: planning text must be portable.");
                }

                var acceptance = References(entry, "acceptance", AcceptanceIds, id, errors);
                var requirements = References(entry, "requirements", RequirementIds, id, errors);
                if (RequirementAcceptance.TryGetValue(id, out var expectedAcceptance))
                {
                    if (!acceptance.SequenceEqual(expectedAcceptance)) errors.Add($"{id}: requirement acceptance mapping must match the design map.");
                }
                if (InventoryRequirements.TryGetValue(id, out var expectedRequirements))
                {
                    if (!requirements.SequenceEqual(expectedRequirements)) errors.Add($"{id}: normative mapping must match the design row.");
                    var inherited = expectedRequirements.SelectMany(requirement => RequirementAcceptance[requirement])
                        .Append("AC-032").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                    if (!acceptance.SequenceEqual(inherited)) errors.Add($"{id}: inventory acceptance mapping must inherit its requirements and AC-032.");
                }
                else if (requirements.Length != 0)
                {
                    errors.Add($"{id}: requirements must be empty outside inventory rows.");
                }
                if (AcceptanceIds.Contains(id) && acceptance.Length != 0) errors.Add($"{id}: acceptance must be empty for acceptance families.");
            }

            if (ids.Count != 419 || !seen.SetEquals(ExpectedKinds.Keys)) errors.Add("Register must contain the exact id set of 419 design entries.");
            foreach (var (prefix, count, _) in Families)
            {
                if (ids.Count(id => id.StartsWith(prefix + "-", StringComparison.Ordinal)) != count)
                {
                    errors.Add($"{prefix}: exact family count must be {count}.");
                }
            }
            if (!ids.SequenceEqual(DesignOrder)) errors.Add("Entries must follow design order, including the acceptance corpus within the release section.");
        }
        catch (JsonException)
        {
            errors.Add("Register must contain valid JSON.");
        }
        return errors;
    }

    private static void Fields(JsonElement element, string[] expected, string location, List<string> errors)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) errors.Add($"{location}: duplicate field.");
        if (names.Length != expected.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            errors.Add($"{location}: fields must be exactly {string.Join(", ", expected)}.");
        }
    }

    private static string String(JsonElement element, string field, string location, List<string> errors)
    {
        if (element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (!string.IsNullOrWhiteSpace(text) && text == text.Trim()) return text;
        }
        errors.Add($"{location}.{field}: require a nonempty string without surrounding whitespace.");
        return "";
    }

    private static string[] References(JsonElement entry, string field, HashSet<string> allowed, string id, List<string> errors)
    {
        if (!entry.TryGetProperty(field, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{id}: {field} must be an array.");
            return [];
        }
        var references = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{id}: {field} reference must be a string.");
                continue;
            }
            var reference = value.GetString()!;
            references.Add(reference);
            if (!allowed.Contains(reference)) errors.Add($"{id}: unknown {field} reference {reference}.");
        }
        if (references.Distinct(StringComparer.Ordinal).Count() != references.Count) errors.Add($"{id}: duplicate {field} reference.");
        return references.ToArray();
    }

    private static int Words(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static IEnumerable<string> Ids(string prefix, int first, int last) =>
        Enumerable.Range(first, last - first + 1).Select(number => $"{prefix}-{number:D3}");

    private static Dictionary<string, string[]> BuildRequirementAcceptance()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string prefix, int first, int last, params int[] families)
        {
            var acceptance = families.Select(number => $"AC-{number:D3}").ToArray();
            foreach (var id in Ids(prefix, first, last)) map.Add(id, acceptance);
        }

        Add("PRD", 1, 8, 1, 23, 24, 25, 26, 27, 28, 31, 32);
        Add("ARC", 1, 4, 1, 5, 6, 28);
        Add("ARC", 5, 8, 2, 3, 4, 5, 10);
        Add("ARC", 9, 11, 6, 7, 11, 17, 21, 29);
        Add("ARC", 12, 15, 24, 26, 27, 28, 31);
        Add("ARC", 16, 18, 3, 4, 9, 13, 17, 18, 19, 22);
        Add("FND", 1, 6, 1, 24, 28, 31);
        Add("FND", 7, 12, 5, 6, 28, 29);
        Add("FND", 13, 19, 2, 3, 4, 10, 15);
        Add("FND", 20, 25, 6, 7, 13, 29, 30);
        Add("FND", 26, 33, 8, 9, 13, 14);
        Add("FND", 34, 40, 12, 22, 25, 28, 29, 30, 31);
        foreach (var number in new[] { 1, 2, 17, 18, 19, 20, 23, 24 }) Add("MOD", number, number, 2, 3, 4, 8, 28, 31);
        foreach (var number in new[] { 13, 26, 27, 28, 29 }) Add("MOD", number, number, 3, 4, 10, 11);
        Add("MOD", 25, 25, 11, 29);
        foreach (var number in new[] { 4, 5, 6, 22 }) Add("MOD", number, number, 7, 12, 30);
        foreach (var number in new[] { 8, 14, 21, 30, 31 }) Add("MOD", number, number, 9, 13, 14);
        Add("MOD", 16, 16, 15, 29);
        foreach (var number in new[] { 3, 12, 15 }) Add("MOD", number, number, 20, 21, 22);
        foreach (var number in new[] { 7, 9, 10, 11 }) Add("MOD", number, number, 16, 17, 18, 19);
        Add("MOD", 32, 32, 25, 31);
        Add("CMS", 1, 10, 9, 16, 17, 18);
        Add("CMS", 11, 20, 17, 18, 19, 20);
        Add("CMS", 21, 24, 15, 28, 29, 30);
        Add("UI", 1, 14, 2, 3, 4, 14, 18, 19, 20, 21, 22, 23);
        Add("DEV", 1, 25, 1, 8, 24, 25, 27, 28);
        Add("LOW", 1, 21, 3, 4, 5, 20, 21, 26, 27);
        Add("OPS", 1, 8, 2, 3, 4, 9, 12, 15, 22, 31);
        Add("OPS", 9, 15, 10, 25, 28, 29);
        Add("OPS", 16, 21, 23, 30, 31);
        // The design maps journeys by scenario and release governance to all applicable cases.
        Add("JRN", 1, 1, 1, 8, 24, 25, 28);
        Add("JRN", 2, 2, 2, 3, 4, 6, 10, 11, 29);
        Add("JRN", 3, 3, 9, 14, 16, 17, 18);
        Add("JRN", 4, 4, 3, 4, 6, 7, 20, 21, 28);
        Add("JRN", 5, 5, 3, 4, 9, 18, 22);
        Add("JRN", 6, 6, 4, 10, 15, 29);
        Add("JRN", 7, 7, 5, 6, 11, 28, 29);
        Add("REL", 1, 8, Enumerable.Range(1, 32).ToArray());
        return map;
    }

    private const string InventoryMappings = """
        F-001 FND-002 OPS-010
        F-002 FND-001 ARC-001
        F-003 FND-003 ARC-014
        F-004 FND-004
        F-005 FND-006
        F-006 FND-006
        F-007 FND-004
        F-008 FND-005
        F-009 FND-005
        F-010 FND-005
        F-011 FND-006
        F-012 FND-007 ARC-003
        F-013 ARC-001 ARC-004 DEV-003
        F-014 DEV-005 DEV-024 ARC-003
        F-015 FND-007
        F-016 FND-007
        F-017 FND-007
        F-018 FND-007
        F-019 FND-007
        F-020 FND-008 ARC-007
        F-021 FND-009
        F-022 FND-008 ARC-007
        F-023 FND-011
        F-024 FND-008 ARC-007
        F-025 FND-008 ARC-007
        F-026 FND-008 ARC-007
        F-027 FND-008 ARC-007
        F-028 FND-010
        F-029 FND-011
        F-030 FND-010
        F-031 FND-012
        F-032 FND-012
        F-033 FND-012
        F-034 FND-013 FND-017
        F-035 FND-015 MOD-026
        F-036 FND-016 ARC-016
        F-037 FND-017
        F-038 FND-013 FND-014
        F-039 FND-014 OPS-002
        F-040 FND-014 OPS-002
        F-041 FND-018
        F-042 FND-018
        F-043 FND-018
        F-044 FND-019
        F-045 FND-019
        F-046 FND-019
        F-047 FND-015 FND-033
        F-048 FND-033 OPS-004
        F-049 ARC-014 OPS-001
        F-050 FND-020
        F-051 FND-020
        F-052 FND-020 FND-021
        F-053 FND-020 FND-021
        F-054 FND-020 FND-021
        F-055 FND-020 FND-021
        F-056 FND-020 FND-021
        F-057 FND-022 FND-023
        F-058 FND-022 FND-023
        F-059 FND-022 FND-023
        F-060 FND-022 FND-023
        F-061 FND-022 FND-023
        F-062 FND-023
        F-063 FND-023
        F-064 FND-024
        F-065 FND-025
        F-066 FND-025
        F-067 FND-025
        F-068 FND-025
        F-069 FND-025
        F-070 FND-030
        F-071 FND-030
        F-072 FND-030
        F-073 FND-030
        F-074 FND-030
        F-075 FND-031
        F-076 FND-031
        F-077 FND-031
        F-078 FND-032 ARC-017
        F-079 FND-032 ARC-017
        F-080 FND-033
        F-081 FND-033
        F-082 FND-029 UI-013
        F-083 FND-033
        F-084 FND-033
        F-085 FND-026
        F-086 FND-026
        F-087 FND-027 ARC-010
        F-088 FND-029
        F-089 FND-029
        F-090 FND-028 DEV-012
        F-091 FND-028 DEV-012
        F-092 FND-028 DEV-012
        F-093 FND-027
        F-094 FND-027
        F-095 FND-034
        F-096 FND-034
        F-097 FND-034
        F-098 FND-034
        F-099 FND-035 FND-036
        F-100 FND-037 OPS-007
        F-101 FND-038 OPS-014
        F-102 FND-040 DEV-023
        F-103 OPS-009 OPS-010 FND-040
        F-104 FND-038 OPS-014
        F-105 FND-039 OPS-013
        F-106 FND-040 REL-006
        T-001 DEV-003 ARC-001 ARC-004
        T-002 DEV-003 UI-001 UI-014
        T-003 DEV-003 DEV-004 UI-003
        T-004 DEV-003 UI-003
        T-005 DEV-004 FND-008 MOD-023
        T-006 DEV-001 DEV-002 DEV-005
        T-007 DEV-002 DEV-025 FND-001
        T-008 UI-003 UI-007 UI-010 DEV-012
        T-009 UI-003 UI-010 DEV-012
        T-010 UI-003 UI-004 DEV-023
        T-011 UI-003 UI-004 DEV-023
        T-012 UI-003 UI-004 DEV-023
        T-013 UI-003 UI-014 DEV-023
        T-014 UI-004 DEV-011
        T-015 UI-002 UI-003 DEV-012
        T-016 UI-002 UI-005 UI-006
        T-017 UI-003 UI-014 DEV-024
        T-018 UI-011
        T-019 UI-011
        T-020 UI-011 DEV-025
        T-021 UI-011 UI-012
        T-022 DEV-014
        T-023 DEV-015
        T-024 DEV-016
        T-025 DEV-017 OPS-009
        T-026 DEV-001 DEV-025
        T-027 DEV-001 DEV-005 DEV-012
        T-028 DEV-001 OPS-004
        T-029 DEV-007 DEV-008
        T-030 DEV-007 DEV-008
        T-031 DEV-009
        T-032 DEV-010 DEV-011
        T-033 DEV-012 DEV-013
        T-034 DEV-012 DEV-013
        T-035 DEV-019 DEV-021
        T-036 DEV-020
        T-037 DEV-019 DEV-020 DEV-021
        T-038 DEV-015 DEV-020
        T-039 DEV-021
        T-040 DEV-022
        T-041 DEV-022
        T-042 LOW-017
        T-043 MOD-003 FND-034 FND-035 FND-036
        T-044 LOW-001 LOW-004 LOW-005
        T-045 LOW-002 LOW-006 LOW-007 LOW-009
        T-046 LOW-008
        T-047 LOW-011 LOW-012
        T-048 LOW-001 LOW-005
        T-049 LOW-005
        T-050 LOW-010
        T-051 LOW-014 ARC-014
        T-052 LOW-013 LOW-015
        T-053 LOW-016
        T-054 LOW-017
        T-055 LOW-021 LOW-006
        T-056 LOW-018 LOW-020 PRD-008
        T-057 DEV-023 REL-002
        T-058 DEV-023 UI-012
        T-059 DEV-024
        T-060 DEV-024 DEV-025
        T-061 DEV-025 REL-006
        T-062 DEV-025 FND-001
        T-063 OPS-009 OPS-010 OPS-012
        """;
}
