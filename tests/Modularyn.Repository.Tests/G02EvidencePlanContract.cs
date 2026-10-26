using System.Text.Json;
using System.Text.RegularExpressions;
using static Modularyn.Repository.Tests.ContentPackageContract;

namespace Modularyn.Repository.Tests;

// Planning metadata only. Security observations remain the Builder's corpus.
internal static class G02EvidencePlanContract
{
    internal const string PlanPath = "docs/evidence/g02-plan.json";
    internal const string SkeletonPath = "docs/capability-reports/g02-skeleton.json";
    internal const string ReferencePath = "docs/profiles/reference.md";
    internal const string RegisterPath = "docs/coverage/register.json";
    internal static readonly string[] Families = ["AC-001", "AC-003", "AC-004", "AC-005", "AC-006", "AC-007", "AC-008", "AC-009"];
    internal static readonly string[] WorkOrders =
    [
        "PLAN-10", "CI-01", "CODE-02", "CODE-03", "CODE-04", "CODE-05", "CODE-06", "CODE-07", "CODE-08", "CODE-09", "CODE-10",
        "SEC-02", "SEC-03", "SEC-04", "SEC-05", "SEC-06", "SEC-07", "SEC-08"
    ];

    internal static List<string> ValidatePlan(string text, IReadOnlyDictionary<string, string> files) => Capture(() =>
    {
        var plan = Parse(text);
        Fields(plan, "format", "formatVersion", "gate", "profile", "status", "corpus", "families", "deploymentModes", "workOrders", "cases");
        Require(String(plan, "format") == "modularyn.g02-evidence-plan" && plan.GetProperty("formatVersion").GetInt32() == 1,
            "plan format and version");
        Require(String(plan, "gate") == "G-02" && String(plan, "profile") == "reference"
            && String(plan, "status") == "planned", "plan must remain G-02 reference planning");
        Require(String(plan, "corpus") == AcceptanceCorpusContract.CorpusPath, "plan corpus location");
        EqualStrings(plan, "families", Families, "plan family selection");
        EqualStrings(plan, "deploymentModes", ["host", "container"], "both deployment modes required");
        var orders = plan.GetProperty("workOrders");
        Fields(orders, WorkOrders);
        foreach (var order in orders.EnumerateObject())
        {
            Fields(order.Value, "owner", "scope");
            Require(String(order.Value, "owner") == (order.Name.StartsWith("SEC-", StringComparison.Ordinal)
                || order.Name is "PLAN-10" or "CI-01" ? "Builder" : "Implementation contributor"), "work-order ownership");
            Nonblank(order.Value, "scope");
        }

        var corpus = ReferenceCases(files);
        Require(corpus.Count == 41, "reference corpus must contain exactly 41 G-02 cases");
        var rows = Items(plan, "cases").ToArray();
        var ids = rows.Select(row => String(row, "caseId")).ToArray();
        Require(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "duplicate planned case");
        Require(ids.SequenceEqual(corpus.Keys, StringComparer.Ordinal), "plan must map exactly the 41 reference cases in corpus order");
        var register = Items(Parse(files[RegisterPath]), "entries").ToDictionary(row => String(row, "id"), StringComparer.Ordinal);
        var allNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Fields(row, "caseId", "family", "profile", "status", "workOrder", "testProject", "plannedTests", "layers", "requirements", "needs", "securityDependencies");
            var id = String(row, "caseId");
            var source = corpus[id];
            Require(String(row, "family") == String(source, "family") && String(row, "profile") == "reference", id + ": family/profile drift");
            Require(String(row, "status") == "planned", "case must remain planned");
            var order = String(row, "workOrder");
            Require(WorkOrders.Contains(order, StringComparer.Ordinal), "unknown G-02 work order");
            Require(order == ExpectedWorkOrder(source), id + ": work order does not cover this case");
            EqualStrings(row, "layers", Strings(source, "layers"), id + ": corpus layers drift");
            EqualStrings(row, "requirements", Strings(source, "requirements"), id + ": corpus requirements drift");
            foreach (var requirement in Strings(row, "requirements"))
                Require(register.TryGetValue(requirement, out var entry) && String(entry, "kind") == "requirement"
                    && Strings(entry, "acceptance").Contains(String(row, "family"), StringComparer.Ordinal), id + ": coverage-register disagreement");

            var project = String(row, "testProject");
            Require(project == ExpectedProject(source), id + ": planned test package drift");
            var tests = Items(row, "plannedTests").ToArray();
            Require(tests.Length > 0, id + ": planned tests required");
            Require(tests.Select(test => String(test, "layer")).SequenceEqual(Strings(source, "layers"), StringComparer.Ordinal), id + ": planned tests must cover corpus layers");
            foreach (var test in tests)
            {
                Fields(test, "name", "layer");
                CheckTestName(project, String(test, "name"), id);
                Require(allNames.Add(String(test, "name")), "duplicate planned test name");
            }
            var needs = row.GetProperty("needs");
            Fields(needs, "realPostgreSql", "filesystemStore", "browser", "containerDeployment");
            Require(needs.GetProperty("realPostgreSql").GetBoolean() && needs.GetProperty("containerDeployment").GetBoolean(), id + ": real database and container run required");
            Require(needs.GetProperty("filesystemStore").GetBoolean() == NeedsFilesystem(source), id + ": filesystem requirement drift");
            Require(needs.GetProperty("browser").GetBoolean() == (id == "AC-003-N02"), id + ": browser requirement drift");
            CheckDependencies(row, source, planning: true);
        }
    });

    internal static List<string> ValidateSkeleton(string text, IReadOnlyDictionary<string, string> files) => Capture(() =>
    {
        var report = Parse(text);
        Fields(report, "format", "formatVersion", "kind", "gate", "profile", "status", "sourceCommit", "qualifiedSubset", "target",
            "capabilities", "supportingCapabilities", "unmetDependencies", "unsupportedSelections", "limitations");
        Require(String(report, "format") == "modularyn.capability-report" && report.GetProperty("formatVersion").GetInt32() == 1,
            "capability format and version");
        Require(String(report, "kind") == "skeleton" && String(report, "gate") == "G-02" && String(report, "profile") == "reference"
            && String(report, "status") == "planned", "report must remain a planning skeleton");
        Require(report.GetProperty("sourceCommit").ValueKind == JsonValueKind.Null && !Items(report, "qualifiedSubset").Any(), "skeleton cannot claim a qualified commit or subset");
        var target = report.GetProperty("target");
        Fields(target, "host", "architecture", "runtimeVersion", "sdkVersion", "languageVersion", "database", "databaseVersion", "tenantPlacement", "topologies");
        Require(String(target, "host") == "Linux" && String(target, "architecture") == "x64"
            && String(target, "database") == "PostgreSQL" && String(target, "databaseVersion") == "18.6"
            && String(target, "tenantPlacement") == "shared-database", "G-02 target tuple");
        EqualStrings(target, "topologies", ["single-host", "single-host-container"], "G-02 target topologies");
        var sdk = String(Parse(files["global.json"]).GetProperty("sdk"), "version");
        var sdkEntry = Items(Parse(files["docs/dependencies/register.json"]), "entries").Single(entry => String(entry, "id") == "dotnet-sdk:dotnet");
        Require(String(target, "sdkVersion") == sdk && String(sdkEntry, "version") == sdk
            && String(sdkEntry, "purpose").Contains(".NET Runtime " + String(target, "runtimeVersion") + ",", StringComparison.Ordinal)
            && String(sdkEntry, "purpose").Contains("C# " + String(target, "languageVersion"), StringComparison.Ordinal), "target must agree with recorded runtime pins");

        var corpus = ReferenceCases(files);
        var plan = Items(Parse(files[PlanPath]), "cases").ToDictionary(row => String(row, "caseId"), StringComparer.Ordinal);
        var capabilities = Items(report, "capabilities").ToArray();
        Require(capabilities.Select(row => String(row, "caseId")).SequenceEqual(corpus.Keys, StringComparer.Ordinal), "capability actions must cover exactly the planned cases");
        foreach (var row in capabilities)
        {
            Fields(row, "caseId", "module", "action", "status", "providers", "ui", "uiDelivery", "tenantPlacement", "topologies", "runtimeVersion", "evidenceRecords");
            var id = String(row, "caseId");
            Unexecuted(row);
            Nonblank(row, "module");
            Require(String(row, "action") == String(corpus[id], "title"), "capability action must name the corpus observation");
            var filesystem = plan[id].GetProperty("needs").GetProperty("filesystemStore").GetBoolean();
            EqualStrings(row, "providers", filesystem ? ["PostgreSQL through EF Core", "filesystem BLOB storage"] : ["PostgreSQL through EF Core"], "capability provider drift");
            Require(String(row, "tenantPlacement") == String(target, "tenantPlacement")
                && String(row, "runtimeVersion") == String(target, "runtimeVersion"), "capability tuple drift");
            EqualStrings(row, "topologies", Strings(target, "topologies"), "capability topology drift");
            var browser = plan[id].GetProperty("needs").GetProperty("browser").GetBoolean();
            Require(String(row, "ui") == (browser ? "Razor sign-in and form" : "none")
                && String(row, "uiDelivery") == (browser ? "planned-native" : "not-applicable"), "capability UI scope drift");
        }
        var supporting = Items(report, "supportingCapabilities").ToArray();
        Require(supporting.Select(row => String(row, "workOrder")).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "SEC-03", "SEC-02", "SEC-06", "SEC-07", "CODE-08", "CODE-09" }.Order(StringComparer.Ordinal)), "supporting capability gaps must remain visible");
        foreach (var row in supporting)
        {
            Fields(row, "capability", "workOrder", "status", "gap", "evidenceRecords");
            Nonblank(row, "capability");
            Nonblank(row, "gap");
            Unexecuted(row);
        }
        var dependencies = Items(report, "unmetDependencies").ToArray();
        var families = corpus.Values.SelectMany(row => Strings(row, "securityDependencies")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Require(dependencies.Select(row => String(row, "family")).SequenceEqual(families, StringComparer.Ordinal), "all security prerequisites must remain visible");
        foreach (var row in dependencies)
        {
            Fields(row, "family", "status", "caseIds", "evidenceRecords");
            Require(String(row, "status") == "unmet" && !Items(row, "evidenceRecords").Any(), "skeleton prerequisites must remain unmet");
            EqualStrings(row, "caseIds", corpus.Where(pair => Strings(pair.Value, "securityDependencies").Contains(String(row, "family"), StringComparer.Ordinal)).Select(pair => pair.Key), "prerequisite case linkage drift");
        }
        var unsupported = Items(report, "unsupportedSelections").ToArray();
        var expected = UnsupportedSelections(files[ReferencePath]);
        Require(unsupported.Length == expected.Count + 2 && unsupported.Select(row => String(row, "selection")).Distinct(StringComparer.Ordinal).Count() == unsupported.Length,
            "unsupported selection set must include the profile and both unqualified hosts");
        foreach (var row in unsupported)
        {
            Fields(row, "selection", "status", "reason");
            var selection = String(row, "selection");
            if (selection is "Windows hosts" or "macOS hosts")
            {
                Require(String(row, "status") == "unqualified", "Windows and macOS hosts must remain unqualified");
                Nonblank(row, "reason");
            }
            else Require(expected.TryGetValue(selection, out var reason) && String(row, "status") == "unsupported"
                && String(row, "reason") == reason, "reference exclusion or reason drift");
        }
        Require(expected.Keys.All(selection => unsupported.Any(row => String(row, "selection") == selection))
            && new[] { "Windows hosts", "macOS hosts" }.All(selection => unsupported.Any(row => String(row, "selection") == selection)), "unsupported selection missing");
        Require(Items(report, "limitations").Any() && Strings(report, "limitations").All(value => !string.IsNullOrWhiteSpace(value)), "report limitations required");
    });

    internal static Dictionary<string, JsonElement> ReferenceCases(IReadOnlyDictionary<string, string> files) =>
        Items(Parse(files[AcceptanceCorpusContract.CorpusPath]), "cases")
            .Where(row => Families.Contains(String(row, "family"), StringComparer.Ordinal) && Strings(row, "profiles").Contains("reference", StringComparer.Ordinal))
            .ToDictionary(row => String(row, "id"), StringComparer.Ordinal);

    // These assignments preserve the reviewed G-02 work-order scope boundaries.
    internal static string ExpectedWorkOrder(JsonElement source) => String(source, "family") switch
    {
        "AC-001" => "CODE-02",
        "AC-003" => String(source, "id") switch
        {
            "AC-003-N01" or "AC-003-N02" or "AC-003-N03" => "SEC-08",
            "AC-003-B02" => "SEC-03",
            "AC-003-P02" => "CODE-04",
            _ => "SEC-05"
        },
        "AC-004" => String(source, "id") is "AC-004-B01" or "AC-004-P01" or "AC-004-R01" ? "SEC-04" : "SEC-08",
        "AC-005" => "CODE-03",
        "AC-006" or "AC-007" => "CODE-05",
        "AC-008" => "CODE-06",
        "AC-009" => String(source, "id") == "AC-009-B01" ? "SEC-06" : "CODE-07",
        _ => throw new ContractViolation("case outside G-02 scope")
    };

    private static string ExpectedProject(JsonElement source)
    {
        var package = String(source, "id") switch
        {
            "AC-003-B02" => "Modularyn.Modules.Identity.Application",
            "AC-003-P02" => "Modularyn.Framework.Settings",
            "AC-009-B01" => "Modularyn.Modules.Files.Application",
            _ => String(source, "family") switch
            {
                "AC-001" => "Modularyn.Framework.Composition",
                "AC-003" => "Modularyn.Framework.Authorization",
                "AC-004" => "Modularyn.Framework.Tenancy",
                "AC-005" => "Modularyn.Framework.Persistence.EfCore",
                "AC-006" => "Modularyn.Framework.Events",
                "AC-007" => "Modularyn.Modules.BackgroundJobs.Application",
                "AC-008" => "Modularyn.Framework.Sdk.Client",
                "AC-009" => "Modularyn.Modules.Files.Persistence.FileSystem",
                _ => throw new ContractViolation("case outside G-02 scope")
            }
        };
        return $"tests/{package}.Tests/{package}.Tests.csproj";
    }

    internal static void CheckTestName(string project, string name, string caseId)
    {
        var assembly = Path.GetFileNameWithoutExtension(project);
        Require(assembly.EndsWith(".Tests", StringComparison.Ordinal) && assembly.Length > 6, "test project must name a tested package");
        var package = assembly[..^6];
        Require(NamesAndLayoutContract.IsPackageId(package) && project == $"tests/{assembly}/{assembly}.csproj"
            && (!NamesAndLayoutContract.RequiresModuleRole(package) || NamesAndLayoutContract.ModuleRole(package).Length > 0), "test project violates the layout contract");
        Require(name.StartsWith(assembly + ".", StringComparison.Ordinal)
            && Regex.IsMatch(name, @"^(?:[A-Za-z][A-Za-z0-9_]*\.)+[A-Za-z][A-Za-z0-9_]*(?![\s\S])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            && name.Contains("." + caseId.Replace("-", "", StringComparison.Ordinal) + "_", StringComparison.Ordinal), "planned test must be fully qualified and name its case");
    }

    internal static bool NeedsFilesystem(JsonElement source) => String(source, "family") is "AC-001" or "AC-009"
        || String(source, "id") is "AC-004-N02" or "AC-004-R02";

    internal static void CheckDependencies(JsonElement row, JsonElement source, bool planning)
    {
        var dependencies = Items(row, "securityDependencies").ToArray();
        Require(dependencies.Select(dependency => String(dependency, "family")).SequenceEqual(Strings(source, "securityDependencies"), StringComparer.Ordinal), "security dependency drift");
        foreach (var dependency in dependencies)
        {
            Fields(dependency, "family", "status", "evidenceRecords");
            var status = String(dependency, "status");
            var count = Items(dependency, "evidenceRecords").Count();
            Require((status is "unmet" or "satisfied") && (status == "unmet" ? count == 0 : count > 0), "dependency status and evidence disagree");
            Require(!planning || status == "unmet", "planned prerequisites must remain unmet");
        }
    }

    private static Dictionary<string, string> UnsupportedSelections(string profile)
    {
        var sections = profile.Replace("\r\n", "\n", StringComparison.Ordinal).Split("## Unsupported selections", StringSplitOptions.None);
        Require(sections.Length == 2, "reference exclusion section missing");
        var rows = sections[1].Split('\n').Where(line => line.StartsWith("| ", StringComparison.Ordinal)
            && !line.StartsWith("| Selection |", StringComparison.Ordinal)).Select(line => line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray()).ToArray();
        Require(rows.Length > 0 && rows.All(row => row.Length == 2), "reference exclusion table malformed");
        return rows.ToDictionary(row => row[0], row => row[1], StringComparer.Ordinal);
    }

    internal static void Fields(JsonElement value, params string[] fields) => Require(value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields), "fields must be exactly: " + string.Join(", ", fields));
    internal static string[] Strings(JsonElement value, string field) => Items(value, field).Select(item => item.GetString()!).ToArray();
    internal static void EqualStrings(JsonElement value, string field, IEnumerable<string> expected, string diagnostic) =>
        Require(Strings(value, field).SequenceEqual(expected, StringComparer.Ordinal), diagnostic);
    internal static void Nonblank(JsonElement value, string field) => Require(!string.IsNullOrWhiteSpace(String(value, field)), field + " must be nonblank");
    private static void Unexecuted(JsonElement row) => Require(String(row, "status") == "planned" && !Items(row, "evidenceRecords").Any(), "skeleton actions must remain planned without evidence");
    internal static List<string> Capture(Action check)
    {
        try { check(); return []; }
        catch (Exception exception) when (exception is ContractViolation or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or ArgumentException)
        { return [exception.Message]; }
    }
}
