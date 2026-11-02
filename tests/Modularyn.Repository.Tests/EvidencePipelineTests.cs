using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class EvidencePipelineTests
{
    [Fact]
    public void Every_added_or_changed_bundle_docker_line_has_a_rejection_case()
    {
        var entries = ToolPinContract.DockerAllowlist.Where(entry =>
            entry.Path == ".github/scripts/g02-container-results.sh"
            || entry.Line.Contains("--env=MODULARYN_EVIDENCE_RUN_ID", StringComparison.Ordinal)
            || entry.Line.Contains("g02-", StringComparison.Ordinal)
            || entry.Line.Contains("The sample image ownership check did not run.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(6, entries.Length);
        foreach (var entry in entries)
        {
            var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            // A run that gains a capability, or a stop that becomes a forced removal, is no longer the listed line.
            var changed = entry.Line.Contains("--cap-drop=ALL", StringComparison.Ordinal)
                ? entry.Line.Replace("--cap-drop=ALL", "--cap-add=ALL", StringComparison.Ordinal)
                : entry.Line.Replace("docker kill ", "docker rm --force ", StringComparison.Ordinal);
            Assert.NotEqual(entry.Line, changed);
            files[entry.Path] = files[entry.Path].Replace(entry.Line, changed, StringComparison.Ordinal);
            Assert.Contains(ToolPinContract.DockerCommandErrors(files), error => error.Contains("not on the allowlist", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Both_jobs_assemble_and_check_the_complete_manifest_even_after_a_test_failure()
    {
        Assert.Empty(EvidencePipelineContract.Errors(RepositoryFiles.Current));
    }

    [Theory]
    [InlineData("assemble")]
    [InlineData("manifest.json")]
    [InlineData("g02-host-results.sh complete")]
    [InlineData("g02-container-results.sh complete")]
    [InlineData("mkdir -m 0770 \"${output}/evidence\"")]
    [InlineData("always()")]
    [InlineData("evidence-bundle.sh collect")]
    [InlineData("evidence-bundle.sh select")]
    [InlineData("-failSkips -method \"Modularyn.Repository.Tests.EvidenceBundleCommandTests.${method}\"")]
    [InlineData("[ \"$(jq -r '.results.summary.passed' \"${report}\")\" = \"1\" ]")]
    [InlineData("env -u MODULARYN_TEST_POSTGRESQL")]
    [InlineData("method=Collect_one_runner_report_into_the_bundle")]
    [InlineData("method=Select_the_planned_methods_that_no_collected_report_ran")]
    [InlineData("method=Assemble_the_bundle_half_from_the_collected_reports")]
    [InlineData("- name: Build the evidence assembler")]
    [InlineData("[ -z \"${writable}\" ] || fail \"Root must own every file of the sample image, and its user may write none.\"")]
    [InlineData("[ -z \"${writable}\" ] || fail \"Root must own every file of the composition fixture image, and its user may write none: ${writable}\"")]
    [InlineData("timeout --kill-after=30s \"${bound}\" ")]
    [InlineData(" -longRunning 60")]
    [InlineData(" 2>&1 | long_running")]
    [InlineData("grep --line-buffered -oE \"\\[Long Running Test\\] '[^']*', Elapsed: [0-9:]+|\\[OUTPUT\\] G02 phase: [a-z0-9-]+$\" || true")]
    [InlineData("|\\[OUTPUT\\] G02 phase: [a-z0-9-]+$")]
    [InlineData(" -showLiveOutput")]
    [InlineData("internal const string PhasePrefix = \"G02 phase: \";")]
    [InlineData("bounded \"g02-${name}\" \"${run}\"")]
    [InlineData("bounded g02-measurement \"${run}\"")]
    [InlineData("docker kill \"$1\" > /dev/null 2>&1 || true")]
    [InlineData("docker kill g02-composition > /dev/null 2>&1 || true")]
    [InlineData("echo \"::error::The G-02 container run $1 did not finish within ${bound} seconds.\"")]
    [InlineData("fail \"The G-02 container run g02-composition did not finish within ${bound} seconds.\"")]
    [InlineData("echo \"G-02 container run ${name}: $(( ${#selected[@]} / 2 )) planned methods, within ${bound} seconds.\"")]
    [InlineData("| select($names | index($name)) | \"\\(.status) \\($name)\"' \"${report}\"")]
    [InlineData("bound=300")]
    public void Removing_a_bundle_stage_is_rejected(string text)
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value.Replace(text, "removed-stage", StringComparison.Ordinal), StringComparer.Ordinal);
        Assert.NotEmpty(EvidencePipelineContract.Errors(files));
    }

    [Fact]
    public void The_container_job_builds_the_assembler_before_its_rows_run()
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var ci = files[ToolPinContract.CiPath];
        var build = ci.IndexOf("      - name: " + EvidencePipelineContract.AssemblerBuildStep + "\n", StringComparison.Ordinal);
        var rows = ci.IndexOf("      - name: " + ToolPinContract.CompositionTestStep + "\n", StringComparison.Ordinal);
        Assert.True(build > 0 && rows > build);
        // Move the build after the rows that collect their reports with it.
        var step = ci[build..ci.IndexOf("      - name: ", build + 1, StringComparison.Ordinal)];
        var moved = ci.Remove(build, step.Length);
        files[ToolPinContract.CiPath] = moved.Insert(moved.IndexOf("      - name: " + ToolPinContract.ContainerEvidenceCheckStep + "\n", StringComparison.Ordinal), step);
        Assert.Contains(EvidencePipelineContract.Errors(files), error => error.Contains("builds the evidence assembler", StringComparison.Ordinal));
    }

    // Raw runner reports can carry test arguments and output, so the host collection removes them however it ends, including
    // when it fails: on an unknown stage, and when the assembler refuses a report during completion.
    [Theory]
    [InlineData("unknown")]
    [InlineData("complete")]
    public async Task Host_collection_removes_raw_reports_when_it_fails(string stage)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The CI scripts run under bash: on Linux in CI and on Linux or macOS locally. "
            + "No CI job runs them on Windows, so this test does not either.");
        var root = Directory.CreateTempSubdirectory("modularyn-host-results-");
        try
        {
            var evidence = Directory.CreateDirectory(Path.Combine(root.FullName, "evidence")).FullName;
            var runner = Path.Combine(root.FullName, "runner");
            var results = Directory.CreateDirectory(Path.Combine(runner, "real-provider-results")).FullName;
            string[] raw = [Path.Combine(evidence, "Alpha.Tests.raw.json"), Path.Combine(evidence, "Alpha.Tests.methods")];
            foreach (var path in raw) File.WriteAllText(path, "{}\n");
            // The first planned project's provider report, which the assembler refuses without a measured selection.
            var project = JsonDocument.Parse(RepositoryFiles.Current[G02EvidencePlanContract.PlanPath]).RootElement.GetProperty("cases").EnumerateArray()
                .SelectMany(row => row.GetProperty("plannedTests").EnumerateArray()).Select(test => test.GetProperty("project").GetString()!)
                .Order(StringComparer.Ordinal).First();
            var trx = Path.Combine(results, Path.GetFileNameWithoutExtension(project) + ".trx");
            File.WriteAllText(trx, "<TestRun />\n");
            var start = new ProcessStartInfo("bash")
            {
                WorkingDirectory = RepositoryFiles.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(Path.Combine(RepositoryFiles.Root, EvidencePipelineContract.HostResultsScriptPath));
            start.ArgumentList.Add(stage);
            start.Environment.Remove("MODULARYN_TEST_POSTGRESQL");
            start.Environment["GITHUB_ACTIONS"] = "true";
            start.Environment["RUNNER_TEMP"] = runner;
            start.Environment["MODULARYN_EVIDENCE_DIR"] = evidence;
            start.Environment["MODULARYN_SOURCE_COMMIT"] = new string('1', 40);
            start.Environment["MODULARYN_EVIDENCE_RUN_ID"] = "metadata-validation";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start bash.");
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            var log = await output + await error;
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("G-02 host result collection failed.", log, StringComparison.Ordinal);
            Assert.DoesNotContain("unbound variable", log, StringComparison.Ordinal);
            Assert.All(raw, path => Assert.False(File.Exists(path), path));
            Assert.Equal(stage == "complete", !File.Exists(trx));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}

// The bundle pipeline's stages (docs/ci.md, "Evidence records"): both jobs collect every planned result, assemble their half with
// the C# assembler on the pinned SDK and check it, even after a test failure.
internal static class EvidencePipelineContract
{
    internal const string BundleScriptPath = ".github/scripts/evidence-bundle.sh";
    internal const string HostResultsScriptPath = ".github/scripts/g02-host-results.sh";
    internal const string ContainerResultsScriptPath = ".github/scripts/g02-container-results.sh";
    internal const string AssemblerBuildStep = "Build the evidence assembler";

    // The published sample image's and the composition fixture image's runtime ownership checks (docs/ci.md, "Container build"
    // and "Container acceptance").
    private static readonly (string Image, string Check)[] OwnershipChecks =
    [
        ("${SAMPLE_IMAGE}", "[ -z \"${writable}\" ] || fail \"Root must own every file of the sample image, and its user may write none.\""),
        ("${COMPOSITION_IMAGE}", "[ -z \"${writable}\" ] || fail \"Root must own every file of the composition fixture image, and its user may write none: ${writable}\"")
    ];

    // Every container test run is bounded and shown (docs/ci.md, "Container acceptance"): it runs under timeout within its bound,
    // by name, with xunit's long-running report, and of its output shows only those lines; a run past its bound stops its
    // container and fails the step naming the run. The readiness poll, a single short test that bounds itself, is the exception.
    internal const string BoundedRun = "timeout --kill-after=30s \"${bound}\" ";
    private const string PhaseTestPath = "tests/Modularyn.Framework.Authorization.Tests/G02AC003Tests.CrossSite.cs";
    private const string Bound = "bound=300";
    private const string LongRunning = """long_running() { grep --line-buffered -oE "\[Long Running Test\] '[^']*', Elapsed: [0-9:]+|\[OUTPUT\] G02 phase: [a-z0-9-]+$" || true; }""";
    private const string Stopped = """
        bounded() {
          if [ "$2" -eq 124 ] || [ "$2" -eq 137 ]; then
            docker kill "$1" > /dev/null 2>&1 || true
            echo "::error::The G-02 container run $1 did not finish within ${bound} seconds."
            exit 1
          fi
        }
        """;
    private static readonly string[] CompositionStopped =
    [
        "if [ \"${status}\" -eq 124 ] || [ \"${status}\" -eq 137 ]; then", "docker kill g02-composition > /dev/null 2>&1 || true",
        "fail \"The G-02 container run g02-composition did not finish within ${bound} seconds.\"", "fi"
    ];
    private const string ProjectCount = "echo \"G-02 container run ${name}: $(( ${#selected[@]} / 2 )) planned methods, within ${bound} seconds.\"";
    private const string ProjectOutcomes = "| select($names | index($name)) | \"\\(.status) \\($name)\"' \"${report}\"";

    private static IEnumerable<string> BoundedRunErrors(IReadOnlyDictionary<string, string> files)
    {
        foreach (var path in new[] { ToolPinContract.CompositionScriptPath, ContainerResultsScriptPath })
        {
            var script = files.GetValueOrDefault(path, "").Replace("\r\n", "\n", StringComparison.Ordinal);
            var lines = script.Split('\n').Select(line => line.Trim()).ToList();
            var runs = lines.Select((line, index) => (Line: line, Index: index)).Where(item => item.Line.Contains("docker run ", StringComparison.Ordinal)
                && item.Line.Contains("\"${COMPOSITION_IMAGE}\"", StringComparison.Ordinal) && !item.Line.StartsWith("if docker run ", StringComparison.Ordinal)).ToList();
            if (!lines.Contains(Bound) || !lines.Contains(LongRunning) || runs.Count == 0
                || runs.Any(run => !run.Line.StartsWith(BoundedRun + "docker run ", StringComparison.Ordinal)
                    || !run.Line.Contains(" -longRunning 60 ", StringComparison.Ordinal)
                    || !Regex.IsMatch(run.Line, @" 2>&1 \| long_running \|\| (?:run|status)=\$\?\z", RegexOptions.CultureInvariant)
                    || !Stops(path, lines, run.Line, run.Index, script)))
                yield return $"{path}: every container test run must run under timeout within {Bound}, by name, with -longRunning 60, show only "
                    + "its long-running-test lines, and stop its container and fail the step naming the run when it exceeds the bound.";
        }

        // Each project's run is announced with its method count, and its planned outcomes are printed before collection.
        var results = files.GetValueOrDefault(ContainerResultsScriptPath, "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var count = results.IndexOf(ProjectCount, StringComparison.Ordinal);
        var project = results.IndexOf("--name=\"g02-${name}\"", StringComparison.Ordinal);
        var outcomes = results.IndexOf(ProjectOutcomes, StringComparison.Ordinal);
        var collect = results.IndexOf("MODULARYN_EVIDENCE_LABEL=\"${name}\"", StringComparison.Ordinal);
        if (count < 0 || project < count || outcomes < project || collect < outcomes)
            yield return $"{ContainerResultsScriptPath}: must name each project and its planned method count before its run, and print its "
                + "report's planned method names and statuses after it, before collection.";

        // Each project runs with xunit's live output, of which the filter passes only the phase markers that AC-003-N02's
        // integration row writes with the same fixed prefix.
        if (project < 0 || !results[project..].Split('\n')[0].Contains(" -showLiveOutput ", StringComparison.Ordinal)
            || !files.GetValueOrDefault(PhaseTestPath, "").Contains("internal const string PhasePrefix = \"G02 phase: \";", StringComparison.Ordinal))
            yield return $"{ContainerResultsScriptPath}: each project must run with -showLiveOutput, of which only {PhaseTestPath}'s "
                + "\"G02 phase: \" markers and the long-running-test lines are shown.";
    }

    // The line after a run stops it at its bound: through bounded, with the run's own name, or for the composition rows inline.
    private static bool Stops(string path, List<string> lines, string run, int index, string script)
    {
        var name = Regex.Match(run, @" --name=(?<name>\S+) ", RegexOptions.CultureInvariant);
        if (!name.Success) return false;
        if (path == ToolPinContract.CompositionScriptPath)
            return name.Groups["name"].Value == "g02-composition" && lines.Skip(index + 1).Take(CompositionStopped.Length).SequenceEqual(CompositionStopped);
        return script.Contains(Stopped.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", StringComparison.Ordinal) && index + 1 < lines.Count
            && lines[index + 1] == "bounded " + name.Groups["name"].Value + " \"${run}\"";
    }

    // Each assembler command and the EvidenceBundleCommandTests method that performs it.
    private static readonly (string Command, string Method)[] Commands =
    [
        ("collect", nameof(EvidenceBundleCommandTests.Collect_one_runner_report_into_the_bundle)),
        ("select", nameof(EvidenceBundleCommandTests.Select_the_planned_methods_that_no_collected_report_ran)),
        ("assemble", nameof(EvidenceBundleCommandTests.Assemble_the_bundle_half_from_the_collected_reports))
    ];

    internal static List<string> Errors(IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        var evidence = files.GetValueOrDefault(ToolPinContract.EvidenceScriptPath, "");
        if (!evidence.Contains("bash " + BundleScriptPath + " assemble\n", StringComparison.Ordinal)
            || !evidence.Contains("[ -f \"${MODULARYN_EVIDENCE_DIR}/manifest.json\" ]", StringComparison.Ordinal)
            || !evidence.Contains("cat \"${MODULARYN_EVIDENCE_DIR}/manifest.json\"", StringComparison.Ordinal))
            errors.Add("Both bundle halves require assembly, a complete manifest and its checked listing.");
        if (!files.GetValueOrDefault(ToolPinContract.TestsScriptPath, "").Contains("g02-host-results.sh complete", StringComparison.Ordinal)
            || !files.GetValueOrDefault(ToolPinContract.CompositionScriptPath, "").Contains("g02-container-results.sh complete", StringComparison.Ordinal))
            errors.Add("Both modes must collect all planned results before qualification.");
        foreach (var path in new[] { HostResultsScriptPath, ContainerResultsScriptPath })
        {
            var script = files.GetValueOrDefault(path, "");
            if (!script.Contains("bash " + BundleScriptPath + " collect || fail", StringComparison.Ordinal)
                || !script.Contains("bash " + BundleScriptPath + " select || fail", StringComparison.Ordinal))
                errors.Add($"{path}: must collect every report and select the remaining planned methods with {BundleScriptPath}, and fail on a refusal.");
        }

        // The assembler runs only through the repository tests' executable: one command's test method, selected by its own
        // filter, failed if it skips, and required by its report to have run once and passed. It never receives the credentials.
        var bundle = files.GetValueOrDefault(BundleScriptPath, "");
        if (!bundle.Contains("env -u MODULARYN_TEST_POSTGRESQL MODULARYN_EVIDENCE_COMMAND=\"$1\" dotnet \"${tests}\" -noLogo -noColor -failSkips -method \"Modularyn.Repository.Tests.EvidenceBundleCommandTests.${method}\" -result-ctrf \"${report}\"", StringComparison.Ordinal)
            || !bundle.Contains("[ \"$(jq -r '.results.summary.tests' \"${report}\")\" = \"1\" ] && [ \"$(jq -r '.results.summary.passed' \"${report}\")\" = \"1\" ]", StringComparison.Ordinal)
            || Commands.Any(command => !bundle.Contains($"  {command.Command}) method={command.Method} ;;\n", StringComparison.Ordinal)))
            errors.Add($"{BundleScriptPath}: must run each assembler command's own test with -failSkips, without the database credentials, "
                + "and fail unless its report shows that it ran once and passed.");
        if (!files.GetValueOrDefault(ToolPinContract.CompositionScriptPath, "").Contains("mkdir -m 0770 \"${output}/evidence\"", StringComparison.Ordinal))
            errors.Add("The runner must own the group-writable evidence root before the fixture writes its measurements.");
        var ci = files.GetValueOrDefault(ToolPinContract.CiPath, "").Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var step in new[] { ToolPinContract.HostEvidenceCheckStep, ToolPinContract.ContainerEvidenceCheckStep })
            if (!ci.Contains("- name: " + step + "\n        if: ${{ always() }}", StringComparison.Ordinal))
                errors.Add("Bundle checking must also run after a test failure.");
        // Each image's runtime ownership check acts on what its find reports: the next line fails unless it found nothing. The
        // allowlist holds the find line itself exactly.
        var lines = ci.Split('\n').Select(line => line.Trim()).ToList();
        foreach (var (image, check) in OwnershipChecks)
        {
            var at = lines.IndexOf(check);
            if (at < 1 || lines.Count(line => line == check) != 1 || !lines[at - 1].StartsWith("writable=\"$(docker run ", StringComparison.Ordinal)
                || !lines[at - 1].Contains("--entrypoint=find \"" + image + "\" /app ", StringComparison.Ordinal))
                errors.Add($"{ToolPinContract.CiPath}: the runtime ownership check of {image} must fail unless its find, on the line before, finds nothing.");
        }

        errors.AddRange(BoundedRunErrors(files));

        var build = ci.IndexOf("      - name: " + AssemblerBuildStep + "\n", StringComparison.Ordinal);
        var rows = ci.IndexOf("      - name: " + ToolPinContract.CompositionTestStep + "\n", StringComparison.Ordinal);
        var job = ci.IndexOf("\n  " + ToolPinContract.ContainerJob + ":\n", StringComparison.Ordinal);
        if (job < 0 || build < job || rows < build
            || !ci[build..rows].Contains("dotnet restore --locked-mode \"${project}\"\n          dotnet build \"${project}\" -c Release --no-restore\n", StringComparison.Ordinal)
            || !ci[build..rows].Contains("project=tests/Modularyn.Repository.Tests/Modularyn.Repository.Tests.csproj\n", StringComparison.Ordinal))
            errors.Add($"{ToolPinContract.CiPath}: the {ToolPinContract.ContainerJob} job builds the evidence assembler from the locked repository tests "
                + $"in a '{AssemblerBuildStep}' step before its '{ToolPinContract.CompositionTestStep}' step.");
        return errors;
    }
}
