using System.Text.Json.Nodes;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class EditionMapRejectionTests
{
    [Theory]
    [InlineData("missing-map", "edition input is missing")]
    [InlineData("missing-readme", "edition input is missing")]
    [InlineData("extra-root-field", "edition fields must be exactly")]
    [InlineData("missing-root-field", "edition fields must be exactly")]
    [InlineData("wrong-version", "edition map version must be 1")]
    [InlineData("string-version", "edition map version must be 1")]
    [InlineData("wrong-register", "edition map must name its exact inputs")]
    [InlineData("wrong-names", "edition map must name its exact inputs")]
    [InlineData("null-entries", "entries must be an array")]
    [InlineData("null-foundation", "foundation must be an array")]
    [InlineData("null-entry", "edition fields must be exactly")]
    [InlineData("extra-entry-field", "edition fields must be exactly")]
    [InlineData("empty-key", "key must be a nonempty single-line string")]
    [InlineData("malformed-key", "malformed edition key")]
    [InlineData("duplicate-key", "duplicate edition key")]
    [InlineData("duplicate-foundation-key", "duplicate edition key")]
    [InlineData("empty-title", "title must be a nonempty single-line string")]
    [InlineData("numeric-title", "title must be a nonempty single-line string")]
    [InlineData("multiline-title", "title must be a nonempty single-line string")]
    [InlineData("long-title", "title must be a nonempty single-line string")]
    [InlineData("unknown-edition", "edition must be community or paid")]
    [InlineData("multiple-editions", "edition must be a nonempty single-line string")]
    [InlineData("community-paid-home", "Community entry cannot use a Modularyn.Pro home")]
    [InlineData("paid-community-home", "paid entry requires a Modularyn.Pro home")]
    [InlineData("undeclared-home", "home must be a declared family or product home")]
    [InlineData("empty-reason", "reason must be a nonempty single-line string")]
    [InlineData("multiline-reason", "reason must be a nonempty single-line string")]
    [InlineData("long-reason", "reason must be a nonempty single-line string")]
    [InlineData("two-sentences", "reason must be one sentence")]
    [InlineData("unfinished-sentence", "reason must be one sentence")]
    [InlineData("machine-path", "edition text must be portable")]
    [InlineData("empty-coverage", "coverage must not be empty")]
    [InlineData("object-coverage", "coverage must be an array")]
    [InlineData("numeric-coverage-id", "coverage id must be a string")]
    [InlineData("unknown-coverage-id", "unknown coverage id")]
    [InlineData("duplicate-coverage-id", "duplicate coverage id")]
    [InlineData("overlapping-coverage", "duplicate coverage id")]
    [InlineData("foundation-overlap", "duplicate coverage id")]
    [InlineData("missing-coverage-id", "every register id must be covered exactly once")]
    [InlineData("missing-foundation-id", "every register id must be covered exactly once")]
    [InlineData("register-edition-drift", "edition must agree with register")]
    [InlineData("payments-paid", "edition must agree with register")]
    [InlineData("cms-paid", "edition must agree with register")]
    [InlineData("saas-community", "edition must agree with register")]
    [InlineData("shared-runner-paid", "edition must agree with register")]
    [InlineData("studio-runner-community", "edition must agree with register")]
    [InlineData("low-code-community", "edition must agree with register")]
    [InlineData("missing-commerce", "exact catalog key set required")]
    [InlineData("renamed-catalog-key", "exact catalog key set required")]
    [InlineData("merged-tooling-entries", "exact catalog key set required")]
    [InlineData("swapped-tooling-ids", "catalog coverage anchor missing")]
    [InlineData("swapped-low-code-ids", "catalog coverage anchor missing")]
    [InlineData("cms-id-in-another-module", "catalog coverage anchor missing")]
    [InlineData("ui-id-in-another-entry", "catalog coverage anchor missing")]
    [InlineData("commerce-paid", "Commerce must be Community")]
    [InlineData("numbered-commerce", "Commerce must be Community")]
    [InlineData("catalog-in-foundation", "exact catalog key set required")]
    [InlineData("renamed-foundation", "exact cross-cutting foundation groups required")]
    [InlineData("swapped-foundation-ids", "cross-cutting foundation must cover only its complete register family")]
    [InlineData("foundation-id-in-catalog", "cross-cutting foundation must cover only its complete register family")]
    public void A_map_violation_is_rejected_for_its_specific_reason(string mutation, string diagnostic)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        Assert.Empty(EditionMapContract.Validate(files));
        var map = JsonNode.Parse(files[EditionMapContract.MapPath])!;
        var entries = map["entries"]!.AsArray();
        var foundation = map["foundation"]!.AsArray();
        var first = entries[0]!;
        var paid = Entry(entries, "ai-management");
        switch (mutation)
        {
            case "missing-map": break;
            case "missing-readme": files.Remove(EditionMapContract.ReadmePath); break;
            case "extra-root-field": map["extra"] = true; break;
            case "missing-root-field": map.AsObject().Remove("names"); break;
            case "wrong-version": map["version"] = 2; break;
            case "string-version": map["version"] = "1"; break;
            case "wrong-register": map["register"] = "docs/coverage/another.json"; break;
            case "wrong-names": map["names"] = "docs/names-and-layout.md"; break;
            case "null-entries": map["entries"] = null; break;
            case "null-foundation": map["foundation"] = null; break;
            case "null-entry": entries[0] = null; break;
            case "extra-entry-field": first["extra"] = true; break;
            case "empty-key": first["key"] = ""; break;
            case "malformed-key": first["key"] = "Account Basics"; break;
            case "duplicate-key": entries.Add(first.DeepClone()); break;
            case "duplicate-foundation-key": foundation[0]!["key"] = "accounts"; break;
            case "empty-title": first["title"] = " "; break;
            case "numeric-title": first["title"] = 1; break;
            case "multiline-title": first["title"] = "Accounts\nAdministration"; break;
            case "long-title": first["title"] = new string('a', 65); break;
            case "unknown-edition": first["edition"] = "standard"; break;
            case "multiple-editions": first["edition"] = new JsonArray("community", "paid"); break;
            case "community-paid-home": first["home"] = "Modularyn.Pro.*"; break;
            case "paid-community-home": paid["home"] = "Modularyn.Modules.*"; break;
            case "undeclared-home": first["home"] = "Modularyn.Modules.Accounts.*"; break;
            case "empty-reason": first["reason"] = ""; break;
            case "multiline-reason": first["reason"] = "Community includes accounts.\n"; break;
            case "long-reason": first["reason"] = new string('a', 301); break;
            case "two-sentences": first["reason"] = "Community includes accounts. It also includes administration."; break;
            case "unfinished-sentence": first["reason"] = "Community includes accounts"; break;
            case "machine-path": first["reason"] = string.Join('/', "", "home", "person", "project") + "."; break;
            case "empty-coverage": first["coverage"] = new JsonArray(); break;
            case "object-coverage": first["coverage"] = new JsonObject(); break;
            case "numeric-coverage-id": first["coverage"]![0] = 1; break;
            case "unknown-coverage-id": first["coverage"]![0] = "MOD-999"; break;
            case "duplicate-coverage-id": first["coverage"]!.AsArray().Add("MOD-001"); break;
            case "overlapping-coverage": Entry(entries, "audit")["coverage"]!.AsArray().Add("MOD-001"); break;
            case "foundation-overlap": foundation[0]!["coverage"]!.AsArray().Add("MOD-001"); break;
            case "missing-coverage-id": first["coverage"]!.AsArray().RemoveAt(0); break;
            case "missing-foundation-id": foundation[0]!["coverage"]!.AsArray().RemoveAt(0); break;
            case "register-edition-drift":
                var register = JsonNode.Parse(files[EditionMapContract.RegisterPath])!;
                register["entries"]!.AsArray().Single(entry => (string?)entry!["id"] == "MOD-001")!["edition"] = "paid";
                files[EditionMapContract.RegisterPath] = register.ToJsonString();
                break;
            case "payments-paid": SetEdition(Entry(entries, "payments"), "paid"); break;
            case "cms-paid": SetEdition(Entry(entries, "cms"), "paid"); break;
            case "saas-community": SetEdition(Entry(entries, "saas-lifecycle"), "community"); break;
            case "shared-runner-paid": SetEdition(Entry(entries, "run-profiles"), "paid"); break;
            case "studio-runner-community": SetEdition(Entry(entries, "tool-studio-runner"), "community"); break;
            case "low-code-community": SetEdition(Entry(entries, "low-model-contract"), "community"); break;
            case "missing-commerce": entries.Remove(Entry(entries, "commerce")); break;
            case "renamed-catalog-key": first["key"] = "account-services"; break;
            case "merged-tooling-entries":
                var removed = Entry(entries, "tool-application-starters");
                Entry(entries, "tool-architecture-families")["coverage"]!.AsArray().Add("T-002");
                entries.Remove(removed);
                break;
            case "swapped-tooling-ids": SwapCoverage(Entry(entries, "tool-architecture-families"), Entry(entries, "tool-application-starters")); break;
            case "swapped-low-code-ids": SwapCoverage(Entry(entries, "low-model-contract"), Entry(entries, "low-revisions")); break;
            case "cms-id-in-another-module": MoveCoverage(Entry(entries, "cms"), first, "CMS-002"); break;
            case "ui-id-in-another-entry": MoveCoverage(Entry(entries, "ui-contracts"), first, "UI-002"); break;
            case "commerce-paid": SetEdition(Entry(entries, "commerce"), "paid"); break;
            case "numbered-commerce": MoveCoverage(Entry(entries, "cms"), Entry(entries, "commerce"), "CMS-002"); break;
            case "catalog-in-foundation":
                entries.Remove(first);
                foundation.Add(first);
                break;
            case "renamed-foundation": foundation[0]!["key"] = "common-contracts"; break;
            case "swapped-foundation-ids": SwapCoverage(foundation[0]!, foundation[1]!); break;
            case "foundation-id-in-catalog": MoveCoverage(foundation[0]!, first, "PRD-001"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        files[EditionMapContract.MapPath] = map.ToJsonString();
        if (mutation == "missing-map") files.Remove(EditionMapContract.MapPath);
        Assert.Contains(EditionMapContract.Validate(files), error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("title")]
    [InlineData("edition")]
    [InlineData("home")]
    [InlineData("coverage")]
    [InlineData("reason")]
    public void Every_entry_field_is_required(string field)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var map = JsonNode.Parse(files[EditionMapContract.MapPath])!;
        map["entries"]![0]!.AsObject().Remove(field);
        files[EditionMapContract.MapPath] = map.ToJsonString();
        Assert.Contains(EditionMapContract.Validate(files), error => error.StartsWith("edition fields must be exactly", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{", "invalid edition JSON")]
    [InlineData("null", "edition fields must be exactly")]
    [InlineData("[]", "edition fields must be exactly")]
    [InlineData("{\"version\":1,\"version\":1}", "duplicate JSON property")]
    public void Malformed_documents_are_rejected(string text, string diagnostic)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        files[EditionMapContract.MapPath] = text;
        Assert.Contains(EditionMapContract.Validate(files), error => error.StartsWith(diagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Community catalog")]
    [InlineData("Paid catalog")]
    [InlineData("Cross-cutting foundation")]
    public void Missing_readme_lists_are_rejected(string heading)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        files[EditionMapContract.ReadmePath] = files[EditionMapContract.ReadmePath].Replace("## " + heading, "## Other entries");
        Assert.Contains("README needs one section: " + heading, EditionMapContract.Validate(files));
    }

    [Theory]
    [InlineData("missing-row")]
    [InlineData("duplicate-row")]
    [InlineData("changed-title")]
    [InlineData("changed-home")]
    [InlineData("wrong-edition-list")]
    public void Readme_catalog_drift_is_rejected(string mutation)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var readme = files[EditionMapContract.ReadmePath].Replace("\r\n", "\n");
        var row = readme.Split('\n').Single(line => line.StartsWith("| `accounts`", StringComparison.Ordinal));
        readme = mutation switch
        {
            "missing-row" => readme.Replace(row + "\n", ""),
            "duplicate-row" => readme.Replace(row, row + "\n" + row),
            "changed-title" => readme.Replace(row, row.Replace("Accounts", "Account tools")),
            "changed-home" => readme.Replace(row, row.Replace("Modularyn.Modules.*", "Modularyn.Framework.*")),
            "wrong-edition-list" => readme.Replace(row + "\n", "").Replace("| `ai-management`", row + "\n| `ai-management`"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        files[EditionMapContract.ReadmePath] = readme;
        Assert.Contains(EditionMapContract.Validate(files), error => error.StartsWith("README catalog list must agree with map", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Each_no_lock_in_rule_is_required(int number)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var lines = files[EditionMapContract.ReadmePath].Replace("\r\n", "\n").Split('\n');
        files[EditionMapContract.ReadmePath] = string.Join('\n', lines.Where(line => !line.StartsWith(number + ". ", StringComparison.Ordinal)));
        Assert.Contains("README must state exactly the five no-lock-in rules", EditionMapContract.Validate(files));
    }

    [Theory]
    [InlineData("community", "CMS-001", "standard")]
    [InlineData("standard", "CMS-001", "standard")]
    [InlineData("pro", "CMS-001", "pro")]
    [InlineData("standard", "LOW-001", "pro")]
    [InlineData("pro", "LOW-001", "standard")]
    [InlineData("standard", "LOW-001", "community")]
    [InlineData("pro", "LOW-001", "paid")]
    [InlineData("standard", "FND-001", "standard")]
    public void Manifest_capability_edition_drift_is_rejected(string distribution, string id, string edition)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var path = ReleaseManifestContract.ExampleDirectory + distribution + ".json";
        var manifest = JsonNode.Parse(files[path])!;
        Assert.NotEqual(edition, manifest["capabilities"]![id]!["edition"]!.GetValue<string>());
        manifest["capabilities"]![id]!["edition"] = edition;
        files[path] = manifest.ToJsonString();
        Assert.Contains("manifest capability edition must agree with map: " + id + " in " + path, EditionMapContract.Validate(files));
    }

    [Theory]
    [InlineData("paid-in-community", "paid capability cannot appear in a Community manifest")]
    [InlineData("unknown-capability", "manifest capability must occur in map")]
    [InlineData("unknown-distribution", "manifest distribution edition must be community, standard or pro")]
    [InlineData("empty-capabilities", "manifest needs capabilities")]
    [InlineData("additional-example", "manifest capability edition must agree with map")]
    [InlineData("no-examples", "release-manifest examples are required")]
    public void All_manifest_examples_are_checked_against_the_map(string mutation, string diagnostic)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var path = ReleaseManifestContract.ExampleDirectory + "community.json";
        var manifest = JsonNode.Parse(files[path])!;
        var capabilities = manifest["capabilities"]!.AsObject();
        switch (mutation)
        {
            case "paid-in-community": capabilities["LOW-001"] = capabilities["CMS-011"]!.DeepClone(); break;
            case "unknown-capability": capabilities["CMS-999"] = capabilities["CMS-011"]!.DeepClone(); break;
            case "unknown-distribution": manifest["edition"] = "paid"; break;
            case "empty-capabilities": manifest["capabilities"] = new JsonObject(); break;
            case "additional-example":
                path = ReleaseManifestContract.ExampleDirectory + "additional.json";
                capabilities["CMS-001"]!["edition"] = "pro";
                break;
            case "no-examples": break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        files[path] = manifest.ToJsonString();
        if (mutation == "no-examples")
            foreach (var example in files.Keys.Where(key => key.StartsWith(ReleaseManifestContract.ExampleDirectory, StringComparison.Ordinal)).ToArray())
                files.Remove(example);
        Assert.Contains(EditionMapContract.Validate(files), error => error.StartsWith(diagnostic, StringComparison.Ordinal));
    }

    private static JsonNode Entry(JsonArray entries, string key) => entries.Single(entry => (string?)entry!["key"] == key)!;

    private static void SetEdition(JsonNode entry, string edition)
    {
        entry["edition"] = edition;
        entry["home"] = edition == "paid" ? "Modularyn.Pro.*" : "Modularyn.Modules.*";
    }

    private static void SwapCoverage(JsonNode first, JsonNode second)
    {
        var id = first["coverage"]![0]!.GetValue<string>();
        first["coverage"]![0] = second["coverage"]![0]!.GetValue<string>();
        second["coverage"]![0] = id;
    }

    private static void MoveCoverage(JsonNode source, JsonNode target, string id)
    {
        var coverage = source["coverage"]!.AsArray();
        coverage.Remove(coverage.Single(value => (string?)value == id));
        target["coverage"]!.AsArray().Add(id);
    }
}
