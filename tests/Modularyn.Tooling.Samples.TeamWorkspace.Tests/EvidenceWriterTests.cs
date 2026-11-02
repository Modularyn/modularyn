using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

public sealed class EvidenceWriterTests
{
    [Theory]
    [InlineData("AC-001-B01", "single-host")]
    [InlineData("AC-001-N01", "single-host")]
    [InlineData("AC-001-P01", "single-host")]
    [InlineData("AC-001-R01", "single-host")]
    [InlineData("AC-001-B01", "single-host-container")]
    [InlineData("AC-001-N01", "single-host-container")]
    [InlineData("AC-001-P01", "single-host-container")]
    [InlineData("AC-001-R01", "single-host-container")]
    public async Task Serialized_records_validate_and_retain_exact_case_mode_run_and_actual_method_outcome(string caseId, string mode)
    {
        var fixture = MetadataFixture(caseId, mode);
        // These are schema-validation inputs, never retained provider evidence. They are deleted even if an assertion fails.
        Directory.CreateDirectory(fixture.Run.OutputDirectory);
        try
        {
            foreach (var outcome in new[] { EvidenceOutcome.Passed, EvidenceOutcome.Failed })
            {
                var record = Create(fixture, caseId, mode, outcome)!;
                var path = Path.Combine(fixture.Run.OutputDirectory, outcome + ".json");
                await EvidenceWriter.WriteNewAsync(path, EvidenceWriter.Bytes(record));
                var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
                var text = Encoding.UTF8.GetString(bytes);
                EvidenceSchema.Validate(text, File.ReadAllText(Path.Combine(fixture.Run.Repository, EvidenceWriter.SchemaPath)));
                var read = JsonNode.Parse(text)!;
                Assert.Equal(fixture.Run.SourceCommit, read["sourceCommit"]!.GetValue<string>());
                Assert.Equal(caseId, read["caseId"]!.GetValue<string>());
                Assert.Equal(mode, read["tuple"]!["deployment"]!["topology"]!.GetValue<string>());
                Assert.Contains(fixture.Run.RunId, read["recordId"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.Equal(outcome == EvidenceOutcome.Failed ? "failed" : "blocked", read["verification"]!["status"]!.GetValue<string>());
                Assert.Equal("unmet", read["securityDependencies"]![0]!["status"]!.GetValue<string>());
                Assert.Equal(EvidenceWriter.Hash(fixture.ResultBytes), read["verification"]!["results"]![0]!["sha256"]!.GetValue<string>());
                var source = EvidenceWriter.Read(fixture.Run.Repository, EvidenceWriter.CorpusPath)["cases"]!.AsArray()
                    .Single(row => row!["id"]!.GetValue<string>() == caseId)!;
                Assert.True(JsonNode.DeepEquals(source["expectedOutcome"], read["expectedOutcomes"]));
                Assert.True(JsonNode.DeepEquals(source["preconditions"], read["fixtureInputs"]));
                var planned = EvidenceWriter.Read(fixture.Run.Repository, EvidenceWriter.PlanPath)["cases"]!.AsArray()
                    .Single(row => row!["caseId"]!.GetValue<string>() == caseId)!["plannedTests"]!.AsArray();
                Assert.Equal(planned.Select(test => test!["name"]!.GetValue<string>()), read["tests"]!.AsArray().Select(test => test!["name"]!.GetValue<string>()));
                Assert.DoesNotContain(fixture.Run.OutputDirectory, text, StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(fixture.Run.OutputDirectory, recursive: true); }
    }

    [Fact]
    public void Result_bytes_keep_the_method_outcome_separate_from_case_qualification()
    {
        var fixture = MetadataFixture("AC-001-P01", "single-host");
        foreach (var (outcome, expected) in new[] { (EvidenceOutcome.Passed, "passed"), (EvidenceOutcome.Failed, "failed") })
        {
            var result = EvidenceWriter.CreateResult(fixture.Run, "AC-001-P01", "single-host", outcome, fixture.Tuple, new CaseObservations());
            var read = JsonNode.Parse(EvidenceWriter.Bytes(result))!;
            Assert.Equal(expected, read["integrationOutcome"]!.GetValue<string>());
            Assert.Equal(fixture.Run.SourceCommit, read["sourceCommit"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(fixture.Tuple, read["tuple"]));
            var record = EvidenceWriter.CreateRecord(fixture.Run, "AC-001-P01", "single-host", outcome, fixture.Tuple, fixture.Artifacts,
                EvidenceWriter.Artifact("urn:modularyn:evidence-writer-test:result", EvidenceWriter.Bytes(result)),
                ["Metadata validation input only; no provider execution took place."])!;
            Assert.Equal(outcome == EvidenceOutcome.Passed ? "blocked" : "failed", record["verification"]!["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Missing_output_and_skipped_modes_write_nothing()
    {
        Assert.Null(EvidenceRun.FromSettings(null, null, null));
        Assert.Null(EvidenceRun.FromSettings(" ", null, null));
        var fixture = MetadataFixture("AC-001-P01", "single-host-container");
        Assert.Null(Create(fixture, "AC-001-P01", "single-host-container", EvidenceOutcome.Skipped));
        await EvidenceWriter.WriteAsync(fixture.Run, "AC-001-P01", "single-host-container", EvidenceOutcome.Skipped, new CaseObservations());
        Assert.False(Directory.Exists(fixture.Run.OutputDirectory));
    }

    [Fact]
    public async Task A_repeated_output_cannot_overwrite_an_earlier_result()
    {
        var fixture = MetadataFixture("AC-001-B01", "single-host");
        Directory.CreateDirectory(fixture.Run.OutputDirectory);
        try
        {
            var path = Path.Combine(fixture.Run.OutputDirectory, "result.json");
            await EvidenceWriter.WriteNewAsync(path, fixture.ResultBytes);
            await Assert.ThrowsAsync<IOException>(() => EvidenceWriter.WriteNewAsync(path, []));
            Assert.Equal(fixture.ResultBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(fixture.Run.OutputDirectory, recursive: true); }
    }

    [Theory]
    [InlineData("module-pin")]
    [InlineData("provider-pin")]
    [InlineData("container-digest")]
    [InlineData("mode")]
    [InlineData("runtime")]
    [InlineData("commit")]
    [InlineData("commit-newline")]
    [InlineData("run-newline")]
    public void Missing_or_inconsistent_identity_is_refused(string mutation)
    {
        var fixture = MetadataFixture("AC-001-R01", "single-host-container");
        switch (mutation)
        {
            case "module-pin": fixture.Tuple["moduleVersions"]![0]!["version"] = null; break;
            case "provider-pin": fixture.Tuple["providers"]![0]!["version"] = null; break;
            case "container-digest": fixture.Tuple["deployment"]!["containerImageDigest"] = null; break;
            case "mode": fixture.Tuple["deployment"]!["topology"] = "single-host"; break;
            case "runtime": fixture.Tuple["runtime"]!["runtimeVersion"] = "10.0.0"; break;
            case "commit": fixture = fixture with { Run = fixture.Run with { SourceCommit = "" } }; break;
            case "commit-newline": fixture = fixture with { Run = fixture.Run with { SourceCommit = fixture.Run.SourceCommit + "\n" } }; break;
            case "run-newline": fixture = fixture with { Run = fixture.Run with { RunId = fixture.Run.RunId + "\n" } }; break;
        }
        Assert.Throws<EvidenceValidationException>(() => Create(fixture, "AC-001-R01", "single-host-container", EvidenceOutcome.Passed));
        Assert.False(Directory.Exists(fixture.Run.OutputDirectory));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("remote-reference")]
    [InlineData("unknown-rule")]
    public void Schema_validation_refuses_unknown_content_and_unsupported_rules(string mutation)
    {
        var fixture = MetadataFixture("AC-001-N01", "single-host");
        var record = Create(fixture, "AC-001-N01", "single-host", EvidenceOutcome.Failed)!;
        var schema = EvidenceWriter.Read(fixture.Run.Repository, EvidenceWriter.SchemaPath);
        var text = record.ToJsonString();
        switch (mutation)
        {
            case "extra": record["qualified"] = true; text = record.ToJsonString(); break;
            case "duplicate": text = text.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1", StringComparison.Ordinal); break;
            case "remote-reference": schema["properties"]!["tuple"]!["$ref"] = "urn:modularyn:external-schema"; break;
            case "unknown-rule": schema["unevaluatedProperties"] = false; break;
        }
        if (mutation == "duplicate") Assert.Throws<JsonException>(() => EvidenceSchema.Validate(text, schema.ToJsonString()));
        else Assert.Throws<EvidenceValidationException>(() => EvidenceSchema.Validate(text, schema.ToJsonString()));
    }

    [Fact]
    public void Each_run_and_mode_has_a_separate_record_identity()
    {
        var host = MetadataFixture("AC-001-P01", "single-host");
        var container = MetadataFixture("AC-001-P01", "single-host-container") with { Run = host.Run };
        var another = host with { Run = host.Run with { RunId = Guid.NewGuid().ToString("N") } };
        var ids = new[] { Create(host, "AC-001-P01", "single-host", EvidenceOutcome.Passed)!,
            Create(container, "AC-001-P01", "single-host-container", EvidenceOutcome.Passed)!,
            Create(another, "AC-001-P01", "single-host", EvidenceOutcome.Passed)! }
            .Select(record => record["recordId"]!.GetValue<string>()).ToArray();
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Output_locations_cannot_escape_the_repository()
    {
        var root = EvidenceRun.FindRepository();
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.RelativeLocation(root, Path.Combine(root, "..", "evidence")));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.RelativeLocation(root, root));
    }

    private static JsonObject? Create(RecordFixture fixture, string caseId, string mode, EvidenceOutcome outcome) =>
        EvidenceWriter.CreateRecord(fixture.Run, caseId, mode, outcome, fixture.Tuple, fixture.Artifacts,
            EvidenceWriter.Artifact("urn:modularyn:evidence-writer-test:result", fixture.ResultBytes),
            ["Metadata validation input only; no provider execution took place."]);

    private static RecordFixture MetadataFixture(string caseId, string mode)
    {
        var root = EvidenceRun.FindRepository();
        var runId = Guid.NewGuid().ToString("N");
        var run = new EvidenceRun(root, Path.Combine(root, "tests", "Modularyn.Tooling.Samples.TeamWorkspace.Tests", "TestResults", runId),
            EvidenceRun.Git(root, "rev-parse", "HEAD").Trim(), runId, DateOnly.FromDateTime(DateTime.UtcNow));
        var tuple = EvidenceWriter.Read(root, "docs/evidence/examples/ac-009-r01-single-host-container.json")["tuple"]!.DeepClone().AsObject();
        var plan = EvidenceWriter.Read(root, EvidenceWriter.PlanPath)["cases"]!.AsArray()
            .Single(row => row!["caseId"]!.GetValue<string>() == caseId)!;
        tuple["moduleVersions"] = new JsonArray(plan["plannedTests"]!.AsArray()
            .Select(test => Path.GetFileNameWithoutExtension(test!["project"]!.GetValue<string>())[..^6]).Distinct(StringComparer.Ordinal)
            .Select(id => (JsonNode)new JsonObject { ["id"] = id, ["version"] = "1.0.0" }).ToArray());
        tuple["deployment"]!["topology"] = mode;
        tuple["deployment"]!["operatingSystem"]!["version"] = "6.8.0";
        var input = Encoding.UTF8.GetBytes("Evidence writer metadata validation input.\n");
        tuple["deployment"]!["containerImageDigest"] = mode == "single-host-container" ? "sha256:" + EvidenceWriter.Hash(input) : null;
        tuple["providers"]![0]!["version"] = "10.0.3";
        tuple["providers"]![1]!["version"] = "1.0.0";
        return new(run, tuple, new JsonArray(EvidenceWriter.Artifact("urn:modularyn:evidence-writer-test:input", input)), input);
    }

    private sealed record RecordFixture(EvidenceRun Run, JsonObject Tuple, JsonArray Artifacts, byte[] ResultBytes);
}
