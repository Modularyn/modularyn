using System.Text.Json.Nodes;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class EvidenceRecordTests
{
    private const string HostExample = EvidenceRecordContract.ExampleDirectory + "ac-005-p01-single-host.json";
    private const string ContainerExample = EvidenceRecordContract.ExampleDirectory + "ac-009-r01-single-host-container.json";

    [Fact]
    public void Every_example_validates_and_explicitly_has_no_execution_or_qualification_evidence()
    {
        var examples = RepositoryFiles.Current.Where(pair => pair.Key.StartsWith(EvidenceRecordContract.ExampleDirectory, StringComparison.Ordinal)
            && pair.Key.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        Assert.True(examples.Length >= 2);
        foreach (var (_, text) in examples)
        {
            Assert.Empty(Validate(text));
            var record = JsonNode.Parse(text)!;
            Assert.Equal("plan", record["kind"]!.GetValue<string>());
            Assert.Null(record["sourceCommit"]);
            Assert.Null(record["verification"]!["date"]);
            Assert.Equal("not-run", record["verification"]!["status"]!.GetValue<string>());
            Assert.Empty(record["verification"]!["results"]!.AsArray());
            Assert.Empty(record["observedOutcomes"]!.AsArray());
        }
    }

    [Theory]
    [InlineData("format")]
    [InlineData("formatVersion")]
    [InlineData("recordId")]
    [InlineData("kind")]
    [InlineData("gate")]
    [InlineData("subjects")]
    [InlineData("caseId")]
    [InlineData("family")]
    [InlineData("profile")]
    [InlineData("workOrder")]
    [InlineData("sourceCommit")]
    [InlineData("corpus")]
    [InlineData("tuple")]
    [InlineData("implementationArtifacts")]
    [InlineData("tests")]
    [InlineData("fixtureInputs")]
    [InlineData("expectedOutcomes")]
    [InlineData("observedOutcomes")]
    [InlineData("verification")]
    [InlineData("documentation")]
    [InlineData("knownLimitations")]
    [InlineData("securityDependencies")]
    public void Every_REL001_traceability_field_is_required(string field)
    {
        var record = Example();
        record.AsObject().Remove(field);
        AssertDiagnostic(Validate(record), "evidence#/" + field + ": schema missing required property");
    }

    [Theory]
    [InlineData("moduleVersions")]
    [InlineData("runtime")]
    [InlineData("database")]
    [InlineData("ui")]
    [InlineData("tenancyMode")]
    [InlineData("deployment")]
    [InlineData("providers")]
    public void Every_tuple_dimension_is_required(string field)
    {
        var record = Example();
        record["tuple"]!.AsObject().Remove(field);
        AssertDiagnostic(Validate(record), "evidence#/tuple/" + field + ": schema missing required property");
    }

    [Theory]
    [InlineData("owner", "evidence owner must agree with coverage register")]
    [InlineData("subject", "evidence requirement coverage drift")]
    [InlineData("case", "unknown evidence case")]
    [InlineData("profile", "evidence family/profile drift")]
    [InlineData("corpus", "evidence corpus identity drift")]
    [InlineData("outcomes", "evidence must retain the corpus observations")]
    [InlineData("test", "planned test must be fully qualified and name its case")]
    [InlineData("compact-name", "planned test must be fully qualified and name its case")]
    [InlineData("work-order", "evidence work-order drift")]
    [InlineData("test-mapping", "evidence planned-test drift")]
    [InlineData("documentation", "evidence documentation must resolve")]
    [InlineData("claim", "schema oneOf")]
    [InlineData("date", "schema oneOf")]
    [InlineData("commit", "schema oneOf")]
    [InlineData("observations", "schema oneOf")]
    [InlineData("version-range", "schema oneOf")]
    [InlineData("runtime", "G-02 evidence runtime pins drift")]
    [InlineData("module", "evidence tuple must include each tested module")]
    [InlineData("in-memory", "schema constant mismatch")]
    [InlineData("mock", "schema constant mismatch")]
    [InlineData("extra", "schema prohibits value")]
    public void Incomplete_or_misleading_records_are_rejected(string mutation, string diagnostic)
    {
        var record = Example();
        Assert.Empty(Validate(record));
        switch (mutation)
        {
            case "owner": record["subjects"]![0]!["owner"] = "Composition"; break;
            case "subject": record["subjects"]!.AsArray().RemoveAt(0); break;
            case "case": record["caseId"] = "AC-005-P99"; break;
            case "profile": record["profile"] = "website"; break;
            case "corpus": record["corpus"]!["sha256"] = new string('1', 64); break;
            case "outcomes": record["expectedOutcomes"]![0] = "A different observation."; break;
            case "test": record["tests"]![0]!["name"] = "PersistenceWorks"; break;
            case "compact-name": record["tests"]![0]!["name"] = record["tests"]![0]!["name"]!.GetValue<string>().Replace("AC_005_P01_", "AC005P01_", StringComparison.Ordinal); break;
            case "work-order": record["workOrder"] = "CODE-10"; break;
            case "test-mapping": record["tests"]![0]!["name"] = record["tests"]![0]!["name"]!.GetValue<string>() + "_Changed"; break;
            case "documentation": record["documentation"]![0] = "docs/evidence/absent.md"; break;
            case "claim": record["verification"]!["status"] = "passed"; break;
            case "date": record["verification"]!["date"] = "2026-10-26"; break;
            case "commit": record["sourceCommit"] = new string('1', 40); break;
            case "observations": record["observedOutcomes"]!.AsArray().Add("A claimed observation."); break;
            case "version-range": record["tuple"]!["providers"]![0]!["version"] = "10.*"; break;
            case "runtime": record["tuple"]!["runtime"]!["runtimeVersion"] = "10.0.13"; break;
            case "module": record["tuple"]!["moduleVersions"]![0]!["id"] = "Modularyn.Framework.Composition"; break;
            case "in-memory": record["tuple"]!["providers"]![0]!["mode"] = "in-memory"; break;
            case "mock": record["tuple"]!["providers"]![0]!["mode"] = "mock"; break;
            case "extra": record["qualified"] = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        AssertDiagnostic(Validate(record), diagnostic);
    }

    [Fact]
    public void Composition_evidence_retains_both_test_authors_and_all_projects_under_its_assembling_order()
    {
        // A transient plan shape: existing source and future methods supply no execution evidence.
        var record = JsonNode.Parse(RepositoryFiles.Current[ContainerExample])!;
        var plan = JsonNode.Parse(RepositoryFiles.Current[G02EvidencePlanContract.PlanPath])!;
        var row = plan["cases"]!.AsArray().Single(item => item!["caseId"]!.GetValue<string>() == "AC-001-B01")!;
        var corpus = JsonNode.Parse(RepositoryFiles.Current[AcceptanceCorpusContract.CorpusPath])!;
        var source = corpus["cases"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "AC-001-B01")!;
        var register = JsonNode.Parse(RepositoryFiles.Current[G02EvidencePlanContract.RegisterPath])!;
        var requirement = register["entries"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == "FND-001")!;
        record["recordId"] = "urn:modularyn:metadata-test:composition-plan";
        record["caseId"] = "AC-001-B01";
        record["family"] = "AC-001";
        record["workOrder"] = "CODE-10";
        record["subjects"] = new JsonArray(new JsonObject
        {
            ["id"] = "FND-001", ["kind"] = "requirement", ["owner"] = requirement["owner"]!.DeepClone()
        });
        record["securityDependencies"] = row["securityDependencies"]!.DeepClone();
        record["fixtureInputs"] = source["preconditions"]!.DeepClone();
        record["expectedOutcomes"] = source["expectedOutcome"]!.DeepClone();
        record["tests"] = new JsonArray(row["plannedTests"]!.AsArray().Select(test => (JsonNode)new JsonObject
        {
            ["project"] = test!["project"]!.DeepClone(), ["name"] = test!["name"]!.DeepClone(), ["layer"] = test!["layer"]!.DeepClone()
        }).ToArray());
        record["tuple"]!["moduleVersions"] = new JsonArray(row["plannedTests"]!.AsArray()
            .Select(test => Path.GetFileNameWithoutExtension(test!["project"]!.GetValue<string>())[..^6]).Distinct(StringComparer.Ordinal)
            .Select(package => (JsonNode)new JsonObject { ["id"] = package, ["version"] = null }).ToArray());
        record["implementationArtifacts"] = new JsonArray(new JsonObject
        {
            ["location"] = "src/Tooling/Modularyn.Tooling.Samples.TeamWorkspace/Modularyn.Tooling.Samples.TeamWorkspace.csproj", ["sha256"] = null
        });
        record["documentation"] = new JsonArray("docs/evidence/README.md", "docs/acceptance/AC-001.md");
        record["knownLimitations"] = new JsonArray("Unexecuted metadata fixture; versions, artifacts and results remain unmeasured.");
        Assert.Empty(Validate(record));
        record["workOrder"] = "CODE-02";
        AssertDiagnostic(Validate(record), "evidence work-order drift");
        record["workOrder"] = "CODE-10";
        // Dropping one unit method still covers both layers, but loses a planned project and observation.
        record["tests"]!.AsArray().RemoveAt(0);
        AssertDiagnostic(Validate(record), "evidence planned-test drift");
    }

    [Theory]
    [InlineData("module")]
    [InlineData("provider")]
    [InlineData("database")]
    [InlineData("operating-system")]
    [InlineData("ui")]
    public void Verification_cannot_leave_any_version_pin_unmeasured(string component)
    {
        var record = VerificationShape();
        record["tuple"]!["ui"]!.AsArray().Add(new JsonObject { ["name"] = "Razor", ["version"] = "10.0.14" });
        Assert.Empty(Validate(record));
        var tuple = record["tuple"]!;
        var selection = component switch
        {
            "module" => tuple["moduleVersions"]![0]!,
            "provider" => tuple["providers"]![0]!,
            "database" => tuple["database"]!,
            "operating-system" => tuple["deployment"]!["operatingSystem"]!,
            "ui" => tuple["ui"]![0]!,
            _ => throw new ArgumentOutOfRangeException(nameof(component))
        };
        selection["version"] = null;
        AssertDiagnostic(Validate(record), "verification requires every exact version pin");
    }

    [Fact]
    public void Verification_dates_container_identity_and_dependencies_cannot_be_invented_by_shape_alone()
    {
        var record = VerificationShape();
        record["verification"]!["date"] = "2026-02-30";
        AssertDiagnostic(Validate(record), "verification requires a real calendar date");
        record = VerificationShape(container: true);
        Assert.Empty(Validate(record));
        record["tuple"]!["deployment"]!["containerImageDigest"] = null;
        AssertDiagnostic(Validate(record), "container verification requires the image digest");
        record = VerificationShape();
        record["verification"]!["status"] = "passed";
        AssertDiagnostic(Validate(record), "passed verification cannot have unmet dependencies");
    }

    [Fact]
    public void Schema_evaluation_rejects_unknown_rules_remote_references_and_duplicate_JSON_fields()
    {
        var schema = JsonNode.Parse(RepositoryFiles.Current[EvidenceRecordContract.SchemaPath])!;
        schema["unevaluatedProperties"] = false;
        AssertDiagnostic(Validate(Example().ToJsonString(), schema.ToJsonString()), "unsupported schema keyword");
        schema.AsObject().Remove("unevaluatedProperties");
        schema["properties"]!["tuple"]!["$ref"] = "urn:modularyn:external-schema";
        AssertDiagnostic(Validate(Example().ToJsonString(), schema.ToJsonString()), "require local schema reference");
        var text = RepositoryFiles.Current[HostExample];
        AssertDiagnostic(Validate(text.Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1", StringComparison.Ordinal)), "duplicate JSON property");
    }

    [Fact]
    public void Corpus_identity_is_independent_of_checkout_line_endings()
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key,
            pair => pair.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), StringComparer.Ordinal);
        Assert.Empty(EvidenceRecordContract.Validate(files[HostExample], files[EvidenceRecordContract.SchemaPath], files));
        Assert.Empty(EvidenceRecordContract.Validate(files[ContainerExample], files[EvidenceRecordContract.SchemaPath], files));
    }

    // This transient metadata fixture is never retained as a result or used as provider evidence.
    private static JsonNode VerificationShape(bool container = false)
    {
        var record = JsonNode.Parse(RepositoryFiles.Current[container ? ContainerExample : HostExample])!;
        record["kind"] = "verification";
        record["sourceCommit"] = new string('1', 40);
        record["observedOutcomes"]!.AsArray().Add("No execution evidence; metadata-validation input only.");
        record["verification"]!["status"] = "blocked";
        record["verification"]!["date"] = "2026-10-26";
        record["verification"]!["results"]!.AsArray().Add(new JsonObject
        {
            ["location"] = "urn:modularyn:metadata-test:result",
            ["sha256"] = new string('2', 64)
        });
        record["implementationArtifacts"]![0]!["sha256"] = new string('3', 64);
        record["tuple"]!["moduleVersions"]![0]!["version"] = "0.1.0";
        foreach (var provider in record["tuple"]!["providers"]!.AsArray()) provider!["version"] = "10.0.14";
        record["tuple"]!["deployment"]!["operatingSystem"]!["version"] = "24.04";
        if (container) record["tuple"]!["deployment"]!["containerImageDigest"] = "sha256:" + new string('4', 64);
        return record;
    }

    private static JsonNode Example() => JsonNode.Parse(RepositoryFiles.Current[HostExample])!;
    private static List<string> Validate(JsonNode record) => Validate(record.ToJsonString());
    private static List<string> Validate(string text, string? schema = null) => EvidenceRecordContract.Validate(text,
        schema ?? RepositoryFiles.Current[EvidenceRecordContract.SchemaPath], RepositoryFiles.Current);
    private static void AssertDiagnostic(IEnumerable<string> errors, string diagnostic) => Assert.Contains(errors, error => error.Contains(diagnostic, StringComparison.Ordinal));
}
