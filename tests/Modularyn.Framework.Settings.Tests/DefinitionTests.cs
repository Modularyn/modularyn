using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Settings.Abstractions;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

public sealed class DefinitionTests
{
    [Theory]
    [InlineData(ValueKind.Boolean, "True")]
    [InlineData(ValueKind.Boolean, "1")]
    [InlineData(ValueKind.Integer, "01")]
    [InlineData(ValueKind.Integer, "+1")]
    [InlineData(ValueKind.Integer, "-0")]
    [InlineData(ValueKind.Integer, "1.0")]
    [InlineData(ValueKind.Integer, "9223372036854775808")]
    [InlineData(ValueKind.SecretReference, "literal")]
    public void Invalid_defaults_are_rejected_without_retaining_the_value(ValueKind kind, string value)
    {
        var error = Assert.Throws<ValueValidationException>(() => new SettingDefinition("Preferences", "Value", TestDefinitions.Version,
            kind, value, TestDefinitions.Staleness));
        Assert.Null(error.InnerException);
        Assert.Empty(error.Data);
        Assert.Equal("The value does not satisfy its declared type or validation rules.", error.Message);
    }

    [Fact]
    public void Types_bounds_allowed_values_and_staleness_are_checked_before_registration()
    {
        Assert.Throws<ValueValidationException>(() => new SettingDefinition("Preferences", "Size", TestDefinitions.Version,
            ValueKind.Integer, "0", TestDefinitions.Staleness, new(minimum: 1, maximum: 100)));
        Assert.Throws<ValueValidationException>(() => new SettingDefinition("Preferences", "Title", TestDefinitions.Version,
            ValueKind.Text, "three", TestDefinitions.Staleness, new(maxLength: 4)));
        Assert.Throws<ValueValidationException>(() => new SettingDefinition("Preferences", "Mode", TestDefinitions.Version,
            ValueKind.Text, "unlisted", TestDefinitions.Staleness, new(allowedValues: ["one", "two"])));
        Assert.Throws<ArgumentException>(() => new ValueValidation(minimum: 2, maximum: 1));
        Assert.Throws<ArgumentException>(() => new ValueValidation(allowedValues: ["same", "same"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureDefinition("Preferences", "Preview", TestDefinitions.Version, false, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FeatureDefinition("Preferences", "Preview", TestDefinitions.Version, false, TimeSpan.FromDays(2)));
        var validation = new ValueValidation(minimum: 1, maximum: 100);
        Assert.True(validation.Accepts(ValueKind.Integer, "1"));
        Assert.True(validation.Accepts(ValueKind.Integer, "100"));
        Assert.False(validation.Accepts(ValueKind.Integer, "101"));
        Assert.False(validation.Accepts(ValueKind.Integer, "0"));
        var allowed = new List<string> { "one", "two" };
        var snapshot = new ValueValidation(allowedValues: allowed);
        allowed.Clear();
        Assert.Equal(2, snapshot.AllowedValues.Count);
    }

    [Fact]
    public async Task Every_runtime_definition_matches_one_manifest_owner_name_and_exact_version()
    {
        var registry = TestDefinitions.Registry();
        Assert.Equal(TestDefinitions.All, registry.Definitions);
        Assert.Throws<InvalidOperationException>(() => new ValueDefinitionRegistry([], TestDefinitions.All));
        Assert.Throws<InvalidOperationException>(() => new ValueDefinitionRegistry([TestDefinitions.Manifest], []));
        Assert.Throws<InvalidOperationException>(() => new ValueDefinitionRegistry([TestDefinitions.Manifest], TestDefinitions.All.Append(TestDefinitions.Title)));
        var changed = new ModuleManifest(TestDefinitions.Module, TestDefinitions.Version,
            definitions: TestDefinitions.All.Select(definition => new ModuleAsset(definition.AssetName, SemanticVersion.Parse("2.0.0"))));
        Assert.Throws<InvalidOperationException>(() => new ValueDefinitionRegistry([changed], TestDefinitions.All));
        var result = await new ModuleComposer([registry]).ComposeAsync(new([changed],
            new CompositionEnvironment(SemanticVersion.Parse("10.0.14"), TestDefinitions.Version)), TestContext.Current.CancellationToken);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MDC300");
    }

    [Fact]
    public void PostgreSQL_timestamps_are_UTC_and_whole_microseconds_at_one_boundary()
    {
        var original = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(-5)).AddTicks(17);
        var normalized = SettingsTimestamp.Normalize(original);
        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        Assert.Equal(0, normalized.Ticks % 10);
        Assert.Equal(7, original.UtcTicks - normalized.UtcTicks);
        Assert.Equal(normalized, SettingsTimestamp.Normalize(normalized));
    }
}
