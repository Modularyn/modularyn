using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class NamesAndLayoutTests
{
    [Fact]
    public void Companion_guide_and_existing_projects_agree()
    {
        Assert.Empty(NamesAndLayoutContract.Validate(RepositoryFiles.Current));
    }

    [Theory]
    [InlineData("identifier", "exact identifier")]
    [InlineData("family", "family placement")]
    [InlineData("family-edition", "family placement")]
    [InlineData("family-repository", "family placement")]
    [InlineData("product-home", "one product home")]
    [InlineData("module-suffix", "module suffix")]
    [InlineData("grammar", "package grammar must be exact")]
    [InlineData("layout", "layout path")]
    [InlineData("extra-property", "names companion fields")]
    [InlineData("guide-family", "guide family")]
    [InlineData("guide-product", "guide product home")]
    [InlineData("guide-suffix", "guide module suffix")]
    [InlineData("guide-layout", "guide layout")]
    [InlineData("schema-grammar", "manifest package grammar")]
    [InlineData("missing-path", "existing layout path missing")]
    [InlineData("project-identity", "project assembly and namespace identity")]
    [InlineData("solution-member", "solution must list every project")]
    public void A_single_names_or_layout_violation_is_rejected(string mutation, string diagnostic)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        Assert.Empty(NamesAndLayoutContract.Validate(files));
        var names = JsonNode.Parse(files[NamesAndLayoutContract.CompanionPath])!;
        switch (mutation)
        {
            case "identifier": names["identifiers"]!["toolPackage"] = "modularyn-tool"; break;
            case "family": names["families"]![2]!["prefix"] = "Modularyn.CMS.*"; break;
            case "family-edition": names["families"]![0]!["editions"]![0] = "pro"; break;
            case "family-repository": names["families"]![5]!["repository"] = "modularyn"; break;
            case "product-home": names["products"]![3]!["home"] = "Modularyn.UI.*"; break;
            case "module-suffix": names["moduleSuffixes"]![0]!["suffix"] = ".Core"; break;
            case "grammar": names["packageIdPattern"] = ".*"; break;
            case "layout": names["layout"]![6]!["path"] = "source/"; break;
            case "extra-property": names["extra"] = true; break;
            case "guide-family": ReplaceGuide(files, "| CMS | `Modularyn.Cms.*`", "| CMS | `Modularyn.CMS.*`"); break;
            case "guide-product": ReplaceGuide(files, "| Admin | `Modularyn.UI.Admin.*`", "| Admin | `Modularyn.UI.*`"); break;
            case "guide-suffix": ReplaceGuide(files, "| Domain | `.Domain`", "| Domain | `.Core`"); break;
            case "guide-layout": ReplaceGuide(files, "| `src/` |", "| `source/` |"); break;
            case "schema-grammar":
                var schema = JsonNode.Parse(files[ReleaseManifestContract.SchemaPath])!;
                schema["$defs"]!["packageId"]!["pattern"] = ".*";
                files[ReleaseManifestContract.SchemaPath] = schema.ToJsonString();
                break;
            case "missing-path": files.Remove("global.json"); break;
            case "project-identity":
                const string path = "tests/Modularyn.Repository.Tests/Modularyn.Repository.Tests.csproj";
                files[path] = files[path].Replace("<PropertyGroup>", "<PropertyGroup><RootNamespace>Modularyn.Wrong</RootNamespace>");
                break;
            case "solution-member": files["Modularyn.slnx"] = "<Solution />"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        files[NamesAndLayoutContract.CompanionPath] = names.ToJsonString();
        Assert.Contains(NamesAndLayoutContract.Validate(files), error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Modularyn", true)]
    [InlineData("dotnet-modularyn", true)]
    [InlineData("Modularyn.Framework.Sdk.Contracts", true)]
    [InlineData("Modularyn.UI.Admin.React", true)]
    [InlineData("Modularyn.Cms.Content.Domain", true)]
    [InlineData("Modularyn.Pro.Studio.Workspace", true)]
    [InlineData("Modularyn.Tooling.Generator.Core", true)]
    [InlineData("Modularyn.Connectors.Email.Smtp", true)]
    [InlineData("Modularyn.CMS.Content", false)]
    [InlineData("Modularyn.Ui.Razor", false)]
    [InlineData("Modularyn.Framework.SDK", false)]
    [InlineData("Modularyn.Modules.files.Domain", false)]
    [InlineData("Modularyn.Modules.Files_Domain", false)]
    [InlineData("Modularyn.Modules.Files-Domain", false)]
    [InlineData("Modularyn.Modules..Domain", false)]
    [InlineData("Modularyn.Unknown.Package", false)]
    [InlineData("Modularyn.Framework", false)]
    [InlineData("Modularyn.Framework.Core\n", false)]
    public void Package_grammar_is_case_sensitive_and_fully_anchored(string id, bool valid)
    {
        Assert.Equal(valid, NamesAndLayoutContract.IsPackageId(id));
    }

    [Theory]
    [InlineData("Modularyn.Modules.Files.Domain", "domain")]
    [InlineData("Modularyn.Modules.Files.Application.Contracts", "application-contracts")]
    [InlineData("Modularyn.Modules.Files.Application", "application-services")]
    [InlineData("Modularyn.Modules.Files.Persistence.EfCore", "persistence-adapter")]
    [InlineData("Modularyn.Modules.Files.HttpApi", "http-endpoints")]
    [InlineData("Modularyn.Modules.Files.Client", "client")]
    [InlineData("Modularyn.Cms.Content.UI.Razor", "ui-adapter")]
    [InlineData("Modularyn.Pro.Workflow.Domain", "domain")]
    [InlineData("Modularyn.Modules.Files.Persistence", "")]
    [InlineData("Modularyn.Modules.Files.UI", "")]
    [InlineData("Modularyn.Modules.Files.Application.Contracts.Extra", "")]
    [InlineData("Modularyn.Modules.Files.UI.React.Native", "")]
    public void Module_roles_have_one_unambiguous_suffix(string id, string role)
    {
        Assert.Equal(role, NamesAndLayoutContract.ModuleRole(id));
    }

    [Fact]
    public void Library_tool_front_door_and_test_projects_fit_the_layout()
    {
        var files = RepositoryFiles.Current.ToDictionary();
        AddProject(files, "src/Modularyn/Modularyn.csproj", "<Project />");
        AddProject(files, "src/Modules/Modularyn.Modules.Files.Domain/Modularyn.Modules.Files.Domain.csproj", "<Project />");
        AddProject(files, "tests/Modularyn.Modules.Files.Domain.Tests/Modularyn.Modules.Files.Domain.Tests.csproj",
            "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>");
        AddProject(files, "src/Tooling/Modularyn.Tooling.Cli/Modularyn.Tooling.Cli.csproj",
            "<Project><PropertyGroup><PackageId>dotnet-modularyn</PackageId><PackAsTool>true</PackAsTool><ToolCommandName>dotnet-modularyn</ToolCommandName></PropertyGroup></Project>");
        Assert.Empty(NamesAndLayoutContract.Validate(files));
    }

    [Theory]
    [InlineData("source-location", "source family layout")]
    [InlineData("package-identity", "project package identity")]
    [InlineData("module-role", "module role suffix")]
    [InlineData("paid-source", "paid sources belong")]
    [InlineData("paid-reference", "Community cannot reference paid")]
    [InlineData("domain-ui-reference", "domain cannot reference a UI framework")]
    [InlineData("orphan-tests", "test project needs its tested package")]
    [InlineData("tool-command", "tool command identity")]
    public void Project_boundary_violations_are_rejected_in_memory(string mutation, string diagnostic)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        Assert.Empty(NamesAndLayoutContract.Validate(files));
        var path = "src/Modules/Modularyn.Modules.Files.Domain/Modularyn.Modules.Files.Domain.csproj";
        var project = "<Project />";
        switch (mutation)
        {
            case "source-location": path = "src/Modularyn.Modules.Files.Domain/Modularyn.Modules.Files.Domain.csproj"; break;
            case "package-identity": project = "<Project><PropertyGroup><PackageId>Modularyn.Modules.Other.Domain</PackageId></PropertyGroup></Project>"; break;
            case "module-role": path = "src/Modules/Modularyn.Modules.Files.Storage/Modularyn.Modules.Files.Storage.csproj"; break;
            case "paid-source": path = "src/Pro/Modularyn.Pro.Studio.Workspace/Modularyn.Pro.Studio.Workspace.csproj"; break;
            case "paid-reference": project = "<Project><ItemGroup><PackageReference Include=\"Modularyn.Pro.Workflow.Domain\" /></ItemGroup></Project>"; break;
            case "domain-ui-reference": project = "<Project><ItemGroup><PackageReference Include=\"Modularyn.UI.Razor\" /></ItemGroup></Project>"; break;
            case "orphan-tests":
                path = "tests/Modularyn.Modules.Files.Domain.Tests/Modularyn.Modules.Files.Domain.Tests.csproj";
                project = "<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>";
                break;
            case "tool-command":
                path = "src/Tooling/Modularyn.Tooling.Cli/Modularyn.Tooling.Cli.csproj";
                project = "<Project><PropertyGroup><PackageId>dotnet-modularyn</PackageId><PackAsTool>true</PackAsTool><ToolCommandName>modularyn</ToolCommandName></PropertyGroup></Project>";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        AddProject(files, path, project);
        Assert.Contains(NamesAndLayoutContract.Validate(files), error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    private static void ReplaceGuide(Dictionary<string, string> files, string before, string after) =>
        files[NamesAndLayoutContract.GuidePath] = files[NamesAndLayoutContract.GuidePath].Replace(before, after);

    private static void AddProject(Dictionary<string, string> files, string path, string text)
    {
        files.Add(path, text);
        var solution = XDocument.Parse(files["Modularyn.slnx"]);
        solution.Root!.Add(new XElement("Project", new XAttribute("Path", path)));
        files["Modularyn.slnx"] = solution.ToString();
    }
}
