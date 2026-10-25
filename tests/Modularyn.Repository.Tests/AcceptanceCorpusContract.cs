using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modularyn.Repository.Tests;

// Validates planning consistency, not product behavior or qualification evidence.
internal static class AcceptanceCorpusContract
{
    public const string Folder = "docs/acceptance/";
    public const string CorpusPath = Folder + "corpus.json";
    public const string ReadmePath = Folder + "README.md";
    private const string RegisterPath = "docs/coverage/register.json";
    private const string ProfilesPath = "docs/profiles/README.md";
    private const string LayersPath = "docs/security/threat-models/README.md";
    internal const string ControlsPath = "docs/security/threat-models/controls.json";

    internal static readonly string[] SecurityFamilies = ["AC-002", "AC-003", "AC-004", "AC-012", "AC-031"];
    internal static readonly string[] ReferenceFamilies = ["AC-001", "AC-005", "AC-006", "AC-007", "AC-008", "AC-009"];
    internal static readonly string[] WebsiteFamilies =
    [
        "AC-001", "AC-005", "AC-006", "AC-007", "AC-008", "AC-009", "AC-013", "AC-016",
        "AC-017", "AC-018", "AC-019", "AC-020", "AC-023", "AC-028", "AC-029", "AC-030"
    ];
    // The Builder's security assignment: AC-003 and AC-004 within G-02 on reference, and all five families within W.
    internal static readonly string[] ReferenceSecurityFamilies = ["AC-003", "AC-004"];
    internal static readonly string[] WebsiteSecurityFamilies = ["AC-002", "AC-003", "AC-004", "AC-012", "AC-031"];
    // G-02 qualifies reference cases against the foundation model; website-model controls that also name reference are G-04 CMS work.
    internal const string FoundationControlPrefix = "TM-G02-";
    internal static readonly string[] Profiles = ["reference", "website"];
    internal static readonly string[] Layers =
        ["unit", "real-provider-integration", "api-contract", "isolation", "ui", "migration", "operational-recovery"];
    private static readonly Dictionary<string, string> TypeLetters = new(StringComparer.Ordinal)
    {
        ["positive"] = "P", ["negative"] = "N", ["boundary"] = "B", ["recovery"] = "R"
    };
    private static readonly string[] CaseFields =
    [
        "id", "title", "family", "type", "profiles", "scope", "requirements", "layers", "securityDependencies",
        "preconditions", "steps", "expectedOutcome", "evidence"
    ];
    private static readonly Regex CaseId = new(@"^AC-[0-9]{3}-[PNBR](?:0[1-9]|[1-9][0-9])$", RegexOptions.CultureInvariant);
    private static readonly Regex ControlCitation = new(@"\bTM-[A-Z0-9]+-[0-9]+\b", RegexOptions.CultureInvariant);
    private static readonly Regex DraftMarker = new(
        @"\b(?:" + "TO" + "DO|TB" + "D|FIX" + "ME|place" + "holder)\\b|lor" + "em ipsum",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static List<string> Validate(string corpusText, IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        var register = ReadRegister(files.GetValueOrDefault(RegisterPath, ""), errors);
        var cases = ReadCases(corpusText, register, errors);
        ValidateVocabulary(files, errors);
        ValidateReadme(files, errors);
        if (cases is null) return errors;

        ValidateScope(cases, errors);
        ValidateControls(cases, files, errors);
        ValidateDocuments(cases, files, errors);
        return errors;
    }

    internal static string[] NonSecurityFamiliesFor(string profile) => profile == "reference" ? ReferenceFamilies : WebsiteFamilies;
    internal static string[] SecurityFamiliesFor(string profile) =>
        profile == "reference" ? ReferenceSecurityFamilies : WebsiteSecurityFamilies;
    internal static string[] FamiliesFor(string profile) => [.. NonSecurityFamiliesFor(profile), .. SecurityFamiliesFor(profile)];

    // Format rules are deliberately independent of the initial assignment's family/profile scope.
    public static List<string> ValidateFormat(string corpusText, string registerText)
    {
        var errors = new List<string>();
        ReadCases(corpusText, ReadRegister(registerText, errors), errors);
        return errors;
    }

    internal static void ValidateIdentity(string id, string family, string type, IReadOnlySet<string> families, List<string> errors)
    {
        if (!families.Contains(family)) errors.Add($"{id}: unknown acceptance family {family}.");
        if (!TypeLetters.TryGetValue(type, out var letter))
            errors.Add($"{id}: type must be positive, negative, boundary or recovery.");
        if (!CaseId.IsMatch(id)) errors.Add($"{id}: case id must use AC-NNN, a type letter and a nonzero two-digit number.");
        else if (!id.StartsWith(family + "-" + letter, StringComparison.Ordinal) || letter is null)
            errors.Add($"{id}: case id must agree with family and type.");
    }

    private static Register ReadRegister(string text, List<string> errors)
    {
        var requirements = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(text);
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString()!;
                switch (entry.GetProperty("kind").GetString())
                {
                    case "acceptance-family": families.Add(id); break;
                    case "requirement":
                        requirements.Add(id, entry.GetProperty("acceptance").EnumerateArray()
                            .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal));
                        break;
                }
            }
            if (families.Count == 0 || requirements.Count == 0) errors.Add("Coverage register must contain requirements and acceptance families.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            errors.Add("Coverage register must contain readable requirement and family mappings.");
        }
        return new Register(requirements, families);
    }

    private static List<Case>? ReadCases(string text, Register register, List<string> errors)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException)
        {
            errors.Add("Corpus must contain valid JSON.");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Corpus must be an object.");
                return null;
            }
            CheckDuplicateProperties(root, "corpus", errors);
            Fields(root, ["version", "date", "cases"], "corpus", errors);
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 1)
                errors.Add("Corpus version must be 1.");
            if (!DateOnly.TryParseExact(Text(root, "date", "corpus", errors), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors.Add("Corpus date must be an ISO calendar date.");
            if (!root.TryGetProperty("cases", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                errors.Add("Corpus cases must be a nonempty array.");
                return null;
            }

            var result = new List<Case>();
            var ids = new List<string>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Corpus case must be an object.");
                    continue;
                }
                var before = errors.Count;
                var id = Text(item, "id", "case", errors);
                var family = Text(item, "family", id, errors);
                var type = Text(item, "type", id, errors);
                var title = Text(item, "title", id, errors);
                Fields(item, CaseFields, id, errors);
                ValidateIdentity(id, family, type, register.Families, errors);
                if (title.Length > 100) errors.Add($"{id}: title must be at most 100 characters.");
                if (ids.Contains(id, StringComparer.Ordinal)) errors.Add($"{id}: duplicate case id.");
                ids.Add(id);

                var profiles = Strings(item, "profiles", id, errors);
                var requirements = Strings(item, "requirements", id, errors);
                var layers = Strings(item, "layers", id, errors);
                var security = Strings(item, "securityDependencies", id, errors, allowEmpty: true);
                foreach (var profile in profiles)
                    if (!Profiles.Contains(profile, StringComparer.Ordinal)) errors.Add($"{id}: unknown profile {profile}.");
                foreach (var layer in layers)
                    if (!Layers.Contains(layer, StringComparer.Ordinal)) errors.Add($"{id}: unknown test layer {layer}.");
                foreach (var dependency in security)
                    if (!SecurityFamilies.Contains(dependency, StringComparer.Ordinal) || !register.Families.Contains(dependency))
                        errors.Add($"{id}: security dependency {dependency} must name a registered security family.");
                foreach (var requirement in requirements)
                {
                    if (!register.Requirements.TryGetValue(requirement, out var mapped))
                        errors.Add($"{id}: {requirement} must name a register requirement.");
                    else if (!mapped.Contains(family)) errors.Add($"{id}: {requirement} is not mapped to {family} in the register.");
                }

                var scope = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!item.TryGetProperty("scope", out var scopeValue) || scopeValue.ValueKind != JsonValueKind.Object)
                    errors.Add($"{id}: scope must be an object keyed by profile.");
                else
                {
                    Fields(scopeValue, profiles, id + ".scope", errors);
                    foreach (var profile in profiles.Distinct(StringComparer.Ordinal))
                        scope[profile] = Text(scopeValue, profile, id + ".scope", errors);
                }
                var preconditions = Strings(item, "preconditions", id, errors);
                var steps = Strings(item, "steps", id, errors);
                var outcomes = Strings(item, "expectedOutcome", id, errors);
                var evidence = Strings(item, "evidence", id, errors);
                if (errors.Count == before)
                    result.Add(new Case(id, title, family, type, profiles, scope, requirements, layers, security,
                        preconditions, steps, outcomes, evidence));
            }
            if (!ids.SequenceEqual(ids.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                errors.Add("Corpus cases must be ordered by id.");
            return result;
        }
    }

    private static void ValidateScope(List<Case> cases, List<string> errors)
    {
        foreach (var item in cases)
        {
            var security = SecurityFamilies.Contains(item.Family, StringComparer.Ordinal);
            foreach (var profile in item.Profiles)
            {
                if (security && !SecurityFamiliesFor(profile).Contains(item.Family, StringComparer.Ordinal))
                    errors.Add($"{item.Id}: {item.Family}/{profile} is outside the security corpus scope.");
                else if (!security && !NonSecurityFamiliesFor(profile).Contains(item.Family, StringComparer.Ordinal))
                    errors.Add($"{item.Id}: {item.Family}/{profile} is outside the initial corpus scope.");
            }
            if (item.SecurityDependencies.Contains(item.Family, StringComparer.Ordinal))
                errors.Add($"{item.Id}: a case cannot name its own family as a security dependency.");
        }
        foreach (var profile in Profiles)
        foreach (var family in FamiliesFor(profile))
        foreach (var type in TypeLetters.Keys)
            if (!cases.Any(item => item.Family == family && item.Type == type && item.Profiles.Contains(profile, StringComparer.Ordinal)))
                errors.Add($"{family}/{profile}: missing {type} case.");
    }

    // Traces cases to the threat-model control map; this shows traceability, never that a control works. A case may cite
    // only controls that the map plans under its own family; behavior planned elsewhere is a dependency on that family.
    private static void ValidateControls(List<Case> cases, IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        var controls = ReadControls(files.GetValueOrDefault(ControlsPath, ""), errors);
        if (controls is null) return;
        var demonstrated = new HashSet<(string Family, string Profile, string Control)>();
        foreach (var item in cases)
        {
            string[] prose = [item.Title, .. item.Scope.Values, .. item.Preconditions, .. item.Steps, .. item.Outcomes, .. item.Evidence];
            foreach (var id in Citations(prose))
            {
                if (!controls.TryGetValue(id, out var control))
                {
                    errors.Add($"{item.Id}: cites undefined control {id}.");
                    continue;
                }
                foreach (var profile in item.Profiles)
                    if (!control.Profiles.Contains(profile))
                        errors.Add($"{item.Id}: control {id} does not name profile {profile}.");
                if (!control.Families.Contains(item.Family))
                    errors.Add($"{item.Id}: control {id} is not planned under {item.Family} in the control map.");
            }
            if (!SecurityFamilies.Contains(item.Family, StringComparer.Ordinal)) continue;
            var shown = Citations([.. item.Steps, .. item.Evidence]).Where(controls.ContainsKey).ToArray();
            if (!shown.Any(id => controls[id].Families.Contains(item.Family)))
                errors.Add($"{item.Id}: a security case must demonstrate a control mapped to {item.Family} in its steps or evidence.");
            foreach (var profile in item.Profiles)
            foreach (var id in shown)
                demonstrated.Add((item.Family, profile, id));
        }
        foreach (var profile in Profiles)
        foreach (var family in SecurityFamiliesFor(profile))
        foreach (var (id, control) in controls.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (control.Families.Contains(family) && control.Profiles.Contains(profile)
                && (profile != "reference" || id.StartsWith(FoundationControlPrefix, StringComparison.Ordinal))
                && !demonstrated.Contains((family, profile, id)))
                errors.Add($"{family}/{profile}: control {id} is not demonstrated by a case.");
    }

    private static IEnumerable<string> Citations(IEnumerable<string> texts) => texts
        .SelectMany(text => ControlCitation.Matches(text).Select(match => match.Value)).Distinct(StringComparer.Ordinal);

    private static Dictionary<string, Control>? ReadControls(string text, List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var controls = new Dictionary<string, Control>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("controls").EnumerateArray())
            {
                controls.Add(entry.GetProperty("id").GetString()!, new Control(
                    entry.GetProperty("profiles").EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal),
                    entry.GetProperty("evidence").GetProperty("acceptance").EnumerateArray().Select(value => value.GetString()!)
                        .ToHashSet(StringComparer.Ordinal)));
            }
            if (controls.Count > 0) return controls;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
        }
        errors.Add($"{ControlsPath}: control map must contain readable control identities, profiles and acceptance families.");
        return null;
    }

    private static void ValidateVocabulary(IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        foreach (var (path, values) in new[] { (ProfilesPath, Profiles), (LayersPath, Layers) })
        foreach (var value in values)
            if (!files.GetValueOrDefault(path, "").Contains("`" + value + "`", StringComparison.Ordinal))
                errors.Add($"{path}: vocabulary is missing {value}.");
    }

    private static void ValidateReadme(IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        if (!files.TryGetValue(ReadmePath, out var text) || string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{ReadmePath}: required corpus conventions are missing.");
            return;
        }
        foreach (var family in Profiles.SelectMany(FamiliesFor).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if (!text.Contains($"[{family}]({family}.md)", StringComparison.Ordinal))
                errors.Add($"{ReadmePath}: missing family link {family}.");
        foreach (var family in SecurityFamilies)
            if (!text.Contains(family, StringComparison.Ordinal)) errors.Add($"{ReadmePath}: missing security ownership reference {family}.");
        if (DraftMarker.IsMatch(text)) errors.Add($"{ReadmePath}: unfinished draft text.");
    }

    private static void ValidateDocuments(List<Case> cases, IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal) { ReadmePath };
        foreach (var family in cases.Select(item => item.Family).Distinct(StringComparer.Ordinal))
        {
            var path = Folder + family + ".md";
            expectedPaths.Add(path);
            if (!files.TryGetValue(path, out var text)) errors.Add($"{path}: required family document is missing.");
            else if (text.Replace("\r\n", "\n", StringComparison.Ordinal) != RenderFamily(family, cases))
                errors.Add($"{path}: Markdown differs from the complete corpus rendering.");
        }
        foreach (var path in files.Keys.Where(path => path.StartsWith(Folder, StringComparison.Ordinal)
                     && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
            if (!expectedPaths.Contains(path)) errors.Add($"{path}: unexpected acceptance document.");
    }

    internal static string RenderFamily(string family, IEnumerable<Case> cases)
    {
        var result = new StringBuilder($"# {family}\n\nPlanning cases; see [corpus conventions](README.md). These are not execution results.\n");
        foreach (var item in cases.Where(item => item.Family == family))
        {
            result.Append($"\n## {item.Id} — {item.Title}\n\n");
            result.Append($"- Family: `{item.Family}`\n- Type: `{item.Type}`\n");
            result.Append($"- Profiles: {CodeList(item.Profiles)}\n- Requirements: {CodeList(item.Requirements)}\n");
            result.Append($"- Layers: {CodeList(item.Layers)}\n- Security dependencies: {CodeList(item.SecurityDependencies)}\n");
            Section("Profile scope", item.Profiles.Select(profile => $"`{profile}`: {item.Scope[profile]}"));
            Section("Preconditions", item.Preconditions);
            Section("Steps", item.Steps, numbered: true);
            Section("Expected outcome", item.Outcomes);
            Section("Evidence to retain", item.Evidence);
        }
        return result.ToString();

        void Section(string title, IEnumerable<string> lines, bool numbered = false)
        {
            result.Append($"\n### {title}\n\n");
            var number = 0;
            foreach (var line in lines)
                result.Append(numbered ? $"{++number}. {line}\n" : $"- {line}\n");
        }
    }

    private static string CodeList(IReadOnlyCollection<string> values) => values.Count == 0
        ? "None." : string.Join(", ", values.Select(value => "`" + value + "`"));

    private static void Fields(JsonElement element, IEnumerable<string> expected, string location, List<string> errors)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        var fields = expected.ToHashSet(StringComparer.Ordinal);
        if (names.Length != fields.Count || !fields.SetEquals(names))
            errors.Add($"{location}: fields must be exactly {string.Join(", ", fields.Order(StringComparer.Ordinal))}.");
    }

    private static string Text(JsonElement element, string field, string location, List<string> errors)
    {
        if (!element.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{location}.{field}: must be a nonempty single-line string.");
            return "";
        }
        var text = value.GetString()!;
        CheckText(text, location + "." + field, errors);
        return text;
    }

    private static void CheckText(string text, string location, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim() || text.Any(char.IsControl))
            errors.Add($"{location}: must be a nonempty single-line string without surrounding whitespace.");
        if (DraftMarker.IsMatch(text)) errors.Add($"{location}: unfinished draft text.");
    }

    private static string[] Strings(JsonElement element, string field, string location, List<string> errors, bool allowEmpty = false)
    {
        if (!element.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Array
            || (!allowEmpty && value.GetArrayLength() == 0))
        {
            errors.Add($"{location}.{field}: must be {(allowEmpty ? "an" : "a nonempty")} array.");
            return [];
        }
        var result = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{location}.{field}: array item must be a string.");
                continue;
            }
            var text = entry.GetString()!;
            CheckText(text, location + "." + field, errors);
            if (result.Contains(text, StringComparer.Ordinal)) errors.Add($"{location}.{field}: duplicate value {text}.");
            result.Add(text);
        }
        return result.ToArray();
    }

    private static void CheckDuplicateProperties(JsonElement element, string location, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) errors.Add($"{location}: duplicate JSON property {property.Name}.");
                CheckDuplicateProperties(property.Value, location + "." + property.Name, errors);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateProperties(item, location, errors);
    }

    private sealed record Register(Dictionary<string, HashSet<string>> Requirements, HashSet<string> Families);
    private sealed record Control(HashSet<string> Profiles, HashSet<string> Families);
    internal sealed record Case(string Id, string Title, string Family, string Type, string[] Profiles,
        Dictionary<string, string> Scope, string[] Requirements, string[] Layers, string[] SecurityDependencies,
        string[] Preconditions, string[] Steps, string[] Outcomes, string[] Evidence);
}
