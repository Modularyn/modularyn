using System.Diagnostics;
using System.Text.Json;
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
            || entry.Line.Contains("The sample image ownership check did not run.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, entries.Length);
        foreach (var entry in entries)
        {
            var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            Assert.Contains("--cap-drop=ALL", entry.Line, StringComparison.Ordinal);
            files[entry.Path] = files[entry.Path].Replace(entry.Line, entry.Line.Replace("--cap-drop=ALL", "--cap-add=ALL", StringComparison.Ordinal), StringComparison.Ordinal);
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
