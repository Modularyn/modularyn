using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ToolPinTests
{
    private const string PinPath = ToolPinContract.SdkPinPath;
    private const string CiPath = ToolPinContract.CiPath;
    private const string SecretScanPath = ToolPinContract.SecretScanPath;
    private const string AllowListPath = ".github/workflows/actions.allow";
    private const string RegisterPath = DependencyRegister.RegisterPath;

    [Fact]
    public void Tool_pins_agree_with_global_json_the_workflows_and_the_register()
    {
        Assert.Empty(ToolPinContract.Validate(RepositoryFiles.Current));
    }

    [Fact]
    public void Tool_pins_accept_platform_line_endings()
    {
        var files = RepositoryFiles.Current.ToDictionary();
        foreach (var path in new[] { PinPath, CiPath, SecretScanPath, RegisterPath, "global.json" })
        {
            files[path] = files[path].Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        Assert.Empty(ToolPinContract.Validate(files));
    }

    [Fact]
    public void Sdk_pin_names_exactly_the_six_hosted_runner_platforms()
    {
        string[] expected = ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64"];
        Assert.Equal(expected, Archives().Select(archive => archive.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Sdk_pin_version_equals_global_json()
    {
        var globalJson = JsonNode.Parse(RepositoryFiles.Current["global.json"])!;
        Assert.Equal(Value(globalJson["sdk"]!, "version"), Value(Pin(), "version"));
    }

    [Fact]
    public void Sdk_pin_urls_are_official_archives_of_that_version_for_their_platform()
    {
        var version = Value(Pin(), "version");
        foreach (var (rid, archive) in Archives())
        {
            var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
            Assert.Equal($"https://builds.dotnet.microsoft.com/dotnet/Sdk/{version}/dotnet-sdk-{version}-{rid}{extension}",
                Value(archive!, "url"));
        }
    }

    [Fact]
    public void Sdk_pin_hashes_are_lowercase_sha512_digests()
    {
        foreach (var (_, archive) in Archives())
        {
            Assert.Matches("^[0-9a-f]{128}$", Value(archive!, "sha512"));
        }
    }

    [Fact]
    public void Ci_installs_the_sdk_from_the_pin_file_without_setup_dotnet()
    {
        var files = RepositoryFiles.Current;
        Assert.Contains(PinPath, files[CiPath], StringComparison.Ordinal);
        foreach (var path in files.Keys.Where(path => path.StartsWith(".github/workflows/", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("setup-dotnet", files[path], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Secret_scan_pins_the_gitleaks_digest_at_the_registered_version()
    {
        var workflow = RepositoryFiles.Current[SecretScanPath].Replace("\r\n", "\n");
        Assert.Matches("^[0-9a-f]{64}$", WorkflowValue(workflow, "GITLEAKS_SHA256"));
        var entries = JsonNode.Parse(RepositoryFiles.Current[RegisterPath])!["entries"]!.AsArray();
        var gitleaks = Assert.Single(entries, entry => (string?)entry!["id"] == "binary:gitleaks")!;
        Assert.Equal("in-use", Value(gitleaks, "status"));
        Assert.Equal(Value(gitleaks, "version"), WorkflowValue(workflow, "GITLEAKS_VERSION"));
        var recorded = Assert.Single(gitleaks["digests"]!.AsArray(), digest => (string?)digest!["target"] == "linux-x64")!;
        Assert.Equal(ToolPinContract.GitleaksArchiveUrl(Value(gitleaks, "version")), Value(recorded, "url"));
        Assert.Equal("sha256:" + WorkflowValue(workflow, "GITLEAKS_SHA256"), Value(recorded, "digest"));
    }

    [Theory]
    [InlineData("missing-pin", "the SDK archive pin file is missing")]
    [InlineData("invalid-json", "must contain valid JSON")]
    [InlineData("array-root", "must be a JSON object")]
    [InlineData("missing-source", "fields must be exactly version, source, archives")]
    [InlineData("extra-field", "fields must be exactly version, source, archives")]
    [InlineData("duplicate-field", "duplicate field")]
    [InlineData("pin-version-mismatch", "must equal the global.json SDK version")]
    [InlineData("global-version-mismatch", "must equal the global.json SDK version")]
    [InlineData("missing-rid", "archives must name exactly")]
    [InlineData("extra-rid", "archives must name exactly")]
    [InlineData("archives-not-object", "archives must be an object")]
    [InlineData("archive-not-object", "must be an object with url and sha512")]
    [InlineData("extra-archive-field", "fields must be exactly url, sha512")]
    [InlineData("missing-hash", "fields must be exactly url, sha512")]
    [InlineData("swapped-urls", "url must be the official SDK archive")]
    [InlineData("other-version-url", "url must be the official SDK archive")]
    [InlineData("http-url", "url must be the official SDK archive")]
    [InlineData("other-host-url", "url must be the official SDK archive")]
    [InlineData("windows-tarball-url", "url must be the official SDK archive")]
    [InlineData("uppercase-hash", "sha512 must be 128 lowercase hexadecimal characters")]
    [InlineData("short-hash", "sha512 must be 128 lowercase hexadecimal characters")]
    [InlineData("non-hex-hash", "sha512 must be 128 lowercase hexadecimal characters")]
    [InlineData("numeric-hash", "sha512 must be 128 lowercase hexadecimal characters")]
    [InlineData("duplicate-hash", "duplicates the hash of")]
    [InlineData("metadata-source", "source must be the dotnet/core release record")]
    [InlineData("other-channel-source", "source must be the dotnet/core release record")]
    [InlineData("uncited-source", "must cite the source record")]
    [InlineData("missing-ci", "the CI workflow is missing")]
    [InlineData("ci-without-pin", "must install the SDK from the archives pinned in")]
    [InlineData("setup-dotnet-step", "must not use actions/setup-dotnet")]
    [InlineData("allowed-setup-dotnet", "must not use actions/setup-dotnet")]
    [InlineData("dotnet-install-script", "must not run the dotnet-install script")]
    [InlineData("missing-secret-scan", "the secret-scan workflow is missing")]
    [InlineData("missing-gitleaks-digest", "must pin GITLEAKS_SHA256 exactly once")]
    [InlineData("duplicate-gitleaks-digest", "must pin GITLEAKS_SHA256 exactly once")]
    [InlineData("uppercase-gitleaks-digest", "GITLEAKS_SHA256 must be 64 lowercase hexadecimal characters")]
    [InlineData("short-gitleaks-digest", "GITLEAKS_SHA256 must be 64 lowercase hexadecimal characters")]
    [InlineData("sha512-gitleaks-digest", "GITLEAKS_SHA256 must be 64 lowercase hexadecimal characters")]
    [InlineData("unused-gitleaks-digest", "must verify the archive against GITLEAKS_SHA256")]
    [InlineData("lax-gitleaks-check", "must verify the archive against GITLEAKS_SHA256")]
    [InlineData("missing-gitleaks-version", "must pin GITLEAKS_VERSION exactly once")]
    [InlineData("gitleaks-version-mismatch", "must equal the register's in-use binary:gitleaks version")]
    [InlineData("register-gitleaks-mismatch", "must equal the register's in-use binary:gitleaks version")]
    [InlineData("missing-register-gitleaks", "needs exactly one in-use binary:gitleaks entry")]
    [InlineData("candidate-register-gitleaks", "needs exactly one in-use binary:gitleaks entry")]
    [InlineData("missing-global-json", "cannot read the SDK version")]
    [InlineData("missing-register", "cannot read the register entries")]
    [InlineData("register-gitleaks-digest-mismatch", "must equal the register's binary:gitleaks linux-x64 digest")]
    [InlineData("register-gitleaks-url-mismatch", "must equal the register's binary:gitleaks linux-x64 digest")]
    [InlineData("missing-register-gitleaks-digest", "must equal the register's binary:gitleaks linux-x64 digest")]
    public void A_single_tool_pin_violation_is_rejected(string mutation, string expectedDiagnostic)
    {
        // Every mutation derives its values from the current files, so a later pin update cannot turn it into a no-op.
        var original = RepositoryFiles.Current;
        var files = original.ToDictionary();
        var pin = JsonNode.Parse(files[PinPath])!;
        var archives = pin["archives"]!.AsObject();
        var (firstRid, first) = archives.First();
        var (_, second) = archives.Skip(1).First();
        var (_, windows) = archives.First(archive => archive.Key.StartsWith("win-", StringComparison.Ordinal));
        var version = Value(pin, "version");
        var channel = string.Join('.', version.Split('.')[..2]);
        var source = Value(pin, "source");
        var register = JsonNode.Parse(files[RegisterPath])!;
        var entries = register["entries"]!.AsArray();
        var sdkEntry = entries.First(entry => (string?)entry!["ecosystem"] == "dotnet-sdk")!;
        var gitleaks = entries.First(entry => (string?)entry!["id"] == "binary:gitleaks")!;
        var scan = files[SecretScanPath];
        var digest = WorkflowValue(scan.Replace("\r\n", "\n"), "GITLEAKS_SHA256");
        string? pinText = null;
        switch (mutation)
        {
            case "missing-pin": Assert.True(files.Remove(PinPath)); break;
            case "invalid-json": pinText = files[PinPath].TrimEnd()[..^1]; break;
            case "array-root": pinText = "[]"; break;
            case "missing-source": Assert.True(pin.AsObject().Remove("source")); break;
            case "extra-field": Add(pin, "runtime", channel); break;
            case "duplicate-field":
                pinText = Changed(files[PinPath], new Regex("\"version\"\\s*:\\s*(\"[^\"]*\")")
                    .Replace(files[PinPath], "\"version\": $1, \"version\": $1", 1));
                break;
            case "pin-version-mismatch": Set(pin, "version", Bump(version)); break;
            case "global-version-mismatch":
                var globalJson = JsonNode.Parse(files["global.json"])!;
                Set(globalJson["sdk"]!, "version", Bump(Value(globalJson["sdk"]!, "version")));
                files["global.json"] = globalJson.ToJsonString();
                break;
            case "missing-rid": Assert.True(archives.Remove(firstRid)); break;
            case "extra-rid":
                var parts = firstRid.Split('-');
                archives.Add($"{parts[0]}-musl-{parts[1]}", first!.DeepClone());
                break;
            case "archives-not-object": Set(pin, "archives", new JsonArray()); break;
            case "archive-not-object": archives[firstRid] = Value(first!, "url"); break;
            case "extra-archive-field": Add(first!, "sha256", digest); break;
            case "missing-hash": Assert.True(first!.AsObject().Remove("sha512")); break;
            case "swapped-urls":
                var url = Value(first!, "url");
                Set(first!, "url", Value(second!, "url"));
                Set(second!, "url", url);
                break;
            case "other-version-url": Set(first!, "url", Value(first!, "url").Replace($"/{version}/", $"/{Bump(version)}/")); break;
            case "http-url": Set(first!, "url", "http://" + Value(first!, "url")["https://".Length..]); break;
            case "other-host-url": Set(first!, "url", Value(first!, "url").Replace("builds.dotnet.microsoft.com", "dotnetcli.azureedge.net")); break;
            case "windows-tarball-url": Set(windows!, "url", Value(windows!, "url").Replace(".zip", ".tar.gz")); break;
            case "uppercase-hash": Set(first!, "sha512", Value(first!, "sha512").ToUpperInvariant()); break;
            case "short-hash": Set(first!, "sha512", Value(first!, "sha512")[..^1]); break;
            case "non-hex-hash": Set(first!, "sha512", "g" + Value(first!, "sha512")[1..]); break;
            case "numeric-hash": Set(first!, "sha512", 512); break;
            case "duplicate-hash": Set(second!, "sha512", Value(first!, "sha512")); break;
            case "metadata-source": Set(pin, "source", $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json"); break;
            case "other-channel-source":
                var otherChannel = (int.Parse(channel.Split('.')[0], CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture) + ".0";
                Set(pin, "source", source.Replace($"/release-notes/{channel}/", $"/release-notes/{otherChannel}/"));
                break;
            case "uncited-source":
                var sources = sdkEntry["maintenance"]!["sources"]!.AsArray();
                Assert.True(sources.Remove(sources.First(cited => (string?)cited == source)));
                break;
            case "missing-ci": Assert.True(files.Remove(CiPath)); break;
            case "ci-without-pin": files[CiPath] = Changed(files[CiPath], files[CiPath].Replace(PinPath, ".github/sdk.json")); break;
            case "setup-dotnet-step": files[CiPath] += "\n      - uses: actions/setup-dotnet@" + new string('a', 40) + "\n"; break;
            case "allowed-setup-dotnet": files[AllowListPath] += "actions/setup-dotnet@" + new string('a', 40) + " # v6.0.0\n"; break;
            case "dotnet-install-script": files[CiPath] += "\n      - run: ./dotnet-install.sh --jsonfile global.json\n"; break;
            case "missing-secret-scan": Assert.True(files.Remove(SecretScanPath)); break;
            case "missing-gitleaks-digest": files[SecretScanPath] = RewriteLine(scan, "GITLEAKS_SHA256", _ => null); break;
            case "duplicate-gitleaks-digest": files[SecretScanPath] = RewriteLine(scan, "GITLEAKS_SHA256", line => line + "\n" + line); break;
            case "uppercase-gitleaks-digest":
                files[SecretScanPath] = RewriteLine(scan, "GITLEAKS_SHA256", line => line.Replace(digest, digest.ToUpperInvariant()));
                break;
            case "short-gitleaks-digest": files[SecretScanPath] = RewriteLine(scan, "GITLEAKS_SHA256", line => line.Replace(digest, digest[..^1])); break;
            case "sha512-gitleaks-digest":
                files[SecretScanPath] = RewriteLine(scan, "GITLEAKS_SHA256", line => line.Replace(digest, Value(first!, "sha512")));
                break;
            case "unused-gitleaks-digest": files[SecretScanPath] = Changed(scan, scan.Replace("${GITLEAKS_SHA256}", "${GITLEAKS_DIGEST}")); break;
            case "lax-gitleaks-check": files[SecretScanPath] = Changed(scan, scan.Replace("sha256sum --check --strict", "sha256sum --check")); break;
            case "missing-gitleaks-version": files[SecretScanPath] = RewriteLine(scan, "GITLEAKS_VERSION", _ => null); break;
            case "gitleaks-version-mismatch":
                var pinned = WorkflowValue(scan.Replace("\r\n", "\n"), "GITLEAKS_VERSION");
                files[SecretScanPath] = RewriteLine(scan, "GITLEAKS_VERSION", line => line.Replace(pinned, Bump(pinned)));
                break;
            case "register-gitleaks-mismatch": Set(gitleaks, "version", Bump(Value(gitleaks, "version"))); break;
            case "missing-register-gitleaks": Assert.True(entries.Remove(gitleaks)); break;
            case "candidate-register-gitleaks": Set(gitleaks, "status", "candidate"); break;
            case "missing-global-json": Assert.True(files.Remove("global.json")); break;
            case "missing-register": Assert.True(files.Remove(RegisterPath)); break;
            case "register-gitleaks-digest-mismatch":
                var recorded = gitleaks["digests"]!.AsArray()[0]!;
                Set(recorded, "digest", "sha256:" + digest[..^1] + (digest[^1] == '0' ? '1' : '0'));
                break;
            case "register-gitleaks-url-mismatch":
                var archive = gitleaks["digests"]!.AsArray()[0]!;
                Set(archive, "url", Value(archive, "url").Replace("_linux_x64.", "_linux_arm64."));
                break;
            case "missing-register-gitleaks-digest": Set(gitleaks, "digests", null); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        if (files.ContainsKey(PinPath))
        {
            files[PinPath] = pinText ?? pin.ToJsonString();
        }

        if (files.ContainsKey(RegisterPath))
        {
            files[RegisterPath] = register.ToJsonString();
        }

        // Compare JSON files by content and every other file by text, so an unchanged mutation fails here.
        var changed = files.Count != original.Count || files.Any(pair => !original.TryGetValue(pair.Key, out var text)
            || Canonical(pair.Key, text) != Canonical(pair.Key, pair.Value));
        Assert.True(changed, $"Mutation {mutation} must change the repository files.");
        Assert.Contains(ToolPinContract.Validate(files), error => error.Contains(expectedDiagnostic, StringComparison.Ordinal));
    }

    private static JsonNode Pin() => JsonNode.Parse(RepositoryFiles.Current[PinPath])!;

    private static JsonObject Archives() => Pin()["archives"]!.AsObject();

    private static string Value(JsonNode node, string field) =>
        (string?)node[field] ?? throw new InvalidOperationException($"The JSON object has no {field}.");

    // Reads a workflow env value such as GITLEAKS_SHA256: '<value>' from a file with normalized line endings.
    private static string WorkflowValue(string workflow, string variable)
    {
        var match = Regex.Match(workflow, $"""(?m)^\s*{variable}\s*:\s*(?<quote>["']?)(?<value>[^"'\s#]*)\k<quote>\s*(?:#.*)?$""");
        Assert.True(match.Success, $"The workflow must define {variable}.");
        return match.Groups["value"].Value;
    }

    // Rewrites or removes the one line that defines a workflow variable, keeping the rest of the file unchanged.
    private static string RewriteLine(string text, string variable, Func<string, string?> rewrite)
    {
        var lines = text.Split('\n').ToList();
        var index = lines.FindIndex(line => Regex.IsMatch(line, $@"^\s*{variable}\s*:"));
        Assert.True(index >= 0, $"The workflow must define {variable}.");
        var replacement = rewrite(lines[index]);
        if (replacement is null)
        {
            lines.RemoveAt(index);
        }
        else
        {
            lines[index] = replacement;
        }

        return Changed(text, string.Join('\n', lines));
    }

    // Sets a field to a different value, so the mutation never silently does nothing.
    private static void Set(JsonNode node, string field, JsonNode? value)
    {
        Assert.NotEqual(node[field]?.ToJsonString(), value?.ToJsonString());
        node[field] = value;
    }

    private static void Add(JsonNode node, string field, JsonNode? value)
    {
        Assert.False(node.AsObject().ContainsKey(field), $"The {field} field must not exist before the mutation.");
        node[field] = value;
    }

    private static string Changed(string before, string after)
    {
        Assert.NotEqual(before, after);
        return after;
    }

    // Raises the last numeric component, which keeps an exact version exact and different.
    private static string Bump(string version)
    {
        var parts = version.Split('.');
        parts[^1] = (int.Parse(parts[^1], CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        return string.Join('.', parts);
    }

    private static string Canonical(string path, string text)
    {
        if (!path.EndsWith(".json", StringComparison.Ordinal))
        {
            return text;
        }

        try
        {
            return JsonNode.Parse(text)?.ToJsonString() ?? "null";
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return text;
        }
    }
}
