using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using Modularyn.Repository.Tests;
using Xunit;
using static Modularyn.Tooling.Samples.TeamWorkspace.Tests.EvidenceSchema;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

public sealed class EvidenceMeasurementTests
{
    // CI invokes this once before collecting reports. It runs in the deployment it measures, including inside the fixture
    // image. No package pin, operating-system version or database version is inferred from a runner label.
    [Fact]
    public async Task Measure_the_evidence_run_before_collecting_results()
    {
        var output = Environment.GetEnvironmentVariable("MODULARYN_EVIDENCE_MEASURE");
        Assert.SkipWhen(output != "1", "The evidence collection scripts explicitly request a measured run.");
        var run = EvidenceRun.FromEnvironment() ?? throw new EvidenceValidationException("evidence output required");
        var session = await PostgreSqlProbe.ConnectAsync(PostgreSqlTarget.FromEnvironment(), TestContext.Current.CancellationToken);
        Require(session.ServerVersionNumber is >= 180000 and < 190000
            && session.Parameters.GetValueOrDefault("is_superuser") == "off", "database role and version");
        var tuple = EvidenceWriter.MeasuredTuple(session.ServerVersionNumber, run.Mode, run.ImageDigest);
        var root = Environment.GetEnvironmentVariable("MODULARYN_EVIDENCE_BINARIES");
        Require(!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) && Directory.Exists(root), "built inventory required");
        var artifacts = new JsonArray();
        var versions = tuple["moduleVersions"]!.AsArray().ToDictionary(row => row!["id"]!.GetValue<string>(),
            row => row!["version"]!.GetValue<string>(), StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root!, "Modularyn.*.dll", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            // The host scan excludes intermediate/reference assemblies. The image holds only published project outputs.
            var normalized = path.Replace(Path.DirectorySeparatorChar, '/');
            if (run.Mode == DeploymentMode.Host && !normalized.Contains("/bin/Release/net10.0/", StringComparison.Ordinal)) continue;
            Require(new FileInfo(path).LinkTarget is null, "linked implementation refused");
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var (name, informationalVersion) = ReadIdentity(bytes);
            Require(informationalVersion.Split('+', '.').Contains(run.SourceCommit, StringComparer.Ordinal), "inventory source revision differs");
            var hash = EvidenceWriter.Hash(bytes);
            if (files.TryGetValue(name, out var prior))
            {
                Require(prior == hash, "ambiguous built implementation");
                continue;
            }
            files.Add(name, hash);
            if (!name.EndsWith(".Tests", StringComparison.Ordinal) && name != "Modularyn.Repository.Tests")
                versions[name] = informationalVersion.Split('+')[0];
            var location = "implementation/" + name + ".dll";
            var target = Path.Combine(run.OutputDirectory, location);
            _ = EvidenceRun.RelativeLocation(run.OutputDirectory, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await EvidenceWriter.WriteNewAsync(target, bytes);
            artifacts.Add(EvidenceWriter.Artifact(location, bytes));
        }
        foreach (var test in EvidenceDocuments.Read(EvidenceWriter.PlanPath)["cases"]!.AsArray()
                     .SelectMany(row => row!["plannedTests"]!.AsArray()))
        {
            var project = Path.GetFileNameWithoutExtension(test!["project"]!.GetValue<string>());
            Require(files.ContainsKey(project) && versions.ContainsKey(project[..^6]), "planned implementation unavailable");
        }
        tuple["moduleVersions"] = new JsonArray(versions.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => (JsonNode)new JsonObject { ["id"] = item.Key, ["version"] = item.Value }).ToArray());
        tuple["ui"] = new JsonArray(new JsonObject { ["name"] = "Razor", ["version"] = tuple["runtime"]!["aspNetCoreVersion"]!.DeepClone() });
        EvidenceWriter.ValidateMeasuredTuple(tuple, run.Mode);
        var measurement = new JsonObject
        {
            ["sourceCommit"] = run.SourceCommit, ["runId"] = run.RunId, ["deploymentMode"] = run.Mode,
            ["date"] = run.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["tuple"] = tuple, ["implementationArtifacts"] = artifacts
        };
        await EvidenceWriter.WriteNewAsync(Path.Combine(run.OutputDirectory, "measurement.json"), EvidenceWriter.Bytes(measurement));
    }

    // Read metadata without loading test assemblies or running their initializers. The retained bytes are exactly the bytes
    // whose informational source revision and package version are used above.
    internal static (string Name, string Version) ReadIdentity(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection"
                || reader.GetString(type.Name) != "AssemblyInformationalVersionAttribute") continue;
            var value = reader.GetBlobReader(attribute.Value);
            Require(value.ReadUInt16() == 1, "assembly version encoding");
            return (reader.GetString(assembly.Name), value.ReadSerializedString() ?? throw new EvidenceValidationException("assembly version absent"));
        }
        throw new EvidenceValidationException("assembly revision absent");
    }
}
