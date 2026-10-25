using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ArchitectureDiscoveryTests
{
    private const string Source = "Modularyn.Modules.Files.Application";

    [Theory]
    [InlineData("missing-solution-entry", "DEP001:")]
    [InlineData("solution-root", "DEP001:")]
    [InlineData("duplicate-solution-entry", "DEP001:")]
    [InlineData("missing-project-file", "DEP001:")]
    [InlineData("assembly-identity", "DEP001:")]
    [InlineData("conditional-identity", "DEP001:")]
    [InlineData("package-identity", "DEP001:")]
    [InlineData("wrong-family-directory", "DEP001:")]
    [InlineData("invalid-module-role", "DEP001:")]
    [InlineData("package-casing", "DEP001:")]
    [InlineData("missing-reference", "DEP002:")]
    [InlineData("dynamic-reference", "DEP002:")]
    [InlineData("reference-list", "DEP002:")]
    [InlineData("reference-glob", "DEP002:")]
    [InlineData("reference-update", "DEP002:")]
    [InlineData("reference-remove", "DEP002:")]
    [InlineData("reference-item-casing", "DEP002:")]
    [InlineData("reference-target", "DEP002:")]
    [InlineData("task-output", "DEP002:")]
    [InlineData("custom-sdk", "DEP002:")]
    [InlineData("explicit-import", "DEP002:")]
    [InlineData("auxiliary-props", "DEP002:")]
    [InlineData("auxiliary-targets", "DEP002:")]
    [InlineData("assembly-reference", "DEP002:")]
    [InlineData("com-reference", "DEP002:")]
    [InlineData("duplicate-version", "DEP002:")]
    [InlineData("conditional-version", "DEP002:")]
    [InlineData("version-override", "DEP002:")]
    [InlineData("ambiguous-version", "DEP002:")]
    [InlineData("conditional-version-element", "DEP002:")]
    [InlineData("paid-framework-reference", "DEP002:")]
    public void Incomplete_or_ambiguous_discovery_is_rejected(string mutation, string diagnostic)
    {
        var graph = new ArchitectureFixture();
        var path = graph.Add(Source);
        Assert.Empty(graph.Validate());
        switch (mutation)
        {
            case "missing-solution-entry": graph.Files["Modularyn.slnx"] = "<Solution />"; break;
            case "solution-root": graph.Files["Modularyn.slnx"] = graph.Files["Modularyn.slnx"].Replace("Solution", "Projects"); break;
            case "duplicate-solution-entry":
                var solution = XDocument.Parse(graph.Files["Modularyn.slnx"]);
                solution.Root!.Add(new XElement("Project", new XAttribute("Path", path)));
                graph.Files["Modularyn.slnx"] = solution.ToString();
                break;
            case "missing-project-file": graph.Files.Remove(path); break;
            case "assembly-identity": graph.Edit(Source, project => project.Root!.Add(new XElement("PropertyGroup", new XElement("AssemblyName", "Modularyn.Modules.Commerce.Application")))); break;
            case "conditional-identity": graph.Edit(Source, project => project.Root!.Add(new XElement("PropertyGroup", new XAttribute("Condition", "false"), new XElement("AssemblyName", Source)))); break;
            case "package-identity": graph.Edit(Source, project => project.Root!.Add(new XElement("PropertyGroup", new XElement("PackageId", "Modularyn.Pro.Workflow.Application")))); break;
            case "wrong-family-directory":
                var moved = path.Replace("src/Modules/", "src/Cms/");
                graph.Files[moved] = graph.Files[path];
                graph.Files.Remove(path);
                graph.Files["Modularyn.slnx"] = graph.Files["Modularyn.slnx"].Replace(path, moved);
                break;
            case "invalid-module-role": graph.PackageReference(Source, "Modularyn.Modules.Commerce.Internal"); break;
            case "package-casing": graph.PackageReference(Source, "modularyn.pro.Workflow.Application.Contracts"); break;
            case "missing-reference": graph.AddReference(Source, new XElement("ProjectReference", new XAttribute("Include", "../Commerce.csproj"))); break;
            case "dynamic-reference": graph.AddReference(Source, new XElement("PackageReference", new XAttribute("Include", "$(ModulePackage)"))); break;
            case "reference-list": graph.AddReference(Source, new XElement("PackageReference", new XAttribute("Include", "Modularyn.Framework.Domain;Modularyn.Pro.Workflow.Domain"))); break;
            case "reference-glob": graph.AddReference(Source, new XElement("ProjectReference", new XAttribute("Include", "../*/*.csproj"))); break;
            case "reference-update": graph.AddReference(Source, new XElement("PackageReference", new XAttribute("Update", "Modularyn.Framework.Domain"))); break;
            case "reference-remove": graph.AddReference(Source, new XElement("PackageReference", new XAttribute("Remove", "Modularyn.Framework.Domain"))); break;
            case "reference-item-casing": graph.AddReference(Source, new XElement("packagereference", new XAttribute("Include", "Modularyn.Pro.Workflow.Domain"))); break;
            case "reference-target": graph.Edit(Source, project => project.Root!.Add(new XElement("Target", new XAttribute("Name", "AddReferences"), new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", "Modularyn.Pro.Workflow.Domain")))))); break;
            case "task-output": graph.Edit(Source, project => project.Root!.Add(new XElement("Target", new XAttribute("Name", "ComputeReferences"), new XElement("ResolveModules", new XElement("Output", new XAttribute("TaskParameter", "Packages"), new XAttribute("ItemName", "PackageReference")))))); break;
            case "custom-sdk": graph.Edit(Source, project => project.Root!.SetAttributeValue("Sdk", "Modularyn.Tooling.Build")); break;
            case "explicit-import": graph.Edit(Source, project => project.Root!.Add(new XElement("Import", new XAttribute("Project", "Module.props")))); break;
            case "auxiliary-props": graph.Files["tests/Module.props"] = "<Project><ItemGroup><PackageReference Include=\"Modularyn.Pro.Workflow.Domain\" /></ItemGroup></Project>"; break;
            case "auxiliary-targets": graph.Files["tests/Module.targets"] = "<Project><Target Name=\"References\"><ItemGroup><ProjectReference Include=\"Module.csproj\" /></ItemGroup></Target></Project>"; break;
            case "assembly-reference": graph.AddReference(Source, new XElement("Reference", new XAttribute("Include", "Modularyn.Pro.Workflow.Domain"))); break;
            case "com-reference": graph.AddReference(Source, new XElement("COMReference", new XAttribute("Include", "Module"))); break;
            case "duplicate-version":
                graph.Files["Directory.Packages.props"] = "<Project><ItemGroup><PackageVersion Include=\"Modularyn.Framework.Domain\" Version=\"1.2.3\" /><PackageVersion Include=\"Modularyn.Framework.Domain\" Version=\"2.0.0\" /></ItemGroup></Project>";
                break;
            case "conditional-version":
                graph.Files["Directory.Packages.props"] = "<Project><ItemGroup Condition=\"false\"><PackageVersion Include=\"Modularyn.Framework.Domain\" Version=\"1.2.3\" /></ItemGroup></Project>";
                break;
            case "version-override": graph.AddReference(Source, new XElement("PackageReference", new XAttribute("Include", "Modularyn.Framework.Domain"), new XAttribute("VersionOverride", "1.2.3"))); break;
            case "ambiguous-version": graph.AddReference(Source, new XElement("PackageReference", new XAttribute("Include", "Modularyn.Framework.Domain"), new XAttribute("Version", "1.2.3"), new XElement("Version", "2.0.0"))); break;
            case "conditional-version-element": graph.AddReference(Source, new XElement("PackageReference", new XAttribute("Include", "Modularyn.Framework.Domain"), new XElement("Version", new XAttribute("Condition", "false"), "1.2.3"))); break;
            case "paid-framework-reference": graph.AddReference(Source, new XElement("FrameworkReference", new XAttribute("Include", "Modularyn.Pro.Workflow.Domain"))); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Contains(graph.Validate(), error => error.StartsWith(diagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("item")]
    [InlineData("group")]
    [InlineData("output-disabled")]
    public void Conditional_or_noncompiling_paid_edges_still_fail_the_Community_boundary(string condition)
    {
        var graph = new ArchitectureFixture();
        graph.Add(Source);
        Assert.Empty(graph.Validate());
        graph.PackageReference(Source, "Modularyn.Pro.Workflow.Application.Contracts");
        graph.Edit(Source, project =>
        {
            var item = project.Descendants("PackageReference").Single();
            if (condition == "group") item.Parent!.SetAttributeValue("Condition", "false");
            else if (condition == "item") item.SetAttributeValue("Condition", "false");
            else item.Add(new XElement("ExcludeAssets", "all"));
        });
        Assert.Contains(graph.Validate(), error => error.StartsWith("DEP003: Community must not reference paid modules.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Isolated_graphs_understand_literal_attribute_and_element_versions(bool element)
    {
        var graph = new ArchitectureFixture("modularyn-pro");
        var item = new XElement("PackageReference", new XAttribute("Include", "Modularyn.Modules.Files.Application.Contracts"));
        if (element) item.Add(new XElement("Version", "1.2.3"));
        else item.Add(new XAttribute("Version", "1.2.3"));
        graph.AddReference("Modularyn.Pro.Workflow.Application", item);
        Assert.Empty(graph.Validate());
    }

    [Fact]
    public void Project_references_with_disabled_output_still_obey_the_edition_boundary()
    {
        var graph = new ArchitectureFixture();
        graph.Add(Source);
        Assert.Empty(graph.Validate());
        graph.ExternalProjectReference(Source, "Modularyn.Pro.Workflow.Application.Contracts");
        graph.Edit(Source, project => project.Descendants("ProjectReference").Single().Add(new XElement("ReferenceOutputAssembly", "false")));
        Assert.Contains(graph.Validate(), error => error.StartsWith("DEP003: Community must not reference paid modules.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("prose")]
    [InlineData("rule-value")]
    [InlineData("extra-field")]
    [InlineData("missing-rule")]
    [InlineData("unknown-role")]
    [InlineData("format-version")]
    [InlineData("event-role")]
    [InlineData("names-grammar")]
    [InlineData("duplicate-field")]
    public void Policy_and_prose_drift_is_rejected(string mutation)
    {
        var graph = new ArchitectureFixture();
        Assert.Empty(graph.Validate());
        var rules = JsonNode.Parse(graph.Files[ArchitecturePolicy.RulesPath])!;
        switch (mutation)
        {
            case "prose": graph.Files[ArchitecturePolicy.GuidePath] = graph.Files[ArchitecturePolicy.GuidePath].Replace("| `Framework` | `Framework` |", "| `Framework` | `UI` |"); break;
            case "rule-value": rules["sameModuleReferences"]!["domain"]!.AsArray().Add("ui-adapter"); break;
            case "extra-field": rules["additionalRule"] = true; break;
            case "missing-rule": rules["diagnostics"]!.AsObject().Remove("DEP003"); break;
            case "unknown-role": rules["sameModuleReferences"]!["domain"]!.AsArray().Add("internal"); break;
            case "format-version": rules["formatVersion"] = 2; break;
            case "event-role": rules["eventContractRole"] = "domain"; break;
            case "names-grammar":
                var names = JsonNode.Parse(graph.Files[NamesAndLayoutContract.CompanionPath])!;
                names["packageIdPattern"] = ".*";
                graph.Files[NamesAndLayoutContract.CompanionPath] = names.ToJsonString();
                break;
            case "duplicate-field": break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        graph.Files[ArchitecturePolicy.RulesPath] = rules.ToJsonString();
        if (mutation == "duplicate-field") graph.Files[ArchitecturePolicy.RulesPath] = graph.Files[ArchitecturePolicy.RulesPath].Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1");
        Assert.Contains(graph.Validate(), error => error.StartsWith("DEP013: The dependency companion, guide and names contract must agree.", StringComparison.Ordinal));
    }
}
