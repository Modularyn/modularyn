using Xunit;

namespace Modularyn.Repository.Tests;

/// <summary>
/// The composition-root role: a sample's host under <c>Modularyn.Tooling.Samples.</c>, ending in <c>.Host</c>, composes module
/// implementations; nothing references it; every other project keeps DEP009.
/// </summary>
public sealed class CompositionRootTests
{
    private const string Host = "Modularyn.Tooling.Samples.TeamWorkspace.Host";
    private const string Dep009 = "DEP009: Another module is accessible only through allowed application or event contracts. ";
    private const string Dep014 = "DEP014: No project may reference a composition root. ";
    private static readonly string[] Implementations =
        ["Modularyn.Modules.Identity.Application", "Modularyn.Modules.Identity.HttpApi", "Modularyn.Modules.Identity.Persistence.EfCore"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_sample_host_composes_application_services_HTTP_endpoints_persistence_adapters_and_the_framework(bool package)
    {
        var graph = new ArchitectureFixture();
        graph.Add(Host);
        Assert.Empty(graph.Validate());
        string[] targets =
        [
            .. Implementations, "Modularyn.Modules.Identity.Application.Contracts", "Modularyn.Cms.Content.Application",
            "Modularyn.Cms.Content.HttpApi", "Modularyn.Cms.Content.Persistence.EfCore", "Modularyn.Framework.Hosting", "Modularyn.Framework.Api"
        ];
        foreach (var target in targets)
        {
            if (package) graph.PackageReference(Host, target);
            else graph.ProjectReference(Host, target);
        }
        Assert.Empty(graph.Validate());
    }

    [Theory]
    [InlineData("Modularyn.Modules.Identity.Domain")]
    [InlineData("Modularyn.Modules.Identity.Client")]
    [InlineData("Modularyn.Modules.Identity.UI.Razor")]
    public void A_sample_host_still_reaches_no_other_module_role(string target)
    {
        var graph = new ArchitectureFixture();
        graph.Add(Host);
        Assert.Empty(graph.Validate());
        graph.ProjectReference(Host, target);
        Assert.Contains(graph.Validate(), error => error.StartsWith(Dep009 + Host + " -> " + target, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Modularyn.Tooling.Samples.TeamWorkspace.Admin.Host")]
    [InlineData("Modularyn.Tooling.Samples.TeamWorkspace")]
    [InlineData("Modularyn.Tooling.Generator.Core")]
    [InlineData("dotnet-modularyn")]
    [InlineData("Modularyn")]
    [InlineData("Modularyn.Framework.Hosting")]
    [InlineData("Modularyn.Modules.Files.Application")]
    [InlineData("Modularyn.Cms.Content.HttpApi")]
    [InlineData("Modularyn.UI.Admin.Shell")]
    [InlineData("Modularyn.Connectors.Files.Storage")]
    [InlineData("tests")]
    public void No_project_may_reference_a_composition_root(string source)
    {
        foreach (var package in new[] { false, true })
        {
            var graph = new ArchitectureFixture();
            graph.Add(Host);
            var from = source == "tests" ? graph.AddTest(Host) : source;
            graph.Add(from);
            Assert.Empty(graph.Validate());
            if (package) graph.PackageReference(from, Host);
            else graph.ProjectReference(from, Host);
            Assert.Contains(graph.Validate(), error => error.StartsWith(Dep014 + from + " -> " + Host, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Modularyn.Tooling.Samples.TeamWorkspace")]
    [InlineData("Modularyn.Tooling.Samples.TeamWorkspace.Hosting")]
    [InlineData("Modularyn.Tooling.Samples.TeamWorkspace.Host.Pages")]
    [InlineData("Modularyn.Tooling.Generator.Core")]
    [InlineData("dotnet-modularyn")]
    [InlineData("Modularyn")]
    [InlineData("Modularyn.Framework.Hosting")]
    [InlineData("Modularyn.UI.Admin.Shell")]
    [InlineData("Modularyn.Connectors.Files.Storage")]
    [InlineData("Modularyn.Cms.Content.Application")]
    public void Every_other_project_including_other_Tooling_projects_keeps_DEP009(string source)
    {
        foreach (var target in Implementations)
        {
            var graph = new ArchitectureFixture();
            graph.Add(source);
            Assert.Empty(graph.Validate());
            graph.ProjectReference(source, target);
            Assert.Contains(graph.Validate(), error => error.StartsWith(Dep009 + source + " -> " + target, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Modularyn.Pro.Studio.Workspace")]
    [InlineData("Modularyn.Pro.Workflow.Application")]
    public void Paid_projects_keep_DEP009_and_cannot_reference_a_composition_root_by_package(string source)
    {
        foreach (var target in Implementations)
        {
            var graph = new ArchitectureFixture("modularyn-pro");
            graph.Add(source);
            Assert.Empty(graph.Validate());
            graph.PackageReference(source, target);
            Assert.Contains(graph.Validate(), error => error.StartsWith(Dep009 + source + " -> " + target, StringComparison.Ordinal));
        }
        var hosted = new ArchitectureFixture("modularyn-pro");
        hosted.PackageReference(source, "Modularyn.Modules.Identity.Application.Contracts");
        Assert.Empty(hosted.Validate());
        hosted.PackageReference(source, Host);
        Assert.Contains(hosted.Validate(), error => error.StartsWith(Dep014 + source + " -> " + Host, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Modularyn.Tooling.Host")]
    [InlineData("Modularyn.Tooling.Samples.Host")]
    [InlineData("Modularyn.Tooling.SamplesGallery.TeamWorkspace.Host")]
    [InlineData("Modularyn.Tooling.Generator.Host")]
    [InlineData("Modularyn.Framework.Host")]
    [InlineData("Modularyn.UI.Admin.Host")]
    [InlineData("Modularyn.Connectors.Files.Host")]
    public void A_host_outside_the_samples_subtree_is_no_composition_root(string source)
    {
        foreach (var target in Implementations)
        {
            var graph = new ArchitectureFixture();
            graph.Add(source);
            Assert.Empty(graph.Validate());
            graph.ProjectReference(source, target);
            Assert.Contains(graph.Validate(), error => error.StartsWith(Dep009 + source + " -> " + target, StringComparison.Ordinal));
        }
        // Nor is it shielded as one: an ordinary project may still reference it.
        var referenced = new ArchitectureFixture();
        referenced.ProjectReference("Modularyn.Tooling.Generator.Core", source);
        Assert.Empty(referenced.Validate());
    }
}
