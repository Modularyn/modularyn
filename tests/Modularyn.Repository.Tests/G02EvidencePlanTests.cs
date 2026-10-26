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
    [InlineData("layers", "corpus layers drift")]
    [InlineData("test-layer", "planned tests must cover corpus layers")]
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
        var rows = plan["cases"]!.AsArray();
        var first = rows[0]!;
        switch (mutation)
        {
            case "missing-case": rows.RemoveAt(0); break;
            case "duplicate-case": rows.Add(first.DeepClone()); break;
            case "foreign-case": first["caseId"] = "AC-001-B99"; break;
            case "family": first["family"] = "AC-005"; break;
            case "profile": first["profile"] = "website"; break;
            case "unknown-order": first["workOrder"] = "CODE-99"; break;
            case "wrong-scope": first["workOrder"] = "CODE-03"; break;
            case "security-owner": plan["workOrders"]!["SEC-08"]!["owner"] = "Implementation contributor"; break;
            case "project": first["testProject"] = "tests/Modularyn.Repository.Tests/Modularyn.Repository.Tests.csproj"; break;
            case "test-name": first["plannedTests"]![0]!["name"] = "ModuleStarts"; break;
            case "layers": first["layers"]!.AsArray().RemoveAt(0); break;
            case "test-layer": first["plannedTests"]![0]!["layer"] = "ui"; break;
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
    [InlineData("AC-003-N01", "SEC-05")]
    [InlineData("AC-003-N02", "SEC-02")]
    [InlineData("AC-003-N03", "CODE-04")]
    [InlineData("AC-003-B02", "SEC-05")]
    [InlineData("AC-003-P02", "SEC-08")]
    [InlineData("AC-004-N02", "CODE-03")]
    [InlineData("AC-004-B02", "SEC-04")]
    [InlineData("AC-004-R02", "CODE-07")]
    [InlineData("AC-009-B01", "CODE-07")]
    public void Related_work_orders_cannot_take_a_case_outside_their_assigned_scope(string caseId, string wrongOrder)
    {
        var plan = Read(G02EvidencePlanContract.PlanPath);
        Case(plan, caseId)["workOrder"] = wrongOrder;
        AssertDiagnostic(ValidatePlan(plan), caseId + ": work order does not cover this case");
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
