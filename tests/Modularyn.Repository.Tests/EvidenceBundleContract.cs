using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Modularyn.Repository.Tests;

// The complete-bundle path supplements, rather than relaxes, the existing record contract. The four-case metadata fixtures
// in CiEvidenceTests still exercise that contract independently; CI requires a manifest and always takes this path.
internal static class EvidenceBundleContract
{
    internal static List<string> Errors(string directory, string? commit, string? mode, IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        try
        {
            Check(Path.IsPathFullyQualified(directory) && Directory.Exists(directory), "bundle directory unavailable");
            var root = new DirectoryInfo(directory);
            for (DirectoryInfo? ancestor = root; ancestor is not null; ancestor = ancestor.Parent)
                Check(ancestor.LinkTarget is null, "linked bundle root");
            Check(commit is not null && Regex.IsMatch(commit, "^[0-9a-f]{40}\\z", RegexOptions.CultureInvariant), "exact source commit required");
            Check(CiEvidence.Modes.Contains(mode, StringComparer.Ordinal), "unknown bundle mode");
            var manifest = Read(Retained(directory, "manifest.json"));
            Check(manifest.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(
                ["format", "formatVersion", "sourceCommit", "runId", "deploymentMode", "tuple", "corpusSha256", "planSha256", "measurement", "reports", "records"]),
                "unbounded manifest fields");
            Check(manifest["format"]!.GetValue<string>() == "modularyn.evidence-bundle"
                && manifest["formatVersion"]!.GetValue<int>() == 1, "bundle format differs");
            Check((string?)manifest["sourceCommit"] == commit && (string?)manifest["deploymentMode"] == mode, "bundle binding differs");
            var deployment = manifest["tuple"]!["deployment"]!;
            Check((string?)deployment["topology"] == mode, "bundle topology differs");
            Check(mode != "single-host" || deployment["containerImageDigest"] is null, "a host record names no container image");
            var run = manifest["runId"]!.GetValue<string>();
            Check(Regex.IsMatch(run, "^[a-z0-9][a-z0-9._-]{0,79}\\z", RegexOptions.CultureInvariant), "run identity differs");
            var plan = JsonNode.Parse(files[G02EvidencePlanContract.PlanPath])!["cases"]!.AsArray();
            var measurement = Read(Retained(directory, manifest["measurement"]!));
            Check(measurement.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(
                ["sourceCommit", "runId", "deploymentMode", "tuple", "date", "implementationArtifacts"]), "unbounded measurement fields");
            foreach (var field in new[] { "sourceCommit", "runId", "deploymentMode", "tuple" })
                Check(JsonNode.DeepEquals(manifest[field], measurement[field]), "measurement binding differs");
            var reports = manifest["reports"]!.AsArray().Select(item => Read(Retained(directory, item!))).ToArray();
            foreach (var report in reports)
            {
                Check(report.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["binding", "project", "rows"]),
                    "unbounded report fields");
                Check(report["binding"]!.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(
                    ["sourceCommit", "runId", "deploymentMode", "tuple"]), "unbounded report binding");
                foreach (var field in new[] { "sourceCommit", "runId", "deploymentMode", "tuple" })
                    Check(JsonNode.DeepEquals(manifest[field], report["binding"]![field]), "report binding differs");
                var project = (string?)report["project"];
                Check(plan.SelectMany(row => row!["plannedTests"]!.AsArray()).Any(test => (string?)test!["project"] == project), "unknown result project");
                var plannedRows = plan.SelectMany(row => row!["plannedTests"]!.AsArray()).Where(test => (string?)test!["project"] == project)
                    .SelectMany(test => ExpectedRows(test!["name"]!.GetValue<string>()).Select(key => (Name: test!["name"]!.GetValue<string>(), Row: key))).ToHashSet();
                var seenRows = new HashSet<(string Name, string Row)>();
                foreach (var resultRow in report["rows"]!.AsArray())
                {
                    Check(resultRow!.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal).SetEquals(["name", "row", "status"]),
                        "unbounded result fields");
                    var key = (Name: resultRow!["name"]!.GetValue<string>(), Row: resultRow!["row"]!.GetValue<string>());
                    Check(plannedRows.Contains(key) && seenRows.Add(key)
                        && (string?)resultRow!["status"] is "passed" or "failed" or "skipped" or "not-run" or "missing", "invalid planned result");
                }
            }
            Check((string?)manifest["corpusSha256"] == Hash(Encoding.UTF8.GetBytes(files[AcceptanceCorpusContract.CorpusPath].Replace("\r\n", "\n", StringComparison.Ordinal))),
                "bundle corpus differs");
            Check((string?)manifest["planSha256"] == Hash(Encoding.UTF8.GetBytes(files[G02EvidencePlanContract.PlanPath].Replace("\r\n", "\n", StringComparison.Ordinal))),
                "bundle plan differs");
            var entries = manifest["records"]!.AsArray();
            var expectedCases = plan.Select(row => row!["caseId"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
            Check(entries.Select(entry => entry!["caseId"]!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(expectedCases, StringComparer.Ordinal),
                "exactly one manifest record per planned case required");
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var caseId = entry!["caseId"]!.GetValue<string>();
                var location = entry!["location"]!.GetValue<string>();
                Check(location == run + "/" + caseId.ToLowerInvariant() + "/" + mode + "/record.json"
                    && (string?)entry!["mode"] == mode && listed.Add(location), "duplicate or misplaced record");
                var path = Retained(directory, entry!);
                var text = File.ReadAllText(path);
                var contract = EvidenceRecordContract.Validate(text, files[EvidenceRecordContract.SchemaPath], files);
                errors.AddRange(contract.Select(error => caseId + ": " + error));
                if (contract.Count != 0) continue;
                var record = Read(path);
                Check((string?)record["caseId"] == caseId && (string?)record["sourceCommit"] == commit
                    && (string?)record["kind"] == "verification", "record commit or case differs");
                Check((string?)record["recordId"] == "urn:modularyn:evidence:g02:" + caseId.ToLowerInvariant() + ":" + mode + ":" + run,
                    "record identity differs");
                Check(JsonNode.DeepEquals(record["verification"]!["date"], measurement["date"]), "record measurement date differs");
                var row = plan.Single(item => (string?)item!["caseId"] == caseId)!;
                var tuple = manifest["tuple"]!.DeepClone();
                if (!row["needs"]!["filesystemStore"]!.GetValue<bool>())
                    tuple["providers"] = new JsonArray(tuple["providers"]!.AsArray()
                        .Where(provider => (string?)provider!["name"] == "EF Core PostgreSQL").Select(provider => provider!.DeepClone()).ToArray());
                Check(JsonNode.DeepEquals(tuple, record["tuple"]), "record tuple differs from measured selection");
                var tupleBytes = Encoding.UTF8.GetBytes(tuple.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }) + "\n");
                Check((string?)entry!["tupleSha256"] == Hash(tupleBytes), "record tuple hash differs");
                foreach (var artifact in record["implementationArtifacts"]!.AsArray().Concat(record["verification"]!["results"]!.AsArray()))
                    _ = Retained(directory, artifact!);
                Check(measurement["implementationArtifacts"]!.AsArray().All(artifact => record["implementationArtifacts"]!.AsArray()
                    .Any(retained => JsonNode.DeepEquals(artifact, retained))), "measured implementation omitted");
                var result = Read(Retained(directory, record["verification"]!["results"]![0]!));
                Check((string?)result["sourceCommit"] == commit && (string?)result["runId"] == run
                    && (string?)result["deploymentMode"] == mode && (string?)result["caseId"] == caseId
                    && JsonNode.DeepEquals(result["tuple"], tuple), "result binding differs");
                var tests = result["tests"]!.AsArray();
                var statuses = new List<string>();
                foreach (var planned in row["plannedTests"]!.AsArray())
                {
                    var name = planned!["name"]!.GetValue<string>();
                    var matches = tests.Where(test => (string?)test!["name"] == name).ToArray();
                    Check(matches.Length == 1, "one result per planned test required");
                    var test = matches[0]!;
                    Check((string?)test["project"] == (string?)planned!["project"]
                        && (string?)test["layer"] == (string?)planned!["layer"], "planned result identity differs");
                    var rows = test["rows"]!.AsArray();
                    var reportedRows = new JsonArray(reports.Where(report => (string?)report["project"] == (string?)planned!["project"])
                        .SelectMany(report => report["rows"]!.AsArray()).Where(item => (string?)item!["name"] == name && (string?)item!["status"] != "missing")
                        .Select(item => item!.DeepClone()).ToArray());
                    Check(JsonNode.DeepEquals(reportedRows, rows), "case rows differ from retained runner results");
                    var expectedRows = ExpectedRows(name);
                    var keys = rows.Select(item => item!["row"]!.GetValue<string>()).ToArray();
                    Check(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length && keys.All(expectedRows.Contains), "duplicate or unexpected test row");
                    Check(rows.All(item => (string?)item!["name"] == name && (string?)item!["status"] is "passed" or "failed" or "skipped" or "not-run" or "missing"),
                        "unbounded test result");
                    var status = keys.Length != expectedRows.Length || rows.Any(item => (string?)item!["status"] is "skipped" or "not-run" or "missing")
                        ? "blocked" : rows.Any(item => (string?)item!["status"] == "failed") ? "failed" : "passed";
                    Check((string?)test["status"] == status, "test outcome contradicts executed rows");
                    statuses.Add(status);
                }
                var extra = tests.Where(test => !row["plannedTests"]!.AsArray().Any(planned => (string?)planned!["name"] == (string?)test!["name"])).ToArray();
                Check(extra.Length == 0 || extra.Length == 1 && caseId.StartsWith("AC-001-", StringComparison.Ordinal)
                    && (string?)extra[0]!["name"] == "composition-observations" && (string?)extra[0]!["status"] == "blocked", "unknown case result");
                if (extra.Length != 0) statuses.Add("blocked");
                if (caseId.StartsWith("AC-001-", StringComparison.Ordinal) && extra.Length == 0)
                {
                    var observationLocation = "observations/" + caseId.ToLowerInvariant() + ".json";
                    Check(record["verification"]!["results"]!.AsArray().Count(item => (string?)item!["location"] == observationLocation) == 1,
                        "composition observations omitted");
                    var observation = Read(Retained(directory, observationLocation));
                    Check((string?)observation["sourceCommit"] == commit && (string?)observation["runId"] == run
                        && (string?)observation["deploymentMode"] == mode && (string?)observation["caseId"] == caseId,
                        "composition observation binding differs");
                    Check(JsonNode.DeepEquals(observation["observations"], result["observations"]), "composition snapshots differ");
                    Check(observation["observedOutcomes"]!.AsArray().All(item => record["observedOutcomes"]!.AsArray()
                        .Any(retained => JsonNode.DeepEquals(item, retained))), "composition outcomes omitted");
                    Check(observation["implementationArtifacts"]!.AsArray().All(item => record["implementationArtifacts"]!.AsArray()
                        .Any(retained => JsonNode.DeepEquals(item, retained))), "composition implementation omitted");
                    foreach (var field in new[] { "runtime", "database", "deployment", "providers", "tenancyMode" })
                        Check(JsonNode.DeepEquals(observation["tuple"]![field], manifest["tuple"]![field]), "composition measurement differs");
                    var integration = tests.Single(item => (string?)item!["layer"] == "real-provider-integration")!;
                    Check((string?)integration["status"] != "passed" || (string?)observation["integrationOutcome"] == "passed",
                        "composition method outcome differs");
                }
                // Functional tests cannot clear Builder-owned security evidence, including circular family dependencies.
                Check(JsonNode.DeepEquals(record["securityDependencies"], row["securityDependencies"]), "security prerequisite differs from reviewed plan");
                var unmet = record["securityDependencies"]!.AsArray().Any(dependency => (string?)dependency!["status"] != "satisfied"
                    || dependency!["evidenceRecords"]!.AsArray().Count == 0);
                var outcome = statuses.Contains("blocked", StringComparer.Ordinal) ? "blocked"
                    : statuses.Contains("failed", StringComparer.Ordinal) ? "failed" : unmet ? "blocked" : "passed";
                Check((string?)record["verification"]!["status"] == outcome && (string?)result["status"] == outcome,
                    "case outcome contradicts planned results or security prerequisites");
                if (caseId is "AC-003-B01" or "AC-003-N01" or "AC-003-P01")
                    Check(record["knownLimitations"]!.AsArray().Any(item => item!.GetValue<string>().Contains("covered by argument", StringComparison.Ordinal)),
                        "argument-only client observation must be stated");
            }
            var actual = Directory.EnumerateFiles(directory, "record.json", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(StringComparer.Ordinal);
            Check(actual.SetEquals(listed), "unlisted or missing record");
            Check(!Directory.EnumerateFiles(directory, "*.pending", SearchOption.AllDirectories).Any(), "interrupted bundle write");
        }
        catch (Exception exception)
        {
            // Never surface raw JSON values, file-system paths or exception data in CI.
            errors.Add(exception is BundleRefusal refusal ? refusal.Message : "Malformed or incomplete evidence bundle.");
        }
        return errors;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string[] ExpectedRows(string name) => name.Contains(".CompositionAcceptanceTests.AC_001_", StringComparison.Ordinal) ? ["mode"]
        : name.Contains(".G02AC008Tests.", StringComparison.Ordinal) ? ["dotnet", "typescript"] : ["method"];
    private static void Check(bool condition, string code) { if (!condition) throw new BundleRefusal(code); }
    private sealed class BundleRefusal(string message) : InvalidOperationException(message);
    private static JsonNode Read(string path)
    {
        var text = File.ReadAllText(path);
        _ = ContentPackageContract.Parse(text); // Includes the repository's duplicate-property refusal.
        return JsonNode.Parse(text)!;
    }
    private static string Retained(string root, JsonNode item)
    {
        var path = Retained(root, item!["location"]!.GetValue<string>());
        Check((string?)item!["sha256"] == Hash(File.ReadAllBytes(path)), "retained bytes differ");
        return path;
    }
    private static string Retained(string root, string location)
    {
        Check(Regex.IsMatch(location, "^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*\\z", RegexOptions.CultureInvariant), "invalid retained location");
        var path = root;
        foreach (var part in location.Split('/'))
        {
            Check(part is not "." and not "..", "escaping retained location");
            path = Path.Combine(path, part);
            Check(new FileInfo(path).LinkTarget is null && new DirectoryInfo(path).LinkTarget is null, "linked retained location");
        }
        Check(File.Exists(path), "retained file missing");
        return path;
    }
}
