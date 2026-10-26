using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Modularity.Tests;

// The corpus has no reference-profile trust case, so these tests carry the control identifier they serve.
public sealed class DeploymentTrustTests : IDisposable
{
    private static readonly SemanticVersion One = SemanticVersion.Parse("1.0.0");
    private static readonly CompositionEnvironment Environment = new(SemanticVersion.Parse("10.0.0"), One);
    private static readonly VersionRange CompatibleB = new(SemanticVersion.Parse("2.0.0"), true, SemanticVersion.Parse("2.2.0"), true);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private readonly string root = Path.Combine(Path.GetTempPath(), "modularyn-trust-" + Guid.NewGuid().ToString("N"));
    private readonly string content;
    private readonly string lockPath;

    public DeploymentTrustTests()
    {
        content = Path.Combine(root, "app");
        Directory.CreateDirectory(content);
        lockPath = Path.Combine(root, "deployment.lock.json");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact(DisplayName = "TM-G02-001 a selection matching its lock and package hashes composes")]
    public async Task TM_G02_001_A_selection_matching_its_lock_and_package_hashes_composes()
    {
        var graph = Graph();
        WriteLock(graph.Select(Package));
        var composer = Composer();
        Assert.True(composer.VerifiesTrust);
        var result = await composer.ComposeAsync(new(graph.Reverse(), Environment), Cancellation);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "C", "B", "A" }, result.Plan!.Modules.Select(module => module.Id));
        var text = File.ReadAllText(lockPath);
        Assert.Equal(text, DeploymentLock.Parse(text).ToJson());
    }

    [Theory(DisplayName = "TM-G02-001 unlisted, unselected and redeclared modules are refused")]
    [InlineData("unlisted", "MDC202", "D", "is not listed in the deployment lock")]
    [InlineData("unselected", "MDC203", "E", "the selection does not include it")]
    [InlineData("version", "MDC204", "B", "selected at 2.2.0, but the deployment lock records 2.1.0")]
    [InlineData("build-metadata", "MDC204", "B", "selected at 2.1.0+rebuilt, but the deployment lock records 2.1.0")]
    [InlineData("widened-range", "MDC205", "A", "differ from its locked manifest digest")]
    [InlineData("added-route", "MDC205", "A", "differ from its locked manifest digest")]
    [InlineData("exclusive-flag", "MDC205", "C", "differ from its locked manifest digest")]
    [InlineData("migration", "MDC206", "C", "declares migrations [Initial 1.0.0, Backfill 1.0.0], but the deployment lock records [Initial 1.0.0]")]
    public async Task TM_G02_001_Unlisted_unselected_and_redeclared_modules_are_refused_before_planning(string mutation, string code, string module, string cause)
    {
        var graph = Graph();
        var locked = graph.Select(Package).ToList();
        if (mutation == "unselected") locked.Add(Package(new("E", One)));
        WriteLock(locked);
        ModuleManifest[] selection = mutation switch
        {
            "unlisted" => [.. graph, new("D", One)],
            "unselected" => graph,
            "version" => [graph[0], B("2.2.0"), graph[2]],
            "build-metadata" => [graph[0], B("2.1.0+rebuilt"), graph[2]],
            "widened-range" => [new("A", One, [new("B", VersionRange.Any)]), graph[1], graph[2]],
            "added-route" => [new("A", One, [new("B", CompatibleB)], routes: [new("POST", "/records")]), graph[1], graph[2]],
            "exclusive-flag" => [graph[0], graph[1], C(exclusive: true)],
            _ => [graph[0], graph[1], C(extraMigration: true)]
        };
        var result = await Composer().ComposeAsync(new(selection, Environment), Cancellation);
        Assert.Null(result.Plan);
        var expected = mutation == "migration" ? new[] { "MDC205", code } : [code];
        Assert.Equal(expected, result.Diagnostics.Select(item => item.Code));
        var diagnostic = result.Diagnostics[^1];
        Assert.Equal(new[] { module }, diagnostic.ModuleIds);
        Assert.Contains(cause, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(code switch { "MDC202" => "remove 'D'", "MDC203" => "Select exactly the locked modules", _ => "Deploy" },
            diagnostic.Message, StringComparison.Ordinal);
        AssertNoLocation(result);
    }

    [Theory(DisplayName = "TM-G02-001 altered, missing and linked package files are refused")]
    [InlineData("altered", "MDC211", "does not match the content hash")]
    [InlineData("appended", "MDC211", "does not match the content hash")]
    [InlineData("missing", "MDC210", "is missing from the content root")]
    [InlineData("directory", "MDC212", "is not a regular file")]
    [InlineData("file-link", "MDC212", "is a link")]
    [InlineData("directory-link", "MDC212", "is reached through a link")]
    public async Task TM_G02_001_Altered_missing_and_linked_package_files_are_refused(string mutation, string code, string cause)
    {
        var graph = Graph();
        WriteLock(graph.Select(Package));
        var file = Path.Combine(content, "modules", "B", "Modularyn.Modules.B.dll");
        var original = File.ReadAllBytes(file);
        var copy = Path.Combine(root, "copy");
        Directory.CreateDirectory(copy);
        switch (mutation)
        {
            case "altered":
                original[^1] ^= 0x01;
                File.WriteAllBytes(file, original);
                break;
            case "appended":
                File.AppendAllText(file, "x");
                break;
            case "missing":
                File.Delete(file);
                break;
            case "directory":
                File.Delete(file);
                Directory.CreateDirectory(file);
                break;
            case "file-link":
                // The link reaches identical bytes; trust still refuses it, because links are never followed.
                var target = Path.Combine(copy, "Modularyn.Modules.B.dll");
                File.WriteAllBytes(target, original);
                File.Delete(file);
                Link(() => File.CreateSymbolicLink(file, target));
                break;
            default:
                var directory = Path.Combine(content, "modules", "B");
                var moved = Path.Combine(copy, "B");
                Directory.Move(directory, moved);
                Link(() => Directory.CreateSymbolicLink(directory, moved));
                break;
        }
        var result = await Composer().ComposeAsync(new(graph, Environment), Cancellation);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(new[] { "B" }, diagnostic.ModuleIds);
        Assert.Contains("Package file 'modules/B/Modularyn.Modules.B.dll' of module 'B'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(cause, diagnostic.Message, StringComparison.Ordinal);
        AssertNoLocation(result);
    }

    [Theory(DisplayName = "TM-G02-001 a missing, unreadable or invalid lock refuses every selected module")]
    [InlineData("missing", "MDC200", "was not found")]
    [InlineData("not-json", "MDC201", "is not valid JSON")]
    [InlineData("not-utf8", "MDC201", "is not UTF-8 text")]
    [InlineData("comment", "MDC201", "is not valid JSON")]
    [InlineData("trailing-comma", "MDC201", "is not valid JSON")]
    [InlineData("duplicate-property", "MDC201", "is not valid JSON")]
    [InlineData("format", "MDC201", "format must be 'modularyn.deployment-lock'")]
    [InlineData("format-version", "MDC201", "formatVersion must be the integer 1")]
    [InlineData("format-version-decimal", "MDC201", "formatVersion must be the integer 1")]
    [InlineData("unknown-root-field", "MDC201", "lock must have exactly the fields")]
    [InlineData("unknown-module-field", "MDC201", "modules[0] must have exactly the fields")]
    [InlineData("missing-field", "MDC201", "modules[0] must have exactly the fields")]
    [InlineData("no-modules", "MDC201", "at least one module")]
    [InlineData("duplicate-module", "MDC201", "Module 'A' is listed more than once")]
    [InlineData("no-files", "MDC201", "must list at least one package file")]
    [InlineData("repeated-file", "MDC201", "more than once")]
    [InlineData("conflicting-file", "MDC201", "different spellings or content hashes")]
    [InlineData("version", "MDC201", "modules[0].version must be a SemVer 2.0.0 version")]
    [InlineData("uppercase-hash", "MDC201", "modules[0].files[0].sha256 must be 64 lowercase hexadecimal digits")]
    [InlineData("short-digest", "MDC201", "modules[0].manifestSha256 must be 64 lowercase hexadecimal digits")]
    [InlineData("parent-path", "MDC201", "modules[0].files[0].path must be a safe relative path")]
    [InlineData("rooted-path", "MDC201", "modules[0].files[0].path must be a safe relative path")]
    [InlineData("backslash-path", "MDC201", "modules[0].files[0].path must be a safe relative path")]
    [InlineData("stream-path", "MDC201", "modules[0].files[0].path must be a safe relative path")]
    [InlineData("device-path", "MDC201", "modules[0].files[0].path must be a safe relative path")]
    [InlineData("dot-segment", "MDC201", "modules[0].files[0].path must be a safe relative path")]
    [InlineData("blank-id", "MDC201", "modules[0].id must be a nonblank name")]
    [InlineData("number-id", "MDC201", "modules[0].id must be a string")]
    public async Task TM_G02_001_A_missing_unreadable_or_invalid_lock_refuses_every_selected_module(string mutation, string code, string cause)
    {
        var graph = Graph();
        WriteLock(graph.Select(Package));
        var text = File.ReadAllText(lockPath);
        var json = JsonNode.Parse(text)!;
        var first = json["modules"]![0]!;
        var file = first["files"]![0]!;
        switch (mutation)
        {
            case "missing": File.Delete(lockPath); break;
            case "not-json": File.WriteAllText(lockPath, "{"); break;
            case "not-utf8": File.WriteAllBytes(lockPath, [0x7B, 0xFF, 0x7D]); break;
            case "comment": File.WriteAllText(lockPath, "// reviewed\n" + text); break;
            case "trailing-comma": File.WriteAllText(lockPath, text.TrimEnd()[..^1].TrimEnd() + ",}"); break;
            case "duplicate-property": File.WriteAllText(lockPath, "{\"format\":\"modularyn.deployment-lock\"," + text.TrimStart()[1..]); break;
            case "format-version-decimal": File.WriteAllText(lockPath, text.Replace("\"formatVersion\": 1,", "\"formatVersion\": 1.0,", StringComparison.Ordinal)); break;
            default:
                switch (mutation)
                {
                    case "format": json["format"] = "modularyn.other-lock"; break;
                    case "format-version": json["formatVersion"] = 2; break;
                    case "unknown-root-field": json["comment"] = "reviewed"; break;
                    case "unknown-module-field": first["optional"] = true; break;
                    case "missing-field": first.AsObject().Remove("migrations"); break;
                    case "no-modules": json["modules"] = new JsonArray(); break;
                    case "duplicate-module": json["modules"]!.AsArray().Add(first.DeepClone()); break;
                    case "no-files": first["files"] = new JsonArray(); break;
                    case "repeated-file": first["files"]!.AsArray().Add(file.DeepClone()); break;
                    case "conflicting-file":
                        var conflicting = file.DeepClone();
                        conflicting["sha256"] = new string('0', 64);
                        json["modules"]![1]!["files"]!.AsArray().Add(conflicting);
                        break;
                    case "version": first["version"] = "1.0"; break;
                    case "uppercase-hash": file["sha256"] = file["sha256"]!.GetValue<string>().ToUpperInvariant(); break;
                    case "short-digest": first["manifestSha256"] = "abc"; break;
                    case "parent-path": file["path"] = "../outside/Modularyn.Modules.A.dll"; break;
                    case "rooted-path": file["path"] = "/modules/A/Modularyn.Modules.A.dll"; break;
                    case "backslash-path": file["path"] = "modules\\A\\Modularyn.Modules.A.dll"; break;
                    case "stream-path": file["path"] = "modules/A/Modularyn.Modules.A.dll:payload"; break;
                    case "device-path": file["path"] = "modules/A/NUL.dll"; break;
                    case "dot-segment": file["path"] = "modules/./A/Modularyn.Modules.A.dll"; break;
                    case "blank-id": first["id"] = " "; break;
                    default: first["id"] = 7; break;
                }
                File.WriteAllText(lockPath, json.ToJsonString());
                break;
        }
        var result = await Composer().ComposeAsync(new(graph, Environment), Cancellation);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(new[] { "A", "B", "C" }, diagnostic.ModuleIds);
        Assert.Contains("The deployment lock 'deployment.lock.json'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(cause, diagnostic.Message, StringComparison.Ordinal);
        AssertNoLocation(result);
    }

    [Fact(DisplayName = "TM-G02-001 a lock larger than the format limit is refused unread")]
    public async Task TM_G02_001_A_lock_larger_than_the_format_limit_is_refused_unread()
    {
        await using (var stream = File.Create(lockPath)) stream.SetLength(DeploymentLock.MaximumBytes + 1L);
        var result = await Composer().ComposeAsync(new(Graph(), Environment), Cancellation);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MDC200", diagnostic.Code);
        Assert.Contains("is larger than", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "TM-G02-001 trust diagnostics appear even when structural checks fail")]
    public async Task TM_G02_001_Trust_diagnostics_are_reported_even_when_structural_checks_fail()
    {
        var graph = Graph();
        WriteLock(graph.Select(Package));
        var calls = new List<string>();
        var composer = new ModuleComposer([new RecordingValidator("ordinary", -10, calls), new DeploymentLockValidator(lockPath, content)]);

        var missing = await composer.ComposeAsync(new(graph[..2], Environment), Cancellation);
        Assert.Equal(new[] { "MDC203", "MDC001" }, missing.Diagnostics.Select(item => item.Code));
        Assert.Equal(new[] { "C" }, missing.Diagnostics[0].ModuleIds);

        var duplicate = await composer.ComposeAsync(new([graph[0], graph[1], B("2.2.0"), graph[2]], Environment), Cancellation);
        Assert.Equal(new[] { "MDC204", "MDC006" }, duplicate.Diagnostics.Select(item => item.Code));

        File.AppendAllText(Path.Combine(content, "modules", "C", "Modularyn.Modules.C.dll"), "x");
        var altered = await composer.ComposeAsync(new([graph[0], B("1.9.9"), graph[2]], Environment), Cancellation);
        Assert.Equal(new[] { "MDC204", "MDC211", "MDC003" }, altered.Diagnostics.Select(item => item.Code));

        Assert.All(new[] { missing, duplicate, altered }, result => Assert.Null(result.Plan));
        Assert.Empty(calls);
    }

    [Fact(DisplayName = "TM-G02-001 trust validators run first, in order, and ordinary validators only after trust passes")]
    public async Task TM_G02_001_Trust_validators_run_first_in_order_and_ordinary_validators_only_after_trust_passes()
    {
        var calls = new List<string>();
        ICompositionValidator[] validators =
        [
            new RecordingValidator("ordinary", -10, calls), new RecordingTrustValidator("late-trust", 20, calls),
            new RecordingTrustValidator("early-trust", 10, calls)
        ];
        var composer = new ModuleComposer(validators);
        Assert.True((await composer.ComposeAsync(new([new("A", One)], Environment), Cancellation)).Succeeded);
        Assert.Equal(new[] { "early-trust", "late-trust", "ordinary" }, calls);

        calls.Clear();
        var refused = await new ModuleComposer([new RecordingValidator("ordinary", 0, calls), new RecordingTrustValidator("trust", 0, calls, refuse: true)])
            .ComposeAsync(new([new("A", One)], Environment), Cancellation);
        Assert.Null(refused.Plan);
        Assert.Equal("MDC299", Assert.Single(refused.Diagnostics).Code);
        Assert.Equal(new[] { "trust" }, calls);
        Assert.False(new ModuleComposer([new RecordingValidator("ordinary", 0, calls)]).VerifiesTrust);
    }

    [Fact(DisplayName = "TM-G02-001 a later attempt observes the files as they are then")]
    public async Task TM_G02_001_A_later_attempt_observes_restored_and_altered_files()
    {
        var graph = Graph();
        WriteLock(graph.Select(Package));
        var composer = Composer();
        var file = Path.Combine(content, "modules", "A", "Modularyn.Modules.A.dll");
        var original = File.ReadAllBytes(file);
        Assert.True((await composer.ComposeAsync(new(graph, Environment), Cancellation)).Succeeded);
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(original.Length));
        Assert.Equal("MDC211", Assert.Single((await composer.ComposeAsync(new(graph, Environment), Cancellation)).Diagnostics).Code);
        File.WriteAllBytes(file, original);
        Assert.True((await composer.ComposeAsync(new(graph, Environment), Cancellation)).Succeeded);
    }

    [Fact(DisplayName = "TM-G02-001 every manifest declaration contributes to its digest")]
    public void TM_G02_001_Every_manifest_declaration_contributes_to_its_digest()
    {
        // A new manifest property must be added to the canonical form and to this list.
        Assert.Equal(new[]
        {
            "BackgroundHandlers", "Definitions", "Dependencies", "FrameworkVersions", "HealthChecks", "Id", "Migrations", "ProvidedCapabilities",
            "ProvidedContracts", "ProvidedProviders", "RequiredCapabilities", "RequiredContracts", "RequiredProviders", "Routes", "RuntimeVersions", "Version"
        }, typeof(ModuleManifest).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        var two = SemanticVersion.Parse("2.0.0");
        ModuleProvision[] provision = [new("P", One)];
        ModuleRequirement[] requirement = [new("P", VersionRange.Any)];
        ModuleAsset[] asset = [new("X", One)];
        var baseline = ModuleManifestDigest.Compute(new("M", One));
        Assert.Equal(baseline, ModuleManifestDigest.Compute(new("M", One)));
        Assert.Matches("^[0-9a-f]{64}$", baseline);
        ModuleManifest[] variants =
        [
            new("N", One), new("M", two), new("M", SemanticVersion.Parse("1.0.0+build")),
            new("M", One, dependencies: [new("D", VersionRange.Any)]), new("M", One, dependencies: [new("D", VersionRange.Any, optional: true)]),
            new("M", One, providedContracts: provision), new("M", One, requiredContracts: requirement),
            new("M", One, providedCapabilities: provision), new("M", One, requiredCapabilities: requirement),
            new("M", One, providedProviders: provision), new("M", One, providedProviders: [new("P", One, exclusive: true)]),
            new("M", One, requiredProviders: requirement), new("M", One, requiredProviders: [new("P", VersionRange.Exact(One))]),
            new("M", One, routes: [new("GET", "/m")]), new("M", One, definitions: asset), new("M", One, migrations: asset),
            new("M", One, backgroundHandlers: asset), new("M", One, healthChecks: asset), new("M", One, healthChecks: [new("X", two)]),
            new("M", One, runtimeVersions: VersionRange.Exact(One)), new("M", One, frameworkVersions: new VersionRange(One, true, two, false)),
            new("M", One, frameworkVersions: new VersionRange(One, true, two, true))
        ];
        var digests = variants.Select(ModuleManifestDigest.Compute).Append(baseline).ToArray();
        Assert.Equal(digests.Length, digests.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory(DisplayName = "TM-G02-001 locked package paths are relative, literal and confined")]
    [InlineData("Modularyn.Modules.A.dll", true)]
    [InlineData("modules/A/Modularyn.Modules.A.dll", true)]
    [InlineData("runtimes/linux-x64/lib/net10.0/A+B_c-1.dll", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("./A.dll", false)]
    [InlineData("a/../A.dll", false)]
    [InlineData("a//A.dll", false)]
    [InlineData("/A.dll", false)]
    [InlineData("A.dll/", false)]
    [InlineData(".hidden", false)]
    [InlineData("trailing.", false)]
    [InlineData("a\\A.dll", false)]
    [InlineData("C:A.dll", false)]
    [InlineData("a/con.txt", false)]
    [InlineData("a/LPT1", false)]
    [InlineData("A dll", false)]
    [InlineData("A\u00e9.dll", false)]
    [InlineData("A.dll\n", false)]
    [InlineData("a/A.dll\n", false)]
    public void TM_G02_001_Locked_package_paths_are_relative_literal_and_confined(string path, bool accepted)
    {
        var hash = new string('a', 64);
        if (accepted) Assert.Equal(path, new LockedFile(path, hash).Path);
        else Assert.Throws<ArgumentException>(() => new LockedFile(path, hash));
    }

    private ModuleComposer Composer() => new([new DeploymentLockValidator(lockPath, content)]);

    private static ModuleManifest[] Graph() => [new("A", One, [new("B", CompatibleB)]), B("2.1.0"), C()];

    private static ModuleManifest B(string version) => new("B", SemanticVersion.Parse(version), [new("C", VersionRange.Exact(One))]);

    private static ModuleManifest C(bool exclusive = false, bool extraMigration = false) => new("C", One,
        providedProviders: [new("FileStore", One, exclusive)],
        migrations: extraMigration ? [new("Initial", One), new("Backfill", One)] : [new("Initial", One)]);

    private LockedModule Package(ModuleManifest manifest)
    {
        var relative = $"modules/{manifest.Id}/Modularyn.Modules.{manifest.Id}.dll";
        var path = Path.Combine(content, "modules", manifest.Id, $"Modularyn.Modules.{manifest.Id}.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = RandomNumberGenerator.GetBytes(4096);
        File.WriteAllBytes(path, bytes);
        return LockedModule.Describe(manifest, [new LockedFile(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
    }

    private void WriteLock(IEnumerable<LockedModule> modules) => File.WriteAllText(lockPath, new DeploymentLock(modules).ToJson(), new UTF8Encoding(false));

    private void AssertNoLocation(CompositionResult result) =>
        Assert.All(result.Diagnostics, item => Assert.DoesNotContain(root, item.Message, StringComparison.OrdinalIgnoreCase));

    private static void Link(Action create)
    {
        try { create(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This account cannot create symbolic links.");
        }
    }

    private class RecordingValidator(string name, int order, List<string> calls, bool refuse = false) : ICompositionValidator
    {
        public int Order => order;
        public ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken)
        {
            calls.Add(name);
            return ValueTask.FromResult<IReadOnlyList<CompositionDiagnostic>>(refuse
                ? [new("MDC299", "Module 'A' is not trusted by the fixture. Deploy the reviewed release.", ["A"])] : []);
        }
    }

    private sealed class RecordingTrustValidator(string name, int order, List<string> calls, bool refuse = false)
        : RecordingValidator(name, order, calls, refuse), ICompositionTrustValidator;
}
