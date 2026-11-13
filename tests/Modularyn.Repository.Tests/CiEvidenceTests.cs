using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

// CI's check of the evidence records its own run wrote: the host records of the real-provider job and the container
// records of the container-build job (docs/ci.md). CI runs the one test below through the test executable's own filter, after
// the run that wrote the records, with MODULARYN_EVIDENCE_DIR, MODULARYN_SOURCE_COMMIT and MODULARYN_EVIDENCE_MODE set.
internal static class CiEvidence
{
    public const string DirectoryVariable = "MODULARYN_EVIDENCE_DIR";
    public const string CommitVariable = "MODULARYN_SOURCE_COMMIT";
    public const string ModeVariable = "MODULARYN_EVIDENCE_MODE";
    public static readonly string[] Cases = ["AC-001-B01", "AC-001-N01", "AC-001-P01", "AC-001-R01"];
    public static readonly string[] Modes = ["single-host", "single-host-container"];
    private static readonly Regex Commit = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Location = new("^[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*$", RegexOptions.CultureInvariant);

    // One record for each AC-001 case, all from one run, each valid under the repository's schema and evidence contracts,
    // bound to the run's exact commit and deployment mode, and retaining exactly the bytes its result and artifact hashes name.
    public static List<string> Errors(string directory, string? commit, string? mode, IReadOnlyDictionary<string, string> files)
    {
        if (File.Exists(Path.Combine(directory, "manifest.json")))
            return EvidenceBundleContract.Errors(directory, commit, mode, files);
        var errors = new List<string>();
        if (commit is null || !Commit.IsMatch(commit))
        {
            errors.Add($"{CommitVariable} must be the run's exact forty-character commit.");
        }

        if (mode is null || !Modes.Contains(mode, StringComparer.Ordinal))
        {
            errors.Add($"{ModeVariable} must be {string.Join(" or ", Modes)}.");
        }

        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
        {
            errors.Add($"{DirectoryVariable} must name an existing absolute directory.");
            return errors;
        }

        foreach (var pending in Directory.EnumerateFiles(directory, "record.pending", SearchOption.AllDirectories))
        {
            errors.Add($"{Relative(directory, pending)}: an interrupted write left a pending record.");
        }

        var cases = new List<string>();
        var runs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "record.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Relative(directory, path);
            var text = File.ReadAllText(path);
            var contract = EvidenceRecordContract.Validate(text, files[EvidenceRecordContract.SchemaPath], files);
            errors.AddRange(contract.Select(error => $"{relative}: {error}"));
            if (contract.Count != 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(text);
            var record = document.RootElement;
            var caseId = record.GetProperty("caseId").GetString()!;
            var deployment = record.GetProperty("tuple").GetProperty("deployment");
            var topology = deployment.GetProperty("topology").GetString();
            cases.Add(caseId);
            var segments = relative.Split('/');
            if (segments.Length != 4 || segments[1] != caseId.ToLowerInvariant() || segments[2] != topology)
            {
                errors.Add($"{relative}: a record lies at <run>/<case>/<mode>/record.json for its own case and mode.");
            }
            else
            {
                runs.Add(segments[0]);
            }

            if (record.GetProperty("kind").GetString() != "verification" || record.GetProperty("family").GetString() != "AC-001"
                || record.GetProperty("workOrder").GetString() != "CODE-10")
            {
                errors.Add($"{relative}: must be a CODE-10 AC-001 verification record.");
            }

            if (record.GetProperty("sourceCommit").GetString() != commit)
            {
                errors.Add($"{relative}: its source commit is not the run's commit {commit}.");
            }

            if (topology != mode)
            {
                errors.Add($"{relative}: its topology {topology} is not the run's deployment mode {mode}.");
            }

            if (topology == "single-host" && deployment.GetProperty("containerImageDigest").ValueKind != JsonValueKind.Null)
            {
                errors.Add($"{relative}: a host record names no container image.");
            }

            var retained = record.GetProperty("implementationArtifacts").EnumerateArray()
                .Concat(record.GetProperty("verification").GetProperty("results").EnumerateArray());
            foreach (var item in retained)
            {
                var location = item.GetProperty("location").GetString()!;
                var hash = item.GetProperty("sha256");
                var target = Path.Combine(directory, location);
                if (!Location.IsMatch(location) || location.Split('/').Any(segment => segment is "." or ".."))
                {
                    errors.Add($"{relative}: {location} is not a location beneath the evidence directory.");
                }
                else if (hash.ValueKind != JsonValueKind.String || !File.Exists(target))
                {
                    errors.Add($"{relative}: {location} must be a retained file with its measured SHA-256.");
                }
                else if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(target))) != hash.GetString())
                {
                    errors.Add($"{relative}: the retained bytes of {location} do not match its SHA-256.");
                }
            }
        }

        if (!cases.Order(StringComparer.Ordinal).SequenceEqual(Cases, StringComparer.Ordinal))
        {
            errors.Add($"The run must write exactly one valid record for each of {string.Join(", ", Cases)}, not [{string.Join(", ", cases)}].");
        }

        if (runs.Count > 1)
        {
            errors.Add($"The records come from {runs.Count} runs; a run checks only the records it wrote.");
        }

        return errors;
    }

    private static string Relative(string directory, string path) => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
}

public sealed class CiEvidenceTests
{
    [Fact]
    public void The_records_this_CI_run_wrote_validate_against_the_repository_contracts()
    {
        var directory = Environment.GetEnvironmentVariable(CiEvidence.DirectoryVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(directory), $"CI sets {CiEvidence.DirectoryVariable} to check the evidence records its run wrote.");
        Assert.Empty(CiEvidence.Errors(directory, Environment.GetEnvironmentVariable(CiEvidence.CommitVariable),
            Environment.GetEnvironmentVariable(CiEvidence.ModeVariable), RepositoryFiles.Current));
    }

    [Theory]
    [InlineData("single-host")]
    [InlineData("single-host-container")]
    public void A_complete_run_of_valid_records_is_accepted(string mode)
    {
        using var run = new MetadataRun(mode);
        Assert.Empty(run.Errors());
    }

    [Theory]
    [InlineData("no-records", "exactly one valid record for each of")]
    [InlineData("missing-case", "exactly one valid record for each of")]
    [InlineData("second-run", "a run checks only the records it wrote")]
    [InlineData("pending-record", "an interrupted write left a pending record")]
    [InlineData("invalid-record", "evidence must retain the corpus observations")]
    [InlineData("other-commit", "is not the run's commit")]
    [InlineData("other-mode", "is not the run's deployment mode")]
    [InlineData("misplaced-record", "a record lies at <run>/<case>/<mode>/record.json")]
    [InlineData("host-digest", "a host record names no container image")]
    [InlineData("changed-result", "do not match its SHA-256")]
    [InlineData("changed-artifact", "do not match its SHA-256")]
    [InlineData("missing-result", "must be a retained file with its measured SHA-256")]
    [InlineData("urn-location", "is not a location beneath the evidence directory")]
    [InlineData("malformed-commit", "must be the run's exact forty-character commit")]
    [InlineData("unknown-mode", "must be single-host or single-host-container")]
    [InlineData("relative-directory", "must name an existing absolute directory")]
    public void A_run_that_breaks_a_rule_is_rejected(string mutation, string diagnostic)
    {
        using var run = new MetadataRun(mutation == "host-digest" ? "single-host" : "single-host-container");
        Assert.Empty(run.Errors());
        var first = run.Records[0];
        var directory = run.Directory;
        var commit = run.Commit;
        var mode = run.Mode;
        switch (mutation)
        {
            case "no-records": foreach (var record in run.Records) File.Delete(record.Path); break;
            case "missing-case": File.Delete(first.Path); break;
            case "second-run": run.Write("AC-001-B01", "second-run"); File.Delete(first.Path); break;
            case "pending-record": File.Copy(first.Path, Path.Combine(Path.GetDirectoryName(first.Path)!, "record.pending")); break;
            case "invalid-record": first.Change(record => record["expectedOutcomes"]![0] = "A different observation."); break;
            case "other-commit": commit = new string('e', 40); break;
            case "other-mode": mode = "single-host"; break;
            case "misplaced-record": System.IO.Directory.Move(Path.Combine(directory, "run", "ac-001-b01"), Path.Combine(directory, "run", "misplaced")); break;
            case "host-digest": first.Change(record => record["tuple"]!["deployment"]!["containerImageDigest"] = "sha256:" + new string('4', 64)); break;
            case "changed-result": File.AppendAllText(first.Result, "\n"); break;
            case "changed-artifact": File.AppendAllText(first.Artifact, "\n"); break;
            case "missing-result": File.Delete(first.Result); break;
            // The schema admits a URN location, which names no retained file beneath the evidence directory.
            case "urn-location": first.Change(record => record["verification"]!["results"]![0]!["location"] = "urn:modularyn:metadata-test:result"); break;
            case "malformed-commit": commit = "0" + commit; break;
            case "unknown-mode": mode = "container"; break;
            case "relative-directory": directory = Path.GetFileName(directory); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        Assert.Contains(CiEvidence.Errors(directory, commit, mode, RepositoryFiles.Current),
            error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    // A transient directory of metadata-validation records shaped as a CI run writes them. Nothing here was executed, and the
    // directory is deleted when the test ends.
    private sealed class MetadataRun : IDisposable
    {
        public MetadataRun(string mode)
        {
            Mode = mode;
            Directory = Path.Combine(Path.GetTempPath(), "modularyn-ci-evidence-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var caseId in CiEvidence.Cases)
            {
                Records.Add(Write(caseId, "run"));
            }
        }

        public string Directory { get; }

        public string Mode { get; }

        public string Commit { get; } = new string('1', 40);

        public List<WrittenRecord> Records { get; } = [];

        public List<string> Errors() => CiEvidence.Errors(Directory, Commit, Mode, RepositoryFiles.Current);

        public WrittenRecord Write(string caseId, string runId)
        {
            var files = RepositoryFiles.Current;
            var folder = Path.Combine(Directory, runId, caseId.ToLowerInvariant(), Mode);
            System.IO.Directory.CreateDirectory(Path.Combine(folder, "implementation"));
            var prefix = runId + "/" + caseId.ToLowerInvariant() + "/" + Mode + "/";
            var artifact = Encoding.UTF8.GetBytes("Metadata-validation implementation bytes.\n");
            var result = Encoding.UTF8.GetBytes("{\"integrationOutcome\": \"passed\"}\n");
            File.WriteAllBytes(Path.Combine(folder, "implementation", "artifact.dll"), artifact);
            File.WriteAllBytes(Path.Combine(folder, "result.json"), result);
            var record = JsonNode.Parse(files[EvidenceRecordContract.ExampleDirectory + "ac-009-r01-single-host-container.json"])!;
            var row = JsonNode.Parse(files[G02EvidencePlanContract.PlanPath])!["cases"]!.AsArray().Single(item => (string?)item!["caseId"] == caseId)!;
            var source = JsonNode.Parse(files[AcceptanceCorpusContract.CorpusPath])!["cases"]!.AsArray().Single(item => (string?)item!["id"] == caseId)!;
            var register = JsonNode.Parse(files[G02EvidencePlanContract.RegisterPath])!["entries"]!.AsArray();
            record["recordId"] = "urn:modularyn:evidence:g02:" + caseId.ToLowerInvariant() + ":" + Mode + ":" + runId;
            record["kind"] = "verification";
            record["caseId"] = caseId;
            record["family"] = "AC-001";
            record["workOrder"] = "CODE-10";
            record["sourceCommit"] = Commit;
            record["subjects"] = new JsonArray(source["requirements"]!.AsArray().Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id!.DeepClone(), ["kind"] = "requirement",
                ["owner"] = register.Single(entry => (string?)entry!["id"] == (string?)id)!["owner"]!.DeepClone()
            }).ToArray());
            record["securityDependencies"] = row["securityDependencies"]!.DeepClone();
            record["fixtureInputs"] = source["preconditions"]!.DeepClone();
            record["expectedOutcomes"] = source["expectedOutcome"]!.DeepClone();
            record["observedOutcomes"] = new JsonArray("No execution evidence; metadata-validation input only.");
            record["tests"] = new JsonArray(row["plannedTests"]!.AsArray().Select(test => (JsonNode)new JsonObject
            {
                ["project"] = test!["project"]!.DeepClone(), ["name"] = test!["name"]!.DeepClone(), ["layer"] = test!["layer"]!.DeepClone()
            }).ToArray());
            var tuple = record["tuple"]!;
            tuple["moduleVersions"] = new JsonArray(row["plannedTests"]!.AsArray()
                .Select(test => Path.GetFileNameWithoutExtension(test!["project"]!.GetValue<string>())[..^6]).Distinct(StringComparer.Ordinal)
                .Select(package => (JsonNode)new JsonObject { ["id"] = package, ["version"] = "1.0.0" }).ToArray());
            foreach (var provider in tuple["providers"]!.AsArray())
            {
                provider!["version"] = "10.0.3";
            }

            tuple["deployment"]!["operatingSystem"]!["version"] = "6.8.0";
            tuple["deployment"]!["topology"] = Mode;
            tuple["deployment"]!["containerImageDigest"] = Mode == "single-host-container" ? "sha256:" + new string('4', 64) : null;
            record["implementationArtifacts"] = new JsonArray(new JsonObject
            {
                ["location"] = prefix + "implementation/artifact.dll", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(artifact))
            });
            record["verification"] = new JsonObject
            {
                ["status"] = "blocked", ["date"] = "2026-11-02",
                ["results"] = new JsonArray(new JsonObject { ["location"] = prefix + "result.json", ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(result)) })
            };
            record["documentation"] = new JsonArray("docs/evidence/README.md", "docs/acceptance/AC-001.md");
            record["knownLimitations"] = new JsonArray("Metadata-validation input only; nothing was executed.");
            var written = new WrittenRecord(caseId, Path.Combine(folder, "record.json"), Path.Combine(folder, "result.json"),
                Path.Combine(folder, "implementation", "artifact.dll"));
            File.WriteAllText(written.Path, record.ToJsonString());
            return written;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed record WrittenRecord(string Case, string Path, string Result, string Artifact)
    {
        public void Change(Action<JsonNode> change)
        {
            var record = JsonNode.Parse(File.ReadAllText(Path))!;
            change(record);
            File.WriteAllText(Path, record.ToJsonString());
        }
    }
}
