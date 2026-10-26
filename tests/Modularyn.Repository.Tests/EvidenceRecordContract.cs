using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Modularyn.Repository.Tests.ContentPackageContract;
using static Modularyn.Repository.Tests.G02EvidencePlanContract;

namespace Modularyn.Repository.Tests;

internal static class EvidenceRecordContract
{
    internal const string SchemaPath = "schemas/evidence-record/evidence-record.schema.json";
    internal const string ExampleDirectory = "docs/evidence/examples/";

    internal static List<string> Validate(string text, string schemaText, IReadOnlyDictionary<string, string> files) => Capture(() =>
    {
        var record = Parse(text);
        var schema = Parse(schemaText);
        SchemaSubset.CheckDocument(schema, "urn:modularyn:evidence-record:1");
        SchemaSubset.Validate(record, schema, schema, "evidence#");
        var planning = String(record, "kind") == "plan";
        var corpus = Items(Parse(files[AcceptanceCorpusContract.CorpusPath]), "cases").ToDictionary(row => String(row, "id"), StringComparer.Ordinal);
        var id = String(record, "caseId");
        Require(corpus.TryGetValue(id, out var source), "unknown evidence case");
        Require(String(record, "family") == String(source, "family")
            && Strings(source, "profiles").Contains(String(record, "profile"), StringComparer.Ordinal), "evidence family/profile drift");
        var normalizedCorpus = files[AcceptanceCorpusContract.CorpusPath].Replace("\r\n", "\n", StringComparison.Ordinal);
        var corpusHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCorpus)));
        Require(String(record.GetProperty("corpus"), "sha256") == corpusHash, "evidence corpus identity drift");
        EqualStrings(record, "expectedOutcomes", Strings(source, "expectedOutcome"), "evidence must retain the corpus observations");
        var register = Items(Parse(files[RegisterPath]), "entries").ToDictionary(row => String(row, "id"), StringComparer.Ordinal);
        var tuple = record.GetProperty("tuple");
        var modules = Items(tuple, "moduleVersions").ToArray();
        var subjects = Items(record, "subjects").ToArray();
        Require(subjects.Select(subject => String(subject, "id")).Distinct(StringComparer.Ordinal).Count() == subjects.Length, "duplicate evidence subject");
        foreach (var subject in subjects)
        {
            var subjectId = String(subject, "id");
            var kind = String(subject, "kind");
            if (kind == "module-contract")
                Require(modules.Any(module => String(module, "id") == subjectId), "module-contract subject must name a tuple module");
            else
            {
                Require(register.TryGetValue(subjectId, out var entry), "unknown evidence subject");
                Require(String(subject, "owner") == String(entry, "owner"), "evidence owner must agree with coverage register");
                Require(kind == "requirement" ? String(entry, "kind") == "requirement"
                    && Strings(entry, "acceptance").Contains(String(record, "family"), StringComparer.Ordinal)
                    : String(entry, "kind") is "foundation-row" or "tooling-row", "evidence subject kind or family mismatch");
            }
        }
        Require(subjects.Where(subject => String(subject, "kind") == "requirement").Select(subject => String(subject, "id"))
            .ToHashSet(StringComparer.Ordinal).SetEquals(Strings(source, "requirements")), "evidence requirement coverage drift");
        CheckDependencies(record, source, planning);
        Require(!Items(record, "securityDependencies").SelectMany(dependency => Strings(dependency, "evidenceRecords"))
            .Contains(String(record, "recordId"), StringComparer.Ordinal), "evidence cannot satisfy its own prerequisite");

        var tests = Items(record, "tests").ToArray();
        Require(tests.Select(test => String(test, "name")).Distinct(StringComparer.Ordinal).Count() == tests.Length, "duplicate evidence test");
        foreach (var test in tests) CheckTestName(String(test, "project"), String(test, "name"), id);
        Require(tests.Select(test => String(test, "layer")).ToHashSet(StringComparer.Ordinal).SetEquals(Strings(source, "layers")), "evidence test layers drift");
        if (String(record, "gate") == "G-02")
        {
            Require(String(record, "profile") == "reference" && Families.Contains(String(record, "family"), StringComparer.Ordinal), "G-02 evidence profile/family");
            var planned = Items(Parse(files[PlanPath]), "cases").Single(row => String(row, "caseId") == id);
            Require(String(record, "workOrder") == String(planned, "workOrder"), "evidence work-order drift");
            Require(tests.All(test => String(test, "project") == String(planned, "testProject"))
                && tests.Select(test => (String(test, "name"), String(test, "layer"))).SequenceEqual(
                    Items(planned, "plannedTests").Select(test => (String(test, "name"), String(test, "layer")))), "evidence planned-test drift");
            Require(String(tuple.GetProperty("database"), "name") == "PostgreSQL"
                && String(tuple, "tenancyMode") == "shared-database"
                && String(tuple.GetProperty("deployment").GetProperty("operatingSystem"), "name") == "Linux"
                && String(tuple.GetProperty("deployment"), "architecture") == "x64", "G-02 evidence tuple selection");
            var target = Parse(files[SkeletonPath]).GetProperty("target");
            var runtime = tuple.GetProperty("runtime");
            Require(new[] { "sdkVersion", "runtimeVersion", "languageVersion" }.All(field => String(runtime, field) == String(target, field))
                && String(runtime, "aspNetCoreVersion") == String(target, "runtimeVersion"), "G-02 evidence runtime pins drift");
            Require(!planned.GetProperty("needs").GetProperty("browser").GetBoolean()
                || Items(tuple, "ui").Any(ui => String(ui, "name") == "Razor"), "browser evidence requires its Razor UI selection");
            string[] expectedProviders = NeedsFilesystem(source) ? ["EF Core PostgreSQL", "filesystem BLOB storage"] : ["EF Core PostgreSQL"];
            Require(Items(tuple, "providers").Select(provider => String(provider, "name")).SequenceEqual(expectedProviders, StringComparer.Ordinal), "G-02 evidence provider selection");
        }
        Require(tests.All(test => modules.Any(module => String(module, "id") == Path.GetFileNameWithoutExtension(String(test, "project"))[..^6])),
            "evidence tuple must include each tested module");
        foreach (var document in Strings(record, "documentation"))
            Require(files.ContainsKey(document.Split('#')[0]), "evidence documentation must resolve");
        Require(modules.Select(module => String(module, "id")).Distinct(StringComparer.Ordinal).Count() == modules.Length, "duplicate module version");
        foreach (var group in new[] { "providers", "ui" })
            Require(Items(tuple, group).Select(component => String(component, "name")).Distinct(StringComparer.Ordinal).Count()
                == Items(tuple, group).Count(), "duplicate tuple component");

        var verification = record.GetProperty("verification");
        if (!planning)
        {
            Require(DateOnly.TryParseExact(String(verification, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _), "verification requires a real calendar date");
            foreach (var module in modules) RequirePinned(module);
            foreach (var provider in Items(tuple, "providers")) RequirePinned(provider);
            foreach (var ui in Items(tuple, "ui")) RequirePinned(ui);
            RequirePinned(tuple.GetProperty("database"));
            RequirePinned(tuple.GetProperty("deployment").GetProperty("operatingSystem"));
            var deployment = tuple.GetProperty("deployment");
            Require(String(deployment, "topology") != "single-host-container"
                || deployment.GetProperty("containerImageDigest").ValueKind == JsonValueKind.String, "container verification requires the image digest");
            if (String(verification, "status") == "passed")
                Require(Items(record, "securityDependencies").All(dependency => String(dependency, "status") == "satisfied"), "passed verification cannot have unmet dependencies");
            else Require(Items(record, "knownLimitations").Any(), "failed or blocked verification requires limitations");
        }
    });

    private static void RequirePinned(JsonElement component) => Require(component.GetProperty("version").ValueKind == JsonValueKind.String,
        "verification requires every exact version pin");
}
