using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;
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
                EvidenceSchema.Validate(text, EvidenceDocuments.Text(EvidenceWriter.SchemaPath));
                var read = JsonNode.Parse(text)!;
                Assert.Equal(fixture.Run.SourceCommit, read["sourceCommit"]!.GetValue<string>());
                Assert.Equal(caseId, read["caseId"]!.GetValue<string>());
                Assert.Equal(mode, read["tuple"]!["deployment"]!["topology"]!.GetValue<string>());
                Assert.Contains(fixture.Run.RunId, read["recordId"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.Equal(outcome == EvidenceOutcome.Failed ? "failed" : "blocked", read["verification"]!["status"]!.GetValue<string>());
                Assert.Equal("unmet", read["securityDependencies"]![0]!["status"]!.GetValue<string>());
                Assert.Equal(EvidenceWriter.Hash(fixture.ResultBytes), read["verification"]!["results"]![0]!["sha256"]!.GetValue<string>());
                var source = EvidenceDocuments.Read(EvidenceWriter.CorpusPath)["cases"]!.AsArray()
                    .Single(row => row!["id"]!.GetValue<string>() == caseId)!;
                Assert.True(JsonNode.DeepEquals(source["expectedOutcome"], read["expectedOutcomes"]));
                Assert.True(JsonNode.DeepEquals(source["preconditions"], read["fixtureInputs"]));
                var planned = EvidenceDocuments.Read(EvidenceWriter.PlanPath)["cases"]!.AsArray()
                    .Single(row => row!["caseId"]!.GetValue<string>() == caseId)!["plannedTests"]!.AsArray();
                Assert.Equal(planned.Select(test => test!["name"]!.GetValue<string>()), read["tests"]!.AsArray().Select(test => test!["name"]!.GetValue<string>()));
                Assert.DoesNotContain(fixture.Run.OutputDirectory, text, StringComparison.Ordinal);
                Assert.Equal(mode == DeploymentMode.Container, read["knownLimitations"]!.AsArray()
                    .Any(limitation => limitation!.GetValue<string>().Contains("composition fixture image", StringComparison.Ordinal)));
            }
        }
        finally { Directory.Delete(fixture.Run.OutputDirectory, recursive: true); }
    }

    [Theory]
    [InlineData("single-host")]
    [InlineData("single-host-container")]
    public async Task A_written_case_directory_holds_a_valid_record_whose_hashes_match_its_retained_bytes(string mode)
    {
        var fixture = MetadataFixture("AC-001-R01", mode);
        var observations = new CaseObservations();
        observations.Bind(180006, DeploymentArtifacts.Describe().Modules.First().Files);
        observations.Observed("Metadata validation input only; no provider execution took place.");
        // These are transient file-handling inputs, never retained provider evidence, and they are deleted even on failure.
        try
        {
            // The tuple of this composition's real modules and providers, with a Linux version in place of this platform's.
            var tuple = EvidenceWriter.Tuple(180006, mode, fixture.Run.ImageDigest, "6.8.0");
            Assert.Equal(mode, tuple["deployment"]!["topology"]!.GetValue<string>());
            await EvidenceWriter.WriteMeasuredAsync(fixture.Run, "AC-001-R01", mode, EvidenceOutcome.Passed, observations, tuple);
            var directory = Path.Combine(fixture.Run.OutputDirectory, fixture.Run.RunId, "ac-001-r01", mode);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, "record.json"), TestContext.Current.CancellationToken);
            EvidenceSchema.Validate(text, EvidenceDocuments.Text(EvidenceWriter.SchemaPath));
            Assert.False(File.Exists(Path.Combine(directory, "record.pending")));
            var record = JsonNode.Parse(text)!;
            var result = Assert.Single(record["verification"]!["results"]!.AsArray())!;
            Assert.Equal(fixture.Run.RunId + "/ac-001-r01/" + mode + "/result.json", result["location"]!.GetValue<string>());
            var retained = record["implementationArtifacts"]!.AsArray().Append(result).ToArray();
            Assert.Equal(observations.ImplementationFiles!.Count + 1, retained.Length);
            foreach (var item in retained)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.Run.OutputDirectory, item!["location"]!.GetValue<string>()),
                    TestContext.Current.CancellationToken);
                Assert.Equal(EvidenceWriter.Hash(bytes), item["sha256"]!.GetValue<string>());
            }
            Assert.Equal("passed", JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "result.json"),
                TestContext.Current.CancellationToken))!["integrationOutcome"]!.GetValue<string>());
            Assert.Equal("blocked", record["verification"]!["status"]!.GetValue<string>());
            Assert.DoesNotContain(fixture.Run.OutputDirectory, text, StringComparison.Ordinal);
            Assert.True(JsonNode.DeepEquals(tuple, record["tuple"]));
            await Assert.ThrowsAnyAsync<IOException>(() => EvidenceWriter.WriteMeasuredAsync(fixture.Run, "AC-001-R01", mode,
                EvidenceOutcome.Failed, observations, tuple));
            Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(directory, "record.json"), TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(fixture.Run.OutputDirectory)) Directory.Delete(fixture.Run.OutputDirectory, recursive: true); }
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
        var schema = EvidenceDocuments.Read(EvidenceWriter.SchemaPath);
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
        var container = MetadataFixture("AC-001-P01", "single-host-container");
        container = container with { Run = container.Run with { RunId = host.Run.RunId } };
        var another = host with { Run = host.Run with { RunId = Guid.NewGuid().ToString("N") } };
        var ids = new[] { Create(host, "AC-001-P01", "single-host", EvidenceOutcome.Passed)!,
            Create(container, "AC-001-P01", "single-host-container", EvidenceOutcome.Passed)!,
            Create(another, "AC-001-P01", "single-host", EvidenceOutcome.Passed)! }
            .Select(record => record["recordId"]!.GetValue<string>()).ToArray();
        Assert.Equal(3, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Output_locations_cannot_escape_the_evidence_directory()
    {
        var output = MetadataFixture("AC-001-P01", "single-host").Run.OutputDirectory;
        Assert.Equal("run/ac-001-p01/single-host/result.json",
            EvidenceRun.RelativeLocation(output, Path.Combine(output, "run", "ac-001-p01", "single-host", "result.json")));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.RelativeLocation(output, Path.Combine(output, "..", "evidence")));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.RelativeLocation(output, output));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.FromSettings(Path.Combine("relative", "evidence"), null, "run"));
    }

    [Fact]
    public void A_row_writes_a_record_only_for_its_own_deployment()
    {
        var host = MetadataFixture("AC-001-B01", "single-host");
        var container = MetadataFixture("AC-001-B01", "single-host-container");
        Assert.Throws<EvidenceValidationException>(() => Create(host with { Run = container.Run }, "AC-001-B01", "single-host", EvidenceOutcome.Passed));
        Assert.Throws<EvidenceValidationException>(() => Create(container with { Run = host.Run }, "AC-001-B01", "single-host-container", EvidenceOutcome.Passed));
        host.Tuple["deployment"]!["containerImageDigest"] = container.Tuple["deployment"]!["containerImageDigest"]!.DeepClone();
        Assert.Throws<EvidenceValidationException>(() => Create(host, "AC-001-B01", "single-host", EvidenceOutcome.Passed));
    }

    [Fact]
    public void A_container_run_binds_to_the_embedded_source_revision_and_the_measured_image_digest()
    {
        // The image has no checkout, so the commit must be the source revision every loaded Modularyn assembly embeds.
        var embedded = typeof(EvidenceWriter).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[^1];
        var output = Path.Combine(Path.GetTempPath(), "modularyn-evidence-" + Guid.NewGuid().ToString("N"));
        var digest = "sha256:" + EvidenceWriter.Hash(Encoding.UTF8.GetBytes("composition fixture image"));
        var run = EvidenceRun.FromSettings(output, embedded, "ci-run", DeploymentMode.Container, digest)!;
        Assert.Null(run.Repository);
        Assert.Equal(DeploymentMode.Container, run.Mode);
        Assert.Equal(digest, run.ImageDigest);
        Assert.Equal(embedded, run.SourceCommit);
        var other = new string(embedded.Select(character => character == '0' ? '1' : '0').ToArray());
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.FromSettings(output, other, "ci-run", DeploymentMode.Container, digest));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.FromSettings(output, embedded, "ci-run", DeploymentMode.Container, null));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.FromSettings(output, embedded, "ci-run", DeploymentMode.Container, digest.ToUpperInvariant()));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.FromSettings(output, embedded, "ci-run", DeploymentMode.Container, digest[7..]));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.FromSettings(output, embedded, "ci-run", DeploymentMode.Host, digest));
        Assert.Throws<EvidenceValidationException>(() => EvidenceRun.FromSettings(output, embedded, "ci-run", "container", digest));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Embedded_evidence_documents_equal_the_checkout_and_a_changed_copy_is_refused()
    {
        var root = EvidenceRun.FindRepository();
        EvidenceDocuments.MatchCheckout(root);
        var copy = Path.Combine(Path.GetTempPath(), "modularyn-evidence-documents-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var path in EvidenceDocuments.Paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(copy, path))!);
                File.Copy(Path.Combine(root, path), Path.Combine(copy, path));
            }
            EvidenceDocuments.MatchCheckout(copy);
            File.AppendAllText(Path.Combine(copy, EvidenceWriter.CorpusPath), "\n");
            Assert.Throws<EvidenceValidationException>(() => EvidenceDocuments.MatchCheckout(copy));
            Assert.Throws<EvidenceValidationException>(() => EvidenceDocuments.Bytes("docs/evidence/README.md"));
        }
        finally { Directory.Delete(copy, recursive: true); }
    }

    private static JsonObject? Create(RecordFixture fixture, string caseId, string mode, EvidenceOutcome outcome) =>
        EvidenceWriter.CreateRecord(fixture.Run, caseId, mode, outcome, fixture.Tuple, fixture.Artifacts,
            EvidenceWriter.Artifact("urn:modularyn:evidence-writer-test:result", fixture.ResultBytes),
            ["Metadata validation input only; no provider execution took place."]);

    private static RecordFixture MetadataFixture(string caseId, string mode)
    {
        var root = EvidenceRun.FindRepository();
        var runId = Guid.NewGuid().ToString("N");
        var input = Encoding.UTF8.GetBytes("Evidence writer metadata validation input.\n");
        var digest = mode == "single-host-container" ? "sha256:" + EvidenceWriter.Hash(input) : null;
        var run = new EvidenceRun(mode == "single-host-container" ? null : root,
            Path.Combine(root, "tests", "Modularyn.Tooling.Samples.TeamWorkspace.Tests", "TestResults", runId),
            EvidenceRun.Git(root, "rev-parse", "HEAD").Trim(), runId, DateOnly.FromDateTime(DateTime.UtcNow), mode, digest);
        var tuple = EvidenceWriter.Read(root, "docs/evidence/examples/ac-009-r01-single-host-container.json")["tuple"]!.DeepClone().AsObject();
        var plan = EvidenceDocuments.Read(EvidenceWriter.PlanPath)["cases"]!.AsArray()
            .Single(row => row!["caseId"]!.GetValue<string>() == caseId)!;
        tuple["moduleVersions"] = new JsonArray(plan["plannedTests"]!.AsArray()
            .Select(test => Path.GetFileNameWithoutExtension(test!["project"]!.GetValue<string>())[..^6]).Distinct(StringComparer.Ordinal)
            .Select(id => (JsonNode)new JsonObject { ["id"] = id, ["version"] = "1.0.0" }).ToArray());
        tuple["deployment"]!["topology"] = mode;
        tuple["deployment"]!["operatingSystem"]!["version"] = "6.8.0";
        tuple["deployment"]!["containerImageDigest"] = digest;
        tuple["providers"]![0]!["version"] = "10.0.3";
        tuple["providers"]![1]!["version"] = "1.0.0";
        return new(run, tuple, new JsonArray(EvidenceWriter.Artifact("urn:modularyn:evidence-writer-test:input", input)), input);
    }

    private sealed record RecordFixture(EvidenceRun Run, JsonObject Tuple, JsonArray Artifacts, byte[] ResultBytes);
}
