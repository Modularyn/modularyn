using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Modularity;
using Modularyn.Modules.Files.Persistence.FileSystem;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using static Modularyn.Tooling.Samples.TeamWorkspace.Tests.EvidenceSchema;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

internal enum EvidenceOutcome { Passed, Failed, Skipped }

internal sealed record EvidenceRun(string Repository, string OutputDirectory, string SourceCommit, string RunId, DateOnly Date)
{
    private static readonly string Session = Guid.NewGuid().ToString("N");

    internal static EvidenceRun? FromEnvironment()
    {
        var output = Environment.GetEnvironmentVariable("MODULARYN_EVIDENCE_DIR");
        return FromSettings(output, Environment.GetEnvironmentVariable("MODULARYN_SOURCE_COMMIT"),
            Environment.GetEnvironmentVariable("MODULARYN_EVIDENCE_RUN_ID"));
    }

    internal static EvidenceRun? FromSettings(string? output, string? sourceCommit, string? runId)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var root = FindRepository();
        var commit = sourceCommit ?? "";
        var run = runId ?? Session;
        Require(Regex.IsMatch(run, "^[a-z0-9][a-z0-9._-]{0,79}\\z", RegexOptions.CultureInvariant), "run identity");
        VerifySource(root, commit);
        var directory = Path.GetFullPath(output, root);
        _ = RelativeLocation(root, directory);
        return new(root, directory, commit, run, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    internal static string FindRepository()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Modularyn.slnx"))) root = root.Parent;
        Require(root is not null, "repository unavailable");
        return root!.FullName;
    }

    internal static string RelativeLocation(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace(Path.DirectorySeparatorChar, '/');
        Require(Regex.IsMatch(relative, "^[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*\\z", RegexOptions.CultureInvariant),
            "output must be a repository-relative location");
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            Require(new FileInfo(current).LinkTarget is null && new DirectoryInfo(current).LinkTarget is null, "output links are refused");
        }
        return relative;
    }

    internal static void VerifySource(string root, string commit)
    {
        Require(Regex.IsMatch(commit, "^[0-9a-f]{40}\\z", RegexOptions.CultureInvariant), "exact source commit required");
        Require(Git(root, "rev-parse", "HEAD").Trim() == commit, "source commit differs from HEAD");
        Require(Git(root, "diff", "--name-only", "HEAD", "--").Length == 0
            && Git(root, "ls-files", "--others", "--exclude-standard").Length == 0, "source tree must be clean");
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(assembly => assembly.GetName().Name?.StartsWith("Modularyn.", StringComparison.Ordinal) == true))
        {
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            Require(version.Split('+', '.').Contains(commit, StringComparer.Ordinal), "built assembly source revision differs from HEAD");
        }
    }

    internal static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new EvidenceValidationException("source identity unavailable");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new EvidenceValidationException("source identity timeout");
        }
        _ = error.GetAwaiter().GetResult();
        Require(process.ExitCode == 0, "source identity unavailable");
        return output.GetAwaiter().GetResult();
    }
}

internal static class EvidenceWriter
{
    internal const string SchemaPath = "schemas/evidence-record/evidence-record.schema.json";
    internal const string CorpusPath = "docs/acceptance/corpus.json";
    internal const string PlanPath = "docs/evidence/g02-plan.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task WriteAsync(EvidenceRun run, string caseId, string mode, EvidenceOutcome outcome, CaseObservations observations)
    {
        if (outcome == EvidenceOutcome.Skipped) return;
        // A database that could not be reached has no measured tuple. The test failure remains in the test runner;
        // do not invent a database pin just to produce a syntactically complete verification record.
        Require(observations.ServerVersionNumber is not null, "no measured provider tuple; retain the test runner failure");
        Require(mode == "single-host", "container lifecycle observations are unavailable");
        EvidenceRun.VerifySource(run.Repository, run.SourceCommit);
        var tuple = MeasuredTuple(observations.ServerVersionNumber!.Value);
        ValidateTarget(run.Repository, tuple);
        var directory = Path.Combine(run.OutputDirectory, run.RunId, caseId.ToLowerInvariant(), mode);
        _ = EvidenceRun.RelativeLocation(run.Repository, directory);
        Directory.CreateDirectory(directory);
        var artifactDirectory = Path.Combine(directory, "implementation");
        Directory.CreateDirectory(artifactDirectory);
        var artifacts = new JsonArray();
        // Retain the exact bytes that the fixture's deployment lock verified, including the test assembly.
        foreach (var file in observations.ImplementationFiles ?? throw new EvidenceValidationException("implementation inventory unavailable"))
        {
            var source = Path.Combine(AppContext.BaseDirectory, file.Path);
            var destination = Path.Combine(artifactDirectory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
            var bytes = await File.ReadAllBytesAsync(destination);
            Require(Hash(bytes) == file.Sha256, "implementation changed while retaining it");
            artifacts.Add(Artifact(EvidenceRun.RelativeLocation(run.Repository, destination), bytes));
        }
        var result = CreateResult(run, caseId, mode, outcome, tuple, observations);
        var resultBytes = Bytes(result);
        var resultPath = Path.Combine(directory, "result.json");
        var record = CreateRecord(run, caseId, mode, outcome, tuple, artifacts,
            Artifact(EvidenceRun.RelativeLocation(run.Repository, resultPath), resultBytes), observations.Outcomes)
            ?? throw new EvidenceValidationException("verification record required");
        Validate(record.ToJsonString(), File.ReadAllText(Path.Combine(run.Repository, SchemaPath)));
        await WriteNewAsync(resultPath, resultBytes);
        // The record is last: an interrupted write cannot leave a record claiming success over missing result bytes.
        var pending = Path.Combine(directory, "record.pending");
        try
        {
            await WriteNewAsync(pending, Bytes(record));
            File.Move(pending, Path.Combine(directory, "record.json"), overwrite: false);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    internal static JsonObject CreateResult(EvidenceRun run, string caseId, string mode, EvidenceOutcome outcome,
        JsonObject tuple, CaseObservations observations) => new()
    {
        ["caseId"] = caseId, ["deploymentMode"] = mode, ["runId"] = run.RunId, ["sourceCommit"] = run.SourceCommit,
        ["tuple"] = tuple.DeepClone(), ["integrationOutcome"] = outcome.ToString().ToLowerInvariant(),
        ["observations"] = observations.Snapshots.DeepClone()
    };

    internal static JsonObject? CreateRecord(EvidenceRun run, string caseId, string mode, EvidenceOutcome outcome,
        JsonObject tuple, JsonArray artifacts, JsonObject result, IEnumerable<string> observed)
    {
        if (outcome == EvidenceOutcome.Skipped) return null;
        Require(outcome is EvidenceOutcome.Passed or EvidenceOutcome.Failed, "test outcome");
        Require(mode is "single-host" or "single-host-container", "deployment mode");
        Require(tuple["deployment"]!["topology"]!.GetValue<string>() == mode, "tuple deployment mode mismatch");
        Require(Regex.IsMatch(run.SourceCommit, "^[0-9a-f]{40}\\z", RegexOptions.CultureInvariant), "exact source commit");
        Require(Regex.IsMatch(run.RunId, "^[a-z0-9][a-z0-9._-]{0,79}\\z", RegexOptions.CultureInvariant), "run identity");
        var corpusText = File.ReadAllText(Path.Combine(run.Repository, CorpusPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var corpus = JsonNode.Parse(corpusText)!["cases"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == caseId)!;
        var plan = Read(run.Repository, PlanPath)["cases"]!.AsArray().Single(row => row!["caseId"]!.GetValue<string>() == caseId)!;
        Require(plan["family"]!.GetValue<string>() == "AC-001", "CODE-10 case scope");
        var register = Read(run.Repository, "docs/coverage/register.json")["entries"]!.AsArray();
        var subjects = new JsonArray(corpus["requirements"]!.AsArray().Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id!.DeepClone(), ["kind"] = "requirement",
            ["owner"] = register.Single(entry => entry!["id"]!.GetValue<string>() == id!.GetValue<string>())!["owner"]!.DeepClone()
        }).ToArray());
        var outcomes = observed.Distinct(StringComparer.Ordinal).ToList();
        outcomes.Add(outcome == EvidenceOutcome.Passed
            ? "The integration method passed; prerequisite and unit-result qualification is still blocked."
            : "The integration method failed; only observations completed before the failure are retained.");
        var record = new JsonObject
        {
            ["format"] = "modularyn.evidence-record", ["formatVersion"] = 1,
            ["recordId"] = "urn:modularyn:evidence:g02:" + caseId.ToLowerInvariant() + ":" + mode + ":" + run.RunId,
            ["kind"] = "verification", ["gate"] = "G-02", ["subjects"] = subjects, ["caseId"] = caseId,
            ["family"] = "AC-001", ["profile"] = "reference", ["workOrder"] = "CODE-10", ["sourceCommit"] = run.SourceCommit,
            ["corpus"] = new JsonObject { ["location"] = CorpusPath, ["sha256"] = Hash(Encoding.UTF8.GetBytes(corpusText)) },
            ["tuple"] = tuple.DeepClone(), ["implementationArtifacts"] = artifacts.DeepClone(),
            ["tests"] = new JsonArray(plan["plannedTests"]!.AsArray().Select(test => (JsonNode)new JsonObject
            {
                ["project"] = test!["project"]!.DeepClone(), ["name"] = test!["name"]!.DeepClone(), ["layer"] = test!["layer"]!.DeepClone()
            }).ToArray()),
            ["fixtureInputs"] = corpus["preconditions"]!.DeepClone(), ["expectedOutcomes"] = corpus["expectedOutcome"]!.DeepClone(),
            ["observedOutcomes"] = JsonSerializer.SerializeToNode(outcomes),
            ["verification"] = new JsonObject
            {
                ["status"] = outcome == EvidenceOutcome.Failed ? "failed" : "blocked",
                ["date"] = run.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["results"] = new JsonArray(result.DeepClone())
            },
            ["documentation"] = new JsonArray("docs/evidence/README.md", "docs/acceptance/AC-001.md",
                "src/Tooling/Modularyn.Tooling.Samples.TeamWorkspace.Host/README.md"),
            ["knownLimitations"] = new JsonArray(
                "AC-031 security prerequisite evidence on this exact commit and tuple has not been supplied by the Builder.",
                "The planned unit methods are retained as required mappings; this integration run supplies no result for those methods.",
                "The published container has no controls for the corpus graph mutations, initialization fault or lifecycle inspection."),
            ["securityDependencies"] = new JsonArray(new JsonObject
            {
                ["family"] = "AC-031", ["status"] = "unmet", ["evidenceRecords"] = new JsonArray()
            })
        };
        ValidatePins(tuple, mode);
        ValidateTarget(run.Repository, tuple);
        Validate(record.ToJsonString(), File.ReadAllText(Path.Combine(run.Repository, SchemaPath)));
        return record;
    }

    internal static JsonObject MeasuredTuple(int serverVersion)
    {
        Require(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "G-02 requires Linux x64");
        var assemblies = new[] { typeof(ModuleHost).Assembly, typeof(ModuleComposer).Assembly, typeof(WorkspaceDefinition).Assembly,
            typeof(TeamWorkspaceHost).Assembly, typeof(FileSystemBlobStore).Assembly };
        var versions = WorkspaceComposition.Declarations().ToDictionary(module => module.Id, module => module.Version.ToString(), StringComparer.Ordinal);
        foreach (var assembly in assemblies) versions[assembly.GetName().Name!] = Version(assembly);
        var metadata = typeof(EvidenceWriter).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item => item.Key, item => item.Value);
        return new JsonObject
        {
            ["moduleVersions"] = new JsonArray(versions.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => (JsonNode)new JsonObject { ["id"] = item.Key, ["version"] = item.Value }).ToArray()),
            ["runtime"] = new JsonObject
            {
                ["sdkVersion"] = metadata.GetValueOrDefault("EvidenceSdkVersion"), ["runtimeVersion"] = Environment.Version.ToString(3),
                ["aspNetCoreVersion"] = Version(typeof(WebApplication).Assembly), ["languageVersion"] = metadata.GetValueOrDefault("EvidenceLanguageVersion")
            },
            ["database"] = new JsonObject { ["name"] = "PostgreSQL", ["version"] = (serverVersion / 10000).ToString(CultureInfo.InvariantCulture) + "." + (serverVersion % 10000).ToString(CultureInfo.InvariantCulture) },
            ["ui"] = new JsonArray(), ["tenancyMode"] = "shared-database",
            ["deployment"] = new JsonObject
            {
                ["operatingSystem"] = new JsonObject { ["name"] = "Linux", ["version"] = Environment.OSVersion.Version.ToString() },
                ["architecture"] = "x64", ["topology"] = "single-host", ["containerImageDigest"] = null
            },
            ["providers"] = new JsonArray(
                new JsonObject { ["name"] = "EF Core PostgreSQL", ["version"] = Version(typeof(NpgsqlDbContextOptionsBuilder).Assembly), ["mode"] = "real" },
                new JsonObject { ["name"] = "filesystem BLOB storage", ["version"] = Version(typeof(FileSystemBlobStore).Assembly), ["mode"] = "real" })
        };
    }

    private static void ValidateTarget(string root, JsonObject tuple)
    {
        var target = Read(root, "docs/capability-reports/g02-skeleton.json")["target"]!;
        foreach (var field in new[] { "sdkVersion", "runtimeVersion", "languageVersion" })
            Require(JsonNode.DeepEquals(target[field], tuple["runtime"]![field]), "G-02 runtime pins differ");
        Require(JsonNode.DeepEquals(target["runtimeVersion"], tuple["runtime"]!["aspNetCoreVersion"]), "ASP.NET Core pin differs");
        Require(tuple["database"]!["name"]!.GetValue<string>() == "PostgreSQL"
            && tuple["deployment"]!["operatingSystem"]!["name"]!.GetValue<string>() == "Linux"
            && tuple["deployment"]!["architecture"]!.GetValue<string>() == "x64"
            && tuple["tenancyMode"]!.GetValue<string>() == "shared-database", "G-02 tuple selection differs");
        Require(tuple["providers"]!.AsArray().Select(provider => provider!["name"]!.GetValue<string>())
            .SequenceEqual(["EF Core PostgreSQL", "filesystem BLOB storage"]), "G-02 provider inventory differs");
    }

    private static void ValidatePins(JsonObject tuple, string mode)
    {
        foreach (var component in tuple["moduleVersions"]!.AsArray().Concat(tuple["providers"]!.AsArray()).Concat(tuple["ui"]!.AsArray())
                     .Append(tuple["database"]).Append(tuple["deployment"]!["operatingSystem"]))
            Require(component?["version"] is JsonValue version && version.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text), "every version must be measured");
        Require(mode != "single-host-container" || tuple["deployment"]!["containerImageDigest"] is not null, "container image digest required");
    }

    private static string Version(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? throw new EvidenceValidationException("assembly version unavailable");
    internal static JsonNode Read(string root, string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, path)))!;
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] Bytes(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString(Json) + "\n");
    internal static JsonObject Artifact(string location, byte[] bytes) => new() { ["location"] = location, ["sha256"] = Hash(bytes) };
    internal static async Task WriteNewAsync(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}
