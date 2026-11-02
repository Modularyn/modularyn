using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using static Modularyn.Repository.Tests.EvidenceBundle;

namespace Modularyn.Repository.Tests;

// The evidence assembler's outcome, binding and sanitization rules, on synthetic metadata-validation inputs. Every input is
// generated beneath the checkout's ignored artifacts directory and deleted when the test ends. Synthetic reports prove the
// assembler's rules only; they are never provider evidence.
public sealed class EvidenceBundleTests
{
    private static IReadOnlyDictionary<string, string> Files => RepositoryFiles.Current;

    private static JsonNode Plan => Parse(Files[G02EvidencePlanContract.PlanPath]);

    [Theory]
    [InlineData(Host)]
    [InlineData(Container)]
    public void Both_modes_have_exactly_one_record_for_each_of_41_cases(string mode)
    {
        using var run = new BundleRun(mode);
        var manifest = run.Assemble();
        var entries = manifest["records"]!.AsArray();
        Assert.Equal(41, entries.Count);
        Assert.Equal(run.Plan["cases"]!.AsArray().Select(row => Str(row!["caseId"])).Order(StringComparer.Ordinal),
            entries.Select(entry => Str(entry!["caseId"])).Order(StringComparer.Ordinal));
        Assert.Equal(41, Directory.EnumerateFiles(run.Directory, "record.json", SearchOption.AllDirectories).Count());
        foreach (var entry in entries)
        {
            var text = File.ReadAllText(Retained(run.Directory, entry));
            Assert.Empty(EvidenceRecordContract.Validate(text, Files[EvidenceRecordContract.SchemaPath], Files));
            var record = Parse(text);
            Assert.Equal(run.Commit, Str(record["sourceCommit"]));
            Assert.Equal(mode, Str(record["tuple"]!["deployment"]!["topology"]));
            Assert.Equal(Digest(Encoded(record["tuple"]!)), Str(entry!["tupleSha256"]));
            Assert.Equal("blocked", Str(record["verification"]!["status"]));
            foreach (var artifact in record["implementationArtifacts"]!.AsArray().Concat(record["verification"]!["results"]!.AsArray()))
                Retained(run.Directory, artifact);
        }

        foreach (var caseId in new[] { "AC-003-B01", "AC-003-N01", "AC-003-P01" })
            Assert.Contains(run.Record(caseId)["knownLimitations"]!.AsArray(), text => Str(text)!.Contains("covered by argument", StringComparison.Ordinal));
        var composition = run.Record("AC-001-P01");
        var results = composition["verification"]!["results"]!.AsArray();
        var result = Read(Retained(run.Directory, results[0]));
        Assert.True(JsonNode.DeepEquals(new JsonArray(new JsonObject { ["stage"] = "metadata-only", ["constructed"] = 3 }), result["observations"]));
        Assert.True(results.Count >= 3);
        Assert.Equal("observations/ac-001-p01.json", Str(results[1]!["location"]));
        // The repository's complete-bundle contract accepts the half the assembler wrote.
        Assert.Empty(CiEvidence.Errors(run.Directory, run.Commit, mode, Files));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("skipped")]
    [InlineData("not-run")]
    [InlineData("failed")]
    public void Every_planned_test_and_row_is_required(string state)
    {
        using var run = new BundleRun();
        run.ChangeRow("AC-005-P01", state, remove: state == "missing");
        run.Assemble();
        Assert.Equal(state == "failed" ? "failed" : "blocked", Str(run.Record("AC-005-P01")["verification"]!["status"]));
    }

    [Fact]
    public void A_missing_row_blocks_even_if_another_row_failed()
    {
        var test = PlannedTests(Plan).First(test => Str(test["name"])!.Contains(".G02AC008Tests.", StringComparison.Ordinal));
        var rows = new List<JsonNode> { Row(test, "dotnet", "failed") };
        Assert.Equal("blocked", MethodOutcome(test, Host, rows));
        rows.Add(Row(test, "typescript", "passed"));
        Assert.Equal("failed", MethodOutcome(test, Host, rows));
    }

    [Fact]
    public void Pass_requires_all_methods_and_security_dependencies()
    {
        var unmet = new JsonArray(new JsonObject { ["family"] = "AC-031", ["status"] = "unmet", ["evidenceRecords"] = new JsonArray() });
        Assert.Equal("blocked", CaseOutcome(["passed", "passed"], unmet));
        Assert.Equal("passed", CaseOutcome(["passed", "passed"], []));
        Assert.Equal("failed", CaseOutcome(["passed", "failed"], []));
        Assert.Equal("blocked", CaseOutcome(["failed", "blocked"], []));
    }

    [Theory]
    [InlineData("sourceCommit")]
    [InlineData("tuple")]
    [InlineData("runId")]
    [InlineData("deploymentMode")]
    public void A_different_commit_tuple_run_or_mode_is_refused(string field)
    {
        using var run = new BundleRun();
        run.ChangeReport(run.FirstReport, report => report["binding"]![field] = "different");
        Assert.Throws<EvidenceRefusal>(() => run.Assemble());
    }

    [Fact]
    public void Duplicate_records_reports_and_properties_are_refused()
    {
        using (var run = new BundleRun())
        {
            run.Assemble();
            Assert.Throws<EvidenceRefusal>(() => run.Assemble());
        }

        using (var another = new BundleRun())
        {
            File.Copy(another.FirstReport, Path.Combine(Path.GetDirectoryName(another.FirstReport)!, "second-report.json"));
            Assert.Throws<EvidenceRefusal>(() => another.Assemble());
        }

        Assert.Throws<EvidenceRefusal>(() => Parse("{\"status\":\"passed\",\"status\":\"failed\"}"));
    }

    [Theory]
    [InlineData("report-field")]
    [InlineData("row-field")]
    [InlineData("row-name")]
    [InlineData("row-status")]
    public void A_retained_report_cannot_bypass_sanitization(string mutation)
    {
        using var run = new BundleRun();
        var marker = Guid.NewGuid().ToString("N");
        run.ChangeReport(run.FirstReport, report =>
        {
            var row = report["rows"]![0]!;
            switch (mutation)
            {
                case "report-field": report["stdout"] = marker; break;
                case "row-field": row["message"] = marker; break;
                case "row-name": row["name"] = marker; break;
                case "row-status": row["status"] = marker; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
            }
        });
        Assert.Throws<EvidenceRefusal>(() => run.Assemble());
        Assert.Empty(Directory.EnumerateFiles(run.Directory, "record.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Report_diagnostics_and_arguments_cannot_reach_retained_results()
    {
        var plan = Plan;
        var test = PlannedTests(plan).First();
        var name = Str(test["name"])!;
        var project = Str(test["project"])!;
        var marker = Guid.NewGuid().ToString("N");
        var first = Ctrf(name, name, "passed");
        first["message"] = marker;
        first["trace"] = marker;
        first["stdout"] = marker;
        var rows = SanitizeReport(Ctrf(first, Ctrf(name, name + "(" + marker + ")", "passed")), project, plan, Host, "ctrf");
        Assert.DoesNotContain(marker, new JsonArray([.. rows]).ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("passed", Str(rows.First(row => Str(row["name"]) == name)["status"]));
        var trx = Trx((name, name, "Failed", "<Output>" + marker + "</Output>"));
        rows = SanitizeReport(trx, project, plan, Host, "trx");
        Assert.DoesNotContain(marker, new JsonArray([.. rows]).ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("failed", Str(rows.First(row => Str(row["name"]) == name)["status"]));
    }

    [Theory]
    [InlineData("FAIL_SKIP", "skipped")]
    [InlineData("Xunit.Sdk.EqualException", "failed")]
    [InlineData(null, "failed")]
    public void A_skip_that_fail_skips_reports_as_a_failure_is_still_a_skip(string? exception, string expected)
    {
        var plan = Plan;
        var test = PlannedTests(plan).First(test => RowNames(test, Host) is [("method", _)]);
        var result = Ctrf(Str(test["name"])!, Str(test["name"])!, "failed");
        if (exception is not null) result["extra"]!["exception"] = exception;
        var rows = SanitizeReport(Ctrf(result), Str(test["project"])!, plan, Host, "ctrf");
        Assert.Equal(expected, Str(rows.Single(row => Str(row["name"]) == Str(test["name"]))["status"]));
        Assert.Equal(expected == "skipped" ? "blocked" : "failed", MethodOutcome(test, Host, rows));
    }

    [Theory]
    [InlineData("trx")]
    [InlineData("ctrf")]
    public void A_report_that_begins_with_a_byte_order_mark_is_collected(string format)
    {
        // VSTest writes every TRX report with a UTF-8 byte-order mark; invalid UTF-8 is still refused.
        using var run = new BundleRun();
        var test = PlannedTests(run.Plan).First();
        var name = Str(test["name"])!;
        var raw = Path.Combine(run.Directory, "runner.raw");
        var text = format == "trx" ? "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + Trx((name, name, "Passed", "")) : Ctrf(Ctrf(name, name, "passed"));
        File.WriteAllBytes(raw, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]);
        EvidenceBundle.Collect(Files, run.Directory, run.Commit, run.Mode, run.Run, Str(test["project"]), raw, format, "marked", null);
        var collected = Read(Path.Combine(run.Directory, "reports", "marked.json"));
        Assert.Equal("passed", Str(collected["rows"]!.AsArray().Single(row => Str(row!["name"]) == name)!["status"]));
        File.WriteAllBytes(raw, [0xEF, 0xBB, 0xBF, 0xFF, .. Encoding.UTF8.GetBytes(text)]);
        Assert.ThrowsAny<Exception>(() => EvidenceBundle.Collect(Files, run.Directory, run.Commit, run.Mode, run.Run, Str(test["project"]), raw, format, "invalid", null));
        Assert.False(File.Exists(raw));
        Assert.False(File.Exists(Path.Combine(run.Directory, "reports", "invalid.json")));
    }

    [Fact]
    public void A_display_name_never_identifies_a_planned_method()
    {
        var plan = Plan;
        var test = PlannedTests(plan).First();
        var name = Str(test["name"])!;
        var project = Str(test["project"])!;
        // A result of another method, or one with no recorded identity, that displays the planned name supplies nothing.
        var other = name[..name.LastIndexOf('.')] + ".Another_method";
        var spoofed = Ctrf(other, name, "passed");
        var unidentified = new JsonObject { ["name"] = name, ["status"] = "passed" };
        Assert.Equal("missing", Str(SanitizeReport(Ctrf(spoofed, unidentified), project, plan, Host, "ctrf").Single(row => Str(row["name"]) == name)["status"]));
        Assert.Equal("missing", Str(SanitizeReport(Trx((other, name, "Passed", "")), project, plan, Host, "trx").Single(row => Str(row["name"]) == name)["status"]));
        Assert.Equal("missing", Str(SanitizeReport(Trx((null, name, "Passed", "")), project, plan, Host, "trx").Single(row => Str(row["name"]) == name)["status"]));
    }

    [Theory]
    [InlineData("ctrf")]
    [InlineData("trx")]
    public void Every_data_row_of_a_planned_method_with_a_display_name_is_its_result(string format)
    {
        // AC-001-B01's activation-boundary theory carries a DisplayName, so no runner names a result after the method.
        var plan = Plan;
        var test = PlannedTests(plan).Single(test => Str(test["name"])!.EndsWith(".AC_001_B01_Only_inclusive_boundary_versions_activate", StringComparison.Ordinal));
        var name = Str(test["name"])!;
        var project = Str(test["project"])!;
        string[] displays = ["(candidate: \"1.9.9\", compatible: False)", "(candidate: \"2.0.0\", compatible: True)",
            "(candidate: \"2.2.0\", compatible: True)", "(candidate: \"2.2.1\", compatible: False)"];
        string Status(params string[] states)
        {
            var results = displays.Zip(states, (display, state) => ("AC-001-B01 selected version boundaries at activation" + display, state)).ToArray();
            var raw = format == "ctrf" ? Ctrf([.. results.Select(result => Ctrf(name, result.Item1, result.state))])
                : Trx([.. results.Select(result => ((string?)name, result.Item1, result.state switch
                {
                    "passed" => "Passed", "failed" => "Failed", _ => "NotExecuted"
                }, ""))]);
            return Str(SanitizeReport(raw, project, plan, Host, format).Single(row => Str(row["name"]) == name)["status"])!;
        }

        Assert.Equal("passed", Status("passed", "passed", "passed", "passed"));
        Assert.Equal("failed", Status("passed", "failed", "passed", "passed"));
        Assert.Equal("skipped", Status("failed", "passed", "skipped", "passed"));
        Assert.Equal("missing", Status());
        // The same data row twice is an ambiguity, never a way to choose a pass.
        var twice = format == "ctrf" ? Ctrf(Ctrf(name, "AC-001-B01" + displays[0], "failed"), Ctrf(name, "AC-001-B01" + displays[0], "passed"))
            : Trx((name, "AC-001-B01" + displays[0], "Failed", ""), (name, "AC-001-B01" + displays[0], "Passed", ""));
        Assert.Throws<EvidenceRefusal>(() => SanitizeReport(twice, project, plan, Host, format));
    }

    [Fact]
    public void The_other_deployment_row_never_counts()
    {
        var plan = Plan;
        var test = plan["cases"]![0]!["plannedTests"]!.AsArray().Last()!;
        var rows = SanitizeReport(Ctrf(Ctrf(Str(test["name"])!, RowNames(test, Host)[0].Display, "passed")), Str(test["project"])!, plan, Container, "ctrf");
        Assert.Equal("blocked", MethodOutcome(test, Container, rows));
        rows = SanitizeReport(Ctrf(Ctrf(Str(test["name"])!, RowNames(test, Container)[0].Display, "passed")), Str(test["project"])!, plan, Container, "ctrf");
        Assert.Equal("passed", MethodOutcome(test, Container, rows));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collection_removes_raw_diagnostics_after_success_or_refusal(bool invalid)
    {
        using var run = new BundleRun();
        var test = PlannedTests(run.Plan).First();
        var marker = Guid.NewGuid().ToString("N");
        var raw = Path.Combine(run.Directory, "runner.raw.json");
        var row = Ctrf(Str(test["name"])!, Str(test["name"])!, "passed");
        row["message"] = marker;
        row["trace"] = marker;
        File.WriteAllText(raw, Ctrf(row));
        var collected = Path.Combine(run.Directory, "reports", "collected.json");
        void Collect() => EvidenceBundle.Collect(Files, run.Directory, invalid ? new string('2', 40) : run.Commit, run.Mode, run.Run,
            Str(test["project"]), raw, "ctrf", "collected", null);
        if (invalid)
        {
            Assert.Throws<EvidenceRefusal>(Collect);
            Assert.False(File.Exists(collected));
        }
        else
        {
            Collect();
            Assert.DoesNotContain(marker, File.ReadAllText(collected), StringComparison.Ordinal);
        }

        Assert.False(File.Exists(raw));
    }

    [Fact]
    public void Unsupported_container_methods_have_specific_reasons()
    {
        using var run = new BundleRun(Container);
        var unsupported = PlannedTests(run.Plan).Where(test => ContainerReason(test) is not null).Select(test => Str(test["name"])).ToHashSet();
        foreach (var path in run.Reports)
            run.ChangeReport(path, report => report["rows"] = new JsonArray([.. report["rows"]!.AsArray()
                .Where(row => !unsupported.Contains(Str(row!["name"]))).Select(row => row!.DeepClone())]));
        run.Assemble();
        Assert.Contains(run.Record("AC-008-P01")["knownLimitations"]!.AsArray(), text => Str(text)!.Contains("Node.js", StringComparison.Ordinal));
        Assert.Contains(run.Record("AC-003-N02")["knownLimitations"]!.AsArray(), text => Str(text)!.Contains("browser", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_or_conflicting_composition_observations_never_qualify()
    {
        using (var run = new BundleRun())
        {
            File.Delete(Path.Combine(run.Directory, "observations", "ac-001-p01.json"));
            run.Assemble();
            Assert.Contains(run.Record("AC-001-P01")["knownLimitations"]!.AsArray(),
                text => Str(text)!.Contains("no detailed observations", StringComparison.Ordinal));
        }

        using var another = new BundleRun();
        another.ChangeReport(Path.Combine(another.Directory, "observations", "ac-001-p01.json"),
            observation => observation["tuple"]!["database"]!["version"] = "18.0");
        Assert.Throws<EvidenceRefusal>(() => another.Assemble());
    }

    [Fact]
    public void Paths_links_hashes_and_xml_entities_are_refused()
    {
        using var run = new BundleRun();
        foreach (var location in new[] { "../escape", "/absolute", "safe/../escape", "safe//escape" })
            Assert.Throws<EvidenceRefusal>(() => SafePath(run.Directory, location));
        Directory.CreateSymbolicLink(Path.Combine(run.Directory, "linked"), Path.Combine(run.Directory, "implementation"));
        Assert.Throws<EvidenceRefusal>(() => SafePath(run.Directory, "linked/metadata.dll"));
        File.WriteAllBytes(Path.Combine(run.Directory, "implementation", "metadata.dll"), "changed"u8.ToArray());
        Assert.Throws<EvidenceRefusal>(() => run.Assemble());
        Assert.Throws<EvidenceRefusal>(() => SanitizeReport("<!DOCTYPE x><TestRun/>", "", run.Plan, run.Mode, "trx"));
    }

    [Theory]
    [InlineData("unknown-commit")]
    [InlineData("extra-field")]
    [InlineData("not-run-status")]
    [InlineData("no-tests")]
    [InlineData("null-hash")]
    public void Schema_checks_do_not_accept_invalid_records(string mutation)
    {
        using var run = new BundleRun();
        run.Assemble();
        var record = run.Record("AC-001-P01");
        var schema = Files[EvidenceRecordContract.SchemaPath];
        Assert.Empty(EvidenceRecordContract.Validate(record.ToJsonString(), schema, Files));
        switch (mutation)
        {
            case "unknown-commit": record["sourceCommit"] = "unknown"; break;
            case "extra-field": record["extra"] = true; break;
            case "not-run-status": record["verification"]!["status"] = "not-run"; break;
            case "no-tests": record["tests"]!.AsArray().Clear(); break;
            case "null-hash": record["implementationArtifacts"]![0]!["sha256"] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        Assert.NotEmpty(EvidenceRecordContract.Validate(record.ToJsonString(), schema, Files));
    }

    [Fact]
    public void Only_documented_theories_expand_planned_methods()
    {
        foreach (var test in PlannedTests(Plan))
        {
            var name = Str(test["name"])!;
            var method = name[(name.LastIndexOf('.') + 1)..];
            var folder = Path.GetDirectoryName(Str(test["project"]))!.Replace('\\', '/') + "/";
            var declaration = new Regex(@"(?:Task|void)\s+" + Regex.Escape(method) + @"\((?<parameters>[^)]*)\)", RegexOptions.CultureInvariant);
            var source = Files.Where(file => file.Key.StartsWith(folder, StringComparison.Ordinal) && file.Key.EndsWith(".cs", StringComparison.Ordinal)
                    && !file.Key[folder.Length..].Contains('/')).Select(file => file.Value).First(text => declaration.IsMatch(text));
            var match = declaration.Match(source);
            // The attributes directly above the method's declaration.
            var attributes = source[..match.Index].Split('\n').Reverse().Skip(1)
                .Select(line => line.Trim()).TakeWhile(line => line.StartsWith('[') || line.StartsWith("//", StringComparison.Ordinal)).ToList();
            var rows = Modes.SelectMany(mode => RowNames(test, mode)).Select(row => row.Row).Distinct(StringComparer.Ordinal).ToList();
            if (rows is ["method"])
            {
                // One row, which every result of the method makes up, whatever its display name or data row.
                Assert.True(attributes.Any(line => line.StartsWith("[Fact", StringComparison.Ordinal) || line.StartsWith("[Theory", StringComparison.Ordinal)), name);
                continue;
            }

            // A documented theory's rows are named by their exact display names: a plain theory over exactly the inventory's
            // values, whose one parameter names them.
            var parameter = name.Contains(".G02AC008Tests.", StringComparison.Ordinal) ? "client" : "mode";
            Assert.True(attributes.Contains("[Theory]"), name);
            Assert.Equal("string " + parameter, match.Groups["parameters"].Value.Trim());
            var data = attributes.Where(line => line.StartsWith("[InlineData(", StringComparison.Ordinal)).ToList();
            Assert.Equal(parameter == "client" ? 2 : Modes.Length, data.Count);
            Assert.All(parameter == "client" ? ["\"dotnet\"", "\"typescript\""] : new[] { "DeploymentMode.Host", "DeploymentMode.Container" },
                value => Assert.Single(data, line => line.StartsWith("[InlineData(" + value, StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData(Host)]
    [InlineData(Container)]
    public void Selection_lists_only_the_planned_methods_that_no_report_executed(string mode)
    {
        using var run = new BundleRun(mode);
        // Its planned methods include the browser method, which the composition fixture image cannot run.
        const string project = "tests/Modularyn.Framework.Authorization.Tests/Modularyn.Framework.Authorization.Tests.csproj";
        var planned = PlannedTests(run.Plan).Where(test => Str(test["project"]) == project).ToList();
        Assert.Contains(planned, test => ContainerReason(test) is not null);
        var report = run.ReportPath(project);
        string[] Selected()
        {
            var output = Path.Combine(run.Directory, Guid.NewGuid().ToString("N") + ".methods");
            EvidenceBundle.Select(Files, run.Directory, mode, project, output);
            return File.ReadAllLines(output);
        }

        Assert.Empty(Selected());
        run.ChangeReport(report, value =>
        {
            foreach (var row in value["rows"]!.AsArray()) row!["status"] = "missing";
        });
        var supported = planned.Where(test => mode == Host || ContainerReason(test) is null).Select(test => Str(test["name"])!).ToArray();
        Assert.NotEmpty(supported);
        Assert.Equal(supported, Selected());
        run.ChangeReport(report, value => value["rows"]!.AsArray().First(row => Str(row!["name"]) == supported[0])!["status"] = "failed");
        Assert.Equal(supported.Skip(1), Selected());
    }

    private static JsonObject Row(JsonNode test, string row, string status) => new()
    {
        ["name"] = Str(test["name"]), ["row"] = row, ["status"] = status
    };

    // One CTRF result as xunit v3 writes it: its display name, status, and its test identity as extra type and method.
    private static JsonObject Ctrf(string identity, string display, string status) => new()
    {
        ["name"] = display, ["status"] = status,
        ["extra"] = new JsonObject { ["type"] = identity[..identity.LastIndexOf('.')], ["method"] = identity[(identity.LastIndexOf('.') + 1)..] }
    };

    private static string Ctrf(params JsonObject[] results) => new JsonObject
    {
        ["results"] = new JsonObject { ["tests"] = new JsonArray([.. results]) }
    }.ToJsonString();

    // A VSTest TRX report: each result, and the test definition that its testId names, with the identity's class and method.
    private static string Trx(params (string? Identity, string Display, string Outcome, string Inner)[] results)
    {
        var ids = results.Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        return new XElement(ns + "TestRun",
            new XElement(ns + "Results", results.Select((result, index) => new XElement(ns + "UnitTestResult",
                new XAttribute("testId", ids[index]), new XAttribute("testName", result.Display), new XAttribute("outcome", result.Outcome),
                XElement.Parse("<Inner>" + result.Inner + "</Inner>").Nodes()))),
            new XElement(ns + "TestDefinitions", results.Select((result, index) => new XElement(ns + "UnitTest",
                new XAttribute("name", result.Display), new XAttribute("id", ids[index]),
                result.Identity is null ? null : new XElement(ns + "TestMethod",
                    new XAttribute("className", result.Identity[..result.Identity.LastIndexOf('.')]),
                    new XAttribute("name", result.Identity[(result.Identity.LastIndexOf('.') + 1)..])))))).ToString();
    }

    // A transient metadata-validation bundle directory: a measurement, a report for every planned project with every row passed,
    // and AC-001's observations, as a CI run collects them. Nothing here was executed.
    private sealed class BundleRun : IDisposable
    {
        public BundleRun(string mode = Host)
        {
            Mode = mode;
            Directory = Path.Combine(RepositoryFiles.Root, "artifacts", "evidence-bundle-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Plan = EvidenceBundleTests.Plan;
            var tuple = JsonNode.Parse(Files[EvidenceRecordContract.ExampleDirectory + "ac-009-r01-single-host-container.json"])!["tuple"]!.DeepClone();
            tuple["moduleVersions"] = new JsonArray([.. PlannedTests(Plan).Select(test => Path.GetFileNameWithoutExtension(Str(test["project"]))![..^6])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(package => (JsonNode)new JsonObject { ["id"] = package, ["version"] = "1.0.0" })]);
            tuple["deployment"]!["topology"] = mode;
            tuple["deployment"]!["containerImageDigest"] = mode == Container ? "sha256:" + new string('4', 64) : null;
            tuple["deployment"]!["operatingSystem"]!["version"] = "6.8.0";
            tuple["ui"] = new JsonArray(new JsonObject { ["name"] = "Razor", ["version"] = tuple["runtime"]!["aspNetCoreVersion"]!.DeepClone() });
            foreach (var provider in tuple["providers"]!.AsArray()) provider!["version"] = "10.0.3";
            var artifact = WriteNew(Directory, "implementation/metadata.dll", Encoding.UTF8.GetBytes("Metadata validation only.\n"));
            Measurement = new JsonObject
            {
                ["sourceCommit"] = Commit, ["runId"] = Run, ["deploymentMode"] = mode, ["date"] = "2026-11-02",
                ["tuple"] = tuple, ["implementationArtifacts"] = new JsonArray(artifact)
            };
            WriteNew(Directory, MeasurementLocation, Encoded(Measurement));
            foreach (var project in PlannedTests(Plan).Select(test => Str(test["project"])!).Distinct(StringComparer.Ordinal))
            {
                var rows = PlannedTests(Plan).Where(test => Str(test["project"]) == project)
                    .SelectMany(test => RowNames(test, mode).Select(row => (JsonNode)Row(test, row.Row, "passed")));
                Write(ReportPath(project), new JsonObject { ["binding"] = Binding(Measurement), ["project"] = project, ["rows"] = new JsonArray([.. rows]) });
            }

            foreach (var planCase in Plan["cases"]!.AsArray().Where(row => Str(row!["family"]) == "AC-001"))
            {
                var caseId = Str(planCase!["caseId"])!;
                var observation = Binding(Measurement);
                observation["caseId"] = caseId;
                observation["integrationOutcome"] = "passed";
                observation["observations"] = new JsonArray(new JsonObject { ["stage"] = "metadata-only", ["constructed"] = 3 });
                observation["observedOutcomes"] = new JsonArray("Metadata-validation observation; no provider execution.");
                observation["implementationArtifacts"] = Measurement["implementationArtifacts"]!.DeepClone();
                WriteNew(Directory, "observations/" + caseId.ToLowerInvariant() + ".json", Encoded(observation));
            }
        }

        public string Directory { get; }

        public string Mode { get; }

        public string Commit { get; } = new('1', 40);

        public string Run { get; } = "metadata-validation";

        public JsonNode Plan { get; }

        public JsonObject Measurement { get; }

        public IEnumerable<string> Reports => System.IO.Directory.EnumerateFiles(Path.Combine(Directory, "reports"), "*.json").Order(StringComparer.Ordinal).ToList();

        public string FirstReport => Reports.First();

        public string ReportPath(string project) => Path.Combine(Directory, "reports", Path.GetFileNameWithoutExtension(project) + ".json");

        public void ChangeRow(string caseId, string status, bool remove = false)
        {
            var test = Plan["cases"]!.AsArray().Single(row => Str(row!["caseId"]) == caseId)!["plannedTests"]![0]!;
            ChangeReport(ReportPath(Str(test["project"])!), report =>
            {
                var rows = report["rows"]!.AsArray();
                var row = rows.First(item => Str(item!["name"]) == Str(test["name"]))!;
                if (remove) rows.Remove(row);
                else row["status"] = status;
            });
        }

        public void ChangeReport(string path, Action<JsonNode> change)
        {
            var value = Read(path);
            change(value);
            Write(path, value);
        }

        public JsonObject Assemble() => EvidenceBundle.Assemble(Files, Directory, Commit, Mode, Run);

        public JsonNode Record(string caseId) => Read(Path.Combine(Directory, Run, caseId.ToLowerInvariant(), Mode, "record.json"));

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

        private static void Write(string path, JsonNode value)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoded(value));
        }
    }
}
