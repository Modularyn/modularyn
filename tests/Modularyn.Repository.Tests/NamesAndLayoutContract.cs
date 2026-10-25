using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using static Modularyn.Repository.Tests.ContentPackageContract;

namespace Modularyn.Repository.Tests;

internal static class NamesAndLayoutContract
{
    internal const string CompanionPath = "docs/names-and-layout.json";
    internal const string GuidePath = "docs/names-and-layout.md";
    internal const string PackagePattern = @"^(?:Modularyn|dotnet-modularyn|Modularyn\.(?:Framework|Modules|Cms|UI|Connectors|Pro|Tooling)(?:\.(?:UI|(?:[A-Z][a-z0-9]+)+))+)(?![\s\S])";
    private const string RepositoryTests = "tests/Modularyn.Repository.Tests/Modularyn.Repository.Tests.csproj";
    private static readonly string[] FamilyNames = ["Framework", "Modules", "Cms", "UI", "Connectors", "Pro", "Tooling"];
    private static readonly (string Name, string Home)[] Products =
    [
        ("Framework", "Modularyn.Framework.*"), ("Modules", "Modularyn.Modules.*"), ("CMS", "Modularyn.Cms.*"),
        ("Admin", "Modularyn.UI.Admin.*"), ("UI", "Modularyn.UI.*"), ("Studio", "Modularyn.Pro.Studio.*"),
        ("CLI", "dotnet-modularyn"), ("Generator", "Modularyn.Tooling.Generator.*"),
        ("SDK", "Modularyn.Framework.Sdk.*"), ("Connectors", "Modularyn.Connectors.*")
    ];
    private static readonly (string Role, string Suffix, string Label)[] Suffixes =
    [
        ("domain", ".Domain", "Domain"), ("application-contracts", ".Application.Contracts", "Application contracts"),
        ("application-services", ".Application", "Application services"),
        ("persistence-adapter", ".Persistence.{Provider}", "Persistence adapter"),
        ("http-endpoints", ".HttpApi", "HTTP endpoints"), ("client", ".Client", "Client"),
        ("ui-adapter", ".UI.{Adapter}", "UI adapter")
    ];
    private static readonly string[] Layout = ["global.json", "Directory.Build.props", "Directory.Packages.props",
        ".editorconfig", ".gitattributes", "Modularyn.slnx", "src/", "tests/", "tools/", "docs/", "schemas/", "brand/"];

    internal static bool IsPackageId(string id) => Regex.IsMatch(id, PackagePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static string ModuleRole(string id)
    {
        if (!IsPackageId(id)) return "";
        var parts = id.Split('.');
        if (parts.Length < 4 || parts[1] is not ("Modules" or "Cms" or "Pro")) return "";
        return string.Join('.', parts[3..]) switch
        {
            "Domain" => "domain",
            "Application.Contracts" => "application-contracts",
            "Application" => "application-services",
            "HttpApi" => "http-endpoints",
            "Client" => "client",
            _ when parts.Length == 5 && parts[3] == "Persistence" => "persistence-adapter",
            _ when parts.Length == 5 && parts[3] == "UI" => "ui-adapter",
            _ => ""
        };
    }

    internal static bool RequiresModuleRole(string id) => id.StartsWith("Modularyn.Modules.", StringComparison.Ordinal)
        || id.StartsWith("Modularyn.Cms.", StringComparison.Ordinal)
        || id.StartsWith("Modularyn.Pro.", StringComparison.Ordinal) && !id.StartsWith("Modularyn.Pro.Studio.", StringComparison.Ordinal);

    public static List<string> Validate(IReadOnlyDictionary<string, string> files)
    {
        try
        {
            var names = Parse(files[CompanionPath]);
            var guide = files[GuidePath].Replace("\r\n", "\n");
            CheckCompanion(names, guide, files);
            CheckProjects(files);
            return [];
        }
        catch (Exception exception) when (exception is ContractViolation or JsonException or XmlException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return [exception.Message];
        }
    }

    private static void CheckCompanion(JsonElement names, string guide, IReadOnlyDictionary<string, string> files)
    {
        Require(names.GetProperty("formatVersion").GetDecimal() == 1, "names format version");
        Fields(names, ["formatVersion", "identifiers", "packageIdPattern", "libraryIdentity", "families", "products",
            "moduleSuffixes", "layout", "repositoryTestProject"]);
        var expectedIdentifiers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["frontDoorPackage"] = "Modularyn", ["toolPackage"] = "dotnet-modularyn", ["toolCommand"] = "dotnet modularyn",
            ["toolAssembly"] = "Modularyn.Tooling.Cli", ["toolRootNamespace"] = "Modularyn.Tooling.Cli"
        };
        var identifiers = names.GetProperty("identifiers");
        Fields(identifiers, expectedIdentifiers.Keys);
        foreach (var (key, value) in expectedIdentifiers)
        {
            Require(String(identifiers, key) == value, "exact identifier: " + key);
            Require(guide.Contains("`" + value + "`", StringComparison.Ordinal), "guide identifier: " + key);
        }
        Require(String(names, "packageIdPattern") == PackagePattern, "package grammar must be exact");
        Require(Parse(files[ReleaseManifestContract.SchemaPath]).GetProperty("$defs").GetProperty("packageId")
            .GetProperty("pattern").GetString() == PackagePattern, "manifest package grammar must match names");
        Require(String(names, "libraryIdentity") == "packageId = assemblyName = rootNamespace = projectName"
            && guide.Contains("package id = assembly name = root namespace =", StringComparison.Ordinal), "library identity rule");
        Require(String(names, "repositoryTestProject") == RepositoryTests && guide.Contains("`" + RepositoryTests + "`", StringComparison.Ordinal),
            "repository test project identity");

        var families = Items(names, "families").ToArray();
        var familyRows = TableRows(guide, "| Family | NuGet prefix | Editions | Source repository | Source directory |");
        Require(families.Length == FamilyNames.Length && familyRows.Length == families.Length, "exact family set");
        for (var index = 0; index < families.Length; index++)
        {
            var family = families[index];
            var name = FamilyNames[index];
            var paid = name == "Pro";
            Fields(family, ["name", "prefix", "editions", "repository", "sourceDirectory"]);
            Require(String(family, "name") == name && String(family, "prefix") == "Modularyn." + name + ".*"
                && String(family, "repository") == (paid ? "modularyn-pro" : "modularyn")
                && String(family, "sourceDirectory") == "src/" + name + "/"
                && Items(family, "editions").Select(value => value.GetString()).SequenceEqual(paid ? new[] { "standard", "pro" } : new[] { "community" }),
                "family placement: " + name);
            Require(familyRows[index].SequenceEqual(new[] { name == "Cms" ? "CMS" : paid ? "Paid modules" : name,
                "`" + String(family, "prefix") + "`", paid ? "Standard, Pro" : "Community",
                "`" + String(family, "repository") + "`", "`" + String(family, "sourceDirectory") + "`" }), "guide family: " + name);
        }
        var products = Items(names, "products").ToArray();
        var productRows = TableRows(guide, "| Product name | Home | Responsibility |");
        Require(products.Length == Products.Length && productRows.Length == products.Length, "exact product set");
        for (var index = 0; index < products.Length; index++)
        {
            Fields(products[index], ["name", "home"]);
            var (name, home) = Products[index];
            Require(String(products[index], "name") == name && String(products[index], "home") == home, "one product home: " + name);
            Require(productRows[index].Length == 3 && productRows[index][0] == name && productRows[index][1] == "`" + home + "`",
                "guide product home: " + name);
        }
        var suffixes = Items(names, "moduleSuffixes").ToArray();
        var suffixRows = TableRows(guide, "| Role | Suffix | Dependency boundary |");
        Require(suffixes.Length == Suffixes.Length && suffixRows.Length == suffixes.Length, "exact module suffix set");
        for (var index = 0; index < suffixes.Length; index++)
        {
            Fields(suffixes[index], ["role", "suffix"]);
            var (role, suffix, label) = Suffixes[index];
            Require(String(suffixes[index], "role") == role && String(suffixes[index], "suffix") == suffix, "module suffix: " + role);
            Require(suffixRows[index].Length == 3 && suffixRows[index][0] == label && suffixRows[index][1] == "`" + suffix + "`",
                "guide module suffix: " + role);
        }
        var layout = Items(names, "layout").ToArray();
        var layoutRows = TableRows(guide, "| Path | Purpose | Present at this baseline |");
        Require(layout.Length == Layout.Length && layoutRows.Length == layout.Length, "exact layout path set");
        for (var index = 0; index < layout.Length; index++)
        {
            Fields(layout[index], ["path", "presentAtBaseline"]);
            var path = Layout[index];
            var present = path is not ("src/" or "tools/" or "brand/");
            Require(String(layout[index], "path") == path && layout[index].GetProperty("presentAtBaseline").GetBoolean() == present, "layout path: " + path);
            Require(layoutRows[index].Length == 3 && layoutRows[index][0] == "`" + path + "`"
                && layoutRows[index][2] == (present ? "Yes" : "No"), "guide layout: " + path);
            if (present) Require(path.EndsWith('/') ? files.Keys.Any(file => file.StartsWith(path, StringComparison.Ordinal)) : files.ContainsKey(path),
                "existing layout path missing: " + path);
        }
    }

    private static void CheckProjects(IReadOnlyDictionary<string, string> files)
    {
        var projects = files.Keys.Where(path => path.EndsWith(".csproj", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var listed = XDocument.Parse(files["Modularyn.slnx"]).Descendants("Project").Select(project => (string?)project.Attribute("Path") ?? "").ToArray();
        Require(projects.SetEquals(listed) && listed.Length == projects.Count, "solution must list every project exactly once");
        Require(projects.Contains(RepositoryTests), "repository test project missing");
        foreach (var path in projects)
        {
            var project = XDocument.Parse(files[path]);
            var name = Path.GetFileNameWithoutExtension(path);
            Require(Identity(project, "AssemblyName", name) == name && Identity(project, "RootNamespace", name) == name, "project assembly and namespace identity");
            if (path.StartsWith("tests/", StringComparison.Ordinal))
            {
                Require(path == "tests/" + name + "/" + name + ".csproj" && name.EndsWith(".Tests", StringComparison.Ordinal), "test project layout");
                Require(Identity(project, "IsPackable", "true") == "false", "test projects must not be packaged");
                if (path != RepositoryTests)
                {
                    var tested = name[..^6];
                    Require(projects.Any(source => source.StartsWith("src/", StringComparison.Ordinal)
                        && Path.GetFileNameWithoutExtension(source) == tested), "test project needs its tested package");
                }
                continue;
            }
            var tool = name == "Modularyn.Tooling.Cli";
            var id = Identity(project, "PackageId", name);
            Require(id == (tool ? "dotnet-modularyn" : name) && IsPackageId(id), "project package identity");
            Require(!RequiresModuleRole(id) || ModuleRole(id).Length > 0, "module role suffix");
            Require(!id.StartsWith("Modularyn.Pro.", StringComparison.Ordinal), "paid sources belong in modularyn-pro");
            var expected = name == "Modularyn" ? "src/Modularyn/Modularyn.csproj"
                : "src/" + name.Split('.')[1] + "/" + name + "/" + name + ".csproj";
            Require(path == expected, "source family layout");
            if (tool)
                Require(Identity(project, "PackAsTool", "false") == "true"
                    && Identity(project, "ToolCommandName", "") == "dotnet-modularyn", "tool command identity");
            foreach (var reference in project.Descendants().Where(node => node.Name.LocalName is "PackageReference" or "ProjectReference" or "FrameworkReference"))
            {
                var target = (string?)reference.Attribute("Include") ?? "";
                Require(!target.Contains("Modularyn.Pro.", StringComparison.Ordinal), "Community cannot reference paid modules");
                if (name.EndsWith(".Domain", StringComparison.Ordinal))
                    Require(!target.Contains(".UI.", StringComparison.Ordinal)
                        && !target.Contains("Microsoft.AspNetCore", StringComparison.Ordinal)
                        && !target.Contains("Microsoft.Maui", StringComparison.Ordinal)
                        && !target.Contains("Microsoft.WindowsDesktop", StringComparison.Ordinal), "domain cannot reference a UI framework");
            }
        }
    }

    private static string Identity(XDocument project, string property, string fallback)
    {
        var values = project.Descendants(property).ToArray();
        Require(values.Length <= 1 && values.All(value => value.Attribute("Condition") is null
            && value.Parent?.Attribute("Condition") is null), "project identity must be unconditional and unique");
        return values.Length == 0 ? fallback : values[0].Value;
    }

    private static string[][] TableRows(string guide, string header)
    {
        var start = guide.IndexOf(header + "\n", StringComparison.Ordinal);
        Require(start >= 0, "guide table missing: " + header);
        return guide[(start + header.Length + 1)..].Split('\n').Skip(1)
            .TakeWhile(line => line.StartsWith('|')).Select(line => line.Split('|')[1..^1].Select(cell => cell.Trim()).ToArray()).ToArray();
    }

    private static void Fields(JsonElement value, IEnumerable<string> expected) =>
        Require(value.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected), "names companion fields");
}
