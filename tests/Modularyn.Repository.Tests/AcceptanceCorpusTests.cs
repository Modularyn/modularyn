using System.Text.Json.Nodes;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class AcceptanceCorpusTests
{
    private const string RegisterPath = "docs/coverage/register.json";

    [Fact]
    public void Corpus_agrees_with_requirements_profiles_and_readable_cases()
    {
        Assert.True(RepositoryFiles.Current.TryGetValue(AcceptanceCorpusContract.CorpusPath, out var corpus),
            "The initial acceptance corpus must exist.");
        Assert.Empty(AcceptanceCorpusContract.Validate(corpus!, RepositoryFiles.Current));
    }

    [Fact]
    public void Corpus_accepts_platform_line_endings()
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key,
            pair => pair.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal),
            StringComparer.Ordinal);
        Assert.Empty(AcceptanceCorpusContract.Validate(files[AcceptanceCorpusContract.CorpusPath], files));
    }

    [Theory]
    [InlineData("AC-002")]
    [InlineData("AC-003")]
    [InlineData("AC-004")]
    [InlineData("AC-012")]
    [InlineData("AC-031")]
    public void Format_identity_rules_already_accommodate_security_families(string family)
    {
        // Exercise metadata only; this defines no security behavior or security acceptance case.
        var families = RegisterEntries().Where(entry => (string?)entry!["kind"] == "acceptance-family")
            .Select(entry => entry!["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var (type, letter) in new[] { ("positive", "P"), ("negative", "N"), ("boundary", "B"), ("recovery", "R") })
        {
            var errors = new List<string>();
            AcceptanceCorpusContract.ValidateIdentity(family + "-" + letter + "01", family, type, families, errors);
            Assert.Empty(errors);
        }
    }

    [Theory]
    [InlineData("id")]
    [InlineData("title")]
    [InlineData("family")]
    [InlineData("type")]
    [InlineData("profiles")]
    [InlineData("scope")]
    [InlineData("requirements")]
    [InlineData("layers")]
    [InlineData("securityDependencies")]
    [InlineData("preconditions")]
    [InlineData("steps")]
    [InlineData("expectedOutcome")]
    [InlineData("evidence")]
    public void Every_case_field_is_required(string field)
    {
        var corpus = ReadCorpus();
        First(corpus).AsObject().Remove(field);
        AssertDiagnostic(Validate(corpus), "fields must be exactly");
    }

    [Theory]
    [InlineData("duplicate-id", "duplicate case id")]
    [InlineData("id-width", "case id must use AC-NNN")]
    [InlineData("id-zero", "case id must use AC-NNN")]
    [InlineData("id-type", "case id must agree with family and type")]
    [InlineData("id-family", "case id must agree with family and type")]
    [InlineData("unknown-type", "type must be positive, negative, boundary or recovery")]
    [InlineData("unknown-family", "unknown acceptance family AC-999")]
    [InlineData("unordered", "cases must be ordered by id")]
    [InlineData("long-title", "title must be at most 100 characters")]
    [InlineData("blank-title", ".title: must be a nonempty single-line string")]
    [InlineData("multiline-step", ".steps: must be a nonempty single-line string")]
    [InlineData("unknown-profile", "unknown profile alternate")]
    [InlineData("duplicate-profile", ".profiles: duplicate value")]
    [InlineData("missing-scope", ".scope: fields must be exactly")]
    [InlineData("extra-scope", ".scope: fields must be exactly")]
    [InlineData("blank-scope", ".scope.reference: must be a nonempty single-line string")]
    [InlineData("nonobject-scope", "scope must be an object keyed by profile")]
    [InlineData("unknown-layer", "unknown test layer manual")]
    [InlineData("duplicate-layer", ".layers: duplicate value")]
    [InlineData("unknown-requirement", "FND-999 must name a register requirement")]
    [InlineData("family-as-requirement", "AC-001 must name a register requirement")]
    [InlineData("inventory-as-requirement", "F-001 must name a register requirement")]
    [InlineData("unmapped-requirement", "is not mapped to AC-001 in the register")]
    [InlineData("duplicate-requirement", ".requirements: duplicate value")]
    [InlineData("invalid-security-dependency", "must name a registered security family")]
    [InlineData("duplicate-security-dependency", ".securityDependencies: duplicate value")]
    [InlineData("extra-case-field", "fields must be exactly")]
    [InlineData("null-case", "case must be an object")]
    [InlineData("wrong-version", "Corpus version must be 1")]
    [InlineData("string-version", "Corpus version must be 1")]
    [InlineData("invalid-date", "date must be an ISO calendar date")]
    [InlineData("extra-root-field", "corpus: fields must be exactly")]
    [InlineData("nonarray-cases", "cases must be a nonempty array")]
    [InlineData("empty-cases", "cases must be a nonempty array")]
    [InlineData("draft-text", "unfinished draft text")]
    public void A_single_corpus_violation_has_its_specific_diagnostic(string mutation, string diagnostic)
    {
        var corpus = ReadCorpus();
        var cases = corpus["cases"]!.AsArray();
        var first = First(corpus);
        switch (mutation)
        {
            case "duplicate-id": cases.Insert(1, first.DeepClone()); break;
            case "id-width": first["id"] = "AC-001-B1"; break;
            case "id-zero": first["id"] = "AC-001-B00"; break;
            case "id-type": first["type"] = "recovery"; break;
            case "id-family": first["family"] = "AC-005"; break;
            case "unknown-type": first["type"] = "success"; break;
            case "unknown-family": first["family"] = "AC-999"; break;
            case "unordered":
                var moved = cases[^1]!;
                cases.RemoveAt(cases.Count - 1);
                cases.Insert(0, moved);
                break;
            case "long-title": first["title"] = new string('a', 101); break;
            case "blank-title": first["title"] = " "; break;
            case "multiline-step": first["steps"]![0] = "Read the selected revision.\nCompare its identity."; break;
            case "unknown-profile": first["profiles"]![0] = "alternate"; break;
            case "duplicate-profile": first["profiles"]!.AsArray().Add((string?)first["profiles"]![0]); break;
            case "missing-scope": first["scope"] = new JsonObject(); break;
            case "extra-scope": first["scope"]!["website"] = "Additional profile scope."; break;
            case "blank-scope": first["scope"]!["reference"] = " "; break;
            case "nonobject-scope": first["scope"] = "reference"; break;
            case "unknown-layer": first["layers"]![0] = "manual"; break;
            case "duplicate-layer": first["layers"]!.AsArray().Add((string?)first["layers"]![0]); break;
            case "unknown-requirement": first["requirements"]![0] = "FND-999"; break;
            case "family-as-requirement": first["requirements"]![0] = "AC-001"; break;
            case "inventory-as-requirement": first["requirements"]![0] = "F-001"; break;
            case "unmapped-requirement": first["requirements"]![0] = RequirementOutside("AC-001"); break;
            case "duplicate-requirement": first["requirements"]!.AsArray().Add((string?)first["requirements"]![0]); break;
            case "invalid-security-dependency": first["securityDependencies"] = new JsonArray("AC-001"); break;
            case "duplicate-security-dependency": first["securityDependencies"] = new JsonArray("AC-031", "AC-031"); break;
            case "extra-case-field": first["passed"] = true; break;
            case "null-case": cases[0] = null; break;
            case "wrong-version": corpus["version"] = 2; break;
            case "string-version": corpus["version"] = "1"; break;
            case "invalid-date": corpus["date"] = "2026-02-30"; break;
            case "extra-root-field": corpus["passed"] = true; break;
            case "nonarray-cases": corpus["cases"] = "AC-001"; break;
            case "empty-cases": corpus["cases"] = new JsonArray(); break;
            case "draft-text": first["evidence"]![0] = "TO" + "DO"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }
        AssertDiagnostic(Validate(corpus), diagnostic);
    }

    [Theory]
    [InlineData("profiles")]
    [InlineData("requirements")]
    [InlineData("layers")]
    [InlineData("preconditions")]
    [InlineData("steps")]
    [InlineData("expectedOutcome")]
    [InlineData("evidence")]
    public void Required_arrays_reject_empty_values_and_nonstring_items(string field)
    {
        var corpus = ReadCorpus();
        First(corpus)[field] = new JsonArray();
        AssertDiagnostic(Validate(corpus), "." + field + ": must be a nonempty array");
        First(corpus)[field] = new JsonArray(1);
        AssertDiagnostic(Validate(corpus), "." + field + ": array item must be a string");
        First(corpus)[field] = new JsonArray(" ");
        AssertDiagnostic(Validate(corpus), "." + field + ": must be a nonempty single-line string");
        First(corpus)[field] = "invalid";
        AssertDiagnostic(Validate(corpus), "." + field + ": must be a nonempty array");
    }

    [Fact]
    public void Every_assigned_family_profile_requires_each_case_type()
    {
        foreach (var profile in AcceptanceCorpusContract.Profiles)
        foreach (var family in AcceptanceCorpusContract.FamiliesFor(profile))
        foreach (var type in new[] { "positive", "negative", "boundary", "recovery" })
        {
            var corpus = ReadCorpus();
            var cases = corpus["cases"]!.AsArray();
            foreach (var item in cases.Where(item => (string?)item!["family"] == family && (string?)item!["type"] == type
                         && Strings(item!["profiles"]!).Contains(profile, StringComparer.Ordinal)).ToArray())
                cases.Remove(item);
            AssertDiagnostic(Validate(corpus), $"{family}/{profile}: missing {type} case.");
        }
    }

    [Fact]
    public void Security_scope_is_exactly_the_assigned_family_profile_pairs()
    {
        Assert.Equal(new[] { "AC-003", "AC-004" }, AcceptanceCorpusContract.SecurityFamiliesFor("reference"));
        Assert.Equal(AcceptanceCorpusContract.SecurityFamilies, AcceptanceCorpusContract.SecurityFamiliesFor("website"));
        foreach (var profile in AcceptanceCorpusContract.Profiles)
            Assert.Empty(AcceptanceCorpusContract.NonSecurityFamiliesFor(profile).Intersect(AcceptanceCorpusContract.SecurityFamilies));
    }

    [Theory]
    [InlineData("AC-002")]
    [InlineData("AC-012")]
    [InlineData("AC-031")]
    public void Security_scope_rejects_unassigned_reference_pairs(string family)
    {
        var corpus = ReadCorpus();
        var item = Case(corpus, family, "website");
        item["profiles"] = new JsonArray("reference");
        item["scope"] = new JsonObject { ["reference"] = "Reference profile slice." };
        AssertDiagnostic(Validate(corpus), $"{(string?)item["id"]}: {family}/reference is outside the security corpus scope");
    }

    [Theory]
    [InlineData("AC-003")]
    [InlineData("AC-004")]
    public void A_relabeled_case_without_control_evidence_is_rejected(string family)
    {
        var corpus = ReadCorpus();
        var first = First(corpus);
        // Change metadata only; the relabeled case cites no control, so it cannot stand as a security case.
        first["family"] = family;
        first["id"] = family + "-B99";
        first["requirements"] = new JsonArray(RequirementFor(family));
        var errors = Validate(corpus);
        AssertDiagnostic(errors, $"{family}-B99: a security case must demonstrate a control mapped to {family}");
        Assert.DoesNotContain(errors, error => error.Contains("outside the security corpus scope", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("AC-010", "reference")]
    [InlineData("AC-010", "website")]
    [InlineData("AC-022", "website")]
    public void Initial_scope_still_rejects_other_families(string family, string profile)
    {
        var corpus = ReadCorpus();
        var first = First(corpus);
        first["family"] = family;
        first["id"] = family + "-B99";
        first["requirements"] = new JsonArray(RequirementFor(family));
        first["profiles"] = new JsonArray(profile);
        first["scope"] = new JsonObject { [profile] = "Out-of-scope family slice." };
        AssertDiagnostic(Validate(corpus), $"{family}-B99: {family}/{profile} is outside the initial corpus scope");
    }

    [Theory]
    [InlineData("undefined-control", "AC-003-B01: cites undefined control TM-W-999")]
    [InlineData("malformed-control", "AC-003-B01: cites undefined control TM-W-1")]
    [InlineData("profile-mismatch", "AC-003-B01: control TM-W-012 does not name profile reference")]
    [InlineData("unplanned-control", "AC-003-B01: control TM-G02-044 is not planned under AC-003 in the control map")]
    [InlineData("unplanned-control-outside-evidence", "AC-003-B01: control TM-G02-042 is not planned under AC-003 in the control map")]
    [InlineData("no-mapped-control", "AC-003-B01: a security case must demonstrate a control mapped to AC-003")]
    [InlineData("citation-outside-evidence", "AC-003-B01: a security case must demonstrate a control mapped to AC-003")]
    [InlineData("undemonstrated-website-control", "AC-002/website: control TM-W-059 is not demonstrated by a case")]
    [InlineData("undemonstrated-foundation-control", "AC-004/reference: control TM-G02-044 is not demonstrated by a case")]
    [InlineData("self-dependency", "AC-003-B01: a case cannot name its own family as a security dependency")]
    [InlineData("missing-control-map", "control map must contain readable control identities")]
    [InlineData("unreadable-control-map", "control map must contain readable control identities")]
    public void Security_control_tracing_has_its_specific_diagnostic(string mutation, string diagnostic)
    {
        var corpus = ReadCorpus();
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var item = Case(corpus, "AC-003", "reference");
        Assert.Equal("AC-003-B01", (string?)item["id"]);
        switch (mutation)
        {
            case "undefined-control": item["evidence"]![0] = (string?)item["evidence"]![0] + " Also TM-W-999."; break;
            case "malformed-control": item["steps"]![0] = (string?)item["steps"]![0] + " Also TM-W-1."; break;
            case "profile-mismatch": item["evidence"]![0] = (string?)item["evidence"]![0] + " Also TM-W-012."; break;
            case "unplanned-control": item["evidence"]![0] = (string?)item["evidence"]![0] + " Also TM-G02-044."; break;
            case "unplanned-control-outside-evidence":
                item["preconditions"]!.AsArray().Add("Files quarantine follows TM-G02-042.");
                break;
            case "no-mapped-control":
                item["steps"] = new JsonArray("Repeat the read.");
                item["evidence"] = new JsonArray("Request and response traces.");
                break;
            case "citation-outside-evidence":
                item["steps"] = new JsonArray("Repeat the read.");
                item["evidence"] = new JsonArray("Request and response traces.");
                item["preconditions"]!.AsArray().Add("The fixture applies TM-G02-017.");
                break;
            case "undemonstrated-website-control": ReplaceCitation(corpus, "AC-002", "TM-W-059", "TM-W-015"); break;
            case "undemonstrated-foundation-control": ReplaceCitation(corpus, "AC-004", "TM-G02-044", "TM-G02-020"); break;
            case "self-dependency": item["securityDependencies"]!.AsArray().Add("AC-003"); break;
            case "missing-control-map": files.Remove(AcceptanceCorpusContract.ControlsPath); break;
            case "unreadable-control-map": files[AcceptanceCorpusContract.ControlsPath] = "{\"controls\": 1}"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }
        AssertDiagnostic(AcceptanceCorpusContract.Validate(corpus.ToJsonString(), files), diagnostic);
    }

    [Fact]
    public void A_non_security_case_cannot_cite_a_control_planned_under_another_family()
    {
        var corpus = ReadCorpus();
        var first = First(corpus);
        Assert.Equal("AC-001", (string?)first["family"]);
        first["evidence"]![0] = (string?)first["evidence"]![0] + " Also TM-G02-044.";
        AssertDiagnostic(Validate(corpus), "AC-001-B01: control TM-G02-044 is not planned under AC-001 in the control map");
        first["evidence"]![0] = "Resolved-selection reports, as evidence for TM-G02-001.";
        Assert.DoesNotContain(Validate(corpus), error => error.Contains("AC-001-B01", StringComparison.Ordinal));
    }

    [Fact]
    public void Reference_coverage_requires_foundation_controls_only()
    {
        // Website-model controls that also name reference implement CMS contracts qualified at G-04, outside the G-02 slice.
        var map = JsonNode.Parse(RepositoryFiles.Current[AcceptanceCorpusContract.ControlsPath])!["controls"]!.AsArray();
        var excluded = map.Where(control => ((string?)control!["id"])!.StartsWith("TM-W-", StringComparison.Ordinal)
                && Strings(control!["profiles"]!).Contains("reference", StringComparer.Ordinal)
                && Strings(control!["evidence"]!["acceptance"]!).Intersect(AcceptanceCorpusContract.ReferenceSecurityFamilies).Any())
            .Select(control => (string?)control!["id"]).ToArray();
        Assert.NotEmpty(excluded);
        var reference = ReadCorpus()["cases"]!.AsArray().Where(item => Strings(item!["profiles"]!).Contains("reference", StringComparer.Ordinal)
            && AcceptanceCorpusContract.ReferenceSecurityFamilies.Contains((string?)item!["family"], StringComparer.Ordinal));
        Assert.DoesNotContain(reference, item => excluded.Any(id => item!.ToJsonString().Contains(id!, StringComparison.Ordinal)));
        Assert.Empty(AcceptanceCorpusContract.Validate(RepositoryFiles.Current[AcceptanceCorpusContract.CorpusPath], RepositoryFiles.Current));
    }

    [Theory]
    [InlineData("AC-002")]
    [InlineData("AC-003")]
    [InlineData("AC-004")]
    [InlineData("AC-012")]
    [InlineData("AC-031")]
    public void Security_family_documents_and_links_are_required(string family)
    {
        var path = AcceptanceCorpusContract.Folder + family + ".md";
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        files.Remove(path);
        AssertDiagnostic(AcceptanceCorpusContract.Validate(files[AcceptanceCorpusContract.CorpusPath], files),
            path + ": required family document is missing");

        files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var outcome = Case(ReadCorpus(), family, "website")["expectedOutcome"]![0]!.GetValue<string>();
        Assert.Contains(outcome, files[path]);
        files[path] = files[path].Replace(outcome, "A different observed outcome is required.", StringComparison.Ordinal);
        AssertDiagnostic(AcceptanceCorpusContract.Validate(files[AcceptanceCorpusContract.CorpusPath], files),
            path + ": Markdown differs from the complete corpus rendering");

        files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        files[AcceptanceCorpusContract.ReadmePath] = files[AcceptanceCorpusContract.ReadmePath]
            .Replace($"[{family}]({family}.md)", family, StringComparison.Ordinal);
        AssertDiagnostic(AcceptanceCorpusContract.Validate(files[AcceptanceCorpusContract.CorpusPath], files),
            "missing family link " + family);
    }

    [Fact]
    public void A_website_family_cannot_claim_reference_foundation_scope()
    {
        var corpus = ReadCorpus();
        var item = corpus["cases"]!.AsArray().First(item => (string?)item!["family"] == "AC-013")!;
        item["profiles"] = new JsonArray("reference");
        item["scope"] = new JsonObject { ["reference"] = "Reference email contract." };
        AssertDiagnostic(Validate(corpus), "AC-013/reference is outside the initial corpus scope");
    }

    [Fact]
    public void Format_allows_multiple_profiles_and_empty_security_dependencies()
    {
        var corpus = ReadCorpus();
        var item = First(corpus);
        item["profiles"]!.AsArray().Add("website");
        item["scope"]!["website"] = "W module compatibility with the declared providers.";
        item["securityDependencies"] = new JsonArray();
        item["layers"]!.AsArray().Add("isolation");
        Assert.Empty(AcceptanceCorpusContract.ValidateFormat(corpus.ToJsonString(), RepositoryFiles.Current[RegisterPath]));
    }

    [Theory]
    [InlineData("missing-family", "required family document is missing")]
    [InlineData("extra-family", "unexpected acceptance document")]
    [InlineData("missing-readme", "required corpus conventions are missing")]
    [InlineData("missing-family-link", "missing family link AC-001")]
    [InlineData("missing-security-reference", "missing security ownership reference AC-031")]
    [InlineData("missing-profile-vocabulary", "vocabulary is missing reference")]
    [InlineData("missing-layer-vocabulary", "vocabulary is missing operational-recovery")]
    [InlineData("changed-outcome", "Markdown differs from the complete corpus rendering")]
    [InlineData("extra-prose", "Markdown differs from the complete corpus rendering")]
    public void Readable_contract_drift_has_its_specific_diagnostic(string mutation, string diagnostic)
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var path = AcceptanceCorpusContract.Folder + "AC-001.md";
        switch (mutation)
        {
            case "missing-family": files.Remove(path); break;
            case "extra-family": files[AcceptanceCorpusContract.Folder + "AC-032.md"] = "# AC-032\n"; break;
            case "missing-readme": files.Remove(AcceptanceCorpusContract.ReadmePath); break;
            case "missing-family-link":
                files[AcceptanceCorpusContract.ReadmePath] = files[AcceptanceCorpusContract.ReadmePath]
                    .Replace("[AC-001](AC-001.md)", "AC-001", StringComparison.Ordinal);
                break;
            case "missing-security-reference":
                files[AcceptanceCorpusContract.ReadmePath] = files[AcceptanceCorpusContract.ReadmePath]
                    .Replace("AC-031", "security assurance", StringComparison.Ordinal);
                break;
            case "missing-profile-vocabulary":
                files["docs/profiles/README.md"] = files["docs/profiles/README.md"].Replace("`reference`", "reference", StringComparison.Ordinal);
                break;
            case "missing-layer-vocabulary":
                files["docs/security/threat-models/README.md"] = files["docs/security/threat-models/README.md"]
                    .Replace("`operational-recovery`", "operational recovery", StringComparison.Ordinal);
                break;
            case "changed-outcome":
                var firstOutcome = First(ReadCorpus())["expectedOutcome"]![0]!.GetValue<string>();
                Assert.Contains(firstOutcome, files[path]);
                files[path] = files[path].Replace(firstOutcome, "A different observed outcome is required.", StringComparison.Ordinal);
                break;
            case "extra-prose": files[path] += "\nAdditional case narrative.\n"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }
        AssertDiagnostic(AcceptanceCorpusContract.Validate(files[AcceptanceCorpusContract.CorpusPath], files), diagnostic);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Malformed_corpora_have_specific_parse_or_root_diagnostics(string text)
    {
        AssertDiagnostic(AcceptanceCorpusContract.Validate(text, RepositoryFiles.Current),
            text == "{" ? "Corpus must contain valid JSON" : "Corpus must be an object");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("title")]
    [InlineData("reference")]
    public void Duplicate_properties_are_rejected_at_root_case_and_scope(string field)
    {
        var text = RepositoryFiles.Current[AcceptanceCorpusContract.CorpusPath];
        var marker = "\"" + field + "\":";
        Assert.Contains(marker, text);
        text = text.Replace(marker, marker + " null, " + marker, StringComparison.Ordinal);
        AssertDiagnostic(AcceptanceCorpusContract.Validate(text, RepositoryFiles.Current), "duplicate JSON property " + field);
    }

    [Fact]
    public void A_missing_register_is_reported_without_throwing()
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        files.Remove(RegisterPath);
        AssertDiagnostic(AcceptanceCorpusContract.Validate(files[AcceptanceCorpusContract.CorpusPath], files),
            "Coverage register must contain readable requirement and family mappings");
    }

    private static JsonNode ReadCorpus() => JsonNode.Parse(RepositoryFiles.Current[AcceptanceCorpusContract.CorpusPath])!;
    private static JsonNode First(JsonNode corpus) => corpus["cases"]![0]!;
    private static JsonNode Case(JsonNode corpus, string family, string profile) => corpus["cases"]!.AsArray().First(item =>
        (string?)item!["family"] == family && Strings(item!["profiles"]!).Contains(profile, StringComparer.Ordinal))!;
    private static JsonArray RegisterEntries() => JsonNode.Parse(RepositoryFiles.Current[RegisterPath])!["entries"]!.AsArray();
    private static string[] Strings(JsonNode node) => node.AsArray().Select(value => value!.GetValue<string>()).ToArray();
    private static string RequirementFor(string family) => RegisterEntries().First(entry =>
        (string?)entry!["kind"] == "requirement" && Strings(entry!["acceptance"]!).Contains(family, StringComparer.Ordinal))!["id"]!.GetValue<string>();
    private static string RequirementOutside(string family) => RegisterEntries().First(entry =>
        (string?)entry!["kind"] == "requirement" && !Strings(entry!["acceptance"]!).Contains(family, StringComparer.Ordinal))!["id"]!.GetValue<string>();
    private static void ReplaceCitation(JsonNode corpus, string family, string citation, string replacement)
    {
        var changed = 0;
        foreach (var item in corpus["cases"]!.AsArray().Where(item => (string?)item!["family"] == family))
        foreach (var field in new[] { "preconditions", "steps", "expectedOutcome", "evidence" })
        {
            var values = item![field]!.AsArray();
            for (var index = 0; index < values.Count; index++)
            {
                var text = values[index]!.GetValue<string>();
                if (!text.Contains(citation, StringComparison.Ordinal)) continue;
                values[index] = text.Replace(citation, replacement, StringComparison.Ordinal);
                changed++;
            }
        }
        Assert.True(changed > 0, $"{family} cases must cite {citation} before the mutation.");
    }
    private static List<string> Validate(JsonNode corpus) => AcceptanceCorpusContract.Validate(corpus.ToJsonString(), RepositoryFiles.Current);
    private static void AssertDiagnostic(IEnumerable<string> errors, string diagnostic) =>
        Assert.Contains(errors, error => error.Contains(diagnostic, StringComparison.Ordinal));
}
