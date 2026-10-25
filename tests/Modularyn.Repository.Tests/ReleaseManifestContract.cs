using System.Globalization;
using System.Text.Json;
using static Modularyn.Repository.Tests.ContentPackageContract;

namespace Modularyn.Repository.Tests;

internal static class ReleaseManifestContract
{
    internal const string SchemaPath = "schemas/release-manifest/release-manifest.schema.json";
    internal const string ExampleDirectory = "docs/release-manifest/examples/";
    private static readonly string[] Editions = ["community", "standard", "pro"];
    private static readonly string[] States = ["Intended", "Designed", "Implemented", "Preview", "Qualified", "Deprecated", "Unsupported"];
    private static readonly string[] TupleFields =
        ["moduleVersions", "runtime", "database", "uiProfile", "tenancyMode", "deploymentTopology", "providers"];

    public static List<string> Validate(string text, string schemaText,
        IReadOnlyDictionary<string, string> files, DateOnly claimDate)
    {
        try
        {
            var manifest = Parse(text);
            var schema = Parse(schemaText);
            SchemaSubset.CheckDocument(schema, "urn:modularyn:release-manifest:1");
            CheckStateRequirements(manifest, claimDate);
            SchemaSubset.Validate(manifest, schema, schema);
            CheckIdentity(manifest);
            CheckReferences(manifest, files);
            return [];
        }
        catch (Exception exception) when (exception is ContractViolation or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return [exception.Message];
        }
    }

    // Give domain-specific diagnostics before the schema checks the complete shape.
    private static void CheckStateRequirements(JsonElement manifest, DateOnly claimDate)
    {
        Require(Editions.Contains(Text(manifest, "edition"), StringComparer.Ordinal), "unknown edition");
        if (manifest.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Array)
        {
            foreach (var package in packages.EnumerateArray())
            {
                Require(NamesAndLayoutContract.IsPackageId(Text(package, "id")), "malformed package id");
                Require(Editions.Contains(Text(package, "edition"), StringComparer.Ordinal), "unknown package edition");
            }
        }
        if (manifest.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
        {
            foreach (var profile in profiles.EnumerateObject())
                foreach (var field in TupleFields)
                    Require(profile.Value.ValueKind == JsonValueKind.Object && profile.Value.TryGetProperty(field, out _),
                        "missing tuple component: " + field);
        }
        if (!manifest.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object) return;
        foreach (var capability in capabilities.EnumerateObject())
        {
            var value = capability.Value;
            Require(Editions.Contains(Text(value, "edition"), StringComparer.Ordinal), "unknown capability edition");
            Require(value.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
                && entries.GetArrayLength() > 0, "capability requires a nonempty entry list");
            foreach (var entry in entries.EnumerateArray())
            {
                var state = Text(entry, "state");
                var claim = Text(entry, "publicClaim");
                Require(States.Contains(state, StringComparer.Ordinal), "unknown state");
                Require(claim != "planned" || state == "Intended", "planned claim requires Intended");
                Require(claim != "supported" || state is "Qualified" or "Deprecated", "supported claim requires Qualified or Deprecated");
                if (state == "Qualified")
                    Require(entry.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array
                        && evidence.GetArrayLength() > 0, "Qualified requires evidence");
                if (state == "Unsupported")
                    Require(!string.IsNullOrWhiteSpace(Text(entry, "reason")), "Unsupported requires a reason");
                if (state == "Deprecated")
                {
                    Require(DateOnly.TryParseExact(Text(entry, "supportEndsOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var supportEnd), "Deprecated requires a valid support end");
                    Require(claim != "supported" || claimDate <= supportEnd, "supported claim exceeds support period");
                }
            }
        }
    }

    private static void CheckIdentity(JsonElement manifest)
    {
        var version = String(manifest, "version").Split('+')[0];
        var prerelease = version.IndexOf('-');
        var channel = String(manifest, "channel");
        Require((channel == "stable") == (prerelease < 0), "channel must agree with prerelease version");
        var isExample = prerelease >= 0 && version[(prerelease + 1)..].Split('.').Contains("example", StringComparer.Ordinal);
        Require((String(manifest, "kind") == "illustration") == isExample, "illustration requires the example prerelease identifier; release forbids it");
    }

    private static void CheckReferences(JsonElement manifest, IReadOnlyDictionary<string, string> files)
    {
        var edition = String(manifest, "edition");
        var packages = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in Items(manifest, "packages"))
        {
            var id = String(package, "id");
            var owner = String(package, "edition");
            Require(packages.TryAdd(id, package), "duplicate package id");
            Require(!NamesAndLayoutContract.RequiresModuleRole(id) || NamesAndLayoutContract.ModuleRole(id).Length > 0, "module role suffix");
            Require(owner == "community" || owner == edition, "package edition is outside this release");
            Require(!id.StartsWith("Modularyn.Pro.", StringComparison.Ordinal) || owner != "community",
                "paid-module package in Community edition");
            Require(id.StartsWith("Modularyn.Pro.", StringComparison.Ordinal) || owner == "community",
                "Community package must keep its edition");
        }

        var profiles = manifest.GetProperty("profiles");
        Require(profiles.EnumerateObject().Any(), "profiles must not be empty");
        foreach (var profile in profiles.EnumerateObject())
        {
            Require(files.TryGetValue("docs/profiles/" + profile.Name + ".md", out var definition)
                && definition.Contains("Profile key: `" + profile.Name + "`", StringComparison.Ordinal), "unknown profile key");
            var modules = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var module in Items(profile.Value, "moduleVersions"))
            {
                var id = String(module, "id");
                var version = String(module, "version");
                Require(modules.TryAdd(id, version), "duplicate module id");
                Require(packages.TryGetValue(id, out var package) && String(package, "id") == id
                    && String(package, "version") == version, "module version must match a release package");
            }
            foreach (var uiPackage in Items(profile.Value.GetProperty("uiProfile"), "packages"))
                Require(modules.ContainsKey(uiPackage.GetString()!), "UI package is outside the profile tuple");
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var provider in Items(profile.Value, "providers"))
            {
                Require(roles.Add(String(provider, "role")), "duplicate provider role");
                Require(modules.TryGetValue(String(provider, "package"), out var version)
                    && version == String(provider, "version"), "provider version must match the profile tuple");
            }
            CheckKnownProfile(profile.Name, profile.Value, roles);
        }

        var register = Items(Parse(files["docs/coverage/register.json"]), "entries")
            .ToDictionary(entry => String(entry, "id"), StringComparer.Ordinal);
        var capabilities = manifest.GetProperty("capabilities");
        Require(capabilities.EnumerateObject().Any(), "capabilities must not be empty");
        var qualified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities.EnumerateObject())
        {
            Require(register.TryGetValue(capability.Name, out var entry), "unknown register id");
            var value = capability.Value;
            var owner = String(value, "edition");
            Require(owner == "community" || owner == edition, "capability edition is outside this release");
            Require((String(entry, "edition") == "paid") == (owner != "community"), "capability edition must match the register");
            var assignedProfiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stateEntry in Items(value, "entries"))
            {
                var applicable = Items(stateEntry, "profiles").Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
                foreach (var profile in applicable)
                {
                    Require(profiles.TryGetProperty(profile, out _), "unknown capability profile");
                    Require(assignedProfiles.Add(profile), "capability entries must name disjoint profiles");
                    if (String(stateEntry, "state") == "Qualified") qualified.Add(capability.Name + "@" + profile);
                }
                if (stateEntry.TryGetProperty("evidence", out var evidence))
                {
                    var evidenced = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var binding in evidence.EnumerateArray())
                    {
                        var profile = String(binding, "profile");
                        Require(applicable.Contains(profile), "evidence profile is outside the capability entry");
                        Require(evidenced.Add(profile), "duplicate evidence profile");
                        foreach (var reference in Items(binding, "references"))
                            Require(String(manifest, "kind") == "illustration"
                                || !reference.GetString()!.StartsWith("urn:modularyn:example:", StringComparison.Ordinal),
                                "release cannot use illustration evidence");
                    }
                    if (String(stateEntry, "state") == "Qualified")
                        Require(evidenced.SetEquals(applicable), "Qualified requires evidence for every profile");
                }
            }
        }
        var subset = manifest.GetProperty("qualifiedSubset");
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in Items(subset, "members"))
            Require(members.Add(String(member, "capability") + "@" + String(member, "profile")), "duplicate qualified subset member");
        Require(members.SetEquals(qualified), "qualified subset must contain exactly the Qualified capability/profile pairs");
        Require(members.Count > 0 || String(subset, "label") == "No qualified capabilities", "empty qualified subset requires its explicit label");
    }

    private static void CheckKnownProfile(string key, JsonElement profile, HashSet<string> roles)
    {
        if (key is not ("reference" or "website")) return;
        var runtime = profile.GetProperty("runtime");
        Require(String(runtime, "name") == ".NET" && String(runtime, "version").StartsWith("10.", StringComparison.Ordinal),
            "profile runtime must match its definition");
        Require(String(profile.GetProperty("database"), "name") == "PostgreSQL", "profile database must match its definition");
        Require(String(profile.GetProperty("uiProfile"), "name") == (key == "reference" ? "react-admin-razor" : "minimal-react-admin-razor"),
            "profile UI must match its definition");
        Require(String(profile, "tenancyMode") == (key == "reference" ? "shared-database" : "single-tenant-single-site"),
            "profile tenancy must match its definition");
        Require(String(profile, "deploymentTopology") == "single-host-container"
            || key == "reference" && String(profile, "deploymentTopology") == "single-host", "profile topology must match its definition");
        Require(roles.SetEquals(["persistence", "files", "email"]), "profile provider roles must match its definition");
    }

    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
}
