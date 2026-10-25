using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ReleaseManifestTests
{
    private static readonly DateOnly ClaimDate = new(2026, 10, 25);

    [Fact]
    public void Every_example_satisfies_the_schema_and_semantics_and_all_editions_are_illustrated()
    {
        var examples = RepositoryFiles.Current.Where(file => file.Key.StartsWith(ReleaseManifestContract.ExampleDirectory, StringComparison.Ordinal)
            && file.Key.EndsWith(".json", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(examples);
        var editions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, text) in examples)
        {
            Assert.Empty(Validate(text));
            var manifest = JsonNode.Parse(text)!;
            editions.Add(manifest["edition"]!.GetValue<string>());
            Assert.Equal("illustration", manifest["kind"]!.GetValue<string>());
            Assert.Contains("-example.", manifest["version"]!.GetValue<string>());
            Assert.True(new[] { "reference", "website" }.All(profile => manifest["profiles"]!.AsObject().ContainsKey(profile)));
            Assert.True(new HashSet<string>(["Intended", "Designed", "Implemented", "Preview", "Qualified", "Deprecated", "Unsupported"], StringComparer.Ordinal)
                .SetEquals(manifest["capabilities"]!.AsObject().Select(entry => entry.Value!["state"]!.GetValue<string>())));
        }
        Assert.True(editions.SetEquals(["community", "standard", "pro"]));
    }

    [Theory]
    [InlineData("unknown-state", "unknown state")]
    [InlineData("planned-without-intent", "planned claim requires Intended")]
    [InlineData("supported-without-qualification", "supported claim requires Qualified or Deprecated")]
    [InlineData("qualified-without-evidence", "Qualified requires evidence")]
    [InlineData("empty-evidence", "Qualified requires evidence")]
    [InlineData("missing-profile-evidence", "evidence for every profile")]
    [InlineData("unrelated-evidence", "evidence profile is outside")]
    [InlineData("duplicate-evidence-profile", "duplicate evidence profile")]
    [InlineData("unsupported-without-reason", "Unsupported requires a reason")]
    [InlineData("blank-reason", "Unsupported requires a reason")]
    [InlineData("deprecated-without-end", "Deprecated requires a valid support end")]
    [InlineData("invalid-support-date", "Deprecated requires a valid support end")]
    [InlineData("unknown-register-id", "unknown register id")]
    [InlineData("unknown-profile-key", "unknown profile key")]
    [InlineData("unknown-capability-profile", "unknown capability profile")]
    [InlineData("unknown-edition", "unknown edition")]
    [InlineData("unknown-package-edition", "unknown package edition")]
    [InlineData("unknown-capability-edition", "unknown capability edition")]
    [InlineData("malformed-package-id", "malformed package id")]
    [InlineData("package-id-newline", "malformed package id")]
    [InlineData("paid-package-in-community", "paid-module package in Community")]
    [InlineData("paid-capability-in-community", "capability edition must match the register")]
    [InlineData("cross-edition-package", "package edition is outside")]
    [InlineData("duplicate-package", "duplicate package id")]
    [InlineData("duplicate-module", "duplicate module id")]
    [InlineData("module-version-mismatch", "module version must match")]
    [InlineData("provider-version-mismatch", "provider version must match")]
    [InlineData("duplicate-provider-role", "duplicate provider role")]
    [InlineData("ui-outside-tuple", "UI package is outside")]
    [InlineData("runtime-outside-profile", "profile runtime")]
    [InlineData("database-outside-profile", "profile database")]
    [InlineData("topology-outside-profile", "profile topology")]
    [InlineData("subset-missing-member", "qualified subset must contain exactly")]
    [InlineData("subset-extra-member", "qualified subset must contain exactly")]
    [InlineData("subset-duplicate-member", "schema duplicate array item")]
    [InlineData("subset-missing-label", "schema missing required property")]
    [InlineData("empty-profiles", "profiles must not be empty")]
    [InlineData("empty-capabilities", "capabilities must not be empty")]
    [InlineData("license-mismatch", "schema oneOf")]
    [InlineData("malformed-version", "schema string pattern")]
    [InlineData("malformed-commit", "schema string pattern")]
    [InlineData("stable-prerelease", "channel must agree")]
    [InlineData("preview-without-prerelease", "channel must agree")]
    [InlineData("illustration-without-label", "example prerelease identifier")]
    [InlineData("release-with-example-label", "example prerelease identifier")]
    [InlineData("release-with-illustration-evidence", "release cannot use illustration evidence")]
    [InlineData("format-version", "schema constant mismatch")]
    [InlineData("extra-property", "schema prohibits value")]
    public void A_single_manifest_violation_is_rejected_for_its_reason(string mutation, string diagnostic)
    {
        var manifest = Example();
        Assert.Empty(Validate(manifest));
        var capabilities = manifest["capabilities"]!.AsObject();
        var qualified = capabilities["CMS-001"]!;
        var deprecated = capabilities["CMS-005"]!;
        var unsupported = capabilities["CMS-011"]!;
        var reference = manifest["profiles"]!["reference"]!;
        switch (mutation)
        {
            case "unknown-state": unsupported["state"] = "Supported"; break;
            case "planned-without-intent": capabilities["DEV-007"]!["publicClaim"] = "planned"; break;
            case "supported-without-qualification": capabilities["CMS-002"]!["publicClaim"] = "supported"; break;
            case "qualified-without-evidence": qualified.AsObject().Remove("evidence"); break;
            case "empty-evidence": qualified["evidence"] = new JsonArray(); break;
            case "missing-profile-evidence": qualified["evidence"]!.AsArray().RemoveAt(1); break;
            case "unrelated-evidence": qualified["evidence"]![0]!["profile"] = "unknown"; break;
            case "duplicate-evidence-profile": qualified["evidence"]![1]!["profile"] = "reference"; break;
            case "unsupported-without-reason": unsupported.AsObject().Remove("reason"); break;
            case "blank-reason": unsupported["reason"] = " "; break;
            case "deprecated-without-end": deprecated.AsObject().Remove("supportEndsOn"); break;
            case "invalid-support-date": deprecated["supportEndsOn"] = "2027-02-29"; break;
            case "unknown-register-id":
                capabilities["CMS-999"] = unsupported.DeepClone();
                capabilities.Remove("CMS-011");
                break;
            case "unknown-profile-key":
                manifest["profiles"]!["unknown"] = reference.DeepClone();
                manifest["profiles"]!.AsObject().Remove("reference");
                break;
            case "unknown-capability-profile": unsupported["profiles"]![0] = "unknown"; break;
            case "unknown-edition": manifest["edition"] = "enterprise"; break;
            case "unknown-package-edition": manifest["packages"]![0]!["edition"] = "enterprise"; break;
            case "unknown-capability-edition": unsupported["edition"] = "enterprise"; break;
            case "malformed-package-id": manifest["packages"]![0]!["id"] = "Modularyn.CMS.Content"; break;
            case "package-id-newline": manifest["packages"]![0]!["id"] = "Modularyn\n"; break;
            case "paid-package-in-community": manifest["packages"]![0]!["id"] = "Modularyn.Pro.Studio.Workspace"; break;
            case "paid-capability-in-community":
                capabilities["LOW-001"] = unsupported.DeepClone();
                capabilities.Remove("CMS-011");
                break;
            case "cross-edition-package": manifest["packages"]![0]!["edition"] = "standard"; break;
            case "duplicate-package":
                var package = manifest["packages"]![0]!.DeepClone();
                package["version"] = "0.2.0-example.1";
                manifest["packages"]!.AsArray().Add(package);
                break;
            case "duplicate-module":
                var module = reference["moduleVersions"]![0]!.DeepClone();
                module["version"] = "0.2.0-example.1";
                reference["moduleVersions"]!.AsArray().Add(module);
                break;
            case "module-version-mismatch": reference["moduleVersions"]![0]!["version"] = "0.2.0-example.1"; break;
            case "provider-version-mismatch": reference["providers"]![0]!["version"] = "0.2.0-example.1"; break;
            case "duplicate-provider-role": reference["providers"]![1]!["role"] = "persistence"; break;
            case "ui-outside-tuple": reference["uiProfile"]!["packages"]![0] = "Modularyn.UI.Blazor"; break;
            case "runtime-outside-profile": reference["runtime"]!["version"] = "11.0.0"; break;
            case "database-outside-profile": reference["database"]!["name"] = "AnotherDatabase"; break;
            case "topology-outside-profile": reference["deploymentTopology"] = "multi-host"; break;
            case "subset-missing-member": manifest["qualifiedSubset"]!["members"]!.AsArray().RemoveAt(1); break;
            case "subset-extra-member":
                manifest["qualifiedSubset"]!["members"]!.AsArray().Add(new JsonObject { ["capability"] = "CMS-002", ["profile"] = "reference" });
                break;
            case "subset-duplicate-member":
                manifest["qualifiedSubset"]!["members"]!.AsArray().Add(manifest["qualifiedSubset"]!["members"]![0]!.DeepClone());
                break;
            case "subset-missing-label": manifest["qualifiedSubset"]!.AsObject().Remove("label"); break;
            case "empty-profiles": manifest["profiles"] = new JsonObject(); break;
            case "empty-capabilities": manifest["capabilities"] = new JsonObject(); break;
            case "license-mismatch": manifest["license"] = "PolyForm-Noncommercial-1.0.0"; break;
            case "malformed-version": manifest["version"] = "0.1.0-01"; break;
            case "malformed-commit": manifest["sourceCommit"] = "main"; break;
            case "stable-prerelease": manifest["channel"] = "stable"; break;
            case "preview-without-prerelease": manifest["version"] = "0.1.0"; break;
            case "illustration-without-label": manifest["version"] = "0.1.0-preview.1"; break;
            case "release-with-example-label": manifest["kind"] = "release"; break;
            case "release-with-illustration-evidence":
                manifest["kind"] = "release";
                manifest["version"] = "0.1.0-preview.1";
                break;
            case "format-version": manifest["formatVersion"] = 2; break;
            case "extra-property": manifest["extra"] = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Contains(Validate(manifest), error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("moduleVersions")]
    [InlineData("runtime")]
    [InlineData("database")]
    [InlineData("uiProfile")]
    [InlineData("tenancyMode")]
    [InlineData("deploymentTopology")]
    [InlineData("providers")]
    public void Every_tuple_component_is_required(string field)
    {
        var manifest = Example();
        manifest["profiles"]!["reference"]!.AsObject().Remove(field);
        Assert.Contains("missing tuple component: " + field, Validate(manifest));
    }

    [Theory]
    [InlineData("Intended")]
    [InlineData("Designed")]
    [InlineData("Implemented")]
    [InlineData("Preview")]
    [InlineData("Unsupported")]
    public void No_other_state_can_claim_support(string state)
    {
        var manifest = Example();
        manifest["capabilities"]!["CMS-011"]!["state"] = state;
        manifest["capabilities"]!["CMS-011"]!["publicClaim"] = "supported";
        Assert.Contains("supported claim requires Qualified or Deprecated", Validate(manifest));
    }

    [Theory]
    [InlineData("2027-10-24", true)]
    [InlineData("2027-10-25", true)]
    [InlineData("2027-10-26", false)]
    public void Deprecated_support_ends_after_the_inclusive_UTC_date(string date, bool supported)
    {
        var manifest = Example();
        manifest["capabilities"]!["CMS-005"]!["publicClaim"] = "supported";
        var errors = Validate(manifest.ToJsonString(), DateOnly.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        if (supported) Assert.Empty(errors);
        else Assert.Contains("supported claim exceeds support period", errors);
    }

    [Fact]
    public void Expired_deprecation_and_an_explicit_empty_qualified_subset_make_no_support_claim()
    {
        var manifest = Example();
        var entry = manifest["capabilities"]!["CMS-001"]!;
        entry["state"] = "Implemented";
        entry["publicClaim"] = "none";
        entry.AsObject().Remove("evidence");
        manifest["qualifiedSubset"]!["members"] = new JsonArray();
        manifest["qualifiedSubset"]!["label"] = "No qualified capabilities";
        Assert.Empty(Validate(manifest.ToJsonString(), new DateOnly(2028, 1, 1)));
        manifest["qualifiedSubset"]!["label"] = "Content subset";
        Assert.Contains("empty qualified subset requires its explicit label", Validate(manifest));
    }

    [Theory]
    [InlineData("0.0.0", true)]
    [InlineData("1.2.3-example.1+build.007", true)]
    [InlineData("1.2.3-0.alpha-1", true)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3-01", false)]
    [InlineData("1.2.3-", false)]
    [InlineData("1.2.3+", false)]
    [InlineData("1.2.3\n", false)]
    [InlineData("1.2.*", false)]
    public void SemVer_is_exact(string version, bool valid)
    {
        var schema = ContentPackageContract.Parse(RepositoryFiles.Current[ReleaseManifestContract.SchemaPath]);
        var exception = Record.Exception(() => SchemaSubset.Validate(JsonSerializer.SerializeToElement(version), schema.GetProperty("$defs").GetProperty("semver"), schema));
        if (valid) Assert.Null(exception);
        else Assert.IsType<ContractViolation>(exception);
    }

    [Fact]
    public void Duplicate_JSON_fields_are_rejected_and_equivalent_integer_notation_is_valid()
    {
        var text = RepositoryFiles.Current[ReleaseManifestContract.ExampleDirectory + "community.json"];
        Assert.Contains("duplicate JSON property", Validate(text.Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1")));
        Assert.Empty(Validate(text.Replace("\"formatVersion\": 1", "\"formatVersion\": 1e0")));
        Assert.Empty(Validate(text.Replace("\n", "\r\n")));
    }

    [Fact]
    public void Schema_changes_cannot_silently_add_unevaluated_rules_or_remote_references()
    {
        var schema = JsonNode.Parse(RepositoryFiles.Current[ReleaseManifestContract.SchemaPath])!;
        schema["unevaluatedProperties"] = false;
        Assert.Contains(Validate(Example().ToJsonString(), schema: schema.ToJsonString()), error => error.Contains("unsupported schema keyword", StringComparison.Ordinal));
        schema.AsObject().Remove("unevaluatedProperties");
        schema["$defs"]!["profile"]!["$ref"] = "urn:modularyn:another-schema";
        Assert.Contains(Validate(Example().ToJsonString(), schema: schema.ToJsonString()), error => error.Contains("require local schema reference", StringComparison.Ordinal));
    }

    private static JsonNode Example() => JsonNode.Parse(RepositoryFiles.Current[ReleaseManifestContract.ExampleDirectory + "community.json"])!;
    private static List<string> Validate(JsonNode manifest) => Validate(manifest.ToJsonString());
    private static List<string> Validate(string text, DateOnly? date = null, string? schema = null) => ReleaseManifestContract.Validate(text,
        schema ?? RepositoryFiles.Current[ReleaseManifestContract.SchemaPath], RepositoryFiles.Current, date ?? ClaimDate);
}
