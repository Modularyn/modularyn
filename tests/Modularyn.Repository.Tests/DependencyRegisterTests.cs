using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class DependencyRegisterTests
{
    private const string RegisterPath = DependencyRegister.RegisterPath;
    private const string ReadmePath = DependencyRegister.ReadmePath;
    private const string LockPath = "tests/Modularyn.Repository.Tests/packages.lock.json";

    [Fact]
    public void Register_agrees_with_the_repository_pins()
    {
        Assert.True(RepositoryFiles.Current.TryGetValue(RegisterPath, out _), "The dependency register must exist.");
        Assert.Empty(DependencyRegister.Validate(RepositoryFiles.Current));
    }

    [Fact]
    public void Register_accepts_platform_line_endings()
    {
        var files = RepositoryFiles.Current.ToDictionary();
        foreach (var path in new[] { RegisterPath, ReadmePath })
        {
            files[path] = files[path].Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        Assert.Empty(DependencyRegister.Validate(files));
    }

    [Theory]
    [InlineData("missing-register", "dependency register is missing")]
    [InlineData("missing-field", "fields must be exactly")]
    [InlineData("extra-field", "fields must be exactly")]
    [InlineData("missing-maintenance-field", "fields must be exactly")]
    [InlineData("extra-clearance-field", "fields must be exactly")]
    [InlineData("nonobject-maintenance", "maintenance must be an object")]
    [InlineData("duplicate-id", "duplicate id")]
    [InlineData("wrong-id", "lowercase ecosystem and name")]
    [InlineData("invalid-name", "name must be a package")]
    [InlineData("unknown-ecosystem", "ecosystem must be")]
    [InlineData("unknown-status", "status must be")]
    [InlineData("unknown-area", "area must be")]
    [InlineData("range-version", "version must be exact")]
    [InlineData("floating-version", "version must be exact")]
    [InlineData("wildcard-version", "version must be exact")]
    [InlineData("caret-version", "version must be exact")]
    [InlineData("prerelease-version", "version must be exact")]
    [InlineData("short-action-sha", "version must be exact")]
    [InlineData("tagged-action", "version must be exact")]
    [InlineData("numeric-version", "nonempty single-line string")]
    [InlineData("inexact-latest", "latestEligible must be an exact version")]
    [InlineData("sdk-mismatch", "must match global.json")]
    [InlineData("missing-sdk-entry", "must match global.json")]
    [InlineData("second-sdk-entry", "must match global.json")]
    [InlineData("package-version-mismatch", "needs an in-use nuget entry")]
    [InlineData("unregistered-package", "needs an in-use nuget entry")]
    [InlineData("candidate-package", "needs an in-use nuget entry")]
    [InlineData("lock-mismatch", "resolved version")]
    [InlineData("unregistered-lock-package", "resolved version")]
    [InlineData("stale-nuget-entry", "absent from every lock file")]
    [InlineData("unregistered-action", "needs an in-use github-action entry")]
    [InlineData("unregistered-allowed-action", "needs an in-use github-action entry")]
    [InlineData("action-sha-mismatch", "needs an in-use github-action entry")]
    [InlineData("unused-action-entry", "absent from the workflows")]
    [InlineData("tool-version-mismatch", "needs an in-use binary entry")]
    [InlineData("unregistered-tool-version", "needs an in-use binary entry")]
    [InlineData("stale-binary-entry", "must match a workflow")]
    [InlineData("unpinned-without-note", "record in unpinned")]
    [InlineData("needless-unpinned", "unpinned must be null")]
    [InlineData("pinned-binary-note", "unpinned must be null")]
    [InlineData("in-use-npm", "lock-file check")]
    [InlineData("unlisted-license", "must be flagged for both distributions")]
    [InlineData("partly-flagged-license", "must be flagged for both distributions")]
    [InlineData("invalid-license", "SPDX license expression")]
    [InlineData("unknown-clearance", "cleared or flagged")]
    [InlineData("empty-note", "nonempty single-line string")]
    [InlineData("widened-allowlist", "allowlist must be exactly")]
    [InlineData("missing-allowlist", "allowlist must be exactly")]
    [InlineData("candidate-not-latest", "candidate version must equal its latestEligible")]
    [InlineData("missing-age-exception", "requires an ageException")]
    [InlineData("needless-age-exception", "ageException must be null")]
    [InlineData("future-published", "on or before reviewedAt")]
    [InlineData("invalid-date", "on or before reviewedAt")]
    [InlineData("early-review", "reviewedAt must be an ISO date")]
    [InlineData("http-source", "unique https URLs")]
    [InlineData("duplicate-source", "unique https URLs")]
    [InlineData("empty-sources", "unique https URLs")]
    [InlineData("machine-path", "text must be portable")]
    [InlineData("multiline-text", "nonempty single-line string")]
    [InlineData("long-text", "at most 600 characters")]
    [InlineData("unsorted", "sorted by status, ecosystem and id")]
    [InlineData("null-entry", "entry must be an object")]
    [InlineData("null-entries", "entries must be an array")]
    [InlineData("wrong-version", "version must be 1")]
    [InlineData("extra-root-field", "fields must be exactly")]
    public void A_single_register_violation_is_rejected(string mutation, string expectedDiagnostic)
    {
        // Every mutation derives its values from the current register and files, so a later review cannot turn it into a no-op.
        var original = RepositoryFiles.Current;
        var files = original.ToDictionary();
        var register = JsonNode.Parse(files[RegisterPath])!;
        var entries = register["entries"]!.AsArray();
        var sdk = First(entries, entry => Value(entry, "ecosystem") == "dotnet-sdk");
        var nuget = First(entries, entry => Value(entry, "ecosystem") == "nuget" && Value(entry, "status") == "in-use");
        var action = First(entries, entry => Value(entry, "ecosystem") == "github-action" && Value(entry, "status") == "in-use");
        var maintenance = nuget["maintenance"]!;
        var reviewedAt = Value(maintenance, "reviewedAt");
        var version = Value(nuget, "version");
        var sha = Value(action, "version");
        switch (mutation)
        {
            case "missing-register": Assert.True(files.Remove(RegisterPath)); break;
            case "missing-field": Assert.True(nuget.AsObject().Remove("license")); break;
            case "extra-field": Add(nuget, "notes", "Additional prose."); break;
            case "missing-maintenance-field": Assert.True(maintenance.AsObject().Remove("sources")); break;
            case "extra-clearance-field": Add(nuget["clearance"]!, "approvedBy", "Reviewer"); break;
            case "nonobject-maintenance": Set(nuget, "maintenance", "Reviewed."); break;
            case "duplicate-id": entries.Add(nuget.DeepClone()); break;
            case "wrong-id": Set(nuget, "id", Value(nuget, "id") + "-other"); break;
            case "invalid-name":
                Set(nuget, "name", Value(nuget, "name") + " copy");
                Set(nuget, "id", $"nuget:{Value(nuget, "name")}".ToLowerInvariant());
                break;
            case "unknown-ecosystem": Set(nuget, "ecosystem", "pypi"); break;
            case "unknown-status": Set(nuget, "status", "adopted"); break;
            case "unknown-area": Set(nuget, "area", "frontend"); break;
            case "range-version": Set(nuget, "version", $"[{version}, )"); break;
            case "floating-version": Set(nuget, "version", version[..version.LastIndexOf('.')] + ".*"); break;
            case "wildcard-version": Set(nuget, "version", "*"); break;
            case "caret-version": Set(nuget, "version", "^" + version); break;
            case "prerelease-version": Set(nuget, "version", Bump(version) + "-beta.1"); break;
            case "short-action-sha": Set(action, "version", sha[..^1]); break;
            case "tagged-action": Set(action, "version", "v1.0.0"); break;
            case "numeric-version": Set(nuget, "version", 4); break;
            case "inexact-latest": Set(maintenance, "latestEligible", Value(maintenance, "latestEligible").Split('.')[0] + ".x"); break;
            case "sdk-mismatch":
                var globalJson = JsonNode.Parse(files["global.json"])!;
                Set(globalJson["sdk"]!, "version", Bump(Value(globalJson["sdk"]!, "version")));
                files["global.json"] = globalJson.ToJsonString();
                break;
            case "missing-sdk-entry": Assert.True(entries.Remove(sdk)); break;
            case "second-sdk-entry":
                var second = sdk.DeepClone();
                Set(second, "name", Value(sdk, "name") + "-second");
                Set(second, "id", $"dotnet-sdk:{Value(second, "name")}");
                entries.Insert(entries.IndexOf(sdk) + 1, second);
                break;
            case "package-version-mismatch":
                var packages = XDocument.Parse(files["Directory.Packages.props"]);
                var package = packages.Descendants("PackageVersion").First();
                package.SetAttributeValue("Version", Bump((string?)package.Attribute("Version") ?? ""));
                files["Directory.Packages.props"] = packages.ToString();
                break;
            case "unregistered-package":
                files["Directory.Packages.props"] = Changed(files["Directory.Packages.props"], files["Directory.Packages.props"]
                    .Replace("</ItemGroup>", "<PackageVersion Include=\"Unreviewed.Package\" Version=\"1.0.0\" /></ItemGroup>"));
                break;
            case "candidate-package": Set(nuget, "status", "candidate"); break;
            case "lock-mismatch":
                files[LockPath] = MutateLock(files[LockPath], locked =>
                {
                    var firstPackage = locked.First().Value!;
                    Set(firstPackage, "resolved", Bump(Value(firstPackage, "resolved")));
                });
                break;
            case "unregistered-lock-package":
                files[LockPath] = MutateLock(files[LockPath], locked => Add(locked, "Unreviewed.Package", new JsonObject
                {
                    ["type"] = "Transitive",
                    ["resolved"] = "1.0.0",
                    ["contentHash"] = Convert.ToBase64String(new byte[64])
                }));
                break;
            case "stale-nuget-entry":
                var absentPackage = JsonNode.Parse(files[LockPath])!["dependencies"]!["net10.0"]!.AsObject().First().Key;
                // A shared test dependency must be removed from every project's lock to become stale.
                foreach (var path in files.Keys.Where(path => Path.GetFileName(path) == "packages.lock.json").ToArray())
                    files[path] = MutateLock(files[path], locked => { locked.Remove(absentPackage); });
                break;
            case "unregistered-action":
                var workflow = files.Keys.Where(path => path.StartsWith(".github/workflows/", StringComparison.Ordinal)
                    && path.EndsWith(".yml", StringComparison.Ordinal) && files[path].Contains(sha, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal).First();
                files[workflow] = Changed(files[workflow], files[workflow].Replace(sha, new string('a', 40)));
                break;
            case "unregistered-allowed-action":
                files[".github/workflows/actions.allow"] += "unreviewed-owner/unreviewed-action@" + new string('c', 40) + " # v1.0.0\n";
                break;
            case "action-sha-mismatch": Set(action, "version", new string(sha[0] == 'b' ? 'c' : 'b', 40)); break;
            case "unused-action-entry":
                var unused = action.DeepClone();
                Set(unused, "name", "unreviewed-owner/unreviewed-action");
                Set(unused, "id", "github-action:unreviewed-owner/unreviewed-action");
                entries.Insert(entries.IndexOf(action) + 1, unused);
                break;
            case "tool-version-mismatch":
                var pin = ToolPin(files);
                files[pin.Path] = Changed(files[pin.Path], new Regex($@"(?m)^(\s*{pin.Variable}\s*:\s*[""']?){Regex.Escape(pin.Version)}")
                    .Replace(files[pin.Path], "${1}" + Bump(pin.Version), 1));
                break;
            case "unregistered-tool-version":
                files[".github/workflows/ci.yml"] += string.Join('\n', "", "env:", "  UNREVIEWED_TOOL_VERSION: '1.0.0'", "");
                break;
            case "stale-binary-entry":
                var renamed = ToolPin(files);
                files[renamed.Path] = Changed(files[renamed.Path], new Regex($@"(?m)^(\s*){renamed.Variable}(\s*:)")
                    .Replace(files[renamed.Path], "${1}RENAMED_TOOL${2}", 1));
                break;
            case "unpinned-without-note":
                var tool = nuget.DeepClone();
                Set(tool, "ecosystem", "binary");
                Set(tool, "name", "unreviewed-tool");
                Set(tool, "id", "binary:unreviewed-tool");
                tool["version"] = "1.0";
                tool["maintenance"]!["latestEligible"] = "1.0";
                tool["unpinned"] = null;
                entries.Add(tool);
                break;
            case "needless-unpinned": Set(nuget, "unpinned", "Selected at run time."); break;
            case "pinned-binary-note":
                var variable = ToolPin(files).Variable;
                Set(First(entries, entry => Value(entry, "ecosystem") == "binary" && Value(entry, "status") == "in-use"
                    && DependencyRegister.VersionVariable(Value(entry, "name")) == variable), "unpinned", "Selected at run time.");
                break;
            case "in-use-npm":
                var npm = nuget.DeepClone();
                Set(npm, "ecosystem", "npm");
                Set(npm, "name", "unreviewed-package");
                Set(npm, "id", "npm:unreviewed-package");
                npm["version"] = "1.0.0";
                npm["maintenance"]!["latestEligible"] = "1.0.0";
                entries.Add(npm);
                break;
            case "unlisted-license":
                Set(nuget, "license", "GPL-3.0-only");
                nuget["clearance"]!["community"] = "cleared";
                break;
            case "partly-flagged-license":
                Set(nuget, "license", "MPL-2.0");
                nuget["clearance"]!["community"] = "flagged";
                nuget["clearance"]!["paid"] = "cleared";
                break;
            case "invalid-license": Set(nuget, "license", Value(nuget, "license") + " OR"); break;
            case "unknown-clearance": Set(nuget["clearance"]!, "paid", "approved"); break;
            case "empty-note": Set(nuget["clearance"]!, "note", ""); break;
            case "widened-allowlist":
                var readme = files[ReadmePath];
                var heading = readme.IndexOf("## License allowlist", StringComparison.Ordinal);
                Assert.True(heading >= 0, "The README must have a license allowlist.");
                var row = readme.IndexOf("\n| `", heading, StringComparison.Ordinal);
                Assert.True(row > heading, "The license allowlist must have rows.");
                files[ReadmePath] = readme.Insert(row + 1, "| `LicenseRef-unreviewed` | Not reviewed. |\n");
                break;
            case "missing-allowlist":
                files[ReadmePath] = Changed(files[ReadmePath], files[ReadmePath].Replace("## License allowlist", "## Licenses"));
                break;
            case "candidate-not-latest":
                Set(nuget, "status", "candidate");
                maintenance["latestEligible"] = Bump(version);
                break;
            case "missing-age-exception":
                maintenance["published"] = reviewedAt;
                maintenance["ageException"] = null;
                break;
            case "needless-age-exception":
                maintenance["published"] = AddDays(reviewedAt, -14);
                maintenance["ageException"] = "Not needed.";
                break;
            case "future-published": Set(maintenance, "published", AddDays(reviewedAt, 1)); break;
            case "invalid-date": Set(maintenance, "published", "2026-02-30"); break;
            case "early-review":
                Set(maintenance, "reviewedAt", DependencyRegister.PolicyStart.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case "http-source": Set(maintenance["sources"]!.AsArray(), 0, "http://api.nuget.org/v3/index.json"); break;
            case "duplicate-source":
                var sources = maintenance["sources"]!.AsArray();
                sources.Add(sources[0]!.DeepClone());
                break;
            case "empty-sources": Set(maintenance, "sources", new JsonArray()); break;
            case "machine-path": Set(nuget, "purpose", "Read from " + string.Join('/', "", "home", "person", "project") + "."); break;
            case "multiline-text": Set(nuget, "reason", "First line.\nSecond line."); break;
            case "long-text": Set(maintenance, "activity", new string('a', 601)); break;
            case "unsorted":
                var moved = entries[1];
                entries.RemoveAt(1);
                entries.Insert(0, moved);
                break;
            case "null-entry": entries[0] = null; break;
            case "null-entries": register["entries"] = null; break;
            case "wrong-version": Set(register, "version", 2); break;
            case "extra-root-field": Add(register, "date", reviewedAt); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        if (files.ContainsKey(RegisterPath))
        {
            files[RegisterPath] = register.ToJsonString();
        }

        // Compare the register by content and every other file by text, so an unchanged mutation fails here.
        var changed = files.Count != original.Count || files.Any(pair => !original.TryGetValue(pair.Key, out var text)
            || (pair.Key == RegisterPath ? JsonNode.Parse(text)!.ToJsonString() != pair.Value : text != pair.Value));
        Assert.True(changed, $"Mutation {mutation} must change the repository files.");
        Assert.Contains(DependencyRegister.Validate(files), error => error.Contains(expectedDiagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":1,\"entries\":{}}")]
    public void Invalid_documents_are_rejected(string text)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        files[RegisterPath] = text;
        Assert.NotEmpty(DependencyRegister.Validate(files));
    }

    [Fact]
    public void Duplicate_json_properties_are_rejected()
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var text = files[RegisterPath];
        files[RegisterPath] = text.Replace("\"status\": \"in-use\"", "\"status\": \"in-use\", \"status\": \"in-use\"");
        Assert.NotEqual(text, files[RegisterPath]);
        Assert.Contains(DependencyRegister.Validate(files), error => error.Contains("duplicate field", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MIT", true)]
    [InlineData("PostgreSQL", true)]
    [InlineData("MIT OR GPL-3.0-only", true)]
    [InlineData("(MIT AND Apache-2.0)", true)]
    [InlineData("MIT AND (ISC OR GPL-2.0-only)", true)]
    [InlineData("GPL-3.0-only", false)]
    [InlineData("MIT AND MPL-2.0", false)]
    [InlineData("Apache-2.0 WITH LLVM-exception", false)]
    [InlineData("LicenseRef-dotnet-library-license", false)]
    [InlineData("mit", false)]
    public void License_expressions_follow_the_allowlist(string expression, bool allowed)
    {
        Assert.Equal(allowed, DependencyRegister.Allowed(expression, DependencyRegister.ReviewedAllowlist));
    }

    [Theory]
    [InlineData("")]
    [InlineData("MIT OR")]
    [InlineData("AND MIT")]
    [InlineData("(MIT")]
    [InlineData("MIT)")]
    [InlineData("MIT Apache-2.0")]
    [InlineData("MIT and ISC")]
    [InlineData("MIT/ISC")]
    [InlineData("MIT WITH")]
    public void Malformed_license_expressions_are_rejected(string expression)
    {
        Assert.Null(DependencyRegister.Allowed(expression, DependencyRegister.ReviewedAllowlist));
    }

    private static JsonNode First(JsonArray entries, Func<JsonNode, bool> predicate) =>
        entries.Select(entry => entry!).First(predicate);

    private static string Value(JsonNode node, string field) =>
        (string?)node[field] ?? throw new InvalidOperationException($"The register entry has no {field}.");

    // Sets a field to a different value, so the mutation never silently does nothing.
    private static void Set(JsonNode node, string field, JsonNode? value)
    {
        Assert.NotEqual(node[field]?.ToJsonString(), value?.ToJsonString());
        node[field] = value;
    }

    private static void Set(JsonArray array, int index, JsonNode? value)
    {
        Assert.NotEqual(array[index]?.ToJsonString(), value?.ToJsonString());
        array[index] = value;
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

    private static string AddDays(string date, int days) =>
        DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static (string Path, string Variable, string Version) ToolPin(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, text) in files.Where(pair => pair.Key.StartsWith(".github/workflows/", StringComparison.Ordinal)
                     && pair.Key.EndsWith(".yml", StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var match = text.Replace("\r\n", "\n").Split('\n').Select(line => DependencyRegister.ToolVersion.Match(line))
                .FirstOrDefault(candidate => candidate.Success);
            if (match is not null)
            {
                return (path, match.Groups["variable"].Value, match.Groups["version"].Value);
            }
        }

        throw new InvalidOperationException("No workflow pins a tool version.");
    }

    private static string MutateLock(string text, Action<JsonObject> mutate)
    {
        var document = JsonNode.Parse(text)!;
        mutate(document["dependencies"]!["net10.0"]!.AsObject());
        return document.ToJsonString();
    }
}

internal static class DependencyRegister
{
    public const string RegisterPath = "docs/dependencies/register.json";
    public const string ReadmePath = "docs/dependencies/README.md";

    // Widening the allowlist is a reviewed policy change: update the README table and this set together.
    public static readonly IReadOnlySet<string> ReviewedAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "MIT", "PostgreSQL"
    };

    private const int MinimumAgeDays = 14;
    private const int MaximumTextLength = 600;
    public static readonly DateOnly PolicyStart = new(2026, 10, 25);
    private static readonly string[] Ecosystems = ["dotnet-sdk", "nuget", "npm", "github-action", "binary"];
    private static readonly string[] Statuses = ["in-use", "candidate"];
    private static readonly string[] Areas = ["build", "ci", "persistence", "identity", "admin", "test"];
    private static readonly string[] Clearances = ["cleared", "flagged"];
    private static readonly string[] EntryFields =
        ["id", "name", "ecosystem", "version", "unpinned", "license", "status", "area", "purpose", "reason", "maintenance", "clearance"];
    private static readonly string[] MaintenanceFields =
        ["reviewedAt", "published", "latestEligible", "ageException", "maintainer", "activity", "advisories", "sources"];
    private static readonly string[] ClearanceFields = ["community", "paid", "note"];

    private const string Number = "(?:0|[1-9][0-9]*)";
    private static readonly Dictionary<string, Regex> ExactVersions = new(StringComparer.Ordinal)
    {
        ["dotnet-sdk"] = new($"^{Number}\\.{Number}\\.[1-9][0-9]{{2}}$", RegexOptions.CultureInvariant),
        ["nuget"] = new($"^{Number}\\.{Number}\\.{Number}(?:\\.{Number})?$", RegexOptions.CultureInvariant),
        ["npm"] = new($"^{Number}\\.{Number}\\.{Number}$", RegexOptions.CultureInvariant),
        ["github-action"] = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant),
        ["binary"] = new($"^{Number}\\.{Number}(?:\\.{Number})?$", RegexOptions.CultureInvariant)
    };

    private static readonly Regex Name = new("^[A-Za-z0-9@][A-Za-z0-9@._/-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex LicenseId = new(
        "^(?:LicenseRef-[A-Za-z0-9.-]+|[A-Za-z0-9][A-Za-z0-9.-]*\\+?)$", RegexOptions.CultureInvariant);
    private static readonly Regex LicenseToken = new(@"\(|\)|[^\s()]+", RegexOptions.CultureInvariant);
    private static readonly Regex AllowlistRow = new(@"^\|\s*`(?<id>[^`]+)`\s*\|", RegexOptions.CultureInvariant);
    private static readonly Regex AllowedAction = new(
        @"^(?<action>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)@(?<sha>[0-9a-f]{40})(?:\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex UsedAction = new(
        """^\s*(?:-\s+)?["']?uses["']?\s*:\s*["']?(?<action>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)@(?<sha>[0-9a-f]{40})""",
        RegexOptions.CultureInvariant);
    public static readonly Regex ToolVersion = new(
        """^\s*(?<variable>[A-Z][A-Z0-9_]*_VERSION)\s*:\s*["']?(?<version>[^"'\s#]+)""", RegexOptions.CultureInvariant);

    private sealed record Dependency(string Id, string Name, string Ecosystem, string Version, string Status, bool Unpinned);

    public static List<string> Validate(IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        if (!files.TryGetValue(RegisterPath, out var text))
        {
            errors.Add($"{RegisterPath}: the dependency register is missing.");
            return errors;
        }

        var allowlist = ReadAllowlist(files.GetValueOrDefault(ReadmePath, ""));
        if (allowlist is null || !allowlist.SetEquals(ReviewedAllowlist))
        {
            errors.Add($"{ReadmePath}: the license allowlist must be exactly {string.Join(", ", ReviewedAllowlist.Order(StringComparer.Ordinal))}.");
        }

        var dependencies = ValidateRegister(text, allowlist ?? new HashSet<string>(StringComparer.Ordinal), errors);
        if (dependencies is not null)
        {
            ValidatePins(files, dependencies, errors);
        }

        return errors;
    }

    public static bool? Allowed(string expression, IReadOnlySet<string> allowlist)
    {
        var tokens = LicenseToken.Matches(expression).Select(match => match.Value).ToList();
        var position = 0;
        bool Next(string token) => position < tokens.Count && tokens[position] == token;

        bool? Expression()
        {
            var result = Term();
            while (result is not null && Next("OR"))
            {
                position++;
                var right = Term();
                result = right is null ? null : result.Value || right.Value;
            }

            return result;
        }

        bool? Term()
        {
            var result = Factor();
            while (result is not null && Next("AND"))
            {
                position++;
                var right = Factor();
                result = right is null ? null : result.Value && right.Value;
            }

            return result;
        }

        bool? Factor()
        {
            if (position >= tokens.Count)
            {
                return null;
            }

            var token = tokens[position++];
            if (token == "(")
            {
                var inner = Expression();
                if (inner is null || !Next(")"))
                {
                    return null;
                }

                position++;
                return inner;
            }

            if (token is "AND" or "OR" or "WITH" or ")" || !LicenseId.IsMatch(token))
            {
                return null;
            }

            if (!Next("WITH"))
            {
                return allowlist.Contains(token);
            }

            // A license exception changes the terms, so it always needs its own review.
            position++;
            return position < tokens.Count && LicenseId.IsMatch(tokens[position++]) ? false : null;
        }

        var allowed = Expression();
        return position == tokens.Count ? allowed : null;
    }

    private static HashSet<string>? ReadAllowlist(string readme)
    {
        var lines = readme.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, "## License allowlist");
        if (start < 0)
        {
            return null;
        }

        var ids = lines.Skip(start + 1).TakeWhile(line => !line.StartsWith('#'))
            .Select(line => AllowlistRow.Match(line)).Where(match => match.Success)
            .Select(match => match.Groups["id"].Value).ToList();
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Count ? ids.ToHashSet(StringComparer.Ordinal) : null;
    }

    private static List<Dependency>? ValidateRegister(string text, IReadOnlySet<string> allowlist, List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("Register must be an object.");
                return null;
            }

            Fields(root, ["version", "entries"], "register", errors);
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 1)
            {
                errors.Add("Register version must be 1.");
            }

            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                errors.Add("Register entries must be an array.");
                return null;
            }

            var dependencies = new List<Dependency>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Each entry must be an object.");
                    continue;
                }

                var dependency = ValidateEntry(entry, allowlist, errors);
                if (!seen.Add(dependency.Id))
                {
                    errors.Add($"{dependency.Id}: duplicate id.");
                }

                dependencies.Add(dependency);
            }

            var order = dependencies.Select(dependency => (Array.IndexOf(Statuses, dependency.Status),
                Array.IndexOf(Ecosystems, dependency.Ecosystem), dependency.Id)).ToList();
            if (!order.SequenceEqual(order.OrderBy(key => key.Item1).ThenBy(key => key.Item2).ThenBy(key => key.Id, StringComparer.Ordinal)))
            {
                errors.Add("Register entries must be sorted by status, ecosystem and id.");
            }

            return dependencies;
        }
        catch (JsonException)
        {
            errors.Add("Register must contain valid JSON.");
            return null;
        }
    }

    private static Dependency ValidateEntry(JsonElement entry, IReadOnlySet<string> allowlist, List<string> errors)
    {
        var id = String(entry, "id", "entry", errors);
        var location = id.Length == 0 ? "entry" : id;
        Fields(entry, EntryFields, location, errors);
        var name = String(entry, "name", location, errors);
        var ecosystem = String(entry, "ecosystem", location, errors);
        var version = String(entry, "version", location, errors);
        var status = String(entry, "status", location, errors);
        if (!Name.IsMatch(name)) errors.Add($"{location}: name must be a package or repository name.");
        if (id != $"{ecosystem}:{name}".ToLowerInvariant()) errors.Add($"{location}: id must be the lowercase ecosystem and name.");
        if (!Ecosystems.Contains(ecosystem)) errors.Add($"{location}: ecosystem must be {string.Join(", ", Ecosystems)}.");
        if (!Statuses.Contains(status)) errors.Add($"{location}: status must be in-use or candidate.");
        if (!Areas.Contains(String(entry, "area", location, errors))) errors.Add($"{location}: area must be {string.Join(", ", Areas)}.");
        var exact = ExactVersions.GetValueOrDefault(ecosystem);
        if (exact is null || !exact.IsMatch(version))
        {
            errors.Add($"{location}: version must be exact for its ecosystem, without ranges, floats, wildcards or prerelease labels.");
        }

        var unpinned = entry.TryGetProperty("unpinned", out var note) && note.ValueKind != JsonValueKind.Null;
        if (unpinned)
        {
            Text(entry, "unpinned", location, errors);
            if (ecosystem != "binary" || status != "in-use")
            {
                errors.Add($"{location}: unpinned must be null unless an in-use binary has no workflow version pin.");
            }
        }

        Text(entry, "purpose", location, errors);
        Text(entry, "reason", location, errors);
        var license = String(entry, "license", location, errors);
        var allowed = Allowed(license, allowlist);
        if (allowed is null) errors.Add($"{location}: license must be an SPDX license expression.");

        if (!entry.TryGetProperty("maintenance", out var maintenance) || maintenance.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{location}: maintenance must be an object.");
        }
        else
        {
            ValidateMaintenance(maintenance, location, version, status, exact, errors);
        }

        if (!entry.TryGetProperty("clearance", out var clearance) || clearance.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{location}: clearance must be an object.");
        }
        else
        {
            Fields(clearance, ClearanceFields, $"{location}.clearance", errors);
            var community = String(clearance, "community", $"{location}.clearance", errors);
            var paid = String(clearance, "paid", $"{location}.clearance", errors);
            if (!Clearances.Contains(community) || !Clearances.Contains(paid))
            {
                errors.Add($"{location}: clearance must be cleared or flagged for each distribution.");
            }

            Text(clearance, "note", $"{location}.clearance", errors);
            if (allowed == false && (community != "flagged" || paid != "flagged"))
            {
                errors.Add($"{location}: license {license} is not on the README allowlist and must be flagged for both distributions.");
            }
        }

        return new Dependency(id, name, ecosystem, version, status, unpinned);
    }

    private static void ValidateMaintenance(JsonElement maintenance, string location, string version, string status,
        Regex? exact, List<string> errors)
    {
        var path = $"{location}.maintenance";
        Fields(maintenance, MaintenanceFields, path, errors);
        var reviewed = Date(String(maintenance, "reviewedAt", path, errors));
        if (reviewed is null || reviewed < PolicyStart)
        {
            errors.Add($"{location}: reviewedAt must be an ISO date on or after {PolicyStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.");
        }

        var published = Date(String(maintenance, "published", path, errors));
        if (published is null || (reviewed is not null && published > reviewed))
        {
            errors.Add($"{location}: published must be an ISO date on or before reviewedAt.");
        }

        var latest = String(maintenance, "latestEligible", path, errors);
        if (exact is null || !exact.IsMatch(latest))
        {
            errors.Add($"{location}: latestEligible must be an exact version for its ecosystem.");
        }

        if (status == "candidate" && latest != version)
        {
            errors.Add($"{location}: candidate version must equal its latestEligible version.");
        }

        if (published is not null && reviewed is not null)
        {
            var young = reviewed.Value.DayNumber - published.Value.DayNumber < MinimumAgeDays;
            var hasException = maintenance.TryGetProperty("ageException", out var exception)
                && exception.ValueKind != JsonValueKind.Null;
            if (young && !hasException)
            {
                errors.Add($"{location}: a version published fewer than {MinimumAgeDays} days before review requires an ageException.");
            }
            else if (young)
            {
                Text(maintenance, "ageException", path, errors);
            }
            else if (hasException)
            {
                errors.Add($"{location}: ageException must be null when the version meets the {MinimumAgeDays}-day age.");
            }
        }

        Text(maintenance, "maintainer", path, errors);
        Text(maintenance, "activity", path, errors);
        Text(maintenance, "advisories", path, errors);
        if (!maintenance.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array
            || sources.GetArrayLength() == 0
            || sources.EnumerateArray().Any(source => source.ValueKind != JsonValueKind.String || !IsHttps(source.GetString()!))
            || sources.EnumerateArray().Select(source => source.ToString()).Distinct(StringComparer.Ordinal).Count() != sources.GetArrayLength())
        {
            errors.Add($"{location}: sources must be unique https URLs.");
        }
    }

    private static void ValidatePins(IReadOnlyDictionary<string, string> files, List<Dependency> dependencies, List<string> errors)
    {
        var inUse = dependencies.Where(dependency => dependency.Status == "in-use").ToList();
        var nuget = inUse.Where(dependency => dependency.Ecosystem == "nuget")
            .GroupBy(dependency => dependency.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Version, StringComparer.OrdinalIgnoreCase);

        ValidateSdk(files.GetValueOrDefault("global.json", ""), dependencies, errors);
        ValidatePackageVersions(files.GetValueOrDefault("Directory.Packages.props", ""), nuget, errors);

        var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in files.Where(pair => Path.GetFileName(pair.Key) == "packages.lock.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
                {
                    foreach (var package in framework.Value.EnumerateObject())
                    {
                        // Local project references are checked by architecture discovery, not the external dependency register.
                        if (package.Value.TryGetProperty("type", out var kind) && kind.GetString() == "Project"
                            && files.Keys.Any(project => project.EndsWith(".csproj", StringComparison.Ordinal)
                                && string.Equals(Path.GetFileNameWithoutExtension(project), package.Name, StringComparison.OrdinalIgnoreCase))) continue;
                        var resolved = package.Value.GetProperty("resolved").GetString() ?? "";
                        locked.Add($"{package.Name}@{resolved}");
                        if (!nuget.TryGetValue(package.Name, out var registered) || registered != resolved)
                        {
                            errors.Add($"{path}: {package.Name} {resolved} needs an in-use nuget entry with the resolved version.");
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                errors.Add($"{path}: invalid dependency lock.");
            }
        }

        foreach (var dependency in inUse.Where(dependency => dependency.Ecosystem == "nuget"
                     && !locked.Contains($"{dependency.Name}@{dependency.Version}")))
        {
            errors.Add($"{dependency.Id}: in-use nuget entry is absent from every lock file at its version.");
        }

        foreach (var dependency in inUse.Where(dependency => dependency.Ecosystem == "npm"))
        {
            errors.Add($"{dependency.Id}: in-use npm entries require a lock-file check that this register does not have yet.");
        }

        ValidateWorkflows(files, inUse, errors);
    }

    private static void ValidateSdk(string text, List<Dependency> dependencies, List<string> errors)
    {
        string? pinned = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            pinned = document.RootElement.GetProperty("sdk").GetProperty("version").GetString();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // The convention tests report the invalid SDK configuration itself.
        }

        var sdks = dependencies.Where(dependency => dependency.Ecosystem == "dotnet-sdk").ToList();
        if (pinned is null || sdks.Count != 1 || sdks[0].Status != "in-use" || sdks[0].Version != pinned)
        {
            errors.Add($"global.json: exactly one in-use dotnet-sdk entry must match global.json version {pinned ?? "(missing)"}.");
        }
    }

    private static void ValidatePackageVersions(string text, Dictionary<string, string> nuget, List<string> errors)
    {
        try
        {
            foreach (var item in XDocument.Parse(text).Descendants("PackageVersion"))
            {
                var name = (string?)item.Attribute("Include") ?? "";
                var version = (string?)item.Attribute("Version") ?? "";
                if (!nuget.TryGetValue(name, out var registered) || registered != version)
                {
                    errors.Add($"Directory.Packages.props: {name} {version} needs an in-use nuget entry with the same version.");
                }
            }
        }
        catch (XmlException)
        {
            errors.Add("Directory.Packages.props: invalid XML.");
        }
    }

    private static void ValidateWorkflows(IReadOnlyDictionary<string, string> files, List<Dependency> inUse, List<string> errors)
    {
        var actions = inUse.Where(dependency => dependency.Ecosystem == "github-action")
            .Select(dependency => $"{dependency.Name}@{dependency.Version}").ToHashSet(StringComparer.Ordinal);
        var binaries = inUse.Where(dependency => dependency.Ecosystem == "binary")
            .GroupBy(dependency => VersionVariable(dependency.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in files.Where(pair => pair.Key.StartsWith(".github/workflows/", StringComparison.Ordinal)))
        {
            var workflow = path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
            if (!workflow && path != ".github/workflows/actions.allow")
            {
                continue;
            }

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var action = (workflow ? UsedAction : AllowedAction).Match(line);
                if (action.Success)
                {
                    var reference = $"{action.Groups["action"].Value}@{action.Groups["sha"].Value}";
                    referenced.Add(reference);
                    if (!actions.Contains(reference))
                    {
                        errors.Add($"{path}: {reference} needs an in-use github-action entry with its SHA.");
                    }
                }

                var tool = workflow ? ToolVersion.Match(line) : Match.Empty;
                if (tool.Success)
                {
                    var variable = tool.Groups["variable"].Value;
                    var version = tool.Groups["version"].Value;
                    tools.Add($"{variable}={version}");
                    if (!binaries.TryGetValue(variable, out var binary) || binary.Version != version)
                    {
                        errors.Add($"{path}: {variable} {version} needs an in-use binary entry with the same version.");
                    }
                }
            }
        }

        foreach (var reference in actions.Where(reference => !referenced.Contains(reference)))
        {
            errors.Add($"github-action:{reference.Split('@')[0].ToLowerInvariant()}: in-use github-action entry is absent from the workflows and actions.allow.");
        }

        var variables = tools.Select(tool => tool.Split('=')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var (variable, binary) in binaries)
        {
            if (variables.Contains(variable) && binary.Unpinned)
            {
                errors.Add($"{binary.Id}: unpinned must be null unless an in-use binary has no workflow version pin.");
            }
            else if (variables.Contains(variable) ? !tools.Contains($"{variable}={binary.Version}") : !binary.Unpinned)
            {
                errors.Add($"{binary.Id}: in-use binary entry must match a workflow {variable} pin or record in unpinned what selects its version.");
            }
        }
    }

    public static string VersionVariable(string name) =>
        Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]", "_") + "_VERSION";

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Length > 0 && !value.Any(char.IsWhiteSpace);

    private static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static void Fields(JsonElement element, string[] expected, string location, List<string> errors)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) errors.Add($"{location}: duplicate field.");
        if (names.Length != expected.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            errors.Add($"{location}: fields must be exactly {string.Join(", ", expected)}.");
        }
    }

    private static string String(JsonElement element, string field, string location, List<string> errors)
    {
        if (element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (!string.IsNullOrWhiteSpace(text) && text == text.Trim() && !text.Any(char.IsControl)) return text;
        }

        errors.Add($"{location}.{field}: require a nonempty single-line string without surrounding whitespace.");
        return "";
    }

    private static void Text(JsonElement element, string field, string location, List<string> errors)
    {
        var text = String(element, field, location, errors);
        if (text.Length > MaximumTextLength)
        {
            errors.Add($"{location}.{field}: keep review text to at most {MaximumTextLength} characters.");
        }

        if (Conventions.HasMachinePath(text))
        {
            errors.Add($"{location}.{field}: review text must be portable.");
        }
    }
}
