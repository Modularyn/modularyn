using Xunit;

namespace Modularyn.Repository.Tests;

// CI's entry points to the evidence assembler, EvidenceBundle (docs/ci.md, "Evidence records"). Each test performs one command
// when MODULARYN_EVIDENCE_COMMAND names it, and skips otherwise, as every ordinary test run does. .github/scripts/evidence-bundle.sh
// runs one through the test executable's own -method filter with -failSkips, and requires its report to show that it ran once and
// passed. A refusal fails the test with its fixed code only: no input value, path or exception text reaches the log.
public sealed class EvidenceBundleCommandTests
{
    internal const string CommandVariable = "MODULARYN_EVIDENCE_COMMAND";
    internal const string RunVariable = "MODULARYN_EVIDENCE_RUN_ID";
    internal const string ProjectVariable = "MODULARYN_EVIDENCE_PROJECT";
    internal const string ReportVariable = "MODULARYN_EVIDENCE_REPORT";
    internal const string ReportFormatVariable = "MODULARYN_EVIDENCE_REPORT_FORMAT";
    internal const string LabelVariable = "MODULARYN_EVIDENCE_LABEL";
    internal const string MethodsVariable = "MODULARYN_EVIDENCE_METHODS";

    [Fact]
    public void Collect_one_runner_report_into_the_bundle() => Perform("collect", directory => EvidenceBundle.Collect(RepositoryFiles.Current,
        directory, Setting(CiEvidence.CommitVariable), Setting(CiEvidence.ModeVariable), Setting(RunVariable), Setting(ProjectVariable),
        Setting(ReportVariable), Setting(ReportFormatVariable), Setting(LabelVariable), Setting(MethodsVariable)));

    [Fact]
    public void Select_the_planned_methods_that_no_collected_report_ran() => Perform("select", directory => EvidenceBundle.Select(
        RepositoryFiles.Current, directory, Setting(CiEvidence.ModeVariable), Setting(ProjectVariable), Setting(MethodsVariable)));

    [Fact]
    public void Assemble_the_bundle_half_from_the_collected_reports() => Perform("assemble", directory => EvidenceBundle.Assemble(
        RepositoryFiles.Current, directory, Setting(CiEvidence.CommitVariable), Setting(CiEvidence.ModeVariable), Setting(RunVariable)));

    private static void Perform(string command, Action<string> action)
    {
        Assert.SkipWhen(Setting(CommandVariable) != command, $"CI sets {CommandVariable} to {command} to run this evidence command.");
        var directory = Setting(CiEvidence.DirectoryVariable);
        Assert.False(directory is null || !Path.IsPathFullyQualified(directory), $"{CiEvidence.DirectoryVariable} must name an absolute directory.");
        try
        {
            action(directory!);
        }
        catch (EvidenceRefusal refusal)
        {
            Assert.Fail($"The evidence {command} command refused its input: {refusal.Message}.");
        }
        catch (Exception)
        {
            Assert.Fail($"The evidence {command} command refused invalid, incomplete or conflicting input.");
        }
    }

    private static string? Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
