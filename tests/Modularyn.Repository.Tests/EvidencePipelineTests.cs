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
    public void Removing_a_bundle_stage_is_rejected(string text)
    {
        var files = RepositoryFiles.Current.ToDictionary(pair => pair.Key, pair => pair.Value.Replace(text, "removed-stage", StringComparison.Ordinal), StringComparer.Ordinal);
        Assert.NotEmpty(EvidencePipelineContract.Errors(files));
    }
}

internal static class EvidencePipelineContract
{
    internal static List<string> Errors(IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        var evidence = files.GetValueOrDefault(ToolPinContract.EvidenceScriptPath, "");
        if (!evidence.Contains("evidence_bundle.py assemble", StringComparison.Ordinal)
            || !evidence.Contains("[ -f \"${MODULARYN_EVIDENCE_DIR}/manifest.json\" ]", StringComparison.Ordinal)
            || !evidence.Contains("cat \"${MODULARYN_EVIDENCE_DIR}/manifest.json\"", StringComparison.Ordinal))
            errors.Add("Both bundle halves require assembly, a complete manifest and its checked listing.");
        if (!files.GetValueOrDefault(ToolPinContract.TestsScriptPath, "").Contains("g02-host-results.sh complete", StringComparison.Ordinal)
            || !files.GetValueOrDefault(ToolPinContract.CompositionScriptPath, "").Contains("g02-container-results.sh complete", StringComparison.Ordinal))
            errors.Add("Both modes must collect all planned results before qualification.");
        if (!files.GetValueOrDefault(ToolPinContract.CompositionScriptPath, "").Contains("mkdir -m 0770 \"${output}/evidence\"", StringComparison.Ordinal))
            errors.Add("The runner must own the group-writable evidence root before the fixture writes its measurements.");
        var ci = files.GetValueOrDefault(ToolPinContract.CiPath, "").Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var step in new[] { ToolPinContract.HostEvidenceCheckStep, ToolPinContract.ContainerEvidenceCheckStep })
            if (!ci.Contains("- name: " + step + "\n        if: ${{ always() }}", StringComparison.Ordinal))
                errors.Add("Bundle checking must also run after a test failure.");
        return errors;
    }
}
