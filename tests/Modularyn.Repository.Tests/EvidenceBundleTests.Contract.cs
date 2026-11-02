using System.Text.Json.Nodes;
using Xunit;
using static Modularyn.Repository.Tests.EvidenceBundle;

namespace Modularyn.Repository.Tests;

// The complete-bundle contract's refusals. CI applies the contract to each half it assembles (docs/ci.md, "Evidence records"),
// so each rule needs a rejection case of its own: every case below assembles a valid half, breaks one rule, re-signs every hash
// that the rule does not concern, and requires the contract to refuse the half with that rule's code and nothing else.
public sealed partial class EvidenceBundleTests
{
    // A case with two planned security dependencies and one planned method, and the AC-001 case whose observations are retained.
    private const string Planned = "AC-005-P01";
    private const string Composed = "AC-001-P01";

    // Each mutation and the one refusal it must produce. Two of the contract's checks have no case, because no bundle reaches
    // them: "escaping retained location", since a retained location's pattern already refuses a "." or ".." segment, and
    // "unbounded test result", since a case's rows must equal the retained report rows, which report validation has already
    // bounded to planned rows and states. For the same reason a case's row can be repeated, from two reports, but never
    // unexpected.
    private static readonly (string Mutation, string Refusal)[] ContractMutations =
    [
        ("relative-directory", "bundle directory unavailable"),
        ("missing-directory", "bundle directory unavailable"),
        ("linked-root", "linked bundle root"),
        ("malformed-commit", "exact source commit required"),
        ("unknown-mode", "unknown bundle mode"),
        ("malformed-manifest", "Malformed or incomplete evidence bundle."),
        ("duplicate-property", "Malformed or incomplete evidence bundle."),
        ("manifest-field", "unbounded manifest fields"),
        ("manifest-format", "bundle format differs"),
        ("other-commit", "bundle binding differs"),
        ("other-mode", "bundle binding differs"),
        ("manifest-topology", "bundle topology differs"),
        ("host-digest", "a host record names no container image"),
        ("manifest-run", "run identity differs"),
        ("invalid-location", "invalid retained location"),
        ("linked-report", "linked retained location"),
        ("missing-report", "retained file missing"),
        ("measurement-bytes", "retained bytes differ"),
        ("report-bytes", "retained bytes differ"),
        ("record-bytes", "retained bytes differ"),
        ("result-bytes", "retained bytes differ"),
        ("artifact-bytes", "retained bytes differ"),
        ("measurement-field", "unbounded measurement fields"),
        ("measurement-binding", "measurement binding differs"),
        ("report-field", "unbounded report fields"),
        ("report-binding-field", "unbounded report binding"),
        ("report-binding", "report binding differs"),
        ("report-project", "unknown result project"),
        ("report-row-field", "unbounded result fields"),
        ("report-row-unplanned", "invalid planned result"),
        ("report-row-twice", "invalid planned result"),
        ("report-row-status", "invalid planned result"),
        ("corpus-hash", "bundle corpus differs"),
        ("plan-hash", "bundle plan differs"),
        ("missing-entry", "exactly one manifest record per planned case required"),
        ("duplicate-entry", "exactly one manifest record per planned case required"),
        ("entry-mode", "duplicate or misplaced record"),
        ("invalid-record", Planned + ": evidence must retain the corpus observations"),
        ("record-commit", "record commit or case differs"),
        ("record-case", "record commit or case differs"),
        ("record-identity", "record identity differs"),
        ("record-date", "record measurement date differs"),
        ("record-tuple", "record tuple differs from measured selection"),
        ("tuple-hash", "record tuple hash differs"),
        ("measured-implementation", "measured implementation omitted"),
        ("result-binding", "result binding differs"),
        ("result-missing-test", "one result per planned test required"),
        ("result-identity", "planned result identity differs"),
        ("result-rows", "case rows differ from retained runner results"),
        ("repeated-row", "duplicate or unexpected test row"),
        ("result-test-status", "test outcome contradicts executed rows"),
        ("result-extra-test", "unknown case result"),
        ("observations-omitted", "composition observations omitted"),
        ("observation-binding", "composition observation binding differs"),
        ("observation-snapshots", "composition snapshots differ"),
        ("observation-outcomes", "composition outcomes omitted"),
        ("observation-implementation", "composition implementation omitted"),
        ("observation-tuple", "composition measurement differs"),
        ("observation-outcome", "composition method outcome differs"),
        ("satisfied-dependencies", "security prerequisite differs from reviewed plan"),
        ("result-status", "case outcome contradicts planned results or security prerequisites"),
        ("record-status", "case outcome contradicts planned results or security prerequisites"),
        ("failure-recorded-as-blocked", "case outcome contradicts planned results or security prerequisites"),
        ("argument-limitation", "argument-only client observation must be stated"),
        ("unlisted-record", "unlisted or missing record"),
        ("pending-write", "interrupted bundle write")
    ];

    public static TheoryData<string, string, string> ContractCases
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var mode in Modes)
                foreach (var (mutation, refusal) in ContractMutations)
                    if (mutation != "host-digest" || mode == Host) data.Add(mode, mutation, refusal);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ContractCases))]
    public void A_half_that_breaks_one_bundle_rule_is_refused_by_that_rule(string mode, string mutation, string refusal)
    {
        using var run = new BundleRun(mode);
        run.Assemble();
        Reseal(run);
        Assert.Empty(EvidenceBundleContract.Errors(run.Directory, run.Commit, mode, Files));
        var test = run.Plan["cases"]!.AsArray().Single(row => Str(row!["caseId"]) == Planned)!["plannedTests"]![0]!;
        var name = Str(test["name"])!;
        var report = run.ReportPath(Str(test["project"])!);
        var manifest = Located(run, ManifestLocation);
        var measurement = Located(run, MeasurementLocation);
        var record = CaseFile(run, Planned, "record.json");
        var result = CaseFile(run, Planned, "result.json");
        var observation = Located(run, "observations/" + Composed.ToLowerInvariant() + ".json");
        var other = mode == Host ? Container : Host;
        var directory = run.Directory;
        var commit = run.Commit;
        var checkedMode = mode;
        var reseal = true;
        string? link = null;
        switch (mutation)
        {
            case "relative-directory": directory = Path.GetFileName(run.Directory); break;
            case "missing-directory": directory = Path.Combine(run.Directory, "absent"); break;
            case "linked-root":
                link = run.Directory + "-link";
                Directory.CreateSymbolicLink(link, run.Directory);
                directory = link;
                break;
            case "malformed-commit": commit = "0" + commit; break;
            case "unknown-mode": checkedMode = "container"; break;
            case "malformed-manifest": File.WriteAllText(manifest, "{"); reseal = false; break;
            case "duplicate-property":
                File.WriteAllText(manifest, "{\"format\":\"modularyn.evidence-bundle\"," + File.ReadAllText(manifest)[1..]);
                reseal = false;
                break;
            case "manifest-field": Change(manifest, value => value["extra"] = true); break;
            case "manifest-format": Change(manifest, value => value["formatVersion"] = 2); break;
            case "other-commit": commit = new string('e', 40); break;
            case "other-mode": checkedMode = other; break;
            case "manifest-topology": Change(manifest, value => value["tuple"]!["deployment"]!["topology"] = other); break;
            case "host-digest": Change(manifest, value => value["tuple"]!["deployment"]!["containerImageDigest"] = "sha256:" + new string('4', 64)); break;
            case "manifest-run": Change(manifest, value => value["runId"] = "Another run"); break;
            case "invalid-location": Change(manifest, value => value["measurement"]!["location"] = "/" + MeasurementLocation); reseal = false; break;
            case "linked-report":
                var target = Path.Combine(run.Directory, "linked-report.json");
                File.Move(report, target);
                File.CreateSymbolicLink(report, target);
                reseal = false;
                break;
            case "missing-report": File.Delete(report); reseal = false; break;
            // The same document with a final line end added: only the retained hash tells it apart.
            case "measurement-bytes": File.AppendAllText(measurement, "\n"); reseal = false; break;
            case "report-bytes": File.AppendAllText(report, "\n"); reseal = false; break;
            case "record-bytes": File.AppendAllText(record, "\n"); reseal = false; break;
            case "result-bytes": File.AppendAllText(result, "\n"); reseal = false; break;
            case "artifact-bytes": File.AppendAllText(Located(run, "implementation/metadata.dll"), "\n"); reseal = false; break;
            case "measurement-field": Change(measurement, value => value["extra"] = true); break;
            case "measurement-binding": Change(measurement, value => value["runId"] = "another-run"); break;
            case "report-field": Change(report, value => value["stdout"] = "Runner output."); break;
            case "report-binding-field": Change(report, value => value["binding"]!["host"] = "runner"); break;
            case "report-binding": Change(report, value => value["binding"]!["runId"] = "another-run"); break;
            case "report-project": Change(report, value => value["project"] = "tests/Modularyn.Another.Tests/Modularyn.Another.Tests.csproj"); break;
            case "report-row-field": Change(report, value => value["rows"]![0]!["message"] = "A failure message."); break;
            case "report-row-unplanned": Change(report, value => value["rows"]![0]!["name"] = name + "_Another"); break;
            case "report-row-twice": Change(report, value => value["rows"]!.AsArray().Add(value["rows"]![0]!.DeepClone())); break;
            case "report-row-status": Change(report, value => value["rows"]![0]!["status"] = "inconclusive"); break;
            case "corpus-hash": Change(manifest, value => value["corpusSha256"] = new string('0', 64)); break;
            case "plan-hash": Change(manifest, value => value["planSha256"] = new string('0', 64)); break;
            case "missing-entry": Change(manifest, value => value["records"]!.AsArray().RemoveAt(0)); break;
            case "duplicate-entry": Change(manifest, value => value["records"]!.AsArray().Add(value["records"]![0]!.DeepClone())); break;
            case "entry-mode": Change(manifest, value => value["records"]![0]!["mode"] = other); break;
            case "invalid-record": Change(record, value => value["expectedOutcomes"]![0] = "A different observation."); break;
            case "record-commit": Change(record, value => value["sourceCommit"] = new string('e', 40)); break;
            // Another case's valid record, at this case's location.
            case "record-case": File.Copy(CaseFile(run, "AC-005-R01", "record.json"), record, overwrite: true); break;
            case "record-identity":
                Change(record, value => value["recordId"] = "urn:modularyn:evidence:g02:" + Planned.ToLowerInvariant() + ":" + mode + ":another-run");
                break;
            case "record-date": Change(record, value => value["verification"]!["date"] = "2026-11-01"); break;
            case "record-tuple": Change(record, value => value["tuple"]!["deployment"]!["operatingSystem"]!["version"] = "6.8.1"); break;
            case "tuple-hash": Change(manifest, value => value["records"]![0]!["tupleSha256"] = new string('0', 64)); break;
            case "measured-implementation":
                var replacement = WriteNew(run.Directory, "implementation/another.dll", "Another implementation.\n"u8.ToArray());
                Change(record, value => value["implementationArtifacts"] = new JsonArray(replacement));
                break;
            case "result-binding": Change(result, value => value["runId"] = "another-run"); break;
            case "result-missing-test": Change(result, value => value["tests"]!.AsArray().RemoveAt(0)); break;
            case "result-identity": Change(result, value => value["tests"]![0]!["layer"] = "unit"); break;
            case "result-rows": Change(result, value => value["tests"]![0]!["rows"]![0]!["status"] = "failed"); break;
            case "repeated-row":
                // A second retained report of the same row, which the case's rows then repeat.
                var repeated = Read(report);
                repeated["rows"] = new JsonArray([.. repeated["rows"]!.AsArray().Where(row => Str(row!["name"]) == name).Select(row => row!.DeepClone())]);
                var artifact = WriteNew(run.Directory, "reports/repeated.json", Encoded(repeated));
                Change(manifest, value => value["reports"]!.AsArray().Add(artifact));
                Change(result, value => value["tests"]![0]!["rows"]!.AsArray().Add(value["tests"]![0]!["rows"]![0]!.DeepClone()));
                break;
            case "result-test-status": Change(result, value => value["tests"]![0]!["status"] = "failed"); break;
            case "result-extra-test":
                // Only an AC-001 case may carry the blocked composition-observations result.
                Change(result, value => value["tests"]!.AsArray().Add(new JsonObject
                {
                    ["name"] = "composition-observations", ["status"] = "blocked", ["rows"] = new JsonArray()
                }));
                break;
            case "observations-omitted":
                Change(CaseFile(run, Composed, "record.json"), value => value["verification"]!["results"]!.AsArray()
                    .Remove(value["verification"]!["results"]!.AsArray().Single(item => Str(item!["location"]) == "observations/" + Composed.ToLowerInvariant() + ".json")));
                break;
            case "observation-binding": Change(observation, value => value["runId"] = "another-run"); break;
            case "observation-snapshots": Change(CaseFile(run, Composed, "result.json"), value => value["observations"] = new JsonArray()); break;
            case "observation-outcomes":
                Change(CaseFile(run, Composed, "record.json"), value => value["observedOutcomes"] = new JsonArray([.. value["observedOutcomes"]!.AsArray()
                    .Where(item => !Str(item)!.StartsWith("Metadata-validation observation", StringComparison.Ordinal)).Select(item => item!.DeepClone())]));
                break;
            case "observation-implementation":
                var observed = WriteNew(run.Directory, "implementation/observed.dll", "Observed implementation.\n"u8.ToArray());
                Change(observation, value => value["implementationArtifacts"]!.AsArray().Add(observed));
                break;
            case "observation-tuple": Change(observation, value => value["tuple"]!["database"]!["version"] = "18.0"); break;
            case "observation-outcome": Change(observation, value => value["integrationOutcome"] = "failed"); break;
            case "satisfied-dependencies":
                // A functional record that clears its own security prerequisites, consistently passed in its record and result.
                Change(record, value =>
                {
                    foreach (var dependency in value["securityDependencies"]!.AsArray())
                    {
                        dependency!["status"] = "satisfied";
                        dependency["evidenceRecords"] = new JsonArray("urn:modularyn:evidence:security:" + Str(dependency["family"])!.ToLowerInvariant());
                    }

                    value["verification"]!["status"] = "passed";
                });
                Change(result, value => value["status"] = "passed");
                break;
            case "result-status": Change(result, value => value["status"] = "failed"); break;
            case "record-status":
                Change(record, value => value["verification"]!["status"] = "failed");
                Change(result, value => value["status"] = "failed");
                break;
            case "failure-recorded-as-blocked":
                // A failed row, retained consistently in its report and its case's rows, beneath a case still recorded as blocked.
                Change(report, value => value["rows"]!.AsArray().Single(row => Str(row!["name"]) == name)!["status"] = "failed");
                Change(result, value =>
                {
                    value["tests"]![0]!["rows"]![0]!["status"] = "failed";
                    value["tests"]![0]!["status"] = "failed";
                });
                break;
            case "argument-limitation":
                Change(CaseFile(run, "AC-003-B01", "record.json"), value => value["knownLimitations"] = new JsonArray([.. value["knownLimitations"]!.AsArray()
                    .Where(item => !Str(item)!.Contains("covered by argument", StringComparison.Ordinal)).Select(item => item!.DeepClone())]));
                break;
            case "unlisted-record":
                var unlisted = Path.Combine(run.Directory, run.Run, "unlisted", mode);
                Directory.CreateDirectory(unlisted);
                File.Copy(record, Path.Combine(unlisted, "record.json"));
                break;
            case "pending-write": File.Copy(record, Path.Combine(Path.GetDirectoryName(record)!, "record.pending")); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        try
        {
            if (reseal) Reseal(run);
            Assert.Equal(new[] { refusal }, EvidenceBundleContract.Errors(directory, commit, checkedMode, Files));
        }
        finally
        {
            if (link is not null) Directory.Delete(link);
        }
    }

    private static string Located(BundleRun run, string location) => Path.Combine([run.Directory, .. location.Split('/')]);

    private static string CaseFile(BundleRun run, string caseId, string file) => Path.Combine(run.Directory, run.Run, caseId.ToLowerInvariant(), run.Mode, file);

    private static void Change(string path, Action<JsonNode> change)
    {
        var value = Read(path);
        change(value);
        File.WriteAllBytes(path, Encoded(value));
    }

    // Re-signs every hash the contract compares, from the retained bytes: each record's artifacts and results, then the
    // manifest's measurement, reports and records. A mutation then reaches the rule it breaks rather than a hash comparison.
    private static void Reseal(BundleRun run)
    {
        void Sign(JsonNode? item)
        {
            var path = Located(run, Str(item!["location"])!);
            if (File.Exists(path)) item["sha256"] = Digest(File.ReadAllBytes(path));
        }

        var manifestPath = Located(run, ManifestLocation);
        var manifest = Read(manifestPath);
        foreach (var entry in manifest["records"]!.AsArray())
        {
            Change(Located(run, Str(entry!["location"])!), record =>
            {
                foreach (var item in record["implementationArtifacts"]!.AsArray().Concat(record["verification"]!["results"]!.AsArray())) Sign(item);
            });
            Sign(entry);
        }

        Sign(manifest["measurement"]);
        foreach (var item in manifest["reports"]!.AsArray()) Sign(item);
        File.WriteAllBytes(manifestPath, Encoded(manifest));
    }
}
