using Xunit;

namespace Modularyn.Repository.Tests;

/// <summary>
/// The composition-root role: a sample's host under <c>Modularyn.Tooling.Samples.</c>, ending in <c>.Host</c>, composes module
/// implementations; only the same sample's test project references it; every other project keeps DEP009.
/// </summary>
public sealed class CompositionRootTests
{
    private const string Host = "Modularyn.Tooling.Samples.TeamWorkspace.Host";
    private const string Dep009 = "DEP009: Another module is accessible only through allowed application or event contracts. ";
    private const string Dep014 = "DEP014: No project but its sample's own tests may reference a composition root. ";
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
    [InlineData("TeamWorkspace", false)]
    [InlineData("TeamWorkspace", true)]
    [InlineData("TeamWorkspace.Admin", false)]
    public void A_sample_s_own_test_project_may_reference_its_host(string sample, bool package)
    {
        var graph = new ArchitectureFixture();
        var host = "Modularyn.Tooling.Samples." + sample + ".Host";
        graph.Add(host);
        var tests = graph.AddTest("Modularyn.Tooling.Samples." + sample);
        Assert.Equal("Modularyn.Tooling.Samples." + sample + ".Tests", tests);
        Assert.Empty(graph.Validate());
        if (package) graph.PackageReference(tests, host);
        else graph.ProjectReference(tests, host);
        Assert.Empty(graph.Validate());
    }

    [Theory]
    [InlineData("Modularyn.Tooling.Samples.Gallery", Host)]
    [InlineData("Modularyn.Tooling.Samples.TeamWorkspace.Admin", Host)]
    [InlineData("Modularyn.Tooling.Samples.TeamWorkspace", "Modularyn.Tooling.Samples.TeamWorkspace.Admin.Host")]
    [InlineData("Modularyn.Tooling.Samples.TeamWorkspace.Host", Host)]
    [InlineData("Modularyn.Tooling.Generator.Core", Host)]
    [InlineData("Modularyn.Modules.Files.Application", Host)]
    [InlineData("Modularyn.Repository", Host)]
    public void No_other_test_project_may_reference_a_composition_root(string tested, string host)
    {
        // Another sample's tests, a nested sample's in either direction, tests named for the host package itself, and any other
        // test project, the repository's included.
        var graph = new ArchitectureFixture();
        graph.Add(host);
        var tests = tested == "Modularyn.Repository" ? "Modularyn.Repository.Tests" : graph.AddTest(tested);
        Assert.Empty(graph.Validate());
        graph.ProjectReference(tests, host);
        Assert.Contains(graph.Validate(), error => error.StartsWith(Dep014 + tests + " -> " + host, StringComparison.Ordinal));
    }

    [Fact]
    public void A_product_named_like_the_sample_s_tests_gets_no_exception()
    {
        // The exception is a test project's: a product with that identity is an ordinary Tooling project.
        const string product = "Modularyn.Tooling.Samples.TeamWorkspace.Tests";
        var graph = new ArchitectureFixture();
        graph.Add(Host);
        Assert.StartsWith("src/Tooling/", graph.Add(product), StringComparison.Ordinal);
        Assert.Empty(graph.Validate());
        graph.ProjectReference(product, Host);
        Assert.Contains(graph.Validate(), error => error.StartsWith(Dep014 + product + " -> " + Host, StringComparison.Ordinal));
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
