using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Modularity;

/// <summary>
/// The SHA-256 digest of a manifest's canonical form, as 64 lowercase hexadecimal digits. Every declaration
/// contributes in declaration order, with exact version text including build metadata, so any change to an
/// identifier, version, range, provision, requirement, route or asset changes the digest.
/// </summary>
public static class ModuleManifestDigest
{
    private const string Format = "modularyn.module-manifest";

    public static string Compute(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("formatVersion", 1);
            writer.WriteString("id", manifest.Id);
            writer.WriteString("version", manifest.Version.Value);
            Items(writer, "dependencies", manifest.Dependencies, item =>
            {
                writer.WriteString("moduleId", item.ModuleId);
                Range(writer, "versions", item.Versions);
                writer.WriteBoolean("optional", item.Optional);
            });
            Provisions(writer, "providedContracts", manifest.ProvidedContracts);
            Requirements(writer, "requiredContracts", manifest.RequiredContracts);
            Provisions(writer, "providedCapabilities", manifest.ProvidedCapabilities);
            Requirements(writer, "requiredCapabilities", manifest.RequiredCapabilities);
            Provisions(writer, "providedProviders", manifest.ProvidedProviders);
            Requirements(writer, "requiredProviders", manifest.RequiredProviders);
            Items(writer, "routes", manifest.Routes, item =>
            {
                writer.WriteString("method", item.Method);
                writer.WriteString("template", item.Template);
            });
            Assets(writer, "definitions", manifest.Definitions);
            Assets(writer, "migrations", manifest.Migrations);
            Assets(writer, "backgroundHandlers", manifest.BackgroundHandlers);
            Assets(writer, "healthChecks", manifest.HealthChecks);
            Range(writer, "runtimeVersions", manifest.RuntimeVersions);
            Range(writer, "frameworkVersions", manifest.FrameworkVersions);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void Provisions(Utf8JsonWriter writer, string name, IReadOnlyList<ModuleProvision> items) => Items(writer, name, items, item =>
    {
        writer.WriteString("name", item.Name);
        writer.WriteString("version", item.Version.Value);
        writer.WriteBoolean("exclusive", item.Exclusive);
    });

    private static void Requirements(Utf8JsonWriter writer, string name, IReadOnlyList<ModuleRequirement> items) => Items(writer, name, items, item =>
    {
        writer.WriteString("name", item.Name);
        Range(writer, "versions", item.Versions);
        writer.WriteBoolean("optional", item.Optional);
    });

    private static void Assets(Utf8JsonWriter writer, string name, IReadOnlyList<ModuleAsset> items) => Items(writer, name, items, item =>
    {
        writer.WriteString("name", item.Name);
        writer.WriteString("version", item.Version.Value);
    });

    private static void Items<T>(Utf8JsonWriter writer, string name, IReadOnlyList<T> items, Action<T> write)
    {
        writer.WriteStartArray(name);
        foreach (var item in items)
        {
            writer.WriteStartObject();
            write(item);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void Range(Utf8JsonWriter writer, string name, VersionRange range)
    {
        writer.WriteStartObject(name);
        if (range.Minimum is null) writer.WriteNull("minimum");
        else writer.WriteString("minimum", range.Minimum.Value);
        writer.WriteBoolean("includeMinimum", range.IncludeMinimum);
        if (range.Maximum is null) writer.WriteNull("maximum");
        else writer.WriteString("maximum", range.Maximum.Value);
        writer.WriteBoolean("includeMaximum", range.IncludeMaximum);
        writer.WriteEndObject();
    }
}
