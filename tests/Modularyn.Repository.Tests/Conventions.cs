using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Modularyn.Repository.Tests;

internal static class Conventions
{
    private static readonly Dictionary<string, string> BuildProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TargetFramework"] = "net10.0",
        ["LangVersion"] = "14.0",
        ["Nullable"] = "enable",
        ["ImplicitUsings"] = "enable",
        ["TreatWarningsAsErrors"] = "true",
        ["Deterministic"] = "true",
        ["RestorePackagesWithLockFile"] = "true",
        ["RestoreEnablePackagePruning"] = "true",
        ["ManagePackageVersionsCentrally"] = "true",
        ["CentralPackageVersionOverrideEnabled"] = "false",
        ["Authors"] = "Jason Furr",
        ["Copyright"] = "Copyright 2026 Jason Furr",
        ["PackageLicenseExpression"] = "Apache-2.0",
        ["RepositoryUrl"] = "https://github.com/Modularyn/modularyn",
        ["RepositoryType"] = "git"
    };

    private static readonly HashSet<string> InheritedProperties = new(
        BuildProperties.Keys.Concat(new[]
        {
            "TargetFrameworks", "ContinuousIntegrationBuild", "ImportDirectoryBuildProps", "DirectoryBuildPropsPath",
            "ImportDirectoryPackagesProps", "DirectoryPackagesPropsPath", "WarningsNotAsErrors", "NoWarn"
        }), StringComparer.OrdinalIgnoreCase);

    private static readonly Regex StableVersion = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex Net10Sdk = new(@"^10\.0\.\d{3}$", RegexOptions.CultureInvariant);
    private static readonly Regex MachinePath = new(
        @"(?<![A-Za-z0-9#])/(?:Users|home|Volumes|mnt|media|run/media|root)(?:/|\b)|[A-Za-z]:\\",
        RegexOptions.CultureInvariant);
    private static readonly Regex Action = new(
        @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex ActionKey = new(
        """(?:^|[\s{,])(?:uses|"uses"|'uses')\s*:""", RegexOptions.CultureInvariant);
    private static readonly Regex ActionLine = new(
        """^\s*(?:-\s+)?(?:uses|"uses"|'uses')\s*:\s*(?<quote>["']?)(?<action>[^\s"'#]+)\k<quote>\s*(?:#.*)?$""",
        RegexOptions.CultureInvariant);
    private static readonly Regex EscapedOrComplexKey = new(
        """^\s*(?:-\s+)?(?:\?|["'][^"']*\\[^"']*["']\s*:)""", RegexOptions.CultureInvariant);

    public static List<string> Validate(IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        foreach (var path in new[]
        {
            ".editorconfig", ".gitignore", "global.json", "Directory.Build.props", "Directory.Packages.props",
            "Modularyn.slnx", "LICENSE", "NOTICE", "README.md", "ROADMAP.md", "SECURITY.md", "docs/ci.md",
            ".github/workflows/ci.yml", ".github/workflows/secret-scan.yml", ".github/workflows/actions.allow"
        })
        {
            if (!files.TryGetValue(path, out var content) || string.IsNullOrWhiteSpace(content))
            {
                errors.Add($"{path}: required nonempty text file is missing.");
            }
        }

        ValidateSdk(files.GetValueOrDefault("global.json", ""), errors);
        ValidateBuild(files.GetValueOrDefault("Directory.Build.props", ""), errors);
        var versions = ValidatePackages(files.GetValueOrDefault("Directory.Packages.props", ""), errors);
        ValidateProjects(files, versions, errors);
        ValidateLicense(files.GetValueOrDefault("LICENSE", ""), errors);
        if (files.GetValueOrDefault("NOTICE", "").Replace("\r\n", "\n").Trim() != "Modularyn\nCopyright 2026 Jason Furr")
        {
            errors.Add("NOTICE: product name and copyright must match the Community edition.");
        }

        foreach (var (path, text) in files)
        {
            if (HasMachinePath(text))
            {
                errors.Add($"{path}: contains an absolute machine path.");
            }
        }

        ValidateActions(files, errors);
        return errors;
    }

    public static bool HasMachinePath(string text) => MachinePath.IsMatch(text);

    private static void ValidateSdk(string text, List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var sdk = document.RootElement.GetProperty("sdk");
            if (!Net10Sdk.IsMatch(sdk.GetProperty("version").GetString() ?? "")
                || sdk.GetProperty("rollForward").GetString() != "disable"
                || sdk.GetProperty("allowPrerelease").GetBoolean())
            {
                errors.Add("global.json: require an exact stable .NET 10 SDK version, rollForward disable, and no prereleases.");
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            errors.Add("global.json: invalid SDK configuration.");
        }
    }

    private static void ValidateBuild(string text, List<string> errors)
    {
        var document = ParseXml("Directory.Build.props", text, errors);
        if (document is null)
        {
            return;
        }

        var root = document.Root;
        if (root is null || root.Name != "Project" || root.Elements().Any(element => element.Name != "PropertyGroup")
            || document.Descendants("PropertyGroup").Any(group => group.Elements().Any(element =>
                !BuildProperties.ContainsKey(element.Name.LocalName) && element.Name != "ContinuousIntegrationBuild")))
        {
            errors.Add("Directory.Build.props: keep build configuration in the declared central properties.");
        }

        foreach (var (name, value) in BuildProperties)
        {
            var elements = document.Descendants(name).ToArray();
            if (elements.Length != 1 || elements[0].Value != value || HasCondition(elements[0])
                || elements[0].Parent?.Name != "PropertyGroup")
            {
                errors.Add($"Directory.Build.props: {name} must be unconditionally set to {value} once.");
            }
        }

        var ci = document.Descendants("ContinuousIntegrationBuild").ToArray();
        if (ci.Length != 1 || ci[0].Value != "true"
            || (string?)ci[0].Attribute("Condition") != "'$(CI)' == 'true'"
            || ci[0].Parent?.Name != "PropertyGroup" || HasCondition(ci[0].Parent!))
        {
            errors.Add("Directory.Build.props: ContinuousIntegrationBuild must be true under CI.");
        }
    }

    private static Dictionary<string, string> ValidatePackages(string text, List<string> errors)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = ParseXml("Directory.Packages.props", text, errors);
        if (document is null)
        {
            return versions;
        }

        var root = document.Root;
        if (root is null || root.Name != "Project" || root.Elements().Any(element => element.Name != "ItemGroup")
            || document.Descendants("ItemGroup").Any(group => group.Elements().Any(element => element.Name != "PackageVersion")))
        {
            errors.Add("Directory.Packages.props: declare package versions without imports or build overrides.");
        }

        foreach (var item in document.Descendants("PackageVersion"))
        {
            var name = (string?)item.Attribute("Include") ?? "";
            var version = (string?)item.Attribute("Version") ?? "";
            if (name.Length == 0 || !StableVersion.IsMatch(version) || HasCondition(item)
                || item.Parent?.Name != "ItemGroup" || !versions.TryAdd(name, version))
            {
                errors.Add("Directory.Packages.props: require one unconditional exact stable version per package.");
            }
        }

        return versions;
    }

    private static void ValidateProjects(IReadOnlyDictionary<string, string> files,
        Dictionary<string, string> versions, List<string> errors)
    {
        var projects = files.Keys.Where(IsProject).ToHashSet(StringComparer.Ordinal);
        var solution = ParseXml("Modularyn.slnx", files.GetValueOrDefault("Modularyn.slnx", ""), errors);
        var listed = solution?.Descendants("Project").Select(p => (string?)p.Attribute("Path") ?? "").ToArray() ?? [];
        if (projects.Count == 0 || !projects.SetEquals(listed) || listed.Length != projects.Count)
        {
            errors.Add("Modularyn.slnx: list every project exactly once.");
        }

        foreach (var (path, text) in files.Where(pair => IsProject(pair.Key)
                     || pair.Key.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                     || pair.Key.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
        {
            if (path is "Directory.Build.props" or "Directory.Packages.props")
            {
                continue;
            }

            var document = ParseXml(path, text, errors);
            if (document is null)
            {
                continue;
            }

            if (Path.GetFileName(path) is "Directory.Build.props" or "Directory.Packages.props" or "Directory.Build.targets")
            {
                errors.Add($"{path}: nested central configuration can shadow repository settings.");
            }

            if (document.Descendants().Any(element => InheritedProperties.Contains(element.Name.LocalName)))
            {
                errors.Add($"{path}: inherit central settings without redeclaring or disabling them.");
            }

            if (document.Descendants("Import").Any() || document.Descendants("Sdk").Any())
            {
                errors.Add($"{path}: explicit imports require a central configuration review.");
            }

            if (document.Descendants("PackageVersion").Any()
                || document.Descendants("PackageReference").Any(reference => reference.Attributes().Any(attribute =>
                        attribute.Name.LocalName is "Version" or "VersionOverride")
                    || reference.Elements().Any(element => element.Name.LocalName is "Version" or "VersionOverride")))
            {
                errors.Add($"{path}: package versions belong only in Directory.Packages.props.");
            }

            if (IsProject(path))
            {
                if ((string?)document.Root?.Attribute("Sdk") != "Microsoft.NET.Sdk")
                {
                    errors.Add($"{path}: use Microsoft.NET.Sdk to inherit the central settings.");
                }

                var references = document.Descendants("PackageReference")
                    .Select(reference => (string?)reference.Attribute("Include") ?? "").ToArray();
                if (references.Any(reference => !versions.ContainsKey(reference)))
                {
                    errors.Add($"{path}: every package reference needs a central version.");
                }

                var lockPath = path[..(path.LastIndexOf('/') + 1)] + "packages.lock.json";
                ValidateLock(lockPath, files.GetValueOrDefault(lockPath, ""), references, versions,
                    projects.Select(project => Path.GetFileNameWithoutExtension(project)).ToHashSet(StringComparer.OrdinalIgnoreCase), errors);
            }
        }
    }

    private static void ValidateLock(string path, string text, string[] references,
        Dictionary<string, string> versions, HashSet<string> projectNames, List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var dependencies = root.GetProperty("dependencies").GetProperty("net10.0");
            var direct = dependencies.EnumerateObject().Where(package =>
                package.Value.GetProperty("type").GetString() == "Direct").Select(package => package.Name).ToArray();
            if (root.GetProperty("version").GetInt32() != 2
                || !new HashSet<string>(references, StringComparer.OrdinalIgnoreCase).SetEquals(direct))
            {
                errors.Add($"{path}: require a version 2 lock matching all direct references.");
            }

            foreach (var package in dependencies.EnumerateObject())
            {
                var value = package.Value;
                // Local project entries have no package archive or content hash.
                if (value.GetProperty("type").GetString() == "Project" && projectNames.Contains(package.Name)) continue;
                if (Convert.FromBase64String(value.GetProperty("contentHash").GetString() ?? "").Length != 64)
                {
                    errors.Add($"{path}: every package needs its SHA-512 content hash.");
                }

                if (value.GetProperty("type").GetString() == "Direct"
                    && (!versions.TryGetValue(package.Name, out var expected)
                        || value.GetProperty("resolved").GetString() != expected
                        || value.GetProperty("requested").GetString() != $"[{expected}, )"))
                {
                    errors.Add($"{path}: direct versions must match Directory.Packages.props.");
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            errors.Add($"{path}: missing or invalid dependency lock.");
        }
    }

    private static void ValidateLicense(string text, List<string> errors)
    {
        // Hash the entire license, including its completed copyright notice, independent of line wrapping.
        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        if (hash != "E0FC81CD3CFE84796EC74B23E99C1FDD2E90B54A7D32EF5DA6B2550CE0671A0C")
        {
            errors.Add("LICENSE: require the complete Apache License 2.0 text and the product copyright notice.");
        }
    }

    private static void ValidateActions(IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in files.GetValueOrDefault(".github/workflows/actions.allow", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('#', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !Action.IsMatch(parts[0])
                || !Regex.IsMatch(parts[1], @"^v\d+\.\d+\.\d+$") || !allowed.Add(parts[0]))
            {
                errors.Add("actions.allow: require unique full action SHAs with release tag comments.");
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var workflows = files.Where(pair => pair.Key.StartsWith(".github/workflows/", StringComparison.Ordinal)
            && (pair.Key.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || pair.Key.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)));
        foreach (var (path, text) in workflows)
        {
            if (text.Split('\n').Any(line => EscapedOrComplexKey.IsMatch(line)))
            {
                errors.Add($"{path}: use plain YAML keys without escape sequences or complex key syntax.");
            }

            foreach (var line in text.Split('\n').Where(line => !line.TrimStart().StartsWith('#') && ActionKey.IsMatch(line)))
            {
                // Fail closed on flow mappings, aliases, folded scalars, and dynamic action selection.
                var match = ActionLine.Match(line);
                if (!match.Success || !Action.IsMatch(match.Groups["action"].Value))
                {
                    errors.Add($"{path}: use a static action with a full SHA on its own uses line.");
                    continue;
                }

                var reference = match.Groups["action"].Value;
                used.Add(reference);
                if (!allowed.Contains(reference))
                {
                    errors.Add($"{path}: action is absent from actions.allow.");
                }
            }
        }

        if (used.Count == 0 || !allowed.SetEquals(used))
        {
            errors.Add("actions.allow: list exactly the actions used by the workflows.");
        }
    }

    private static XDocument? ParseXml(string path, string text, List<string> errors)
    {
        try
        {
            return XDocument.Parse(text);
        }
        catch (XmlException)
        {
            errors.Add($"{path}: invalid XML.");
            return null;
        }
    }

    private static bool HasCondition(XElement element) => element.AncestorsAndSelf().Any(ancestor =>
        ancestor.Attribute("Condition") is not null || ancestor.Name.LocalName is "When" or "Otherwise" or "Target");

    private static bool IsProject(string path) => new[] { ".csproj", ".fsproj", ".vbproj" }
        .Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
