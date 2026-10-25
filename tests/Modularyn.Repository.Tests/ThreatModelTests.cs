using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ThreatModelTests
{
    private const string RegisterPath = "docs/coverage/register.json";

    [Fact]
    public void Threat_models_agree_with_the_control_map_and_register()
    {
        Assert.True(RepositoryFiles.Current.TryGetValue(ThreatModelContract.MapPath, out var map), "The control map must exist.");
        Assert.Empty(Validate(map!, Documents()));
    }

    [Fact]
    public void Threat_models_accept_platform_line_endings()
    {
        var documents = Documents().ToDictionary(pair => pair.Key, pair => Crlf(pair.Value), StringComparer.Ordinal);
        Assert.Empty(Validate(Crlf(RepositoryFiles.Current[ThreatModelContract.MapPath]), documents));
    }

    [Theory]
    [InlineData("duplicate-control", "duplicate control id")]
    [InlineData("malformed-control-id", "control id must be TM-G02 or TM-W")]
    [InlineData("unordered-controls", "ordered by model and number")]
    [InlineData("blank-title", ".title: must be a nonempty string")]
    [InlineData("multiline-title", "title must be a single line")]
    [InlineData("long-title", "title must be a single line")]
    [InlineData("missing-field", "fields must be exactly")]
    [InlineData("extra-field", "fields must be exactly")]
    [InlineData("extra-evidence-field", ".evidence: fields must be exactly")]
    [InlineData("nonobject-evidence", "evidence must be an object")]
    [InlineData("unknown-owner", "owner must name a register owner")]
    [InlineData("owner-without-requirement", "owner must own at least one listed requirement")]
    [InlineData("unknown-requirement", "must name a register requirement")]
    [InlineData("acceptance-as-requirement", "AC-001 must name a register requirement")]
    [InlineData("duplicate-requirement", "duplicate requirements reference")]
    [InlineData("empty-requirements", "requirements must be a nonempty array")]
    [InlineData("unknown-acceptance", "unknown acceptance family")]
    [InlineData("unmapped-acceptance", "is not mapped to a listed requirement")]
    [InlineData("empty-acceptance", "acceptance must be a nonempty array")]
    [InlineData("unknown-layer", "unknown test layer manual")]
    [InlineData("duplicate-layer", "duplicate layers reference")]
    [InlineData("unknown-profile", "profile must be reference or website")]
    [InlineData("missing-reference-profile", "must name the reference profile")]
    [InlineData("missing-website-profile", "must name the website profile")]
    [InlineData("unknown-threat", "is not defined in the prose")]
    [InlineData("malformed-threat", "threat id TH-G02-1 must be TH-G02 or TH-W")]
    [InlineData("empty-threats", "threats must be a nonempty array")]
    [InlineData("cross-model-threat", "may address only website threats")]
    [InlineData("foundation-without-foundation-threat", "must address at least one foundation threat")]
    [InlineData("unknown-boundary", "is not defined in the prose")]
    [InlineData("malformed-boundary", "boundary must be TB-G02 or TB-W")]
    [InlineData("cross-model-boundary", "boundary must belong to the control's model")]
    [InlineData("boundary-without-control", "has no control in the map")]
    [InlineData("changed-threat-mapping", "controls listed in the prose differ from the map")]
    [InlineData("removed-control", "is not in the map")]
    [InlineData("uncited-control", "is not cited in the prose")]
    [InlineData("wrong-version", "version must be 1")]
    [InlineData("string-version", "version must be 1")]
    [InlineData("invalid-date", "date must be an ISO calendar date")]
    [InlineData("extra-root-field", "control map: fields must be exactly")]
    [InlineData("null-control", "control must be an object")]
    [InlineData("nonarray-controls", "controls must be a nonempty array")]
    public void A_single_control_map_violation_is_rejected(string mutation, string diagnostic)
    {
        var map = JsonNode.Parse(RepositoryFiles.Current[ThreatModelContract.MapPath])!;
        var controls = map["controls"]!.AsArray();
        var first = controls[0]!;
        var website = controls.First(control => Id(control!).StartsWith("TM-W-", StringComparison.Ordinal))!;
        // When set, the diagnostic must also name this identifier, derived so that it stays absent as the data grows.
        string? expected = null;
        switch (mutation)
        {
            case "duplicate-control": controls.Add(first.DeepClone()); break;
            case "malformed-control-id": first["id"] = "TM-G02-1"; break;
            case "unordered-controls":
                var last = controls[^1]!;
                controls.RemoveAt(controls.Count - 1);
                controls.Insert(0, last);
                break;
            case "blank-title": first["title"] = " "; break;
            case "multiline-title": first["title"] = "Validate the graph\nbefore activation"; break;
            case "long-title": first["title"] = new string('a', 101); break;
            case "missing-field": first.AsObject().Remove("owner"); break;
            case "extra-field": first["status"] = "planned"; break;
            case "extra-evidence-field": first["evidence"]!["notes"] = "Additional prose"; break;
            case "nonobject-evidence": first["evidence"] = "AC-001"; break;
            case "unknown-owner": first["owner"] = AbsentOwner(); break;
            case "owner-without-requirement": first["owner"] = OwnerOutside(first); break;
            case "unknown-requirement": first["requirements"]![0] = expected = AbsentRegisterId("FND"); break;
            case "acceptance-as-requirement": first["requirements"]![0] = "AC-001"; break;
            case "duplicate-requirement": first["requirements"]!.AsArray().Add(Strings(first["requirements"]!)[0]); break;
            case "empty-requirements": first["requirements"] = new JsonArray(); break;
            case "unknown-acceptance": first["evidence"]!["acceptance"]![0] = expected = AbsentRegisterId("AC"); break;
            case "unmapped-acceptance": first["evidence"]!["acceptance"]!.AsArray().Add(expected = FamilyOutside(first)); break;
            case "empty-acceptance": first["evidence"]!["acceptance"] = new JsonArray(); break;
            case "unknown-layer": first["evidence"]!["layers"]![0] = "manual"; break;
            case "duplicate-layer": first["evidence"]!["layers"]!.AsArray().Add(Strings(first["evidence"]!["layers"]!)[0]); break;
            case "unknown-profile": first["profiles"]![0] = "enterprise"; break;
            case "missing-reference-profile": first["profiles"] = new JsonArray("website"); break;
            case "missing-website-profile":
                var reused = controls.First(control => Id(control!).StartsWith("TM-G02-", StringComparison.Ordinal)
                    && Strings(control!["threats"]!).Any(threat => threat.StartsWith("TH-W-", StringComparison.Ordinal)))!;
                reused["profiles"] = new JsonArray("reference");
                expected = Id(reused);
                break;
            case "unknown-threat": first["threats"]!.AsArray().Add(expected = AbsentModelId("TH-G02", 3)); break;
            case "malformed-threat": first["threats"]![0] = "TH-G02-1"; break;
            case "empty-threats": first["threats"] = new JsonArray(); break;
            case "cross-model-threat": website["threats"]!.AsArray().Add(Strings(first["threats"]!)[0]); break;
            case "foundation-without-foundation-threat": first["threats"] = new JsonArray(Strings(website["threats"]!)[0]); break;
            case "unknown-boundary": first["boundary"] = expected = AbsentModelId("TB-G02", 2); break;
            case "malformed-boundary": first["boundary"] = "TB-G02-1"; break;
            case "cross-model-boundary": website["boundary"] = (string?)first["boundary"]; break;
            case "boundary-without-control":
                expected = (string?)controls.Last(control => Id(control!).StartsWith("TM-G02-", StringComparison.Ordinal))!["boundary"];
                foreach (var control in controls.Where(control => (string?)control!["boundary"] == expected))
                {
                    control!["boundary"] = (string?)first["boundary"];
                }
                break;
            case "changed-threat-mapping":
                var addressed = Strings(first["threats"]!);
                expected = controls.Skip(1).SelectMany(control => Strings(control!["threats"]!))
                    .First(threat => threat.StartsWith("TH-G02-", StringComparison.Ordinal) && !addressed.Contains(threat));
                first["threats"]!.AsArray().Add(expected);
                break;
            case "removed-control":
                expected = Id(first);
                controls.RemoveAt(0);
                break;
            case "uncited-control":
                var copy = first.DeepClone();
                copy["id"] = expected = AbsentModelId("TM-G02", 3);
                controls.Insert(controls.IndexOf(website), copy);
                break;
            case "wrong-version": map["version"] = 2; break;
            case "string-version": map["version"] = "1"; break;
            case "invalid-date": map["date"] = "2026-02-30"; break;
            case "extra-root-field": map["description"] = "Additional prose"; break;
            case "null-control": controls[0] = null; break;
            case "nonarray-controls": map["controls"] = "TM-G02-001"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        AssertDiagnostic(Validate(map.ToJsonString(), Documents()), diagnostic, expected);
    }

    [Theory]
    [InlineData("missing-document", "required threat-model document is missing")]
    [InlineData("missing-section", "missing required section 'Abuse cases'")]
    [InlineData("empty-section", "section 'Privileged operations' is empty")]
    [InlineData("unknown-category", "category must be a STRIDE category or Abuse case")]
    [InlineData("abuse-category-under-boundary", "under a boundary heading must use a STRIDE category")]
    [InlineData("stride-category-in-abuse-cases", "must use the Abuse case category")]
    [InlineData("threat-without-control", "must list at least one control")]
    [InlineData("malformed-control-reference", "comma-separated list of control identifiers")]
    [InlineData("blank-description", "description must be nonblank")]
    [InlineData("duplicate-threat", "is defined more than once")]
    [InlineData("misplaced-threat", "belongs to another model")]
    [InlineData("threat-outside-boundary", "must appear under a boundary heading")]
    [InlineData("unmapped-threat", "threat has no control in the map")]
    [InlineData("duplicate-boundary", "is defined more than once")]
    [InlineData("added-boundary", "has no control in the map")]
    [InlineData("added-boundary", "has no threat")]
    [InlineData("undefined-cited-threat", "is not defined in a threat model")]
    [InlineData("undefined-cited-control", "is not in the map")]
    [InlineData("undefined-cited-boundary", "is not defined")]
    [InlineData("malformed-citation", "malformed identifier TM-G02-01")]
    [InlineData("undescribed-control", "control has no row in its model's control table")]
    [InlineData("duplicate-control-row", "is described more than once")]
    [InlineData("misplaced-control-row", "belongs to another model")]
    [InlineData("prose-owner", "owner in the prose control table differs from the map")]
    [InlineData("unknown-classification", "classification must be Public, Internal, Confidential, Restricted or Secret")]
    [InlineData("unknown-integrity", "integrity must be Critical or Standard")]
    [InlineData("draft-marker", "contains an unfinished-draft marker")]
    public void A_single_prose_violation_is_rejected(string mutation, string diagnostic)
    {
        var documents = Documents();
        const string G02 = ThreatModelContract.G02Path;
        const string Readme = ThreatModelContract.ReadmePath;
        var foundation = documents[G02];
        // When set, the diagnostic must also name this identifier, derived so that it stays absent as the data grows.
        string? expected = null;
        switch (mutation)
        {
            case "missing-document": documents.Remove(ThreatModelContract.WebsitePath); break;
            case "missing-section": documents[G02] = foundation.Replace("\n## Abuse cases\n", "\n", StringComparison.Ordinal); break;
            case "empty-section":
                var start = foundation.IndexOf("## Privileged operations\n", StringComparison.Ordinal) + "## Privileged operations\n".Length;
                documents[G02] = foundation[..start] + "\n" + foundation[foundation.IndexOf("## External inputs", StringComparison.Ordinal)..];
                break;
            case "unknown-category": documents[G02] = ChangeRow(foundation, "| TH-G02-", cells => [cells[0], "Phishing", cells[2], cells[3]]); break;
            case "abuse-category-under-boundary":
                documents[G02] = ChangeRow(foundation, "| TH-G02-", cells => [cells[0], "Abuse case", cells[2], cells[3]]);
                break;
            case "stride-category-in-abuse-cases":
                documents[G02] = ChangeRow(foundation, "| TH-G02-", cells => [cells[0], "Tampering", cells[2], cells[3]], last: true);
                break;
            case "threat-without-control": documents[G02] = ChangeRow(foundation, "| TH-G02-", cells => [cells[0], cells[1], cells[2], ""]); break;
            case "malformed-control-reference":
                documents[G02] = ChangeRow(foundation, "| TH-G02-", cells => [cells[0], cells[1], cells[2], cells[3] + " and TM-G02-002"]);
                break;
            case "blank-description": documents[G02] = ChangeRow(foundation, "| TH-G02-", cells => [cells[0], cells[1], "", cells[3]]); break;
            case "duplicate-threat":
                expected = FirstRowId(foundation, "| TH-G02-");
                documents[G02] = DuplicateRow(foundation, "| TH-G02-");
                break;
            case "misplaced-threat":
                var foreign = AbsentModelId("TH-W", 3);
                expected = foreign;
                documents[G02] = ChangeRow(foundation, "| TH-G02-", cells => [foreign, cells[1], cells[2], cells[3]]);
                break;
            case "threat-outside-boundary":
                expected = AbsentModelId("TH-G02", 3);
                documents[G02] = foundation.Replace("## Threats\n",
                    "## Threats\n\n| " + expected + " | Tampering | An unplaced threat. | TM-G02-001 |\n", StringComparison.Ordinal);
                break;
            case "unmapped-threat":
                expected = AbsentModelId("TH-G02", 3);
                documents[G02] = InsertAfter(foundation, "| TH-G02-", "| " + expected + " | Abuse case | An added abuse case. | TM-G02-001 |");
                break;
            case "duplicate-boundary":
                expected = FirstRowId(foundation, "| TB-G02-");
                documents[G02] = DuplicateRow(foundation, "| TB-G02-");
                break;
            case "added-boundary":
                expected = AbsentModelId("TB-G02", 2);
                documents[G02] = InsertAfter(foundation, "| TB-G02-", "| " + expected + " | Added boundary | One side | Other side |");
                break;
            case "undefined-cited-threat": documents[Readme] += "\n" + (expected = AbsentModelId("TH-G02", 3)) + "\n"; break;
            case "undefined-cited-control": documents[Readme] += "\n" + (expected = AbsentModelId("TM-W", 3)) + "\n"; break;
            case "undefined-cited-boundary": documents[Readme] += "\n" + (expected = AbsentModelId("TB-W", 2)) + "\n"; break;
            case "malformed-citation": documents[Readme] += "\nTM-G02-01\n"; break;
            case "undescribed-control":
                expected = FirstRowId(foundation, "| TM-G02-");
                documents[G02] = RemoveRow(foundation, "| " + expected + " |");
                break;
            case "duplicate-control-row":
                expected = FirstRowId(foundation, "| TM-G02-");
                documents[G02] = DuplicateRow(foundation, "| TM-G02-");
                break;
            case "misplaced-control-row":
                var described = FirstRowId(documents[ThreatModelContract.WebsitePath], "| TM-W-");
                expected = described;
                documents[G02] = ChangeRow(foundation, "| TM-G02-", cells => [described, cells[1], cells[2]]);
                break;
            case "prose-owner": documents[G02] = ChangeRow(foundation, "| TM-G02-", cells => [cells[0], "Security", cells[2]]); break;
            case "unknown-classification": documents[G02] = ChangeAsset(foundation, cells => [cells[0], "Top secret", cells[2], cells[3]]); break;
            case "unknown-integrity": documents[G02] = ChangeAsset(foundation, cells => [cells[0], cells[1], "Severe", cells[3]]); break;
            // Assemble the marker so this source file does not itself contain one.
            case "draft-marker": documents[Readme] += "\n" + "TO" + "DO" + "\n"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        AssertDiagnostic(Validate(RepositoryFiles.Current[ThreatModelContract.MapPath], documents), diagnostic, expected);
    }

    [Theory]
    [InlineData("{", "Control map must contain valid JSON")]
    [InlineData("null", "Control map must be an object")]
    [InlineData("[]", "Control map must be an object")]
    [InlineData("{\"controls\":{}}", "Control map controls must be a nonempty array")]
    public void Invalid_control_maps_are_rejected(string text, string diagnostic)
    {
        AssertDiagnostic(Validate(text, Documents()), diagnostic, null);
    }

    [Fact]
    public void Duplicate_json_properties_are_rejected()
    {
        var text = RepositoryFiles.Current[ThreatModelContract.MapPath]
            .Replace("\"owner\": \"Framework composition\"", "\"owner\": \"Framework composition\", \"owner\": \"Framework composition\"", StringComparison.Ordinal);
        AssertDiagnostic(Validate(text, Documents()), "duplicate JSON property", null);
    }

    private static void AssertDiagnostic(List<string> errors, string diagnostic, string? expected) =>
        Assert.Contains(errors, error => error.Contains(diagnostic, StringComparison.Ordinal)
            && (expected is null || error.Contains(expected, StringComparison.Ordinal)));

    private static List<string> Validate(string map, IReadOnlyDictionary<string, string> documents) =>
        ThreatModelContract.Validate(map, RepositoryFiles.Current[RegisterPath], documents);

    private static Dictionary<string, string> Documents() => ThreatModelContract.DocumentPaths
        .Where(RepositoryFiles.Current.ContainsKey)
        .ToDictionary(path => path, path => RepositoryFiles.Current[path].Replace("\r\n", "\n", StringComparison.Ordinal), StringComparer.Ordinal);

    private static string Crlf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private static string Id(JsonNode control) => (string?)control["id"] ?? "";

    private static string[] Strings(JsonNode values) => values.AsArray().Select(value => (string?)value ?? "").ToArray();

    private static JsonArray RegisterEntries() => JsonNode.Parse(RepositoryFiles.Current[RegisterPath])!["entries"]!.AsArray();

    private static JsonNode[] RequirementsOf(JsonNode control)
    {
        var listed = Strings(control["requirements"]!);
        return RegisterEntries().Where(entry => listed.Contains((string?)entry!["id"] ?? "")).Select(entry => entry!).ToArray();
    }

    // One past the register's highest number in a family, so the identifier cannot exist yet.
    private static string AbsentRegisterId(string family)
    {
        var highest = RegisterEntries().Select(entry => (string?)entry!["id"] ?? "")
            .Where(id => id.StartsWith(family + "-", StringComparison.Ordinal))
            .Max(id => int.Parse(id[(family.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture));
        return family + "-" + (highest + 1).ToString("D3", CultureInfo.InvariantCulture);
    }

    // One past the highest identifier with this prefix in the models and the map.
    private static string AbsentModelId(string prefix, int digits)
    {
        var pattern = new Regex(Regex.Escape(prefix) + @"-(\d+)\b", RegexOptions.CultureInvariant);
        var highest = Documents().Values.Append(RepositoryFiles.Current[ThreatModelContract.MapPath])
            .SelectMany(text => pattern.Matches(text).Cast<Match>())
            .Select(match => int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();
        return prefix + "-" + (highest + 1).ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static string AbsentOwner()
    {
        var owners = RegisterEntries().Select(entry => (string?)entry!["owner"] ?? "").ToHashSet(StringComparer.Ordinal);
        var owner = "Unlisted subsystem";
        while (owners.Contains(owner))
        {
            owner += " variant";
        }

        return owner;
    }

    private static string OwnerOutside(JsonNode control)
    {
        var owned = RequirementsOf(control).Select(entry => (string?)entry["owner"]).ToHashSet();
        return RegisterEntries().Select(entry => (string?)entry!["owner"] ?? "").First(owner => !owned.Contains(owner));
    }

    private static string FamilyOutside(JsonNode control)
    {
        var mapped = RequirementsOf(control).SelectMany(entry => Strings(entry["acceptance"]!)).ToHashSet(StringComparer.Ordinal);
        return RegisterEntries().Where(entry => (string?)entry!["kind"] == "acceptance-family")
            .Select(entry => (string?)entry!["id"] ?? "").First(family => !mapped.Contains(family));
    }

    private static string FirstRowId(string text, string prefix) =>
        ThreatModelContract.Cells(text.Split('\n').First(line => line.StartsWith(prefix, StringComparison.Ordinal)))[0];

    private static string ChangeRow(string text, string prefix, Func<string[], string[]> change, bool last = false)
    {
        var lines = text.Split('\n');
        var index = last ? Array.FindLastIndex(lines, line => line.StartsWith(prefix, StringComparison.Ordinal))
            : Array.FindIndex(lines, line => line.StartsWith(prefix, StringComparison.Ordinal));
        lines[index] = "| " + string.Join(" | ", change(ThreatModelContract.Cells(lines[index]))) + " |";
        return string.Join('\n', lines);
    }

    private static string ChangeAsset(string text, Func<string[], string[]> change)
    {
        var lines = text.Split('\n');
        var section = Array.IndexOf(lines, "## Assets and classification");
        var header = Array.FindIndex(lines, section, line => line.StartsWith('|'));
        lines[header + 2] = "| " + string.Join(" | ", change(ThreatModelContract.Cells(lines[header + 2]))) + " |";
        return string.Join('\n', lines);
    }

    private static string DuplicateRow(string text, string prefix)
    {
        var lines = text.Split('\n').ToList();
        var index = lines.FindIndex(line => line.StartsWith(prefix, StringComparison.Ordinal));
        lines.Insert(index + 1, lines[index]);
        return string.Join('\n', lines);
    }

    private static string InsertAfter(string text, string prefix, string row)
    {
        var lines = text.Split('\n').ToList();
        lines.Insert(lines.FindLastIndex(line => line.StartsWith(prefix, StringComparison.Ordinal)) + 1, row);
        return string.Join('\n', lines);
    }

    private static string RemoveRow(string text, string prefix) =>
        string.Join('\n', text.Split('\n').Where(line => !line.StartsWith(prefix, StringComparison.Ordinal)));
}

// Consistency checks for the planning contract; they cannot show that a threat list is complete or that a control exists.
internal static class ThreatModelContract
{
    public const string Folder = "docs/security/threat-models/";
    public const string MapPath = Folder + "controls.json";
    public const string ReadmePath = Folder + "README.md";
    public const string G02Path = Folder + "g02-foundation.md";
    public const string WebsitePath = Folder + "website-profile.md";
    public static readonly string[] DocumentPaths = [ReadmePath, G02Path, WebsitePath];

    private const string AbuseCategory = "Abuse case";
    private static readonly Dictionary<string, string> Models = new(StringComparer.Ordinal) { [G02Path] = "G02", [WebsitePath] = "W" };
    private static readonly string[] Sections =
    [
        "Scope and assumptions", "Trust boundaries", "Assets and classification", "Privileged operations",
        "External inputs", "Threats", "Abuse cases", "Controls", "Residual risks and out of scope"
    ];
    private static readonly HashSet<string> StrideCategories = new(StringComparer.Ordinal)
    {
        "Spoofing", "Tampering", "Repudiation", "Information disclosure", "Denial of service", "Elevation of privilege"
    };
    private static readonly HashSet<string> Classifications = new(StringComparer.Ordinal) { "Public", "Internal", "Confidential", "Restricted", "Secret" };
    private static readonly HashSet<string> IntegrityRatings = new(StringComparer.Ordinal) { "Critical", "Standard" };
    private static readonly HashSet<string> Layers = new(StringComparer.Ordinal)
    {
        "unit", "real-provider-integration", "api-contract", "isolation", "ui", "migration", "operational-recovery"
    };
    private static readonly HashSet<string> Profiles = new(StringComparer.Ordinal) { "reference", "website" };
    private static readonly string[] RootFields = ["version", "date", "controls"];
    private static readonly string[] ControlFields = ["id", "title", "threats", "boundary", "owner", "requirements", "evidence", "profiles"];
    private static readonly string[] EvidenceFields = ["acceptance", "layers"];
    private static readonly Regex BoundaryId = new(@"^TB-(?:G02|W)-\d{2}$", RegexOptions.CultureInvariant);
    private static readonly Regex ThreatId = new(@"^TH-(?:G02|W)-\d{3}$", RegexOptions.CultureInvariant);
    private static readonly Regex ControlId = new(@"^TM-(?:G02|W)-\d{3}$", RegexOptions.CultureInvariant);
    private static readonly Regex Citation = new(@"\bT[BHM]-[A-Z0-9]+-\d+\b", RegexOptions.CultureInvariant);
    // Markers are assembled from parts so this source file does not itself contain one.
    private static readonly Regex DraftMarker = new(
        @"\b(?:" + "TO" + "DO|TB" + "D|FIX" + "ME)\\b|(?i:place" + "holder|lor" + "em ipsum)", RegexOptions.CultureInvariant);

    public static List<string> Validate(string mapText, string registerText, IReadOnlyDictionary<string, string> documents)
    {
        var errors = new List<string>();
        var register = ReadRegister(registerText, errors);
        var prose = ReadProse(documents, errors);
        var controls = ReadMap(mapText, register, prose, errors);
        if (controls is not null)
        {
            CrossCheck(controls, prose, errors);
        }

        return errors;
    }

    public static string[] Cells(string line)
    {
        var parts = line.TrimEnd().Split('|');
        return parts[1..(parts.Length > 2 && parts[^1].Length == 0 ? ^1 : ^0)].Select(cell => cell.Trim()).ToArray();
    }

    private static string Model(string id) => id.Split('-')[1];

    private static Register ReadRegister(string text, List<string> errors)
    {
        var register = new Register();
        try
        {
            using var document = JsonDocument.Parse(text);
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString()!;
                var owner = entry.GetProperty("owner").GetString()!;
                register.Owners.Add(owner);
                switch (entry.GetProperty("kind").GetString())
                {
                    case "requirement":
                        register.Requirements[id] = new Requirement(owner, entry.GetProperty("acceptance").EnumerateArray()
                            .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal));
                        break;
                    case "acceptance-family":
                        register.Acceptance.Add(id);
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            errors.Add("The coverage register must be readable to check the control map.");
        }

        return register;
    }

    private static Prose ReadProse(IReadOnlyDictionary<string, string> documents, List<string> errors)
    {
        var prose = new Prose();
        foreach (var path in DocumentPaths)
        {
            if (!documents.TryGetValue(path, out var raw))
            {
                errors.Add($"{path}: required threat-model document is missing.");
                continue;
            }

            var text = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (DraftMarker.IsMatch(text))
            {
                errors.Add($"{path}: contains an unfinished-draft marker.");
            }

            foreach (Match match in Citation.Matches(text))
            {
                if (BoundaryId.IsMatch(match.Value)) prose.CitedBoundaries.Add(match.Value);
                else if (ThreatId.IsMatch(match.Value)) prose.CitedThreats.Add(match.Value);
                else if (ControlId.IsMatch(match.Value)) prose.CitedControls.Add(match.Value);
                else errors.Add($"{path}: malformed identifier {match.Value}.");
            }

            if (Models.TryGetValue(path, out var model))
            {
                ReadModel(path, model, text, prose, errors);
            }
        }

        foreach (var boundary in prose.Boundaries.Keys.Where(boundary => !prose.ThreatsPerBoundary.ContainsKey(boundary)).Order(StringComparer.Ordinal))
        {
            errors.Add($"boundary {boundary} has no threat.");
        }

        return prose;
    }

    private static void ReadModel(string path, string model, string text, Prose prose, List<string> errors)
    {
        var content = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? section = null;
        string? boundary = null;
        var assetLines = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..].Trim();
                if (!content.TryAdd(section, false)) errors.Add($"{path}: section '{section}' appears more than once.");
                boundary = null;
                continue;
            }

            if (section is not null && !string.IsNullOrWhiteSpace(line)) content[section] = true;
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var token = line[4..].Split(' ')[0];
                boundary = BoundaryId.IsMatch(token) ? token : null;
                if (boundary is not null && Model(boundary) != model) errors.Add($"{path}: boundary heading {boundary} belongs to another model.");
                continue;
            }

            if (!line.StartsWith('|')) continue;
            var cells = Cells(line);
            if (line.StartsWith("| TB-", StringComparison.Ordinal)) BoundaryRow(path, model, section, cells, prose, errors);
            else if (line.StartsWith("| TH-", StringComparison.Ordinal)) ThreatRow(path, model, section, boundary, cells, prose, errors);
            else if (line.StartsWith("| TM-", StringComparison.Ordinal)) ControlRow(path, model, section, cells, prose, errors);
            else if (section == "Assets and classification" && ++assetLines > 2) AssetRow(path, cells, errors);
        }

        foreach (var required in Sections)
        {
            if (!content.TryGetValue(required, out var hasContent)) errors.Add($"{path}: missing required section '{required}'.");
            else if (!hasContent) errors.Add($"{path}: section '{required}' is empty.");
        }

        if (content.ContainsKey("Assets and classification") && assetLines <= 2)
        {
            errors.Add($"{path}: the assets section must list at least one asset.");
        }
    }

    private static void BoundaryRow(string path, string model, string? section, string[] cells, Prose prose, List<string> errors)
    {
        var id = cells[0];
        if (!BoundaryId.IsMatch(id))
        {
            errors.Add($"{path}: malformed boundary identifier {id}.");
            return;
        }

        if (cells.Length != 4 || cells.Any(string.IsNullOrWhiteSpace)) errors.Add($"{path}: boundary {id} needs a name and both sides of the crossing.");
        if (section != "Trust boundaries") errors.Add($"{path}: boundary {id} must be defined in the Trust boundaries section.");
        if (Model(id) != model) errors.Add($"{path}: boundary {id} belongs to another model.");
        if (!prose.Boundaries.TryAdd(id, model)) errors.Add($"{path}: boundary {id} is defined more than once.");
    }

    private static void ThreatRow(string path, string model, string? section, string? boundary, string[] cells, Prose prose, List<string> errors)
    {
        var id = cells[0];
        if (!ThreatId.IsMatch(id))
        {
            errors.Add($"{path}: malformed threat identifier {id}.");
            return;
        }

        if (cells.Length != 4)
        {
            errors.Add($"{path}: threat {id} needs an identifier, category, description and controls.");
            return;
        }

        if (Model(id) != model) errors.Add($"{path}: threat {id} belongs to another model.");
        var category = cells[1];
        if (!StrideCategories.Contains(category) && category != AbuseCategory)
            errors.Add($"{path}: threat {id} category must be a STRIDE category or {AbuseCategory}.");
        else if (section == "Threats" && category == AbuseCategory)
            errors.Add($"{path}: threat {id} under a boundary heading must use a STRIDE category.");
        else if (section == "Abuse cases" && category != AbuseCategory)
            errors.Add($"{path}: abuse case {id} must use the {AbuseCategory} category.");

        if (section == "Threats")
        {
            if (boundary is null) errors.Add($"{path}: threat {id} must appear under a boundary heading.");
            else prose.ThreatsPerBoundary[boundary] = prose.ThreatsPerBoundary.GetValueOrDefault(boundary) + 1;
        }
        else if (section != "Abuse cases")
        {
            errors.Add($"{path}: threat {id} must be defined in the Threats or Abuse cases section.");
        }

        if (string.IsNullOrWhiteSpace(cells[2])) errors.Add($"{path}: threat {id} description must be nonblank.");
        var controls = cells[3].Length == 0 ? Array.Empty<string>() : cells[3].Split(", ");
        if (controls.Length == 0) errors.Add($"{path}: threat {id} must list at least one control.");
        if (controls.Any(control => !ControlId.IsMatch(control))) errors.Add($"{path}: threat {id} controls must be a comma-separated list of control identifiers.");
        if (controls.Distinct(StringComparer.Ordinal).Count() != controls.Length) errors.Add($"{path}: threat {id} lists a control more than once.");
        if (!prose.Threats.TryAdd(id, controls.Where(control => ControlId.IsMatch(control)).ToHashSet(StringComparer.Ordinal)))
        {
            errors.Add($"{path}: threat {id} is defined more than once.");
        }
    }

    private static void ControlRow(string path, string model, string? section, string[] cells, Prose prose, List<string> errors)
    {
        var id = cells[0];
        if (!ControlId.IsMatch(id))
        {
            errors.Add($"{path}: malformed control identifier {id}.");
            return;
        }

        if (cells.Length != 3)
        {
            errors.Add($"{path}: control {id} needs an identifier, owner and required behavior.");
            return;
        }

        if (section != "Controls") errors.Add($"{path}: control {id} must be described in the Controls section.");
        if (Model(id) != model) errors.Add($"{path}: control {id} belongs to another model.");
        if (string.IsNullOrWhiteSpace(cells[2])) errors.Add($"{path}: control {id} required behavior must be nonblank.");
        if (!prose.ControlOwners.TryAdd(id, cells[1])) errors.Add($"{path}: control {id} is described more than once.");
    }

    private static void AssetRow(string path, string[] cells, List<string> errors)
    {
        if (cells.Length != 4 || string.IsNullOrWhiteSpace(cells[0]))
        {
            errors.Add($"{path}: asset rows need an asset, classification, integrity and notes.");
            return;
        }

        if (!Classifications.Contains(cells[1]))
            errors.Add($"{path}: asset '{cells[0]}' classification must be Public, Internal, Confidential, Restricted or Secret.");
        if (!IntegrityRatings.Contains(cells[2]))
            errors.Add($"{path}: asset '{cells[0]}' integrity must be Critical or Standard.");
    }

    private static List<Control>? ReadMap(string text, Register register, Prose prose, List<string> errors)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            errors.Add("Control map must contain valid JSON.");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Control map must be an object.");
                return null;
            }

            if (HasDuplicateProperty(root)) errors.Add("Control map contains a duplicate JSON property.");
            Fields(root, RootFields, "control map", errors);
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 1)
            {
                errors.Add("Control map version must be 1.");
            }

            if (!DateOnly.TryParseExact(Text(root, "date", "control map", errors), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                errors.Add("Control map date must be an ISO calendar date.");
            }

            if (!root.TryGetProperty("controls", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                errors.Add("Control map controls must be a nonempty array.");
                return null;
            }

            var controls = new List<Control>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in items.EnumerateArray())
            {
                var location = $"controls[{index++}]";
                if (element.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{location}: control must be an object.");
                    continue;
                }

                var control = ReadControl(element, location, register, prose, ids, errors);
                if (control is not null) controls.Add(control);
            }

            var order = controls.Select(control => control.Id).ToArray();
            if (!order.SequenceEqual(order.OrderBy(id => Model(id) == "G02" ? 0 : 1).ThenBy(id => id, StringComparer.Ordinal)))
            {
                errors.Add("Control map controls must be ordered by model and number.");
            }

            return controls;
        }
    }

    private static Control? ReadControl(JsonElement element, string location, Register register, Prose prose,
        HashSet<string> ids, List<string> errors)
    {
        var id = Text(element, "id", location, errors);
        location = id.Length > 0 ? id : location;
        Fields(element, ControlFields, location, errors);
        if (!ControlId.IsMatch(id))
        {
            errors.Add($"{location}: control id must be TM-G02 or TM-W followed by three digits.");
            return null;
        }

        if (!ids.Add(id)) errors.Add($"{id}: duplicate control id.");
        var model = Model(id);
        var title = Text(element, "title", id, errors);
        if (title.Length > 100 || title.Any(char.IsControl)) errors.Add($"{id}: title must be a single line of at most 100 characters.");

        var threats = Strings(element, "threats", id, errors);
        foreach (var threat in threats)
        {
            if (!ThreatId.IsMatch(threat)) errors.Add($"{id}: threat id {threat} must be TH-G02 or TH-W followed by three digits.");
            else if (!prose.Threats.ContainsKey(threat)) errors.Add($"{id}: threat {threat} is not defined in the prose.");
        }

        var threatModels = threats.Where(threat => ThreatId.IsMatch(threat)).Select(Model).ToHashSet(StringComparer.Ordinal);
        if (model == "W" && threatModels.Contains("G02")) errors.Add($"{id}: a website control may address only website threats.");
        if (model == "G02" && !threatModels.Contains("G02")) errors.Add($"{id}: a foundation control must address at least one foundation threat.");

        var boundary = Text(element, "boundary", id, errors);
        if (!BoundaryId.IsMatch(boundary)) errors.Add($"{id}: boundary must be TB-G02 or TB-W followed by two digits.");
        else if (Model(boundary) != model) errors.Add($"{id}: boundary must belong to the control's model.");
        else if (!prose.Boundaries.ContainsKey(boundary)) errors.Add($"{id}: boundary {boundary} is not defined in the prose.");

        var requirements = Strings(element, "requirements", id, errors);
        foreach (var requirement in requirements.Where(requirement => !register.Requirements.ContainsKey(requirement)))
        {
            errors.Add($"{id}: {requirement} must name a register requirement.");
        }

        var known = requirements.Where(register.Requirements.ContainsKey).Select(requirement => register.Requirements[requirement]).ToArray();
        var owner = Text(element, "owner", id, errors);
        if (!register.Owners.Contains(owner)) errors.Add($"{id}: owner must name a register owner.");
        else if (known.Length > 0 && known.All(requirement => requirement.Owner != owner))
            errors.Add($"{id}: owner must own at least one listed requirement in the register.");
        if (prose.ControlOwners.TryGetValue(id, out var described) && described != owner)
            errors.Add($"{id}: owner in the prose control table differs from the map.");

        if (!element.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{id}: evidence must be an object with acceptance and layers.");
        }
        else
        {
            Fields(evidence, EvidenceFields, id + ".evidence", errors);
            var mapped = known.SelectMany(requirement => requirement.Acceptance).ToHashSet(StringComparer.Ordinal);
            foreach (var family in Strings(evidence, "acceptance", id, errors))
            {
                if (!register.Acceptance.Contains(family)) errors.Add($"{id}: unknown acceptance family {family}.");
                else if (!mapped.Contains(family)) errors.Add($"{id}: acceptance family {family} is not mapped to a listed requirement.");
            }

            foreach (var layer in Strings(evidence, "layers", id, errors).Where(layer => !Layers.Contains(layer)))
            {
                errors.Add($"{id}: unknown test layer {layer}.");
            }
        }

        var profiles = Strings(element, "profiles", id, errors);
        if (profiles.Any(profile => !Profiles.Contains(profile))) errors.Add($"{id}: profile must be reference or website.");
        if (threatModels.Contains("G02") && !profiles.Contains("reference"))
            errors.Add($"{id}: must name the reference profile because it addresses a foundation threat.");
        if (threatModels.Contains("W") && !profiles.Contains("website"))
            errors.Add($"{id}: must name the website profile because it addresses a website threat.");

        return new Control(id, threats.Where(threat => ThreatId.IsMatch(threat)).ToArray(), boundary);
    }

    private static void CrossCheck(List<Control> controls, Prose prose, List<string> errors)
    {
        var mapped = controls.Select(control => control.Id).ToHashSet(StringComparer.Ordinal);
        var addressing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var control in controls)
        {
            foreach (var threat in control.Threats)
            {
                if (!addressing.TryGetValue(threat, out var set)) addressing[threat] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(control.Id);
            }
        }

        foreach (var (threat, described) in prose.Threats)
        {
            if (!addressing.TryGetValue(threat, out var addressedBy)) errors.Add($"{threat}: threat has no control in the map.");
            else if (!addressedBy.SetEquals(described)) errors.Add($"{threat}: controls listed in the prose differ from the map.");
        }

        foreach (var threat in prose.CitedThreats.Where(threat => !prose.Threats.ContainsKey(threat)).Order(StringComparer.Ordinal))
            errors.Add($"cited threat {threat} is not defined in a threat model.");
        foreach (var control in prose.CitedControls.Where(control => !mapped.Contains(control)).Order(StringComparer.Ordinal))
            errors.Add($"cited control {control} is not in the map.");
        foreach (var control in controls.Where(control => !prose.CitedControls.Contains(control.Id)))
            errors.Add($"{control.Id}: control {control.Id} is not cited in the prose.");
        foreach (var control in controls.Where(control => !prose.ControlOwners.ContainsKey(control.Id)))
            errors.Add($"{control.Id}: control has no row in its model's control table.");
        foreach (var boundary in prose.CitedBoundaries.Where(boundary => !prose.Boundaries.ContainsKey(boundary)).Order(StringComparer.Ordinal))
            errors.Add($"cited boundary {boundary} is not defined.");

        var used = controls.Select(control => control.Boundary).ToHashSet(StringComparer.Ordinal);
        foreach (var boundary in prose.Boundaries.Keys.Where(boundary => !used.Contains(boundary)).Order(StringComparer.Ordinal))
            errors.Add($"boundary {boundary} has no control in the map.");
    }

    private static void Fields(JsonElement element, string[] expected, string location, List<string> errors)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != expected.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            errors.Add($"{location}: fields must be exactly {string.Join(", ", expected)}.");
        }
    }

    private static string Text(JsonElement element, string field, string location, List<string> errors)
    {
        if (element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (!string.IsNullOrWhiteSpace(text) && text == text.Trim()) return text;
        }

        errors.Add($"{location}.{field}: must be a nonempty string without surrounding whitespace.");
        return "";
    }

    private static string[] Strings(JsonElement element, string field, string location, List<string> errors)
    {
        if (!element.TryGetProperty(field, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0
            || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
        {
            errors.Add($"{location}: {field} must be a nonempty array of strings.");
            return values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray()
                : [];
        }

        var result = values.EnumerateArray().Select(value => value.GetString()!).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) errors.Add($"{location}: duplicate {field} reference.");
        return result;
    }

    private static bool HasDuplicateProperty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
                != value.EnumerateObject().Count()
            || value.EnumerateObject().Any(property => HasDuplicateProperty(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(HasDuplicateProperty),
        _ => false
    };

    private sealed record Requirement(string Owner, HashSet<string> Acceptance);

    private sealed record Control(string Id, string[] Threats, string Boundary);

    private sealed class Register
    {
        public Dictionary<string, Requirement> Requirements { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Acceptance { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Owners { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Prose
    {
        public Dictionary<string, HashSet<string>> Threats { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Boundaries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ControlOwners { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ThreatsPerBoundary { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CitedThreats { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CitedControls { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CitedBoundaries { get; } = new(StringComparer.Ordinal);
    }
}
