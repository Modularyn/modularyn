using System.Xml.Linq;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ArchitectureRejectionTests
{
    private static readonly string[] Suffixes = ["Domain", "Application.Contracts", "Application", "Persistence.EfCore", "HttpApi", "Client", "UI.Razor"];

    [Theory]
    [InlineData("community-package", "DEP003: Community must not reference paid modules.")]
    [InlineData("community-project", "DEP003: Community must not reference paid modules.")]
    [InlineData("front-door-paid", "DEP003: Community must not reference paid modules.")]
    [InlineData("tool-paid", "DEP003: Community must not reference paid modules.")]
    [InlineData("test-paid", "DEP003: Community must not reference paid modules.")]
    [InlineData("domain-ui", "DEP007: Domain must not reference UI, HTTP, client or persistence adapters.")]
    [InlineData("domain-http", "DEP007: Domain must not reference UI, HTTP, client or persistence adapters.")]
    [InlineData("domain-persistence", "DEP007: Domain must not reference UI, HTTP, client or persistence adapters.")]
    [InlineData("domain-framework-ui", "DEP007: Domain must not reference UI, HTTP, client or persistence adapters.")]
    [InlineData("other-module-domain", "DEP009: Another module is accessible only through allowed application or event contracts.")]
    [InlineData("other-module-persistence", "DEP009: Another module is accessible only through allowed application or event contracts.")]
    [InlineData("framework-module", "DEP006: The source family does not allow the target family.")]
    [InlineData("framework-cms", "DEP006: The source family does not allow the target family.")]
    [InlineData("framework-ui", "DEP006: The source family does not allow the target family.")]
    [InlineData("paid-project", "DEP004: Paid projects must consume Community through packages, never project references.")]
    [InlineData("contracts-domain", "DEP008: The source role does not allow this role within its module.")]
    [InlineData("http-persistence", "DEP008: The source role does not allow this role within its module.")]
    [InlineData("persistence-http", "DEP008: The source role does not allow this role within its module.")]
    [InlineData("domain-framework-runtime", "DEP010: The source role does not allow this support package.")]
    [InlineData("contracts-framework-domain", "DEP010: The source role does not allow this support package.")]
    [InlineData("product-tests", "DEP012: Product projects must not reference test projects.")]
    public void Forbidden_references_report_their_specific_reason(string mutation, string diagnostic)
    {
        var graph = new ArchitectureFixture(mutation == "paid-project" ? "modularyn-pro" : "modularyn");
        var source = mutation switch
        {
            "front-door-paid" => "Modularyn",
            "tool-paid" => "dotnet-modularyn",
            "test-paid" => "Modularyn.Repository.Tests",
            "paid-project" => "Modularyn.Pro.Workflow.Application",
            "contracts-domain" or "contracts-framework-domain" => "Modularyn.Modules.Files.Application.Contracts",
            "http-persistence" => "Modularyn.Modules.Files.HttpApi",
            "persistence-http" => "Modularyn.Modules.Files.Persistence.EfCore",
            _ when mutation.StartsWith("domain-", StringComparison.Ordinal) => "Modularyn.Modules.Files.Domain",
            _ when mutation.StartsWith("framework-", StringComparison.Ordinal) => "Modularyn.Framework.Composition",
            _ => "Modularyn.Modules.Files.Application"
        };
        graph.Add(source);
        Assert.Empty(graph.Validate());
        switch (mutation)
        {
            case "community-package": case "front-door-paid": case "tool-paid": case "test-paid":
                graph.PackageReference(source, "Modularyn.Pro.Workflow.Application.Contracts"); break;
            case "community-project": graph.ExternalProjectReference(source, "Modularyn.Pro.Workflow.Application.Contracts"); break;
            case "domain-ui": graph.ProjectReference(source, "Modularyn.Modules.Files.UI.Razor"); break;
            case "domain-http": graph.ProjectReference(source, "Modularyn.Modules.Files.HttpApi"); break;
            case "domain-persistence": graph.ProjectReference(source, "Modularyn.Modules.Files.Persistence.EfCore"); break;
            case "domain-framework-ui": graph.ProjectReference(source, "Modularyn.Framework.UI.Contracts"); break;
            case "other-module-domain": graph.ProjectReference(source, "Modularyn.Modules.Commerce.Domain"); break;
            case "other-module-persistence": graph.ProjectReference(source, "Modularyn.Modules.Commerce.Persistence.EfCore"); break;
            case "framework-module": graph.ProjectReference(source, "Modularyn.Modules.Files.Application.Contracts"); break;
            case "framework-cms": graph.ProjectReference(source, "Modularyn.Cms.Content.Application.Contracts"); break;
            case "framework-ui": graph.ProjectReference(source, "Modularyn.UI.Admin.Shell"); break;
            case "paid-project": graph.ExternalProjectReference(source, "Modularyn.Modules.Files.Application.Contracts"); break;
            case "contracts-domain": graph.ProjectReference(source, "Modularyn.Modules.Files.Domain"); break;
            case "http-persistence": graph.ProjectReference(source, "Modularyn.Modules.Files.Persistence.EfCore"); break;
            case "persistence-http": graph.ProjectReference(source, "Modularyn.Modules.Files.HttpApi"); break;
            case "domain-framework-runtime": graph.ProjectReference(source, "Modularyn.Framework.Runtime"); break;
            case "contracts-framework-domain": graph.ProjectReference(source, "Modularyn.Framework.Domain"); break;
            case "product-tests": graph.ProjectReference(source, "Modularyn.Repository.Tests"); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Contains(graph.Validate(), error => error.StartsWith(diagnostic, StringComparison.Ordinal));
    }

    public static TheoryData<string, string, bool, bool> RolePairs() => Pairs(false);
    public static TheoryData<string, string, bool, bool> CrossModulePairs() => Pairs(true);

    private static TheoryData<string, string, bool, bool> Pairs(bool crossModule)
    {
        var data = new TheoryData<string, string, bool, bool>();
        foreach (var package in new[] { false, true })
        for (var source = 0; source < Suffixes.Length; source++)
        for (var target = 0; target < Suffixes.Length; target++)
        {
            var allowed = crossModule ? source != 0 && target == 1
                : source switch { 2 => target is 0 or 1, 3 => target == 0, 4 or 5 => target == 1, 6 => target is 1 or 5, _ => false };
            data.Add(Suffixes[source], Suffixes[target], package, allowed);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RolePairs))]
    public void Every_same_module_role_pair_is_enforced_for_projects_and_packages(string sourceRole, string targetRole, bool package, bool allowed)
    {
        var graph = new ArchitectureFixture();
        var source = "Modularyn.Modules.Files." + sourceRole;
        var target = "Modularyn.Modules.Files." + targetRole;
        graph.Add(source);
        graph.Add(target);
        Assert.Empty(graph.Validate());
        if (package) graph.PackageReference(source, target);
        else graph.ProjectReference(source, target);
        var errors = graph.Validate();
        if (allowed) Assert.Empty(errors);
        else Assert.Contains(errors, error => error.StartsWith("DEP008: The source role does not allow this role within its module.", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(CrossModulePairs))]
    public void Every_cross_module_role_pair_is_enforced_for_projects_and_packages(string sourceRole, string targetRole, bool package, bool allowed)
    {
        var graph = new ArchitectureFixture();
        var source = "Modularyn.Modules.Files." + sourceRole;
        var target = "Modularyn.Cms.Content." + targetRole;
        graph.Add(source);
        graph.Add(target);
        Assert.Empty(graph.Validate());
        if (package) graph.PackageReference(source, target);
        else graph.ProjectReference(source, target);
        var errors = graph.Validate();
        if (allowed) Assert.Empty(errors);
        else Assert.Contains(errors, error => error.StartsWith("DEP009: Another module is accessible only through allowed application or event contracts.", StringComparison.Ordinal));
    }

    public static TheoryData<string, string, bool> FamilyPairs()
    {
        var data = new TheoryData<string, string, bool>();
        var sources = new[] { "Modularyn.Framework.Composition", "Modularyn.Modules.Files.Application", "Modularyn.Cms.Content.Application",
            "Modularyn.UI.Admin.Shell", "Modularyn.Connectors.Files.Storage", "Modularyn.Pro.Studio.Workspace", "Modularyn.Tooling.Generator.Core", "Modularyn" };
        var targets = new[] { "Modularyn.Framework.Sdk.Contracts", "Modularyn.Modules.Commerce.Application.Contracts", "Modularyn.Cms.Search.Application.Contracts",
            "Modularyn.UI.Components", "Modularyn.Connectors.Content.Storage", "Modularyn.Pro.Workflow.Application.Contracts", "Modularyn.Tooling.Cli.Contracts", "Modularyn" };
        int[][] allowedTargets = [[0], [0, 1, 2, 3, 4], [0, 1, 2, 3, 4], [0, 1, 2, 3],
            [0, 1, 2, 4], [0, 1, 2, 3, 4, 5, 6], [0, 1, 2, 3, 4, 6], [0, 1, 2, 3, 4]];
        for (var source = 0; source < sources.Length; source++)
        for (var target = 0; target < targets.Length; target++)
            data.Add(sources[source], targets[target], allowedTargets[source].Contains(target));
        return data;
    }

    public static TheoryData<string, string, bool> SupportPairs()
    {
        var data = new TheoryData<string, string, bool>();
        var targets = new[] { "Modularyn.Framework.Sdk.Contracts", "Modularyn.UI.Components", "Modularyn.Connectors.Files.Storage",
            "Modularyn.Tooling.Generator.Core", "Modularyn", "Modularyn.Pro.Studio.Workspace" };
        for (var role = 0; role < Suffixes.Length; role++)
        for (var target = 0; target < targets.Length; target++)
            data.Add(Suffixes[role], targets[target], target == 0 || role == 3 && target == 2 || role == 6 && target == 1);
        return data;
    }

    [Theory]
    [MemberData(nameof(SupportPairs))]
    public void Every_module_role_has_a_support_family_boundary(string suffix, string target, bool allowed)
    {
        var graph = new ArchitectureFixture();
        var source = "Modularyn.Modules.Files." + suffix;
        graph.Add(source);
        Assert.Empty(graph.Validate());
        graph.PackageReference(source, target);
        var errors = graph.Validate().Where(error => error.StartsWith("DEP010:", StringComparison.Ordinal));
        if (allowed) Assert.Empty(errors);
        else Assert.Contains(errors, error => error.StartsWith("DEP010: The source role does not allow this support package.", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(FamilyPairs))]
    public void Every_family_pair_has_the_declared_family_boundary(string source, string target, bool allowed)
    {
        var graph = new ArchitectureFixture(source.StartsWith("Modularyn.Pro.", StringComparison.Ordinal) ? "modularyn-pro" : "modularyn");
        graph.Add(source);
        Assert.Empty(graph.Validate());
        graph.PackageReference(source, target);
        var familyErrors = graph.Validate().Where(error => error.StartsWith("DEP006:", StringComparison.Ordinal));
        if (allowed) Assert.Empty(familyErrors);
        else Assert.Contains(familyErrors, error => error.StartsWith("DEP006: The source family does not allow the target family.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.*")]
    [InlineData("*")]
    [InlineData("[1.2.3,2.0.0)")]
    [InlineData("[1.2.3]")]
    [InlineData("1.2")]
    [InlineData("1.2.3-preview.1")]
    [InlineData("1.2.3+build")]
    [InlineData("01.2.3")]
    [InlineData("$(CommunityVersion)")]
    [InlineData("1.2.3\n")]
    public void Paid_Community_versions_must_be_literal_exact_stable_declarations(string? version)
    {
        var graph = new ArchitectureFixture("modularyn-pro");
        const string source = "Modularyn.Pro.Workflow.Application";
        graph.Add(source);
        Assert.Empty(graph.Validate());
        graph.PackageReference(source, "Modularyn.Modules.Files.Application.Contracts", version);
        Assert.Contains(graph.Validate(), error => error.StartsWith("DEP005: Paid references to Community require an exact stable version.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.App", "FrameworkReference")]
    [InlineData("Microsoft.AspNetCore.Components", "PackageReference")]
    [InlineData("Microsoft.Maui.Controls", "PackageReference")]
    [InlineData("Microsoft.WindowsDesktop.App.WPF", "FrameworkReference")]
    [InlineData("Microsoft.JSInterop", "PackageReference")]
    [InlineData("Microsoft.EntityFrameworkCore", "PackageReference")]
    [InlineData("System.Net.Http", "PackageReference")]
    [InlineData("System.Windows.Forms", "FrameworkReference")]
    public void Domain_and_contracts_reject_platform_adapters_even_under_false_conditions(string target, string kind)
    {
        foreach (var suffix in new[] { "Domain", "Application.Contracts" })
        {
            var graph = new ArchitectureFixture();
            var source = "Modularyn.Modules.Files." + suffix;
            graph.Add(source);
            Assert.Empty(graph.Validate());
            graph.AddReference(source, new XElement(kind, new XAttribute("Include", target), new XAttribute("Condition", "false")));
            var diagnostic = suffix == "Domain" ? "DEP007:" : "DEP010:";
            Assert.Contains(graph.Validate(), error => error.StartsWith(diagnostic, StringComparison.Ordinal) && error.Contains(target, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("UseWPF")]
    [InlineData("UseWindowsForms")]
    [InlineData("UseMaui")]
    [InlineData("Microsoft.NET.Sdk.Web")]
    [InlineData("Microsoft.NET.Sdk.Razor")]
    [InlineData("Microsoft.NET.Sdk.WindowsDesktop")]
    public void Domain_rejects_UI_build_configuration(string setting)
    {
        var graph = new ArchitectureFixture();
        const string source = "Modularyn.Modules.Files.Domain";
        graph.Add(source);
        Assert.Empty(graph.Validate());
        graph.Edit(source, project =>
        {
            if (setting.StartsWith("Use", StringComparison.Ordinal))
                project.Root!.Add(new XElement("PropertyGroup", new XAttribute("Condition", "false"), new XElement(setting, "true")));
            else project.Root!.SetAttributeValue("Sdk", setting);
        });
        Assert.Contains(graph.Validate(), error => error.StartsWith("DEP007:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("self")]
    [InlineData("project")]
    [InlineData("package")]
    [InlineData("module")]
    public void Cycles_fail_with_the_cycle_and_its_scope(string kind)
    {
        var graph = new ArchitectureFixture();
        const string first = "Modularyn.Framework.Composition.Contracts";
        const string second = "Modularyn.Framework.Options.Contracts";
        if (kind == "module")
        {
            graph.ProjectReference("Modularyn.Modules.Files.Application", "Modularyn.Cms.Content.Application.Contracts");
            Assert.Empty(graph.Validate());
            graph.ProjectReference("Modularyn.Cms.Content.Application", "Modularyn.Modules.Files.Application.Contracts");
        }
        else
        {
            graph.ProjectReference(first, second);
            Assert.Empty(graph.Validate());
            if (kind == "self") graph.ProjectReference(first, first);
            else if (kind == "package") graph.PackageReference(second, first);
            else graph.ProjectReference(second, first);
        }
        var errors = graph.Validate();
        Assert.Contains(errors, error => error.StartsWith("DEP011: Dependency cycles are forbidden. " + (kind == "module" ? "module" : "package") + ":", StringComparison.Ordinal));
        Assert.All(errors, error => Assert.StartsWith("DEP011:", error));
    }
}
