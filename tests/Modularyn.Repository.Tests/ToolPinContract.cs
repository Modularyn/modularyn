using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modularyn.Repository.Tests;

// Checks the hashes pinned for the tools CI downloads: the SDK archives and the gitleaks archive.
internal static class ToolPinContract
{
    public const string SdkPinPath = ".github/dotnet-sdk.json";
    public const string CiPath = ".github/workflows/ci.yml";
    public const string SecretScanPath = ".github/workflows/secret-scan.yml";
    public const string ArchiveBase = "https://builds.dotnet.microsoft.com/dotnet/Sdk/";

    // The runtime identifiers of every platform a GitHub-hosted runner can have.
    public static readonly string[] Rids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];

    private static readonly string[] PinFields = ["version", "source", "archives"];
    private static readonly string[] ArchiveFields = ["url", "sha512"];
    private static readonly Regex Sha512 = new("^[0-9a-f]{128}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex ReleaseRecord = new(
        @"^https://github\.com/dotnet/core/blob/main/release-notes/(?<channel>[0-9]+\.[0-9]+)/[0-9]+\.[0-9]+\.[0-9]+/release\.json$",
        RegexOptions.CultureInvariant);
    private static readonly Regex GitleaksDigest = new(
        """^\s*GITLEAKS_SHA256\s*:\s*(?<quote>["']?)(?<value>[^"'\s#]*)\k<quote>\s*(?:#.*)?$""", RegexOptions.CultureInvariant);

    private sealed record RegisterEntry(string Id, string Ecosystem, string Status, string Version, string[] Sources);

    public static List<string> Validate(IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        var sdkVersion = GlobalSdkVersion(files.GetValueOrDefault("global.json", ""), errors);
        var register = ReadRegister(files, errors);
        ValidateSdkPin(files, sdkVersion, register, errors);
        ValidateWorkflows(files, errors);
        ValidateGitleaks(files, register, errors);
        return errors;
    }

    public static string ArchiveUrl(string version, string rid) =>
        $"{ArchiveBase}{version}/dotnet-sdk-{version}-{rid}.{(rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}";

    private static string? GlobalSdkVersion(string text, List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var version = document.RootElement.GetProperty("sdk").GetProperty("version");
            if (version.ValueKind == JsonValueKind.String)
            {
                return version.GetString();
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Reported below.
        }

        errors.Add("global.json: cannot read the SDK version that the SDK archive pins must match.");
        return null;
    }

    private static List<RegisterEntry> ReadRegister(IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        var entries = new List<RegisterEntry>();
        try
        {
            using var document = JsonDocument.Parse(files.GetValueOrDefault(DependencyRegister.RegisterPath, ""));
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                var sources = entry.TryGetProperty("maintenance", out var maintenance) && maintenance.ValueKind == JsonValueKind.Object
                    && maintenance.TryGetProperty("sources", out var list) && list.ValueKind == JsonValueKind.Array
                        ? list.EnumerateArray().Where(source => source.ValueKind == JsonValueKind.String)
                            .Select(source => source.GetString()!).ToArray()
                        : [];
                entries.Add(new RegisterEntry(Text(entry, "id") ?? "", Text(entry, "ecosystem") ?? "",
                    Text(entry, "status") ?? "", Text(entry, "version") ?? "", sources));
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            errors.Add($"{DependencyRegister.RegisterPath}: cannot read the register entries that the tool pins must agree with.");
        }

        return entries;
    }

    private static void ValidateSdkPin(IReadOnlyDictionary<string, string> files, string? sdkVersion,
        List<RegisterEntry> register, List<string> errors)
    {
        if (!files.TryGetValue(SdkPinPath, out var text))
        {
            errors.Add($"{SdkPinPath}: the SDK archive pin file is missing.");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{SdkPinPath}: the pin file must be a JSON object.");
                return;
            }

            Fields(root, PinFields, SdkPinPath, errors);
            var version = Text(root, "version");
            if (version is null || version != sdkVersion)
            {
                errors.Add($"{SdkPinPath}: version {version ?? "(missing)"} must equal the global.json SDK version {sdkVersion ?? "(missing)"}.");
            }

            var source = Text(root, "source");
            var record = ReleaseRecord.Match(source ?? "");
            if (!record.Success || version is null || !version.StartsWith(record.Groups["channel"].Value + ".", StringComparison.Ordinal))
            {
                errors.Add($"{SdkPinPath}: source must be the dotnet/core release record, release-notes/<channel>/<release>/release.json, for the SDK's channel.");
            }
            else if (!register.Any(entry => entry.Ecosystem == "dotnet-sdk" && entry.Status == "in-use" && entry.Sources.Contains(source)))
            {
                errors.Add($"{SdkPinPath}: the register's in-use dotnet-sdk entry must cite the source record {source}.");
            }

            if (!root.TryGetProperty("archives", out var archives) || archives.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{SdkPinPath}: archives must be an object keyed by runtime identifier.");
                return;
            }

            var rids = archives.EnumerateObject().Select(archive => archive.Name).ToList();
            if (rids.Count != Rids.Length || !rids.ToHashSet(StringComparer.Ordinal).SetEquals(Rids))
            {
                errors.Add($"{SdkPinPath}: archives must name exactly {string.Join(", ", Rids)}.");
            }

            // The URLs must name the version that global.json pins, so a stale pin file cannot install another SDK.
            var expectedVersion = sdkVersion ?? version ?? "<version>";
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var archive in archives.EnumerateObject())
            {
                var location = $"{SdkPinPath}: {archive.Name}";
                if (archive.Value.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{location} must be an object with url and sha512.");
                    continue;
                }

                Fields(archive.Value, ArchiveFields, location, errors);
                var expected = ArchiveUrl(expectedVersion, archive.Name);
                if (Text(archive.Value, "url") != expected)
                {
                    errors.Add($"{location} url must be the official SDK archive {expected}.");
                }

                var sha512 = Text(archive.Value, "sha512");
                if (sha512 is null || !Sha512.IsMatch(sha512))
                {
                    errors.Add($"{location} sha512 must be 128 lowercase hexadecimal characters.");
                }
                else if (!hashes.TryAdd(sha512, archive.Name))
                {
                    errors.Add($"{location} sha512 duplicates the hash of {hashes[sha512]}.");
                }
            }
        }
        catch (JsonException)
        {
            errors.Add($"{SdkPinPath}: the pin file must contain valid JSON.");
        }
    }

    private static void ValidateWorkflows(IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        foreach (var (path, text) in files.Where(pair => pair.Key.StartsWith(".github/workflows/", StringComparison.Ordinal))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (text.Contains("setup-dotnet", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{path}: must not use actions/setup-dotnet; CI installs the SDK from {SdkPinPath}.");
            }

            if (text.Contains("dotnet-install", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{path}: must not run the dotnet-install script, which verifies no pinned hash.");
            }
        }

        if (!files.TryGetValue(CiPath, out var ci))
        {
            errors.Add($"{CiPath}: the CI workflow is missing.");
        }
        else if (!ci.Contains(SdkPinPath, StringComparison.Ordinal))
        {
            errors.Add($"{CiPath}: must install the SDK from the archives pinned in {SdkPinPath}.");
        }
    }

    private static void ValidateGitleaks(IReadOnlyDictionary<string, string> files, List<RegisterEntry> register, List<string> errors)
    {
        if (!files.TryGetValue(SecretScanPath, out var text))
        {
            errors.Add($"{SecretScanPath}: the secret-scan workflow is missing.");
            return;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var digests = lines.Select(line => GitleaksDigest.Match(line)).Where(match => match.Success).ToList();
        if (digests.Count != 1)
        {
            errors.Add($"{SecretScanPath}: must pin GITLEAKS_SHA256 exactly once.");
        }
        else if (!Sha256.IsMatch(digests[0].Groups["value"].Value))
        {
            errors.Add($"{SecretScanPath}: GITLEAKS_SHA256 must be 64 lowercase hexadecimal characters.");
        }

        if (!text.Contains("${GITLEAKS_SHA256}", StringComparison.Ordinal)
            || !text.Contains("sha256sum --check --strict", StringComparison.Ordinal))
        {
            errors.Add($"{SecretScanPath}: must verify the archive against GITLEAKS_SHA256 with sha256sum --check --strict.");
        }

        var versions = lines.Select(line => DependencyRegister.ToolVersion.Match(line))
            .Where(match => match.Success && match.Groups["variable"].Value == "GITLEAKS_VERSION").ToList();
        var registered = register.Where(entry => entry.Id == "binary:gitleaks" && entry.Status == "in-use").ToList();
        if (versions.Count != 1)
        {
            errors.Add($"{SecretScanPath}: must pin GITLEAKS_VERSION exactly once.");
        }
        else if (registered.Count != 1)
        {
            errors.Add($"{DependencyRegister.RegisterPath}: needs exactly one in-use binary:gitleaks entry for the secret-scan workflow.");
        }
        else if (versions[0].Groups["version"].Value != registered[0].Version)
        {
            errors.Add($"{SecretScanPath}: GITLEAKS_VERSION {versions[0].Groups["version"].Value} must equal the register's in-use binary:gitleaks version {registered[0].Version}.");
        }
    }

    private static void Fields(JsonElement element, string[] expected, string location, List<string> errors)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            errors.Add($"{location}: duplicate field.");
        }

        if (names.Length != expected.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            errors.Add($"{location}: fields must be exactly {string.Join(", ", expected)}.");
        }
    }

    private static string? Text(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
