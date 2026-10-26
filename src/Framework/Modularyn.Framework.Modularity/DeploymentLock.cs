using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Modularity;

/// <summary>
/// The deployment lock manifest, format version 1: the exact modules a release composes, each with its exact
/// version, manifest digest, migrations and the content hashes of its package files. Unknown fields, duplicate
/// properties and unsupported versions are refused rather than skipped.
/// </summary>
public sealed class DeploymentLock
{
    public const string FormatName = "modularyn.deployment-lock";
    public const int FormatVersion = 1;
    /// <summary>Larger lock files are refused before parsing.</summary>
    public const int MaximumBytes = 16 * 1024 * 1024;

    public DeploymentLock(IEnumerable<LockedModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var copy = modules.ToArray();
        if (copy.Length == 0) throw new ArgumentException("A deployment lock must list at least one module.", nameof(modules));
        if (copy.Any(module => module is null)) throw new ArgumentException("Modules cannot contain null entries.", nameof(modules));
        var duplicate = copy.GroupBy(module => module.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Module '{duplicate.Key}' is listed more than once.", nameof(modules));
        var conflict = copy.SelectMany(module => module.Files).GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Select(file => file.Sha256).Distinct(StringComparer.Ordinal).Count() > 1
                || group.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() > 1);
        if (conflict is not null) throw new ArgumentException($"Package file '{conflict.Key}' is listed with different spellings or content hashes.", nameof(modules));
        Modules = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<LockedModule> Modules { get; }

    /// <summary>Parses format version 1 strictly. Errors name the failing location, never file system paths.</summary>
    public static DeploymentLock Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowDuplicateProperties = false,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        }
        catch (JsonException exception)
        {
            throw new DeploymentLockFormatException(exception.LineNumber is { } line
                ? $"the lock is not valid JSON (line {(line + 1).ToString(CultureInfo.InvariantCulture)})"
                : "the lock is not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            Fields(root, "lock", "format", "formatVersion", "modules");
            if (String(root, "format", "lock") != FormatName) throw new DeploymentLockFormatException($"format must be '{FormatName}'");
            var version = root.GetProperty("formatVersion");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != FormatVersion
                || version.GetRawText() != FormatVersion.ToString(CultureInfo.InvariantCulture))
                throw new DeploymentLockFormatException($"formatVersion must be the integer {FormatVersion}");
            var modules = Items(root, "modules", "lock").Select((item, index) => ReadModule(item, $"modules[{index}]")).ToArray();
            try
            {
                return new DeploymentLock(modules);
            }
            catch (ArgumentException exception)
            {
                throw new DeploymentLockFormatException(exception.Message.Split(" (Parameter", 2)[0].TrimEnd('.'));
            }
        }
    }

    /// <summary>Writes the canonical, indented form that <see cref="Parse"/> accepts.</summary>
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteStartArray("modules");
            foreach (var module in Modules)
            {
                writer.WriteStartObject();
                writer.WriteString("id", module.Id);
                writer.WriteString("version", module.Version.Value);
                writer.WriteString("manifestSha256", module.ManifestSha256);
                writer.WriteStartArray("migrations");
                foreach (var migration in module.Migrations)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", migration.Name);
                    writer.WriteString("version", migration.Version.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("files");
                foreach (var file in module.Files)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", file.Path);
                    writer.WriteString("sha256", file.Sha256);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static LockedModule ReadModule(JsonElement item, string location)
    {
        Fields(item, location, "id", "version", "manifestSha256", "migrations", "files");
        var id = Name(String(item, "id", location), location + ".id");
        var version = Version(String(item, "version", location), location + ".version");
        var digest = String(item, "manifestSha256", location);
        if (!LockedFile.IsSha256(digest)) throw new DeploymentLockFormatException(location + ".manifestSha256 must be 64 lowercase hexadecimal digits");
        var migrations = Items(item, "migrations", location).Select((migration, index) =>
        {
            var at = $"{location}.migrations[{index}]";
            Fields(migration, at, "name", "version");
            return new ModuleAsset(Name(String(migration, "name", at), at + ".name"), Version(String(migration, "version", at), at + ".version"));
        }).ToArray();
        var files = Items(item, "files", location).Select((file, index) =>
        {
            var at = $"{location}.files[{index}]";
            Fields(file, at, "path", "sha256");
            var path = String(file, "path", at);
            if (!LockedFile.IsSafePath(path)) throw new DeploymentLockFormatException(at + ".path must be a safe relative path");
            var sha256 = String(file, "sha256", at);
            if (!LockedFile.IsSha256(sha256)) throw new DeploymentLockFormatException(at + ".sha256 must be 64 lowercase hexadecimal digits");
            return new LockedFile(path, sha256);
        }).ToArray();
        try
        {
            return new LockedModule(id, version, digest, migrations, files);
        }
        catch (ArgumentException exception)
        {
            throw new DeploymentLockFormatException(location + ": " + exception.Message.Split(" (Parameter", 2)[0].TrimEnd('.'));
        }
    }

    private static void Fields(JsonElement element, string location, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new DeploymentLockFormatException(location + " must be an object");
        var present = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (!present.ToHashSet(StringComparer.Ordinal).SetEquals(names) || present.Length != names.Length)
            throw new DeploymentLockFormatException($"{location} must have exactly the fields {string.Join(", ", names)}");
    }

    private static string String(JsonElement element, string name, string location)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw new DeploymentLockFormatException($"{location}.{name} must be a string");
        return value.GetString()!;
    }

    private static JsonElement[] Items(JsonElement element, string name, string location)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw new DeploymentLockFormatException($"{location}.{name} must be an array");
        return value.EnumerateArray().ToArray();
    }

    private static string Name(string value, string location)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            throw new DeploymentLockFormatException(location + " must be a nonblank name without surrounding whitespace or control characters");
        return value;
    }

    private static SemanticVersion Version(string value, string location) => SemanticVersion.TryParse(value, out var version)
        ? version : throw new DeploymentLockFormatException(location + " must be a SemVer 2.0.0 version");
}

/// <summary>One module as the release locked it.</summary>
public sealed class LockedModule
{
    public LockedModule(string id, SemanticVersion version, string manifestSha256, IEnumerable<ModuleAsset> migrations, IEnumerable<LockedFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id != id.Trim() || id.Any(char.IsControl)) throw new ArgumentException("Module identifiers have no surrounding whitespace or control characters.", nameof(id));
        Id = id;
        Version = version ?? throw new ArgumentNullException(nameof(version));
        ArgumentNullException.ThrowIfNull(manifestSha256);
        if (!LockedFile.IsSha256(manifestSha256)) throw new ArgumentException("The manifest digest must be 64 lowercase hexadecimal digits.", nameof(manifestSha256));
        ManifestSha256 = manifestSha256;
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(files);
        var migrationCopy = migrations.ToArray();
        var fileCopy = files.ToArray();
        if (migrationCopy.Any(item => item is null) || fileCopy.Any(item => item is null)) throw new ArgumentException("Declarations cannot contain null entries.");
        if (fileCopy.Length == 0) throw new ArgumentException($"Module '{id}' must list at least one package file.", nameof(files));
        var repeated = fileCopy.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (repeated is not null) throw new ArgumentException($"Module '{id}' lists package file '{repeated.Key}' more than once.", nameof(files));
        Migrations = Array.AsReadOnly(migrationCopy);
        Files = Array.AsReadOnly(fileCopy);
    }

    public string Id { get; }
    public SemanticVersion Version { get; }
    public string ManifestSha256 { get; }
    public IReadOnlyList<ModuleAsset> Migrations { get; }
    public IReadOnlyList<LockedFile> Files { get; }

    /// <summary>Describes a reviewed manifest and its package files for a release's lock.</summary>
    public static LockedModule Describe(ModuleManifest manifest, IEnumerable<LockedFile> files)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new(manifest.Id, manifest.Version, ModuleManifestDigest.Compute(manifest), manifest.Migrations, files);
    }
}

/// <summary>A package file relative to the content root, with its SHA-256 content hash.</summary>
public sealed record LockedFile
{
    private static readonly Regex Hash = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Segment = new(@"\A[A-Za-z0-9_+-](?:[A-Za-z0-9._+-]*[A-Za-z0-9_+-])?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public LockedFile(string path, string sha256)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(sha256);
        if (!IsSafePath(path)) throw new ArgumentException("Use a relative path of '/'-separated names without traversal, links or drive letters.", nameof(path));
        if (!IsSha256(sha256)) throw new ArgumentException("The content hash must be 64 lowercase hexadecimal digits.", nameof(sha256));
        Path = path;
        Sha256 = sha256;
    }

    public string Path { get; }
    public string Sha256 { get; }

    internal static bool IsSha256(string value) => Hash.IsMatch(value);

    /// <summary>
    /// Accepts only '/'-separated segments of letters, digits, '.', '_', '+' and '-' that neither start nor end with
    /// '.', so no segment can be '.', '..', rooted, a drive, a stream or a reserved device name.
    /// </summary>
    internal static bool IsSafePath(string value)
    {
        if (value.Length is 0 or > 1024) return false;
        var segments = value.Split('/');
        return segments.All(segment => segment.Length <= 255 && Segment.IsMatch(segment) && !ReservedNames.Contains(segment.Split('.')[0]));
    }
}

/// <summary>A lock that is not valid format version 1. The message names the failing location only.</summary>
public sealed class DeploymentLockFormatException(string message) : FormatException(message);
