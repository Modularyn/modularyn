using System.Reflection;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Modularity.Abstractions.Tests;

public sealed class ManifestTests
{
    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3-alpha.1+build.001")]
    [InlineData("1.2.3-0A.0.-+001")]
    [InlineData("999999999999999999999999999999.0.0")]
    public void SemVer_values_round_trip_without_numeric_limits(string value)
    {
        Assert.True(SemanticVersion.TryParse(value, out var version));
        Assert.NotNull(version);
        Assert.Equal(value, version.ToString());
        Assert.Equal(version, SemanticVersion.Parse(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-a.01")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+build_1")]
    [InlineData("1.2.3\n")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.٣")]
    public void Invalid_SemVer_is_refused(string? value)
    {
        Assert.False(SemanticVersion.TryParse(value, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void SemVer_precedence_obeys_numeric_prerelease_and_release_rules()
    {
        string[] values = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        for (var left = 0; left < values.Length; left++)
        for (var right = 0; right < values.Length; right++)
            Assert.Equal(Math.Sign(left.CompareTo(right)), Math.Sign(SemanticVersion.Parse(values[left]).CompareTo(SemanticVersion.Parse(values[right]))));
        Assert.True(SemanticVersion.Parse("1.0.0-999999999999999999999999").CompareTo(SemanticVersion.Parse("1.0.0-1000000000000000000000000")) < 0);
    }

    [Fact]
    public void Build_metadata_is_retained_but_does_not_change_range_membership()
    {
        var first = SemanticVersion.Parse("2.0.0+build.1");
        var second = SemanticVersion.Parse("2.0.0+build.2");
        Assert.NotEqual(first, second);
        Assert.Equal(0, first.CompareTo(second));
        Assert.True(VersionRange.Exact(first).Contains(second));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, false)]
    public void Each_interval_edge_is_explicit(bool includeMinimum, bool includeMaximum, bool acceptsMinimum, bool acceptsMaximum)
    {
        var minimum = SemanticVersion.Parse("2.0.0");
        var maximum = SemanticVersion.Parse("2.2.0");
        var range = new VersionRange(minimum, includeMinimum, maximum, includeMaximum);
        Assert.Equal(acceptsMinimum, range.Contains(minimum));
        Assert.Equal(acceptsMaximum, range.Contains(maximum));
        Assert.True(range.Contains(SemanticVersion.Parse("2.1.0")));
        Assert.False(range.Contains(SemanticVersion.Parse("1.9.9")));
        Assert.False(range.Contains(SemanticVersion.Parse("2.2.1")));
    }

    [Fact]
    public void Unbounded_intervals_and_prerelease_bounds_have_defined_semantics()
    {
        var prerelease = SemanticVersion.Parse("2.0.0-rc.1");
        Assert.True(VersionRange.Any.Contains(prerelease));
        Assert.True(new VersionRange(null, false, SemanticVersion.Parse("2.0.0"), false).Contains(prerelease));
        Assert.True(new VersionRange(prerelease, true, null, false).Contains(SemanticVersion.Parse("2.0.0")));
        Assert.False(new VersionRange(SemanticVersion.Parse("2.0.0"), true, null, false).Contains(prerelease));
    }

    [Fact]
    public void Empty_inverted_or_inclusively_unbounded_intervals_are_refused()
    {
        var one = SemanticVersion.Parse("1.0.0");
        var two = SemanticVersion.Parse("2.0.0");
        Assert.Throws<ArgumentException>(() => new VersionRange(two, true, one, true));
        Assert.Throws<ArgumentException>(() => new VersionRange(one, false, one, true));
        Assert.Throws<ArgumentException>(() => new VersionRange(null, true, two, true));
        Assert.Throws<ArgumentException>(() => new VersionRange(one, true, null, true));
    }

    [Fact]
    public void Manifests_and_selections_snapshot_all_declarations()
    {
        var version = SemanticVersion.Parse("1.0.0");
        var dependencies = new List<ModuleDependency> { new("B", VersionRange.Any, optional: true) };
        var assets = new List<ModuleAsset> { new("Owned", version) };
        var provisions = new List<ModuleProvision> { new("Storage", version, exclusive: true) };
        var requirements = new List<ModuleRequirement> { new("Storage", VersionRange.Any) };
        var routes = new List<ModuleRoute> { new("get", "/records") };
        var manifest = new ModuleManifest("A", version, dependencies,
            provisions, requirements, provisions, requirements, provisions, requirements,
            routes, assets, assets, assets, assets);
        var modules = new List<ModuleManifest> { manifest };
        var selection = new CompositionSelection(modules, new(version, version));
        dependencies.Clear(); assets.Clear(); provisions.Clear(); requirements.Clear(); routes.Clear(); modules.Clear();
        Assert.Single(selection.Modules);
        Assert.True(Assert.Single(manifest.Dependencies).Optional);
        Assert.True(Assert.Single(manifest.ProvidedCapabilities).Exclusive);
        Assert.Single(manifest.ProvidedContracts);
        Assert.Single(manifest.RequiredContracts);
        Assert.Single(manifest.RequiredCapabilities);
        Assert.Single(manifest.ProvidedProviders);
        Assert.Single(manifest.RequiredProviders);
        Assert.Equal("GET", Assert.Single(manifest.Routes).Method);
        Assert.Single(manifest.Definitions);
        Assert.Single(manifest.Migrations);
        Assert.Single(manifest.BackgroundHandlers);
        Assert.Single(manifest.HealthChecks);
        Assert.Throws<NotSupportedException>(() => ((IList<ModuleManifest>)selection.Modules).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ModuleDependency>)manifest.Dependencies).Clear());
    }

    [Fact]
    public void Public_asynchronous_extension_contracts_require_cancellation()
    {
        var methods = new[] { typeof(IModule), typeof(ICompositionValidator) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.ReturnType == typeof(Task) || method.ReturnType.IsGenericType);
        Assert.All(methods, method => Assert.Contains(method.GetParameters(), parameter =>
            parameter.ParameterType == typeof(CancellationToken) && !parameter.IsOptional));
    }
}
