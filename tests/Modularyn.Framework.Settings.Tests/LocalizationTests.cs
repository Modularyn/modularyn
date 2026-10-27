using Modularyn.Framework.Localization;
using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("eng")]
    [InlineData("en-US")]
    [InlineData("eng-US")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hant-TW")]
    [InlineData("es-419")]
    [InlineData("")]
    public void Canonical_resource_tags_do_not_need_host_culture_data(string culture)
    {
        var resource = new ModuleLocalizationResource("Preferences", "Labels", TestDefinitions.Version, culture,
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [culture] = new Dictionary<string, string> { ["Title"] = "Title" }
            });
        var bootstrap = new ModuleLocalization([new("Preferences", TestDefinitions.Version, definitions: [resource.Declaration])], [resource]);
        Assert.Equal(new LocalizedText("Title", culture), bootstrap.Bootstrap("Preferences", "Labels", culture).Strings["Title"]);
    }

    [Theory]
    [InlineData("en-us")]
    [InlineData("en_US")]
    [InlineData(" fr ")]
    [InlineData("en-US-extra")]
    public void Noncanonical_resource_tags_are_refused(string culture) =>
        Assert.Throws<ArgumentException>(() => new ModuleLocalizationResource("Preferences", "Labels", TestDefinitions.Version, culture,
            new Dictionary<string, IReadOnlyDictionary<string, string>>()));

    [Fact]
    public void Bootstrap_uses_per_key_fallback_and_copies_versioned_resources_without_crossing_modules()
    {
        var french = new Dictionary<string, string> { ["Title"] = "Titre", ["ParentOnly"] = "Parent" };
        var resource = new ModuleLocalizationResource("Preferences", "Labels", TestDefinitions.Version, "en",
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["en"] = new Dictionary<string, string> { ["Title"] = "Title", ["DefaultOnly"] = "Default" },
                ["fr"] = french,
                ["fr-CA"] = new Dictionary<string, string> { ["Title"] = "Titre local" },
                [""] = new Dictionary<string, string> { ["InvariantOnly"] = "Invariant" }
            });
        french["Title"] = "changed after registration";
        var other = new ModuleLocalizationResource("OtherModule", "Labels", TestDefinitions.Version, "en",
            new Dictionary<string, IReadOnlyDictionary<string, string>> { ["en"] = new Dictionary<string, string> { ["Title"] = "Other title" } });
        var bootstrap = new ModuleLocalization([
            new("Preferences", TestDefinitions.Version, definitions: [resource.Declaration]),
            new("OtherModule", TestDefinitions.Version, definitions: [other.Declaration])], [resource, other]);
        var result = bootstrap.Bootstrap("Preferences", "Labels", "fr-CA");
        Assert.Equal("1.0.0", result.Version);
        Assert.Equal("fr-CA", result.Culture);
        Assert.Equal(new LocalizedText("Titre local", "fr-CA"), result.Strings["Title"]);
        Assert.Equal(new LocalizedText("Parent", "fr"), result.Strings["ParentOnly"]);
        Assert.Equal(new LocalizedText("Default", "en"), result.Strings["DefaultOnly"]);
        Assert.Equal(new LocalizedText("Invariant", ""), result.Strings["InvariantOnly"]);
        Assert.Equal("Titre", bootstrap.Bootstrap("Preferences", "Labels", "fr-FR").Strings["Title"].Value);
        Assert.Equal("Title", bootstrap.Bootstrap("Preferences", "Labels", "de").Strings["Title"].Value);
        Assert.Equal("Other title", bootstrap.Bootstrap("OtherModule", "Labels", "fr-CA").Strings["Title"].Value);
        Assert.False(result.Strings.ContainsKey("Missing"));
        Assert.Throws<InvalidOperationException>(() => bootstrap.Bootstrap("Unregistered", "Labels", "en"));
        Assert.Throws<InvalidOperationException>(() => new ModuleLocalization([], [resource]));
        Assert.Throws<ArgumentException>(() => new ModuleLocalization([new("Preferences", TestDefinitions.Version, definitions: [resource.Declaration])], [resource, resource]));
    }
}
