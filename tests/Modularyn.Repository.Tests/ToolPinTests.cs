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
    private const string NodePinPath = ToolPinContract.NodePinPath;
    private const string ScriptPath = ToolPinContract.DatabaseScriptPath;
    private const string TestsScriptPath = ToolPinContract.TestsScriptPath;
    private const string FixturePath = ToolPinContract.FixtureDockerfile;

    [Fact]
    public void Tool_pins_agree_with_global_json_the_workflows_and_the_register()
    {
        Assert.Empty(ToolPinContract.Validate(RepositoryFiles.Current));
    }

    [Fact]
    public void Tool_pins_accept_platform_line_endings()
    {
        var files = RepositoryFiles.Current.ToDictionary();
        foreach (var path in new[] { PinPath, CiPath, SecretScanPath, RegisterPath, "global.json", NodePinPath, ScriptPath, TestsScriptPath, FixturePath })
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

    [Fact]
    public void Real_provider_job_runs_the_build_test_steps_it_shares_and_the_pinned_service()
    {
        var jobs = ToolPinContract.Jobs(RepositoryFiles.Current[CiPath]);
        Assert.Equal(new[] { ToolPinContract.BuildTestJob, ToolPinContract.RealProviderJob, ToolPinContract.ContainerJob }, jobs.Keys);
        foreach (var step in ToolPinContract.SharedLinuxSteps)
        {
            var run = ToolPinContract.Run(jobs[ToolPinContract.BuildTestJob], step);
            Assert.False(string.IsNullOrWhiteSpace(run), $"build-test must run '{step}'.");
            Assert.StartsWith("set -euo pipefail\n", run.TrimStart(), StringComparison.Ordinal);
            Assert.Equal(run, ToolPinContract.Run(jobs[ToolPinContract.RealProviderJob], step));
        }

        var entries = JsonNode.Parse(RepositoryFiles.Current[RegisterPath])!["entries"]!.AsArray();
        var postgres = Assert.Single(entries, entry => (string?)entry!["id"] == ToolPinContract.PostgresImageId)!;
        var index = Assert.Single(postgres["digests"]!.AsArray(), digest => (string?)digest!["target"] == "index")!;
        var reference = ToolPinContract.PinnedReference(Value(postgres, "name"), Value(index, "url"), Value(index, "digest"));
        Assert.Equal(new[] { reference }, ToolPinContract.Values(jobs[ToolPinContract.RealProviderJob], "image"));
        Assert.Equal("docker.io/library/postgres:18.6-trixie@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6", reference);
    }

    [Fact]
    public void Node_pin_records_the_registered_archives_for_the_workflow_version()
    {
        var pin = JsonNode.Parse(RepositoryFiles.Current[NodePinPath])!;
        var version = Value(pin, "version");
        Assert.Equal(new[] { version }, ToolPinContract.Values(RepositoryFiles.Current[CiPath], "NODE_VERSION"));
        var entries = JsonNode.Parse(RepositoryFiles.Current[RegisterPath])!["entries"]!.AsArray();
        var node = Assert.Single(entries, entry => (string?)entry!["id"] == "binary:node")!;
        Assert.Equal(("in-use", version), (Value(node, "status"), Value(node, "version")));
        var recorded = node["digests"]!.AsArray().ToDictionary(digest => Value(digest!, "target"), digest => (Value(digest!, "url"), Value(digest!, "digest")));
        var pinned = pin["archives"]!.AsObject().ToDictionary(archive => archive.Key, archive => (Value(archive.Value!, "url"), "sha256:" + Value(archive.Value!, "sha256")));
        Assert.Equal(recorded.OrderBy(pair => pair.Key, StringComparer.Ordinal), pinned.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("FROM {0} AS base\nFROM base AS final\nCOPY --from=base /app /app\n")]
    [InlineData("FROM --platform=linux/amd64 {0} AS Build\nRUN --mount=type=cache,from=build,target=/cache true\nFROM {0}\nCOPY --from=0 /a /a\n")]
    [InlineData("# A comment that mentions from=elsewhere is ignored.\r\nfrom {0}\r\n")]
    [InlineData("FROM {0}\nADD --checksum=sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef https://example.com/tool.tar.gz /opt/tool.tar.gz\n")]
    [InlineData("FROM {0}\nADD app.tar.gz /app/\nadd --chown=1654 config /app/config\n")]
    public void Dockerfiles_on_the_pinned_base_and_its_stages_are_accepted(string dockerfile)
    {
        const string pinned = "mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee";
        Assert.Empty(ToolPinContract.DockerfileErrors("Dockerfile", string.Format(CultureInfo.InvariantCulture, dockerfile, pinned), pinned));
    }

    [Theory]
    [InlineData("Dockerfile", true)]
    [InlineData("src/Tooling/Sample/Dockerfile", true)]
    [InlineData("deploy/Dockerfile.release", true)]
    [InlineData("deploy/app.dockerfile", true)]
    [InlineData("deploy/Containerfile", true)]
    [InlineData("src/Tooling/Sample/dockerfile", true)]
    [InlineData("deploy/DOCKERFILE.prod", true)]
    [InlineData("deploy/App.DockerFile", true)]
    [InlineData("deploy/containerfile", true)]
    [InlineData("docs/dockerfiles.md", false)]
    [InlineData(".dockerignore", false)]
    public void Dockerfiles_are_found_as_the_container_build_job_finds_them(string path, bool expected)
    {
        Assert.Equal(expected, ToolPinContract.IsDockerfile(path));
    }

    [Theory]
    [InlineData("missing-node-pin", "the Node.js archive pin file is missing")]
    [InlineData("invalid-node-pin", "must contain valid JSON")]
    [InlineData("node-pin-version-mismatch", "must equal the workflow's NODE_VERSION")]
    [InlineData("node-workflow-version-mismatch", "must equal the workflow's NODE_VERSION")]
    [InlineData("duplicate-node-version", "must pin NODE_VERSION exactly once")]
    [InlineData("node-register-candidate", "needs exactly one in-use binary:node entry")]
    [InlineData("node-register-version-mismatch", "must equal the register's in-use binary:node version")]
    [InlineData("node-other-source", "source must be the release's")]
    [InlineData("node-uncited-source", "must cite the source")]
    [InlineData("missing-node-target", "archives must name exactly")]
    [InlineData("extra-node-target", "archives must name exactly")]
    [InlineData("node-other-host", "url must be the official Node.js archive")]
    [InlineData("node-windows-tarball", "url must be the official Node.js archive")]
    [InlineData("node-uppercase-hash", "sha256 must be 64 lowercase hexadecimal characters")]
    [InlineData("node-sha512-field", "fields must be exactly url, sha256")]
    [InlineData("node-duplicate-hash", "duplicates the hash of")]
    [InlineData("node-register-digest-mismatch", "must equal the register's binary:node")]
    [InlineData("ci-without-node-pin", "must install Node.js from the archives pinned in")]
    [InlineData("unpinned-service-image", "must be <registry>/<repository>:<tag>@sha256:<index digest>")]
    [InlineData("other-service-digest", "only service image must be the register's")]
    [InlineData("postgres-image-env-mismatch", "POSTGRES_IMAGE must equal its service image")]
    [InlineData("postgres-register-candidate", "needs exactly one in-use container-image:docker.io/library/postgres entry")]
    [InlineData("inline-container", "give a job container as a mapping")]
    [InlineData("aspnet-image-env-mismatch", "ASPNET_IMAGE must be the register's")]
    [InlineData("aspnet-register-candidate", "needs exactly one in-use container-image:mcr.microsoft.com/dotnet/aspnet entry")]
    [InlineData("missing-fixture", "the container-build fixture is missing")]
    [InlineData("dockerfile-other-base", "or an earlier stage")]
    [InlineData("dockerfile-unpinned-base", "or an earlier stage")]
    [InlineData("dockerfile-arg-base", "or an earlier stage")]
    [InlineData("dockerfile-syntax-directive", "a syntax directive pulls a frontend image")]
    [InlineData("dockerfile-copy-from-image", "is not an earlier stage")]
    [InlineData("dockerfile-mount-from-image", "is not an earlier stage")]
    [InlineData("dockerfile-later-stage", "is not an earlier stage")]
    [InlineData("dockerfile-other-platform", "the only FROM option allowed is --platform=linux/amd64")]
    [InlineData("dockerfile-continued-from", "keep each FROM instruction on one line")]
    [InlineData("dockerfile-extra-from-words", "FROM takes an image and an optional AS <stage>")]
    [InlineData("dockerfile-without-from", "a Dockerfile needs a FROM instruction")]
    [InlineData("second-dockerfile-other-base", "or an earlier stage")]
    [InlineData("lowercase-dockerfile-other-base", "or an earlier stage")]
    [InlineData("dockerfile-add-url", "ADD of a remote or variable source needs exactly one source and --checksum")]
    [InlineData("dockerfile-add-git", "ADD of a remote or variable source needs exactly one source and --checksum")]
    [InlineData("dockerfile-add-uppercase-checksum", "ADD of a remote or variable source needs exactly one source and --checksum")]
    [InlineData("dockerfile-add-two-remote", "ADD of a remote or variable source needs exactly one source and --checksum")]
    [InlineData("dockerfile-add-variable", "ADD of a remote or variable source needs exactly one source and --checksum")]
    [InlineData("dockerfile-onbuild-add-url", "ADD of a remote or variable source needs exactly one source and --checksum")]
    [InlineData("dockerfile-add-continued", "keep each ADD instruction on one line")]
    [InlineData("missing-real-provider-job", "the real-provider job is missing")]
    [InlineData("real-provider-on-windows", "real-provider job must run on ubuntu-latest")]
    [InlineData("service-host-trust", "must start with POSTGRES_HOST_AUTH_METHOD reject")]
    [InlineData("missing-health-check", "needs a pg_isready health check")]
    [InlineData("postgresql-version-mismatch", "POSTGRESQL_VERSION must equal")]
    [InlineData("postgresql-register-candidate", "POSTGRESQL_VERSION must equal")]
    [InlineData("database-step-other-command", "step must come after its Build step and run")]
    [InlineData("database-before-build", "step must come after its Build step and run")]
    [InlineData("database-in-another-step", "may run .github/scripts/postgresql-test-database.sh, so the credentials exist in no other step")]
    [InlineData("database-to-github-env", "never to GITHUB_ENV or GITHUB_OUTPUT")]
    [InlineData("unmasked-password", "must generate the password from /dev/urandom, mask it")]
    [InlineData("missing-database-script", "must generate the password from /dev/urandom, mask it")]
    [InlineData("real-provider-direct-test", "every dotnet test in the real-provider job must select its tests")]
    [InlineData("tests-script-unfiltered", "must run dotnet test with --filter \"Requires=PostgreSQL\", and only with it")]
    [InlineData("tests-script-unit-filter", "must run dotnet test with --filter \"Requires=PostgreSQL\", and only with it")]
    [InlineData("missing-tests-script", "must run dotnet test with --filter \"Requires=PostgreSQL\", and only with it")]
    [InlineData("tests-script-without-minimum", "require at least one PostgreSQL test to have run")]
    [InlineData("tests-script-allows-failures", "require at least one PostgreSQL test to have run")]
    [InlineData("tests-script-shared-results", "require at least one PostgreSQL test to have run")]
    [InlineData("tests-script-some-projects", "require at least one PostgreSQL test to have run")]
    [InlineData("build-test-unfiltered", "every dotnet test in the build-test job must select its tests")]
    [InlineData("build-test-real-provider-filter", "every dotnet test in the build-test job must select its tests")]
    [InlineData("build-test-without-tests", "the build-test job must run dotnet test")]
    [InlineData("diverged-shared-step", "exactly as build-test does")]
    [InlineData("missing-shared-step", "exactly as build-test does")]
    [InlineData("missing-container-job", "the container-build job is missing")]
    [InlineData("container-on-macos", "container-build job must run on ubuntu-latest")]
    [InlineData("container-other-platform", "must build for linux/amd64")]
    [InlineData("container-other-fixture", "must build tests/Fixtures/container/Dockerfile")]
    [InlineData("playwright-install-step", "must not install Playwright browsers")]
    [InlineData("playwright-install-script", "must not install Playwright browsers")]
    [InlineData("unpinned-docker-run", "uses docker.io/library/alpine:3.22, which is not digest-pinned")]
    [InlineData("unpinned-docker-pull-script", "uses postgres:18, which is not digest-pinned")]
    [InlineData("unpinned-container-run-script", "uses alpine, which is not digest-pinned")]
    [InlineData("space-form-option", "uses none, which is not digest-pinned")]
    [InlineData("local-tag-without-pull-never", "uses modularyn-fixture:ci, which is not digest-pinned or a local tag")]
    [InlineData("pull-local-tag", "uses modularyn-fixture:ci, which is not digest-pinned.")]
    [InlineData("unresolved-image-variable", "names an image that this check cannot resolve")]
    [InlineData("computed-image", "names an image that this check cannot resolve")]
    [InlineData("image-from-github-env", "names an image that this check cannot resolve")]
    [InlineData("build-from-stdin", "must name exactly one tracked Dockerfile with --file=")]
    [InlineData("build-untracked-dockerfile", "must name exactly one tracked Dockerfile with --file=")]
    [InlineData("build-remote-context", "must name a literal local context")]
    [InlineData("build-stdin-context", "must name a literal local context")]
    [InlineData("build-image-context", "docker-image:// context only as a digest-pinned reference")]
    public void A_single_ci_pin_violation_is_rejected(string mutation, string expectedDiagnostic)
    {
        // Every mutation derives its values from the current files, so a later pin update cannot turn it into a no-op.
        var original = RepositoryFiles.Current;
        var files = original.ToDictionary();
        Assert.Empty(ToolPinContract.Validate(files));
        var pin = JsonNode.Parse(files[NodePinPath])!;
        var archives = pin["archives"]!.AsObject();
        var (firstTarget, first) = archives.First();
        var (_, second) = archives.Skip(1).First();
        var (_, windows) = archives.First(archive => archive.Key.StartsWith("win-", StringComparison.Ordinal));
        var register = JsonNode.Parse(files[RegisterPath])!;
        var entries = register["entries"]!.AsArray();
        JsonNode Entry(string id) => entries.Single(entry => (string?)entry!["id"] == id)!;
        var node = Entry("binary:node");
        var postgres = Entry(ToolPinContract.PostgresImageId);
        var aspnet = Entry(ToolPinContract.AspNetImageId);
        var postgresReference = Reference(postgres);
        var aspnetReference = Reference(aspnet);
        var ci = files[CiPath];
        var fixture = files[FixturePath];
        string? pinText = null;
        switch (mutation)
        {
            case "missing-node-pin": Assert.True(files.Remove(NodePinPath)); break;
            case "invalid-node-pin": pinText = files[NodePinPath].TrimEnd()[..^1]; break;
            case "node-pin-version-mismatch": Set(pin, "version", Bump(Value(pin, "version"))); break;
            case "node-workflow-version-mismatch":
                files[CiPath] = RewriteLine(ci, "NODE_VERSION", line => line.Replace(Value(pin, "version"), Bump(Value(pin, "version"))));
                break;
            case "duplicate-node-version": files[CiPath] = RewriteLine(ci, "NODE_VERSION", line => line + "\n" + line); break;
            case "node-register-candidate": Set(node, "status", "candidate"); break;
            case "node-register-version-mismatch": Set(node, "version", Bump(Value(node, "version"))); break;
            case "node-other-source": Set(pin, "source", Value(pin, "source").Replace("SHASUMS256.txt", "SHASUMS256.txt.asc")); break;
            case "node-uncited-source":
                var sources = node["maintenance"]!["sources"]!.AsArray();
                Assert.True(sources.Remove(sources.First(source => (string?)source == Value(pin, "source"))));
                break;
            case "missing-node-target": Assert.True(archives.Remove(firstTarget)); break;
            case "extra-node-target": archives.Add(firstTarget.Split('-')[0] + "-ppc64le", first!.DeepClone()); break;
            case "node-other-host": Set(first!, "url", Value(first!, "url").Replace("nodejs.org", "mirror.example")); break;
            case "node-windows-tarball": Set(windows!, "url", Value(windows!, "url").Replace(".zip", ".tar.gz")); break;
            case "node-uppercase-hash": Set(first!, "sha256", Value(first!, "sha256").ToUpperInvariant()); break;
            case "node-sha512-field":
                first!["sha512"] = Value(first!, "sha256");
                Assert.True(first.AsObject().Remove("sha256"));
                break;
            case "node-duplicate-hash": Set(second!, "sha256", Value(first!, "sha256")); break;
            case "node-register-digest-mismatch":
                var recorded = node["digests"]!.AsArray().First(digest => (string?)digest!["target"] == firstTarget)!;
                var digest = Value(recorded, "digest");
                Set(recorded, "digest", digest[..^1] + (digest[^1] == '0' ? '1' : '0'));
                break;
            case "ci-without-node-pin": files[CiPath] = Changed(ci, ci.Replace(NodePinPath, ".github/node-pin.json", StringComparison.Ordinal)); break;
            case "unpinned-service-image":
                files[CiPath] = ReplaceLine(ci, "        image: " + postgresReference, "        image: " + postgresReference.Split('@')[0]);
                break;
            case "other-service-digest":
                files[CiPath] = ReplaceLine(ci, "        image: " + postgresReference, "        image: " + Flip(postgresReference));
                break;
            case "postgres-image-env-mismatch":
                files[CiPath] = ReplaceLine(ci, "      POSTGRES_IMAGE: " + postgresReference, "      POSTGRES_IMAGE: " + Flip(postgresReference));
                break;
            case "postgres-register-candidate": Set(postgres, "status", "candidate"); break;
            case "inline-container":
                files[CiPath] = ReplaceLine(ci, "    timeout-minutes: 15", "    timeout-minutes: 15\n    container: " + aspnetReference, firstOnly: true);
                break;
            case "aspnet-image-env-mismatch":
                files[CiPath] = ReplaceLine(ci, "      ASPNET_IMAGE: " + aspnetReference, "      ASPNET_IMAGE: " + Flip(aspnetReference));
                break;
            case "aspnet-register-candidate": Set(aspnet, "status", "candidate"); break;
            case "missing-fixture": Assert.True(files.Remove(FixturePath)); break;
            case "dockerfile-other-base":
                files[FixturePath] = Changed(fixture, fixture.Replace("/dotnet/aspnet:", "/dotnet/runtime:", StringComparison.Ordinal));
                break;
            case "dockerfile-unpinned-base":
                files[FixturePath] = Changed(fixture, fixture.Replace(aspnetReference, aspnetReference.Split('@')[0], StringComparison.Ordinal));
                break;
            case "dockerfile-arg-base":
                files[FixturePath] = Changed(fixture, fixture.Replace("FROM " + aspnetReference, "ARG BASE=" + aspnetReference + "\nFROM ${BASE}",
                    StringComparison.Ordinal));
                break;
            case "dockerfile-syntax-directive": files[FixturePath] = "# syntax=docker/dockerfile:1\n" + fixture; break;
            case "dockerfile-copy-from-image": files[FixturePath] = fixture + "COPY --from=docker.io/library/busybox:1 /bin/busybox /busybox\n"; break;
            case "dockerfile-mount-from-image": files[FixturePath] = fixture + "RUN --mount=type=bind,from=alpine,target=/opt/alpine true\n"; break;
            case "dockerfile-later-stage": files[FixturePath] = fixture + "COPY --from=later /a /a\nFROM " + aspnetReference + " AS later\n"; break;
            case "dockerfile-other-platform":
                files[FixturePath] = Changed(fixture, fixture.Replace("FROM " + aspnetReference, "FROM --platform=linux/arm64 " + aspnetReference,
                    StringComparison.Ordinal));
                break;
            case "dockerfile-continued-from":
                files[FixturePath] = Changed(fixture, fixture.Replace("FROM " + aspnetReference, "FROM \\\n  " + aspnetReference, StringComparison.Ordinal));
                break;
            case "dockerfile-extra-from-words":
                files[FixturePath] = Changed(fixture, fixture.Replace("FROM " + aspnetReference, "FROM " + aspnetReference + " AS base extra",
                    StringComparison.Ordinal));
                break;
            case "dockerfile-without-from":
                files[FixturePath] = Changed(fixture, string.Join('\n', fixture.Split('\n').Where(line => !line.StartsWith("FROM ", StringComparison.Ordinal))));
                break;
            case "second-dockerfile-other-base":
                files["src/Tooling/Sample/Dockerfile"] = "FROM docker.io/library/alpine:3.22@sha256:" + new string('a', 64) + "\n";
                break;
            case "lowercase-dockerfile-other-base":
                files["src/Tooling/Sample/dockerfile"] = "FROM docker.io/library/alpine:3.22@sha256:" + new string('a', 64) + "\n";
                break;
            case "dockerfile-add-url": files[FixturePath] = fixture + "ADD https://example.com/tool.tar.gz /opt/tool.tar.gz\n"; break;
            case "dockerfile-add-git": files[FixturePath] = fixture + "ADD git@github.com:example/tool.git /opt/tool\n"; break;
            case "dockerfile-add-uppercase-checksum":
                files[FixturePath] = fixture + "ADD --checksum=sha256:" + new string('A', 64) + " https://example.com/tool.tar.gz /opt/\n";
                break;
            case "dockerfile-add-two-remote":
                files[FixturePath] = fixture + "ADD --checksum=sha256:" + new string('a', 64) + " https://example.com/a https://example.com/b /opt/\n";
                break;
            case "dockerfile-add-variable": files[FixturePath] = fixture + "ARG TOOL\nADD ${TOOL} /opt/tool\n"; break;
            case "dockerfile-onbuild-add-url": files[FixturePath] = fixture + "ONBUILD ADD https://example.com/tool.tar.gz /opt/\n"; break;
            case "dockerfile-add-continued": files[FixturePath] = fixture + "ADD \\\n  https://example.com/tool.tar.gz /opt/\n"; break;
            case "missing-real-provider-job": files[CiPath] = ReplaceLine(ci, "  real-provider:", "  integration:"); break;
            case "real-provider-on-windows": files[CiPath] = ReplaceInJob(ci, ToolPinContract.RealProviderJob, "runs-on: ubuntu-latest", "runs-on: windows-latest"); break;
            case "service-host-trust": files[CiPath] = ReplaceLine(ci, "          POSTGRES_HOST_AUTH_METHOD: reject", "          POSTGRES_HOST_AUTH_METHOD: trust"); break;
            case "missing-health-check": files[CiPath] = ReplaceInJob(ci, ToolPinContract.RealProviderJob, "\"pg_isready ", "\"true "); break;
            case "postgresql-version-mismatch":
                files[CiPath] = RewriteLine(ci, "POSTGRESQL_VERSION", line => line.Replace(Value(postgres, "version"), Bump(Value(postgres, "version"))));
                break;
            case "postgresql-register-candidate": Set(Entry("binary:postgresql"), "status", "candidate"); break;
            case "database-step-other-command":
                files[CiPath] = ReplaceLine(ci, "        run: " + ToolPinContract.RealProviderTestRun,
                    $"        run: bash {ScriptPath} ci dotnet test -c Release --no-build " + ToolPinContract.RealProviderFilter);
                break;
            case "database-before-build":
                var testStep = ci[ci.IndexOf("      - name: " + ToolPinContract.RealProviderTestStep, StringComparison.Ordinal)..];
                testStep = testStep[..(testStep.IndexOf("\n\n", StringComparison.Ordinal) + 1)];
                var withoutTest = ReplaceInJob(ci, ToolPinContract.RealProviderJob, testStep, "");
                files[CiPath] = ReplaceInJob(withoutTest, ToolPinContract.RealProviderJob, "      - name: Restore locked dependencies\n",
                    testStep + "      - name: Restore locked dependencies\n");
                break;
            case "database-in-another-step":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.RealProviderJob, "      - name: Restore locked dependencies",
                    $"      - name: Prepare\n        run: bash {ScriptPath} ci true\n      - name: Restore locked dependencies");
                break;
            case "database-to-github-env":
                files[ScriptPath] = Changed(files[ScriptPath], files[ScriptPath].Replace("  local status\n",
                    "  echo \"MODULARYN_TEST_DATABASE=${name}\" >> \"${GITHUB_ENV}\"\n  local status\n", StringComparison.Ordinal));
                break;
            case "unmasked-password":
                files[ScriptPath] = Changed(files[ScriptPath], files[ScriptPath].Replace("echo \"::add-mask::${password}\"", "true", StringComparison.Ordinal));
                break;
            case "missing-database-script": Assert.True(files.Remove(ScriptPath)); break;
            case "real-provider-direct-test":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.RealProviderJob, "      - name: Restore locked dependencies",
                    "      - name: Test everything\n        run: dotnet test -c Release --no-build\n      - name: Restore locked dependencies");
                break;
            case "tests-script-unfiltered": files[TestsScriptPath] = ReplaceText(files[TestsScriptPath], " " + ToolPinContract.RealProviderFilter, ""); break;
            case "tests-script-unit-filter":
                files[TestsScriptPath] = ReplaceText(files[TestsScriptPath], ToolPinContract.RealProviderFilter, ToolPinContract.UnitFilter);
                break;
            case "missing-tests-script": Assert.True(files.Remove(TestsScriptPath)); break;
            case "tests-script-without-minimum": files[TestsScriptPath] = ReplaceText(files[TestsScriptPath], "[ \"${passed}\" -ge 1 ]", "true"); break;
            case "tests-script-allows-failures":
                files[TestsScriptPath] = ReplaceText(files[TestsScriptPath], "[ \"${passed}\" -eq \"${total}\" ]", "true");
                break;
            case "tests-script-shared-results":
                files[TestsScriptPath] = ReplaceText(files[TestsScriptPath], "LogFileName=${name}.trx", "LogFileName=results.trx");
                break;
            case "tests-script-some-projects":
                files[TestsScriptPath] = ReplaceText(files[TestsScriptPath], "'tests/*/*.Tests.csproj'", "'tests/Modularyn.Repository.Tests/*.csproj'");
                break;
            case "build-test-unfiltered": files[CiPath] = ReplaceInJob(ci, ToolPinContract.BuildTestJob, " " + ToolPinContract.UnitFilter, ""); break;
            case "build-test-real-provider-filter":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.BuildTestJob, ToolPinContract.UnitFilter, ToolPinContract.RealProviderFilter);
                break;
            case "build-test-without-tests":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.BuildTestJob, "run: dotnet test -c Release --no-build " + ToolPinContract.UnitFilter,
                    "run: echo no tests");
                break;
            case "diverged-shared-step": files[CiPath] = ReplaceInJob(ci, ToolPinContract.RealProviderJob, "--retry 3", "--retry 1"); break;
            case "missing-shared-step":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.RealProviderJob, "- name: " + ToolPinContract.SharedLinuxSteps[^1], "- name: Verify Node.js");
                break;
            case "missing-container-job": files[CiPath] = ReplaceLine(ci, "  container-build:", "  image-build:"); break;
            case "container-on-macos": files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "runs-on: ubuntu-latest", "runs-on: macos-latest"); break;
            case "container-other-platform":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "docker build --platform=linux/amd64 ", "docker build --platform=linux/arm64 ");
                break;
            case "container-other-fixture":
                files[CiPath] = ReplaceLine(ci, "      FIXTURE_DOCKERFILE: " + FixturePath, "      FIXTURE_DOCKERFILE: tests/Fixtures/other/Dockerfile");
                break;
            case "playwright-install-step":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.RealProviderJob, "      - name: Restore locked dependencies",
                    "      - name: Install browsers\n        run: pwsh playwright.ps1 install --with-deps chromium\n      - name: Restore locked dependencies");
                break;
            case "playwright-install-script": files[".github/scripts/browsers.sh"] = "#!/usr/bin/env bash\nnpx playwright install chromium\n"; break;
            case "unpinned-docker-run":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "          printf '%s\\n' \"${reported}\"\n",
                    "          printf '%s\\n' \"${reported}\"\n          docker run --rm --pull=never docker.io/library/alpine:3.22 true\n");
                break;
            case "unpinned-docker-pull-script": files[".github/scripts/images.sh"] = "#!/usr/bin/env bash\ndocker pull postgres:18\n"; break;
            case "unpinned-container-run-script":
                files[".github/scripts/images.sh"] = "#!/usr/bin/env bash\nresult=\"$(docker container run --rm alpine true)\"\n";
                break;
            case "space-form-option": files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "--network=none", "--network none"); break;
            case "local-tag-without-pull-never": files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "--pull=never ", ""); break;
            case "pull-local-tag":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "          printf '%s\\n' \"${reported}\"\n",
                    "          printf '%s\\n' \"${reported}\"\n          docker pull \"${FIXTURE_IMAGE}\"\n");
                break;
            case "unresolved-image-variable":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "          printf '%s\\n' \"${reported}\"\n",
                    "          printf '%s\\n' \"${reported}\"\n          docker run --rm \"${OTHER_IMAGE}\" true\n");
                break;
            case "computed-image":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "          printf '%s\\n' \"${reported}\"\n",
                    "          printf '%s\\n' \"${reported}\"\n          docker run --rm \"$(cat image.txt)\" true\n");
                break;
            case "image-from-github-env":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "          set -euo pipefail\n",
                    "          set -euo pipefail\n          echo \"FIXTURE_IMAGE=docker.io/library/alpine:3\" >> \"${GITHUB_ENV}\"\n");
                break;
            case "build-from-stdin":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "--file=\"${FIXTURE_DOCKERFILE}\"", "--file=-");
                break;
            case "build-untracked-dockerfile":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "--file=\"${FIXTURE_DOCKERFILE}\"", "--file=tests/Fixtures/other/Dockerfile");
                break;
            case "build-remote-context":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "\" tests/Fixtures/container\n",
                    "\" https://github.com/Modularyn/modularyn.git\n");
                break;
            case "build-stdin-context": files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "\" tests/Fixtures/container\n", "\" -\n"); break;
            case "build-image-context":
                files[CiPath] = ReplaceInJob(ci, ToolPinContract.ContainerJob, "--no-cache ",
                    "--no-cache --build-context=base=docker-image://docker.io/library/alpine:3 ");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        if (files.ContainsKey(NodePinPath))
        {
            files[NodePinPath] = pinText ?? pin.ToJsonString();
        }

        files[RegisterPath] = register.ToJsonString();

        // Compare JSON files by content and every other file by text, so an unchanged mutation fails here.
        var changed = files.Count != original.Count || files.Any(pair => !original.TryGetValue(pair.Key, out var text)
            || Canonical(pair.Key, text) != Canonical(pair.Key, pair.Value));
        Assert.True(changed, $"Mutation {mutation} must change the repository files.");
        Assert.Contains(ToolPinContract.Validate(files), error => error.Contains(expectedDiagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("docker pull --platform=linux/amd64 docker.io/library/postgres:18.6-trixie@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6")]
    [InlineData("docker build --pull --tag=local-image:1 --file=deploy/Dockerfile deploy\ndocker run --rm --pull=never local-image:1")]
    [InlineData("image=\"${PINNED}\"\nresult=\"$(docker container run --rm --network=none \"${image}\")\" || exit 1")]
    [InlineData("docker image pull \\\n  \"${PINNED}\" > /dev/null")]
    [InlineData("docker inspect --format '{{.Image}}' local-image:1\ndocker exec -i container psql")]
    public void Docker_commands_with_pinned_or_built_images_are_accepted(string script)
    {
        var workflow = string.Join('\n', "env:",
            "  PINNED: docker.io/library/postgres:18.6-trixie@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6",
            "jobs:", "  job:", "    steps:", "      - name: Use images", "        run: |") + "\n"
            + string.Join('\n', script.Split('\n').Select(line => "          " + line)) + "\n";
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".github/workflows/images.yml"] = workflow,
            ["deploy/Dockerfile"] = "FROM docker.io/library/postgres:18.6-trixie@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6\n"
        };
        Assert.Empty(ToolPinContract.DockerCommandErrors(files));
    }

    private static string ReplaceText(string text, string value, string replacement)
    {
        Assert.Contains(value, text, StringComparison.Ordinal);
        return Changed(text, text.Replace(value, replacement, StringComparison.Ordinal));
    }

    private static string Reference(JsonNode image)
    {
        var index = image["digests"]!.AsArray().Single(digest => (string?)digest!["target"] == "index")!;
        return ToolPinContract.PinnedReference(Value(image, "name"), Value(index, "url"), Value(index, "digest"))
            ?? throw new InvalidOperationException("The image entry has no index fetched by tag.");
    }

    // Changes the last hexadecimal digit of a digest-pinned reference, which keeps it well formed and different.
    private static string Flip(string reference) => reference[..^1] + (reference[^1] == '0' ? '1' : '0');

    // Replaces one exact line of a file, or the first of several when firstOnly is set.
    private static string ReplaceLine(string text, string line, string replacement, bool firstOnly = false)
    {
        var lines = text.Split('\n').ToList();
        var matches = lines.Select((value, index) => (value, index)).Where(pair => pair.value.TrimEnd('\r') == line).ToList();
        Assert.True(firstOnly ? matches.Count >= 1 : matches.Count == 1, $"The file must contain the line '{line}' {(firstOnly ? "at least" : "exactly")} once.");
        lines[matches[0].index] = replacement;
        return Changed(text, string.Join('\n', lines));
    }

    // Replaces the first occurrence of a text within one job of the CI workflow.
    private static string ReplaceInJob(string workflow, string job, string value, string replacement)
    {
        var start = workflow.IndexOf($"\n  {job}:\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The workflow must define the {job} job.");
        var next = Regex.Match(workflow[(start + 1)..], @"\n  [A-Za-z_][A-Za-z0-9_-]*:\n");
        var end = next.Success ? start + 1 + next.Index : workflow.Length;
        var at = workflow.IndexOf(value, start, end - start, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The {job} job must contain '{value}'.");
        return Changed(workflow, workflow[..at] + replacement + workflow[(at + value.Length)..]);
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
