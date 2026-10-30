using System.Xml.Linq;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Repository_projects_and_dependency_documents_satisfy_the_architecture_contract()
    {
        Assert.Empty(ArchitectureContract.Validate(RepositoryFiles.Current));
    }

    [Fact]
    public void Complete_inward_graph_and_public_cross_module_contracts_are_allowed()
    {
        var graph = new ArchitectureFixture();
        graph.ProjectReference("Modularyn.Modules.Files.Domain", "Modularyn.Framework.Domain");
        graph.ProjectReference("Modularyn.Modules.Files.Domain", "Modularyn.Framework.Modularity.Abstractions");
        graph.ProjectReference("Modularyn.Modules.Files.Application.Contracts", "Modularyn.Framework.Modularity.Abstractions");
        graph.ProjectReference("Modularyn.Framework.Hosting", "Modularyn.Framework.Modularity");
        graph.ProjectReference("Modularyn.Framework.Hosting", "Modularyn.Framework.Observability");
        graph.AddReference("Modularyn.Framework.Observability", new XElement("FrameworkReference", new XAttribute("Include", "Microsoft.AspNetCore.App")));
        graph.ProjectReference("Modularyn.Framework.Modularity", "Modularyn.Framework.Modularity.Abstractions");
        graph.ProjectReference("Modularyn.Modules.Files.Application.Contracts", "Modularyn.Framework.Sdk.Contracts");
        graph.ProjectReference("Modularyn.Modules.Files.Application", "Modularyn.Modules.Files.Domain");
        graph.ProjectReference("Modularyn.Modules.Files.Application", "Modularyn.Modules.Files.Application.Contracts");
        graph.ProjectReference("Modularyn.Modules.Files.Application", "Modularyn.Cms.Content.Application.Contracts");
        graph.ProjectReference("Modularyn.Modules.Files.Persistence.EfCore", "Modularyn.Modules.Files.Domain");
        graph.ProjectReference("Modularyn.Modules.Files.Persistence.EfCore", "Modularyn.Connectors.Files.Storage");
        graph.ProjectReference("Modularyn.Modules.Files.HttpApi", "Modularyn.Modules.Files.Application.Contracts");
        graph.ProjectReference("Modularyn.Modules.Files.Client", "Modularyn.Modules.Files.Application.Contracts");
        graph.ProjectReference("Modularyn.Modules.Files.UI.Razor", "Modularyn.Modules.Files.Client");
        graph.ProjectReference("Modularyn.Modules.Files.UI.Razor", "Modularyn.Modules.Files.Application.Contracts");
        graph.ProjectReference("Modularyn.Modules.Files.UI.Razor", "Modularyn.UI.Components");
        graph.ProjectReference("Modularyn", "Modularyn.Modules.Files.Application.Contracts");
        graph.ProjectReference("dotnet-modularyn", "Modularyn.Tooling.Generator.Core");
        graph.ProjectReference("Modularyn.Framework.Sdk.Contracts", "Modularyn.Framework.Core.Abstractions");
        graph.ProjectReference(graph.AddTest("Modularyn.Modules.Files.Domain"), "Modularyn.Modules.Files.Domain");
        Assert.Empty(graph.Validate());
    }

    [Theory]
    [InlineData("Modularyn.Modules.Files.Domain", "Modules", "Modularyn.Modules.Files", "domain", "community")]
    [InlineData("Modularyn.Cms.Content.Persistence.EfCore", "Cms", "Modularyn.Cms.Content", "persistence-adapter", "community")]
    [InlineData("Modularyn.Cms.Content.UI.Razor", "Cms", "Modularyn.Cms.Content", "ui-adapter", "community")]
    [InlineData("Modularyn.Pro.Workflow.Application.Contracts", "Pro", "Modularyn.Pro.Workflow", "application-contracts", "paid")]
    [InlineData("Modularyn.Pro.Studio.Domain", "Pro", "", "", "paid")]
    [InlineData("Modularyn.Framework.Sdk.Contracts", "Framework", "", "", "community")]
    [InlineData("Modularyn.Framework.Modularity.Abstractions", "Framework", "", "", "community")]
    [InlineData("Modularyn.Framework.Modularity", "Framework", "", "", "community")]
    [InlineData("Modularyn.Framework.Hosting", "Framework", "", "", "community")]
    [InlineData("Modularyn.Framework.Observability", "Framework", "", "", "community")]
    [InlineData("Modularyn.UI.Admin.Shell", "UI", "", "", "community")]
    [InlineData("Modularyn", "FrontDoor", "", "", "community")]
    [InlineData("dotnet-modularyn", "Tooling", "", "", "community")]
    public void Package_identity_uses_the_names_contract(string id, string family, string module, string role, string edition)
    {
        var policy = ArchitecturePolicy.Load(RepositoryFiles.Current);
        var node = policy.Package(id);
        Assert.Equal((family, module, role, edition), (node.Family, node.Module, node.Role, node.Edition));
        var fixture = new ArchitectureFixture(node.Repository);
        Assert.Equal(policy.SourcePath(node), fixture.Add(id));
        Assert.Empty(fixture.Validate());
    }

    [Theory]
    [InlineData("Modularyn.Modules.Files.Domain")]
    [InlineData("Modularyn.Modules.Files.Application.Contracts")]
    public void Domain_and_contracts_cannot_acquire_the_observability_adapter(string source)
    {
        var graph = new ArchitectureFixture();
        graph.Add(source);
        Assert.Empty(graph.Validate());
        graph.ProjectReference(source, "Modularyn.Framework.Observability");
        Assert.Contains(graph.Validate(), error => error.StartsWith("DEP010:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.0.0")]
    [InlineData("12.34.567")]
    public void Paid_projects_accept_exact_Community_package_declarations(string version)
    {
        var graph = new ArchitectureFixture("modularyn-pro");
        graph.PackageReference("Modularyn.Pro.Workflow.Application", "Modularyn.Modules.Files.Application.Contracts", version);
        graph.PackageReference("Modularyn.Pro.Studio.Workspace", "dotnet-modularyn", "1.2.3");
        Assert.Empty(graph.Validate());
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void Every_edition_and_reference_kind_pair_is_enforced(bool sourcePaid, bool targetPaid, bool package, bool allowed)
    {
        var graph = new ArchitectureFixture(sourcePaid ? "modularyn-pro" : "modularyn");
        var source = sourcePaid ? "Modularyn.Pro.Workflow.Application" : "Modularyn.Modules.Files.Application";
        var target = targetPaid ? "Modularyn.Pro.Subscriptions.Application.Contracts" : "Modularyn.Modules.Commerce.Application.Contracts";
        graph.Add(source);
        Assert.Empty(graph.Validate());
        if (package) graph.PackageReference(source, target);
        else if (sourcePaid != targetPaid) graph.ExternalProjectReference(source, target);
        else graph.ProjectReference(source, target);
        var errors = graph.Validate();
        if (allowed) Assert.Empty(errors);
        else Assert.Contains(errors, error => error.StartsWith(sourcePaid
            ? "DEP004: Paid projects must consume Community through packages, never project references."
            : "DEP003: Community must not reference paid modules.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_slash_styles_resolve_project_references(bool backslashes)
    {
        var graph = new ArchitectureFixture();
        var source = "Modularyn.Modules.Files.Application";
        graph.ProjectReference(source, "Modularyn.Modules.Files.Domain");
        if (backslashes)
            graph.Edit(source, project => project.Descendants("ProjectReference").Single().Attribute("Include")!.Value =
                project.Descendants("ProjectReference").Single().Attribute("Include")!.Value.Replace('/', '\\'));
        Assert.Empty(graph.Validate());
    }

    [Fact]
    public void Platform_prefixes_do_not_match_unrelated_package_names()
    {
        var graph = new ArchitectureFixture();
        graph.PackageReference("Modularyn.Modules.Files.Domain", "Microsoft.AspNetCoreTools", "1.2.3");
        Assert.Empty(graph.Validate());
    }

    [Fact]
    public void Policy_agreement_is_independent_of_line_endings()
    {
        var graph = new ArchitectureFixture();
        graph.Files[ArchitecturePolicy.GuidePath] = graph.Files[ArchitecturePolicy.GuidePath].Replace("\n", "\r\n");
        Assert.Empty(graph.Validate());
    }
}

internal sealed class ArchitectureFixture
{
    internal readonly Dictionary<string, string> Files;
    private readonly string repository;
    private readonly Dictionary<string, string> paths = new(StringComparer.Ordinal);
    private readonly ArchitecturePolicy policy;

    internal ArchitectureFixture(string repository = "modularyn")
    {
        this.repository = repository;
        var original = RepositoryFiles.Current;
        Files = new(StringComparer.Ordinal);
        foreach (var path in new[] { ArchitecturePolicy.RulesPath, ArchitecturePolicy.GuidePath,
            NamesAndLayoutContract.CompanionPath, "Directory.Packages.props" }) Files.Add(path, original[path]);
        policy = ArchitecturePolicy.Load(Files);
        Files.Add("Modularyn.slnx", "<Solution />");
        var test = policy.TestProject;
        // Each case declares its own graph. The repository test project's own project references are checked against the real repository.
        var testProject = XDocument.Parse(original[test]);
        testProject.Descendants("ProjectReference").Remove();
        Files.Add(test, testProject.ToString());
        ListProject(test);
        paths.Add("Modularyn.Repository.Tests", test);
    }

    internal string Add(string id)
    {
        if (paths.TryGetValue(id, out var existing)) return existing;
        var node = policy.Package(id);
        var path = policy.SourcePath(node);
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"));
        if (id == "dotnet-modularyn") project.Add(new XElement("PropertyGroup", new XElement("PackageId", id)));
        Files.Add(path, project.ToString());
        paths.Add(id, path);
        ListProject(path);
        return path;
    }

    internal string AddTest(string product)
    {
        Add(product);
        var name = product + ".Tests";
        var path = "tests/" + name + "/" + name + ".csproj";
        Files.Add(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>");
        paths.Add(name, path);
        ListProject(path);
        return name;
    }

    internal void ProjectReference(string source, string target)
    {
        var sourcePath = Add(source);
        var targetPath = Add(target);
        var sourceParts = sourcePath.Split('/').SkipLast(1).ToArray();
        var targetParts = targetPath.Split('/');
        var common = 0;
        while (common < sourceParts.Length && common < targetParts.Length && sourceParts[common] == targetParts[common]) common++;
        var relative = string.Join('/', Enumerable.Repeat("..", sourceParts.Length - common).Concat(targetParts.Skip(common)));
        AddReference(source, new XElement("ProjectReference", new XAttribute("Include", relative)));
    }

    internal void ExternalProjectReference(string source, string target)
    {
        var targetNode = policy.Package(target);
        AddReference(source, new XElement("ProjectReference", new XAttribute("Include",
            "../../../../" + targetNode.Repository + "/" + policy.SourcePath(targetNode))));
    }

    internal void PackageReference(string source, string target, string? version = "1.2.3")
    {
        if (version is not null)
        {
            var versions = XDocument.Parse(Files["Directory.Packages.props"]);
            if (!versions.Descendants("PackageVersion").Any(item => (string?)item.Attribute("Include") == target))
                versions.Root!.Add(new XElement("ItemGroup", new XElement("PackageVersion", new XAttribute("Include", target), new XAttribute("Version", version))));
            Files["Directory.Packages.props"] = versions.ToString();
        }
        AddReference(source, new XElement("PackageReference", new XAttribute("Include", target)));
    }

    internal void AddReference(string source, XElement item) => Edit(source, project => project.Root!.Add(new XElement("ItemGroup", item)));

    internal void Edit(string id, Action<XDocument> edit)
    {
        var path = Add(id);
        var project = XDocument.Parse(Files[path]);
        edit(project);
        Files[path] = project.ToString();
    }

    internal List<string> Validate() => ArchitectureContract.Validate(Files, repository);

    private void ListProject(string path)
    {
        var solution = XDocument.Parse(Files["Modularyn.slnx"]);
        solution.Root!.Add(new XElement("Project", new XAttribute("Path", path)));
        Files["Modularyn.slnx"] = solution.ToString();
    }
}
