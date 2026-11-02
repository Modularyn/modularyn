using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;

namespace Modularyn.Repository.Tests;

/// <summary>A refusal of the evidence assembler. Its message is a fixed code: an input may contain credentials, so no input
/// value, path or exception text ever reaches it.</summary>
internal sealed class EvidenceRefusal(string code) : Exception(code);

// The G-02 evidence assembler (docs/evidence/README.md, "CODE-10 record output"). It reads the runner reports that CI's jobs
// write, the plan, the corpus and the measured selection, and writes one half of the bundle: one record for each plan case in
// the run's deployment mode, and a manifest. It never executes, edits, skips or renames a test. Runner reports are untrusted:
// only exact planned identities and bounded outcomes are retained, never display names, output, exception text, attachments
// or environment values. CI runs its commands through EvidenceBundleCommandTests.
internal static class EvidenceBundle
{
    internal const string Host = "single-host";
    internal const string Container = "single-host-container";
    internal const string MeasurementLocation = "measurement.json";
    internal const string ManifestLocation = "manifest.json";
    internal static readonly string[] Modes = [Host, Container];
    internal static readonly string[] States = ["passed", "failed", "skipped", "not-run"];
    private static readonly string[] Bounded = [.. States, "missing"];
    private static readonly string[] Unexecuted = ["missing", "skipped", "not-run"];
    private static readonly string[] BindingFields = ["sourceCommit", "runId", "deploymentMode", "tuple"];
    private static readonly string[] MeasurementFields = [.. BindingFields, "date", "implementationArtifacts"];
    private static readonly string[] ObservedTupleFields = ["runtime", "database", "deployment", "providers", "tenancyMode"];
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly Regex Location = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*(?:/[A-Za-z0-9][A-Za-z0-9._-]*)*\z", RegexOptions.CultureInvariant);
    private static readonly Regex Commit = new(@"^[0-9a-f]{40}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Run = new(@"^[a-z0-9][a-z0-9._-]{0,79}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ImageDigest = new(@"^sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    // VSTest's TRX outcomes. Any other outcome, and an absent one, is an unexecuted row.
    private static readonly Dictionary<string, string> TrxStates = new(StringComparer.Ordinal)
    {
        ["Passed"] = "passed", ["Failed"] = "failed", ["NotExecuted"] = "skipped", ["Inconclusive"] = "not-run",
        ["Aborted"] = "not-run", ["Timeout"] = "failed", ["Error"] = "failed"
    };

    // CTRF's statuses. Any other status, and an absent one, is an unexecuted row.
    private static readonly Dictionary<string, string> CtrfStates = new(StringComparer.Ordinal)
    {
        ["passed"] = "passed", ["failed"] = "failed", ["skipped"] = "skipped", ["not-run"] = "not-run",
        ["pending"] = "not-run", ["other"] = "not-run"
    };

    /// <summary>The bytes of a bundle JSON file: indented, with LF line ends on every platform, and a final line end.</summary>
    internal static byte[] Encoded(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString(Format) + "\n");

    internal static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void Require(bool condition, string code)
    {
        if (!condition) throw new EvidenceRefusal(code);
    }

    /// <summary>Parses JSON and refuses a duplicate property anywhere in it.</summary>
    internal static JsonNode Parse(string text)
    {
        using (var document = JsonDocument.Parse(text)) Unique(document.RootElement);
        return JsonNode.Parse(text) ?? throw new EvidenceRefusal("null JSON document");
    }

    internal static JsonNode Read(string path) => Parse(Text(File.ReadAllBytes(path)));

    /// <summary>Strict UTF-8, with or without a byte-order mark, which VSTest writes at the start of every TRX report.</summary>
    internal static string Text(byte[] bytes)
    {
        ReadOnlySpan<byte> mark = [0xEF, 0xBB, 0xBF];
        var body = bytes.AsSpan().StartsWith(mark) ? bytes.AsSpan(mark.Length) : bytes.AsSpan();
        return Utf8.GetString(body);
    }

    /// <summary>The path of a retained location beneath the bundle root. Linked roots, linked components, absolute
    /// locations and escaping segments are refused.</summary>
    internal static string SafePath(string root, string? location)
    {
        Require(location is not null && Location.IsMatch(location), "invalid retained location");
        root = Path.GetFullPath(root);
        for (DirectoryInfo? ancestor = new(root); ancestor is not null; ancestor = ancestor.Parent)
            Require(ancestor.LinkTarget is null, "linked bundle root");
        var target = root;
        foreach (var part in location!.Split('/'))
        {
            Require(part is not "." and not "..", "escaping retained location");
            target = Path.Combine(target, part);
            Require(!Linked(target), "linked retained location");
        }

        return target;
    }

    internal static JsonObject WriteNew(string root, string location, byte[] bytes)
    {
        var path = SafePath(root, location);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes);
        return Artifact(location, bytes);
    }

    internal static string Retained(string root, JsonNode? item)
    {
        var path = SafePath(root, Str(item?["location"]));
        Require(File.Exists(path) && Digest(File.ReadAllBytes(path)) == Str(item!["sha256"]), "retained bytes differ");
        return path;
    }

    internal static JsonObject Binding(JsonNode measurement)
    {
        var binding = new JsonObject();
        foreach (var field in BindingFields) binding[field] = measurement[field]?.DeepClone();
        return binding;
    }

    internal static void ValidateMeasurement(JsonNode value, string? commit, string? mode, string? run)
    {
        Require(Keys(value).SetEquals(MeasurementFields), "unbounded measurement fields");
        Require(commit is not null && Commit.IsMatch(commit), "exact commit required");
        Require(mode is not null && Modes.Contains(mode, StringComparer.Ordinal) && run is not null && Run.IsMatch(run), "invalid run or mode");
        Require(Str(value["sourceCommit"]) == commit && Str(value["deploymentMode"]) == mode && Str(value["runId"]) == run,
            "measurement binding differs");
        var tuple = value["tuple"]!;
        var deployment = tuple["deployment"]!;
        Require(Str(deployment["topology"]) == mode && Str(deployment["architecture"]) == "x64"
            && Str(deployment["operatingSystem"]!["name"]) == "Linux", "measurement platform differs");
        Require(deployment.AsObject().TryGetPropertyValue("containerImageDigest", out var digest)
            && (mode == Host && digest is null || mode == Container && ImageDigest.IsMatch(Str(digest) ?? "")), "measurement image differs");
        Require(Str(tuple["database"]!["name"]) == "PostgreSQL"
            && tuple["providers"]!.AsArray().All(provider => Str(provider!["mode"]) == "real"), "real providers required");
        var date = Str(value["date"]);
        Require(DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == date, "invalid measurement date");
    }

    internal static IEnumerable<JsonNode> PlannedTests(JsonNode plan) =>
        plan["cases"]!.AsArray().SelectMany(row => row!["plannedTests"]!.AsArray()).Select(test => test!);

    /// <summary>The rows a planned method must report in a mode, each with the display name its runner gives it. Only these
    /// documented theories expand: AC-001's deployment row, and AC-008's two advertised clients.</summary>
    internal static List<(string Row, string Display)> RowNames(JsonNode test, string mode)
    {
        var name = Str(test["name"])!;
        if (name.Contains(".CompositionAcceptanceTests.AC_001_", StringComparison.Ordinal))
            return [("mode", name + "(mode: \"" + mode + "\")")];
        if (name.Contains(".G02AC008Tests.", StringComparison.Ordinal))
            return [.. new[] { "dotnet", "typescript" }.Select(client => (client, name + "(client: \"" + client + "\")"))];
        return [("method", name)];
    }

    /// <summary>Why a planned method cannot run in the composition fixture image, or null when it can.</summary>
    internal static string? ContainerReason(JsonNode test)
    {
        if (Str(test["name"])!.Contains(".G02AC008Tests.", StringComparison.Ordinal))
            return "The planned method requires both advertised clients; Node.js is absent from the pinned fixture base.";
        if (Str(test["layer"]) == "ui")
            return "The planned browser method requires the pinned browser and its runtime dependencies, absent from the fixture image.";
        return null;
    }

    /// <summary>Every expected row of a project's planned methods, missing rows included. A runner result belongs to a planned
    /// method by its test identity, the fully qualified type and method the report records, never by its display name, which a
    /// DisplayName attribute or a data row replaces. A documented theory's row is also named by its exact display name; every
    /// other planned method has one row, which all of its results make up, so each data row of a theory is required to have
    /// passed. Only the planned identity and a bounded state survive; data arguments, messages, traces and output never do.</summary>
    internal static List<JsonObject> SanitizeReport(string raw, string project, JsonNode plan, string mode, string format)
    {
        var expected = new List<(string Name, string Row, string? Display)>();
        foreach (var test in PlannedTests(plan).Where(test => Str(test["project"]) == project))
        {
            var name = Str(test["name"])!;
            foreach (var (row, display) in RowNames(test, mode))
                if (expected.All(item => item.Name != name || item.Row != row)) expected.Add((name, row, row == "method" ? null : display));
        }

        var observed = expected.ToDictionary(item => (item.Name, item.Row), _ => new List<(string? Display, string State)>());
        foreach (var (identity, display, state) in format switch
                 {
                     "ctrf" => CtrfRows(raw),
                     "trx" => TrxRows(raw),
                     _ => throw new EvidenceRefusal("unknown report format")
                 })
        {
            if (identity is null) continue;
            foreach (var item in expected.Where(item => item.Name == identity && (item.Display is null || item.Display == display)))
                observed[(item.Name, item.Row)].Add((display, state));
        }

        var result = new List<JsonObject>();
        foreach (var (name, row, display) in expected)
        {
            var results = observed[(name, row)];
            Require(display is null ? results.Select(item => item.Display).Distinct(StringComparer.Ordinal).Count() == results.Count : results.Count <= 1,
                "duplicate planned test row");
            var states = results.Select(item => item.State).ToList();
            // An unexecuted data row blocks its method, even if another row failed.
            var status = states.Count == 0 ? "missing"
                : states.FirstOrDefault(state => state is "skipped" or "not-run")
                ?? (states.Contains("failed", StringComparer.Ordinal) ? "failed" : "passed");
            result.Add(new JsonObject { ["name"] = name, ["row"] = row, ["status"] = status });
        }

        return result;
    }

    // CTRF's test identity is its extra type and method, which xunit v3 records whatever the display name is. CI runs the
    // planned methods with -failSkips, under which xunit v3 reports a skipped test as failed with the exception FAIL_SKIP: that
    // test did not execute, so it is skipped and blocks its case, as a skip does, rather than failing it.
    private static IEnumerable<(string? Identity, string? Display, string State)> CtrfRows(string raw)
    {
        var rows = new List<(string?, string?, string)>();
        foreach (var row in Parse(raw)["results"]!["tests"]!.AsArray())
        {
            var item = row!.AsObject();
            var extra = item["extra"] as JsonObject;
            var type = Str(extra?["type"]);
            var method = Str(extra?["method"]);
            var state = CtrfStates.GetValueOrDefault(Str(item["status"]) ?? "", "not-run");
            if (state == "failed" && Str(extra?["exception"]) == "FAIL_SKIP") state = "skipped";
            rows.Add((type is null || method is null ? null : type + "." + method, Str(item["name"]), state));
        }

        return rows;
    }

    // TRX's test identity is the class and method name of the test definition that a result's testId names.
    private static IEnumerable<(string? Identity, string? Display, string State)> TrxRows(string raw)
    {
        Require(!raw.Contains("<!DOCTYPE", StringComparison.Ordinal) && !raw.Contains("<!ENTITY", StringComparison.Ordinal), "XML entities refused");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        var results = new List<(string? TestId, string? Display, string State)>();
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        string? definition = null;
        using (var reader = XmlReader.Create(new StringReader(raw), settings))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.LocalName)
                {
                    case "UnitTestResult":
                        results.Add((reader.GetAttribute("testId"), reader.GetAttribute("testName"),
                            TrxStates.GetValueOrDefault(reader.GetAttribute("outcome") ?? "", "not-run")));
                        break;
                    case "UnitTest":
                        definition = reader.GetAttribute("id");
                        break;
                    case "TestMethod" when definition is not null:
                        var className = reader.GetAttribute("className");
                        var method = reader.GetAttribute("name");
                        if (className is not null && method is not null)
                        {
                            var identity = className + "." + method;
                            Require(identities.TryAdd(definition, identity) || identities[definition] == identity, "conflicting test identity");
                        }

                        definition = null;
                        break;
                }
            }
        }

        return results.Select(result => (result.TestId is not null && identities.TryGetValue(result.TestId, out var identity) ? identity : null,
            result.Display, result.State)).ToList();
    }

    /// <summary>A planned method's outcome in a mode from its retained rows.</summary>
    internal static string MethodOutcome(JsonNode test, string mode, IEnumerable<JsonNode> rows)
    {
        var name = Str(test["name"]);
        var expected = RowNames(test, mode).Select(row => row.Row).ToHashSet(StringComparer.Ordinal);
        var actual = rows.Where(row => Str(row["name"]) == name).ToList();
        var keys = actual.Select(row => Str(row["row"])).ToList();
        Require(keys.Distinct(StringComparer.Ordinal).Count() == actual.Count, "duplicate planned test row");
        Require(actual.All(row => expected.Contains(Str(row["row"]) ?? "") && Bounded.Contains(Str(row["status"]), StringComparer.Ordinal)),
            "invalid planned result");
        // Missing or skipped evidence blocks the complete case, even if another row failed.
        if (!keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected)
            || actual.Any(row => Unexecuted.Contains(Str(row["status"]), StringComparer.Ordinal)))
            return "blocked";
        return actual.Any(row => Str(row["status"]) == "failed") ? "failed" : "passed";
    }

    internal static string CaseOutcome(IEnumerable<string> statuses, JsonArray dependencies)
    {
        var all = statuses.ToList();
        if (all.Contains("blocked", StringComparer.Ordinal)) return "blocked";
        if (all.Contains("failed", StringComparer.Ordinal)) return "failed";
        if (dependencies.Any(dependency => Str(dependency!["status"]) != "satisfied" || dependency!["evidenceRecords"]!.AsArray().Count == 0))
            return "blocked";
        return "passed";
    }

    internal static void ValidateReport(JsonNode report, JsonNode measurement, JsonNode plan)
    {
        Require(Keys(report).SetEquals(["binding", "project", "rows"]), "unbounded report fields");
        Require(JsonNode.DeepEquals(report["binding"], Binding(measurement)), "report commit or tuple differs");
        var project = Str(report["project"]);
        var tests = PlannedTests(plan).Where(test => Str(test["project"]) == project).ToList();
        Require(tests.Count != 0, "unknown report project");
        var mode = Str(measurement["deploymentMode"])!;
        var expected = tests.SelectMany(test => RowNames(test, mode).Select(row => (Str(test["name"])!, row.Row))).ToHashSet();
        var seen = new HashSet<(string, string)>();
        foreach (var row in report["rows"]!.AsArray())
        {
            Require(Keys(row!).SetEquals(["name", "row", "status"]), "unbounded result fields");
            var key = (Str(row!["name"]) ?? "", Str(row["row"]) ?? "");
            Require(expected.Contains(key) && seen.Add(key) && Bounded.Contains(Str(row["status"]), StringComparer.Ordinal), "invalid planned result");
        }
    }

    internal static JsonNode ProjectedTuple(JsonNode measured, JsonNode planCase)
    {
        var value = measured.DeepClone();
        if (!planCase["needs"]!["filesystemStore"]!.GetValue<bool>())
            value["providers"] = new JsonArray([.. value["providers"]!.AsArray()
                .Where(provider => Str(provider!["name"]) == "EF Core PostgreSQL").Select(provider => provider!.DeepClone())]);
        return value;
    }

    /// <summary>Writes the bundle half: a result and a record for each plan case, then the manifest. Nothing is written
    /// unless every input is accepted, and nothing already written is replaced.</summary>
    internal static JsonObject Assemble(IReadOnlyDictionary<string, string> files, string directory, string? commit, string? mode, string? run)
    {
        directory = Path.GetFullPath(directory);
        var plan = Parse(files[G02EvidencePlanContract.PlanPath]);
        var corpusText = files[AcceptanceCorpusContract.CorpusPath].Replace("\r\n", "\n", StringComparison.Ordinal);
        var corpusBytes = Encoding.UTF8.GetBytes(corpusText);
        var corpus = Parse(corpusText)["cases"]!.AsArray().ToDictionary(row => Str(row!["id"])!, row => row!, StringComparer.Ordinal);
        var owners = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var entry in Parse(files[G02EvidencePlanContract.RegisterPath])["entries"]!.AsArray()) owners[Str(entry!["id"])!] = entry["owner"];
        var measurementBytes = File.ReadAllBytes(SafePath(directory, MeasurementLocation));
        var measurement = Parse(Text(measurementBytes));
        ValidateMeasurement(measurement, commit, mode, run);
        Require(!Directory.EnumerateFiles(directory, "record.json", SearchOption.AllDirectories).Any()
            && !Path.Exists(Path.Combine(directory, ManifestLocation)), "duplicate record or run");
        foreach (var item in measurement["implementationArtifacts"]!.AsArray()) Retained(directory, item);

        var reports = new List<(JsonNode Report, JsonObject Artifact)>();
        var reportDirectory = Path.Combine(directory, "reports");
        if (Directory.Exists(reportDirectory))
        {
            foreach (var name in Directory.EnumerateFileSystemEntries(reportDirectory).Select(Path.GetFileName)
                         .Where(name => name!.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var location = "reports/" + name;
                var bytes = File.ReadAllBytes(SafePath(directory, location));
                var report = Parse(Text(bytes));
                ValidateReport(report, measurement, plan);
                reports.Add((report, Artifact(location, bytes)));
            }
        }

        var writes = new List<(string Location, byte[] Bytes)>();
        var entries = new JsonArray();
        foreach (var planCase in plan["cases"]!.AsArray().Select(row => row!))
        {
            var caseId = Str(planCase["caseId"])!;
            var lower = caseId.ToLowerInvariant();
            var source = corpus[caseId];
            var planned = planCase["plannedTests"]!.AsArray().Select(test => test!).ToList();
            var tests = planned.Select(test => new JsonObject
            {
                ["project"] = test["project"]!.DeepClone(), ["name"] = test["name"]!.DeepClone(), ["layer"] = test["layer"]!.DeepClone()
            }).ToList();
            var tuple = ProjectedTuple(measurement["tuple"]!, planCase);
            var results = new List<JsonObject>();
            var limitations = new List<string>();
            foreach (var test in tests)
            {
                // A second report of an executed row is an ambiguity, never a way to choose a pass.
                var rows = reports.Where(item => Str(item.Report["project"]) == Str(test["project"]))
                    .SelectMany(item => item.Report["rows"]!.AsArray().Select(row => row!))
                    .Where(row => Str(row["name"]) == Str(test["name"]) && Str(row["status"]) != "missing").ToList();
                var status = MethodOutcome(test, mode!, rows);
                var result = (JsonObject)test.DeepClone();
                result["status"] = status;
                result["rows"] = new JsonArray([.. rows.Select(row => row.DeepClone())]);
                results.Add(result);
                if (status != "passed")
                    limitations.Add(Str(test["name"]) + ": " + ((mode == Container ? ContainerReason(test) : null)
                        ?? "Every planned row must execute and pass; see the bounded result states."));
            }

            // No functional record can promote its own security prerequisites. The plan's unmet state is retained.
            var dependencies = planCase["securityDependencies"]!.DeepClone().AsArray();
            Require(dependencies.All(dependency => Str(dependency!["status"]) == "unmet" && dependency["evidenceRecords"]!.AsArray().Count == 0),
                "security evidence needs Builder review");
            limitations.AddRange(dependencies.Select(dependency => Str(dependency!["family"])
                + " prerequisite evidence for this commit and tuple has not been supplied by the Builder."));
            if (caseId is "AC-003-B01" or "AC-003-P01")
                limitations.Add("The advertised .NET and TypeScript client observations are covered by argument about the shared server boundary; "
                    + "these methods execute direct API and server entry points.");
            if (caseId == "AC-003-N01")
                limitations.Add("The second advertised client is covered by argument about the shared server boundary, not by execution of that client.");
            if (mode == Container)
                limitations.Add("Execution uses the test-only composition fixture image; the published sample image's HTTP readiness is checked separately.");

            var prefix = run + "/" + lower + "/" + mode + "/";
            JsonNode observations = new JsonArray();
            var artifacts = measurement["implementationArtifacts"]!.DeepClone().AsArray();
            var attachments = new List<JsonObject>();
            var observed = results.Select(result => Str(result["name"]) + ": " + Str(result["status"]) + ".").ToList();
            var observationLocation = "observations/" + lower + ".json";
            var observationPath = SafePath(directory, observationLocation);
            if (Str(planCase["family"]) == "AC-001")
            {
                if (File.Exists(observationPath))
                {
                    var observationBytes = File.ReadAllBytes(observationPath);
                    var observation = Parse(Text(observationBytes));
                    Require(Str(observation["sourceCommit"]) == commit && Str(observation["runId"]) == run
                        && Str(observation["deploymentMode"]) == mode && Str(observation["caseId"]) == caseId, "composition observation binding differs");
                    foreach (var field in ObservedTupleFields)
                        Require(JsonNode.DeepEquals(observation["tuple"]![field], measurement["tuple"]![field]), "composition tuple differs");
                    var measured = measurement["tuple"]!["moduleVersions"]!.AsArray()
                        .ToDictionary(item => Str(item!["id"])!, item => Str(item!["version"]), StringComparer.Ordinal);
                    Require(observation["tuple"]!["moduleVersions"]!.AsArray().All(item =>
                        measured.TryGetValue(Str(item!["id"]) ?? "", out var version) && version == Str(item["version"])), "composition implementation version differs");
                    foreach (var item in observation["implementationArtifacts"]!.AsArray())
                    {
                        Retained(directory, item);
                        if (!artifacts.Any(artifact => JsonNode.DeepEquals(artifact, item))) artifacts.Add(item!.DeepClone());
                    }

                    observations = observation["observations"]!.DeepClone();
                    observed.AddRange(observation["observedOutcomes"]!.AsArray().Select(item => item!.GetValue<string>()));
                    attachments.Add(Artifact(observationLocation, observationBytes));
                    var integration = results.First(result => Str(result["layer"]) == "real-provider-integration");
                    Require(Str(integration["status"]) != "passed" || Str(observation["integrationOutcome"]) == "passed", "composition outcome differs");
                }
                else
                {
                    limitations.Add("The composition method retained no detailed observations; case evidence is incomplete.");
                    results.Add(new JsonObject { ["name"] = "composition-observations", ["status"] = "blocked", ["rows"] = new JsonArray() });
                }
            }

            var outcome = CaseOutcome(results.Select(result => Str(result["status"])!), dependencies);
            var caseResult = Binding(measurement);
            caseResult["tuple"] = tuple.DeepClone();
            caseResult["caseId"] = caseId;
            caseResult["tests"] = new JsonArray([.. results]);
            caseResult["observations"] = observations;
            caseResult["status"] = outcome;
            var resultBytes = Encoded(caseResult);
            writes.Add((prefix + "result.json", resultBytes));
            var projects = tests.Select(test => Str(test["project"])).ToHashSet(StringComparer.Ordinal);
            var supporting = reports.Where(item => projects.Contains(Str(item.Report["project"]))).Select(item => (JsonNode)item.Artifact.DeepClone());
            var record = new JsonObject
            {
                ["format"] = "modularyn.evidence-record", ["formatVersion"] = 1,
                ["recordId"] = "urn:modularyn:evidence:g02:" + lower + ":" + mode + ":" + run,
                ["kind"] = "verification", ["gate"] = "G-02",
                ["subjects"] = new JsonArray([.. source["requirements"]!.AsArray().Select(id => (JsonNode)new JsonObject
                {
                    ["id"] = id!.DeepClone(), ["kind"] = "requirement", ["owner"] = owners[Str(id)!]?.DeepClone()
                })]),
                ["caseId"] = caseId, ["family"] = planCase["family"]!.DeepClone(), ["profile"] = "reference",
                ["workOrder"] = (planned.FirstOrDefault(test => Str(test["layer"]) == "real-provider-integration") ?? planned[0])["workOrder"]!.DeepClone(),
                ["sourceCommit"] = commit, ["corpus"] = Artifact(AcceptanceCorpusContract.CorpusPath, corpusBytes),
                ["tuple"] = tuple.DeepClone(), ["implementationArtifacts"] = artifacts, ["tests"] = new JsonArray([.. tests]),
                ["fixtureInputs"] = source["preconditions"]!.DeepClone(), ["expectedOutcomes"] = source["expectedOutcome"]!.DeepClone(),
                ["observedOutcomes"] = new JsonArray([.. observed.Select(text => (JsonNode)text)]),
                ["verification"] = new JsonObject
                {
                    ["status"] = outcome, ["date"] = measurement["date"]!.DeepClone(),
                    ["results"] = new JsonArray([Artifact(prefix + "result.json", resultBytes), .. attachments, .. supporting])
                },
                ["documentation"] = new JsonArray([.. new[] { "docs/evidence/README.md", "docs/acceptance/" + Str(planCase["family"]) + ".md" }
                    .Concat(Str(planCase["family"]) == "AC-001" ? ["src/Tooling/Modularyn.Tooling.Samples.TeamWorkspace.Host/README.md"] : [])
                    .Select(text => (JsonNode)text)]),
                ["knownLimitations"] = new JsonArray([.. limitations.Select(text => (JsonNode)text)]),
                ["securityDependencies"] = dependencies
            };
            var recordBytes = Encoded(record);
            writes.Add((prefix + "record.json", recordBytes));
            var entry = new JsonObject { ["caseId"] = caseId, ["mode"] = mode, ["tupleSha256"] = Digest(Encoded(tuple)) };
            foreach (var (key, value) in Artifact(prefix + "record.json", recordBytes)) entry[key] = value?.DeepClone();
            entries.Add(entry);
        }

        var manifest = new JsonObject { ["format"] = "modularyn.evidence-bundle", ["formatVersion"] = 1 };
        foreach (var (key, value) in Binding(measurement)) manifest[key] = value?.DeepClone();
        manifest["corpusSha256"] = Digest(corpusBytes);
        manifest["planSha256"] = Digest(Encoding.UTF8.GetBytes(files[G02EvidencePlanContract.PlanPath].Replace("\r\n", "\n", StringComparison.Ordinal)));
        manifest["measurement"] = Artifact(MeasurementLocation, measurementBytes);
        manifest["reports"] = new JsonArray([.. reports.Select(item => (JsonNode)item.Artifact.DeepClone())]);
        manifest["records"] = entries;
        // The manifest is last: an interrupted write leaves no manifest, and the bundle contract requires one.
        foreach (var (location, bytes) in writes) WriteNew(directory, location, bytes);
        WriteNew(directory, ManifestLocation, Encoded(manifest));
        return manifest;
    }

    /// <summary>Sanitizes one runner report into the bundle's reports, then deletes the raw report, whether or not it was
    /// accepted: raw diagnostics are never bundle artifacts or CI log output.</summary>
    internal static void Collect(IReadOnlyDictionary<string, string> files, string directory, string? commit, string? mode, string? run,
        string? project, string? report, string? format, string? label, string? methods)
    {
        Require(report is not null, "runner report required");
        try
        {
            var plan = Parse(files[G02EvidencePlanContract.PlanPath]);
            var measurement = Read(SafePath(directory, MeasurementLocation));
            ValidateMeasurement(measurement, commit, mode, run);
            Require(project is not null && label is not null, "report project and label required");
            var rows = SanitizeReport(Text(File.ReadAllBytes(report!)), project!, plan, mode!, format ?? "ctrf");
            if (methods is not null)
            {
                var selected = File.ReadAllLines(methods).ToHashSet(StringComparer.Ordinal);
                rows = [.. rows.Where(row => selected.Contains(Str(row["name"]) ?? ""))];
            }

            var value = new JsonObject { ["binding"] = Binding(measurement), ["project"] = project, ["rows"] = new JsonArray([.. rows]) };
            ValidateReport(value, measurement, plan);
            WriteNew(directory, "reports/" + label + ".json", Encoded(value));
        }
        finally
        {
            File.Delete(report!);
        }
    }

    /// <summary>Writes, one per line, the project's planned methods that no collected report executed, except, in container
    /// mode, those the composition fixture image cannot run.</summary>
    internal static void Select(IReadOnlyDictionary<string, string> files, string directory, string? mode, string? project, string? output)
    {
        Require(mode is not null && Modes.Contains(mode, StringComparer.Ordinal) && project is not null && output is not null, "invalid selection");
        var plan = Parse(files[G02EvidencePlanContract.PlanPath]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reportDirectory = Path.Combine(Path.GetFullPath(directory), "reports");
        if (Directory.Exists(reportDirectory))
        {
            foreach (var name in Directory.EnumerateFileSystemEntries(reportDirectory).Select(Path.GetFileName)
                         .Where(name => name!.EndsWith(".json", StringComparison.Ordinal)))
            {
                var report = Read(SafePath(directory, "reports/" + name));
                if (Str(report["project"]) == project)
                    seen.UnionWith(report["rows"]!.AsArray().Where(row => Str(row!["status"]) != "missing").Select(row => Str(row!["name"]) ?? ""));
            }
        }

        var selected = PlannedTests(plan).Where(test => Str(test["project"]) == project && !seen.Contains(Str(test["name"])!)
            && (mode != Container || ContainerReason(test) is null)).Select(test => Str(test["name"])!).Distinct(StringComparer.Ordinal);
        using var stream = new FileStream(output!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(string.Concat(selected.Select(name => name + "\n"))));
    }

    internal static JsonObject Artifact(string location, byte[] bytes) => new() { ["location"] = location, ["sha256"] = Digest(bytes) };

    internal static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static HashSet<string> Keys(JsonNode node) => node.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

    private static bool Linked(string path) => new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;

    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "duplicate JSON property");
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) Unique(item);
        }
    }
}
