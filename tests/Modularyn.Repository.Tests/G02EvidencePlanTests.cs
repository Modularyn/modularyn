using System.Text.Json.Nodes;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class G02EvidencePlanTests
{
    [Fact]
    public void The_41_case_plan_and_unqualified_report_agree_with_the_corpus_register_and_profile()
    {
        var files = RepositoryFiles.Current;
        Assert.Empty(G02EvidencePlanContract.ValidatePlan(files[G02EvidencePlanContract.PlanPath], files));
        Assert.Empty(G02EvidencePlanContract.ValidateSkeleton(files[G02EvidencePlanContract.SkeletonPath], files));
    }

    [Theory]
    [InlineData("missing-case", "plan must map exactly the 41 reference cases")]
    [InlineData("duplicate-case", "duplicate planned case")]
    [InlineData("foreign-case", "plan must map exactly the 41 reference cases")]
    [InlineData("family", "family/profile drift")]
    [InlineData("profile", "family/profile drift")]
    [InlineData("unknown-order", "unknown G-02 work order")]
    [InlineData("wrong-scope", "work order does not cover this case")]
    [InlineData("security-owner", "work-order ownership")]
    [InlineData("project", "planned test package drift")]
    [InlineData("test-name", "planned test must be fully qualified and name its case")]
    [InlineData("compact-name", "planned test must be fully qualified and name its case")]
    [InlineData("case-in-class", "planned test must be fully qualified and name its case")]
    [InlineData("duplicate-test", "duplicate planned test name")]
    [InlineData("missing-order", "fields must be exactly")]
    [InlineData("missing-status", "fields must be exactly")]
    [InlineData("test-status", "test status must be planned or existing")]
    [InlineData("existing-name", "existing test must name a declared test method")]
    [InlineData("existing-planned", "merged AC-001 unit tests must be marked existing")]
    [InlineData("planned-existing", "existing test project must be present")]
    [InlineData("case-order", "fields must be exactly")]
    [InlineData("layers", "corpus layers drift")]
    [InlineData("test-layer", "planned tests must cover corpus layers")]
    [InlineData("missing-test-layer", "planned tests must cover corpus layers")]
    [InlineData("requirements", "corpus requirements drift")]
    [InlineData("database", "real database and container run required")]
    [InlineData("container", "real database and container run required")]
    [InlineData("filesystem", "filesystem requirement drift")]
    [InlineData("browser", "browser requirement drift")]
    [InlineData("dependency", "security dependency drift")]
    [InlineData("satisfied-dependency", "planned prerequisites must remain unmet")]
    [InlineData("claim", "case must remain planned")]
    [InlineData("unknown-field", "fields must be exactly")]
    public void Plan_drift_is_rejected_for_its_specific_reason(string mutation, string diagnostic)
    {
        var plan = Read(G02EvidencePlanContract.PlanPath);
        Assert.Empty(ValidatePlan(plan));
        var rows = plan["cases"]!.AsArray();
        var first = rows[0]!;
        var test = first["plannedTests"]![0]!;
        switch (mutation)
        {
            case "missing-case": rows.RemoveAt(0); break;
            case "duplicate-case": rows.Add(first.DeepClone()); break;
            case "foreign-case": first["caseId"] = "AC-001-B99"; break;
            case "family": first["family"] = "AC-005"; break;
            case "profile": first["profile"] = "website"; break;
            case "unknown-order": test["workOrder"] = "CODE-99"; break;
            case "wrong-scope": test["workOrder"] = "CODE-03"; break;
            case "security-owner": plan["workOrders"]!["SEC-08"]!["owner"] = "Implementation contributor"; break;
            case "project": test["project"] = "tests/Modularyn.Repository.Tests/Modularyn.Repository.Tests.csproj"; break;
            case "test-name": test["name"] = "ModuleStarts"; break;
            case "compact-name": test["name"] = test["name"]!.GetValue<string>().Replace("AC_001_B01_", "AC001B01_", StringComparison.Ordinal); break;
            case "case-in-class": test["name"] = "Modularyn.Framework.Hosting.Tests.AC_001_B01_AcceptanceTests.Only_inclusive_boundary_versions_activate"; break;
            case "duplicate-test": first["plannedTests"]!.AsArray().Add(test.DeepClone()); break;
            case "missing-order": test.AsObject().Remove("workOrder"); break;
            case "missing-status": test.AsObject().Remove("status"); break;
            case "test-status": test["status"] = "passed"; break;
            case "existing-name": test["name"] = test["name"]!.GetValue<string>() + "_Renamed"; break;
            case "existing-planned": test["status"] = "planned"; break;
            case "planned-existing": first["plannedTests"]!.AsArray().Last()!["status"] = "existing"; break;
            case "case-order": first["workOrder"] = "CODE-02"; break;
            case "layers": first["layers"]!.AsArray().RemoveAt(0); break;
            case "test-layer": test["layer"] = "ui"; break;
            case "missing-test-layer": first["plannedTests"]!.AsArray().RemoveAt(2); break;
            case "requirements": first["requirements"]!.AsArray().Clear(); break;
            case "database": first["needs"]!["realPostgreSql"] = false; break;
            case "container": first["needs"]!["containerDeployment"] = false; break;
            case "filesystem": Case(plan, "AC-009-P01")["needs"]!["filesystemStore"] = false; break;
            case "browser": Case(plan, "AC-003-N02")["needs"]!["browser"] = false; break;
            case "dependency": first["securityDependencies"]!.AsArray().Clear(); break;
            case "satisfied-dependency":
                first["securityDependencies"]![0]!["status"] = "satisfied";
                first["securityDependencies"]![0]!["evidenceRecords"]!.AsArray().Add("urn:modularyn:metadata-test:dependency");
                break;
            case "claim": first["status"] = "passed"; break;
            case "unknown-field": first["qualified"] = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        AssertDiagnostic(ValidatePlan(plan), diagnostic);
    }

    [Theory]
    [InlineData("AC-001-B01", "unit", "CODE-10")]
    [InlineData("AC-001-P01", "real-provider-integration", "CODE-02")]
    [InlineData("AC-003-N01", "isolation", "SEC-05")]
    [InlineData("AC-003-N02", "ui", "SEC-02")]
    [InlineData("AC-003-N03", "unit", "CODE-04")]
    [InlineData("AC-003-B02", "unit", "SEC-05")]
    [InlineData("AC-003-P02", "unit", "SEC-08")]
    [InlineData("AC-004-N02", "isolation", "CODE-03")]
    [InlineData("AC-004-B02", "isolation", "SEC-04")]
    [InlineData("AC-004-R02", "isolation", "CODE-07")]
    [InlineData("AC-009-B01", "real-provider-integration", "CODE-07")]
    public void Related_work_orders_cannot_take_a_case_layer_outside_their_assigned_scope(string caseId, string layer, string wrongOrder)
    {
        var plan = Read(G02EvidencePlanContract.PlanPath);
        Assert.Empty(ValidatePlan(plan));
        Case(plan, caseId)["plannedTests"]!.AsArray().First(test => test!["layer"]!.GetValue<string>() == layer)!["workOrder"] = wrongOrder;
        AssertDiagnostic(ValidatePlan(plan), caseId + ": work order does not cover this case");
    }

    [Theory]
    [InlineData("project", "existing test project must be present")]
    [InlineData("source", "existing test must name a declared test method")]
    [InlineData("namespace", "existing test must name a declared test method")]
    [InlineData("class", "existing test must name a declared test method")]
    [InlineData("method", "existing test must name a declared test method")]
    [InlineData("attribute", "existing test must name a declared test method")]
    public void Existing_test_references_must_resolve_to_the_declared_test(string mutation, string diagnostic)
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        const string project = "tests/Modularyn.Framework.Hosting.Tests/Modularyn.Framework.Hosting.Tests.csproj";
        const string source = "tests/Modularyn.Framework.Hosting.Tests/AcceptanceTests.cs";
        const string method = "AC_001_B01_Only_inclusive_boundary_versions_activate";
        Assert.Empty(G02EvidencePlanContract.ValidatePlan(files[G02EvidencePlanContract.PlanPath], files));
        switch (mutation)
        {
            case "project": files.Remove(project); break;
            case "source": files.Remove(source); break;
            case "namespace": files[source] = files[source].Replace("namespace Modularyn.Framework.Hosting.Tests;", "namespace Modularyn.Framework.Hosting.Tests.Moved;", StringComparison.Ordinal); break;
            case "class": files[source] = files[source].Replace("class AcceptanceTests", "class RenamedAcceptanceTests", StringComparison.Ordinal); break;
            case "method": files[source] = files[source].Replace(method, method + "_Renamed", StringComparison.Ordinal) + "\n// " + method + "\n"; break;
            case "attribute": files[source] = files[source].Replace("[Theory(DisplayName = \"AC-001-B01 selected version boundaries at activation\")]", "", StringComparison.Ordinal); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        AssertDiagnostic(G02EvidencePlanContract.ValidatePlan(files[G02EvidencePlanContract.PlanPath], files), diagnostic);
    }

    [Fact]
    public void Changing_the_corpus_or_coverage_cannot_leave_the_plan_silently_current()
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var corpus = Read(AcceptanceCorpusContract.CorpusPath);
        corpus["cases"]!.AsArray().RemoveAt(0);
        files[AcceptanceCorpusContract.CorpusPath] = corpus.ToJsonString();
        AssertDiagnostic(G02EvidencePlanContract.ValidatePlan(files[G02EvidencePlanContract.PlanPath], files), "reference corpus must contain exactly 41");

        files[AcceptanceCorpusContract.CorpusPath] = RepositoryFiles.Current[AcceptanceCorpusContract.CorpusPath];
        var register = Read(G02EvidencePlanContract.RegisterPath);
        var requirement = register["entries"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == "FND-001")!;
        requirement["acceptance"]!.AsArray().Clear();
        files[G02EvidencePlanContract.RegisterPath] = register.ToJsonString();
        AssertDiagnostic(G02EvidencePlanContract.ValidatePlan(files[G02EvidencePlanContract.PlanPath], files), "coverage-register disagreement");
    }

    [Theory]
    [InlineData("subset", "skeleton cannot claim a qualified commit or subset")]
    [InlineData("commit", "skeleton cannot claim a qualified commit or subset")]
    [InlineData("root-state", "report must remain a planning skeleton")]
    [InlineData("action-state", "skeleton actions must remain planned without evidence")]
    [InlineData("action-evidence", "skeleton actions must remain planned without evidence")]
    [InlineData("missing-action", "capability actions must cover exactly")]
    [InlineData("runtime", "capability tuple drift")]
    [InlineData("ui", "capability UI scope drift")]
    [InlineData("missing-support", "supporting capability gaps must remain visible")]
    [InlineData("missing-dependency", "all security prerequisites must remain visible")]
    [InlineData("met-dependency", "skeleton prerequisites must remain unmet")]
    [InlineData("missing-exclusion", "unsupported selection set must include")]
    [InlineData("exclusion-reason", "reference exclusion or reason drift")]
    [InlineData("host", "Windows and macOS hosts must remain unqualified")]
    public void The_skeleton_cannot_claim_qualification_or_erase_gaps(string mutation, string diagnostic)
    {
        var report = Read(G02EvidencePlanContract.SkeletonPath);
        var action = report["capabilities"]![0]!;
        switch (mutation)
        {
            case "subset": report["qualifiedSubset"]!.AsArray().Add("AC-001-B01"); break;
            case "commit": report["sourceCommit"] = new string('1', 40); break;
            case "root-state": report["status"] = "qualified"; break;
            case "action-state": action["status"] = "qualified"; break;
            case "action-evidence": action["evidenceRecords"]!.AsArray().Add("urn:modularyn:metadata-test:result"); break;
            case "missing-action": report["capabilities"]!.AsArray().RemoveAt(0); break;
            case "runtime": action["runtimeVersion"] = "10.0.13"; break;
            case "ui": action["uiDelivery"] = "native"; break;
            case "missing-support": report["supportingCapabilities"]!.AsArray().RemoveAt(0); break;
            case "missing-dependency": report["unmetDependencies"]!.AsArray().RemoveAt(0); break;
            case "met-dependency": report["unmetDependencies"]![0]!["status"] = "satisfied"; break;
            case "missing-exclusion": report["unsupportedSelections"]!.AsArray().RemoveAt(0); break;
            case "exclusion-reason": report["unsupportedSelections"]![0]!["reason"] = "Scope changed without review."; break;
            case "host":
                report["unsupportedSelections"]!.AsArray().Single(row => row!["selection"]!.GetValue<string>() == "Windows hosts")!["status"] = "qualified";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        AssertDiagnostic(G02EvidencePlanContract.ValidateSkeleton(report.ToJsonString(), RepositoryFiles.Current), diagnostic);
    }

    [Fact]
    public void A_new_reference_exclusion_is_detected_without_changing_the_test()
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        files[G02EvidencePlanContract.ReferencePath] += "\n| Additional deployment selection | Separate qualification is required. |\n";
        AssertDiagnostic(G02EvidencePlanContract.ValidateSkeleton(files[G02EvidencePlanContract.SkeletonPath], files), "unsupported selection set must include");
    }

    [Fact]
    public void Plans_accept_platform_line_endings_and_reject_duplicate_JSON_properties()
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key,
            pair => pair.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), StringComparer.Ordinal);
        Assert.Empty(G02EvidencePlanContract.ValidatePlan(files[G02EvidencePlanContract.PlanPath], files));
        Assert.Empty(G02EvidencePlanContract.ValidateSkeleton(files[G02EvidencePlanContract.SkeletonPath], files));
        var duplicate = files[G02EvidencePlanContract.PlanPath].Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1", StringComparison.Ordinal);
        AssertDiagnostic(G02EvidencePlanContract.ValidatePlan(duplicate, files), "duplicate JSON property");
    }

    private static JsonNode Read(string path) => JsonNode.Parse(RepositoryFiles.Current[path])!;
    private static JsonNode Case(JsonNode plan, string id) => plan["cases"]!.AsArray().Single(row => row!["caseId"]!.GetValue<string>() == id)!;
    private static List<string> ValidatePlan(JsonNode plan) => G02EvidencePlanContract.ValidatePlan(plan.ToJsonString(), RepositoryFiles.Current);
    private static void AssertDiagnostic(IEnumerable<string> errors, string diagnostic) => Assert.Contains(errors, error => error.Contains(diagnostic, StringComparison.Ordinal));
}
