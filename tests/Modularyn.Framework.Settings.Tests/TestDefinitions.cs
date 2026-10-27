using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Settings.Abstractions;

namespace Modularyn.Framework.Settings.Tests;

internal static class TestDefinitions
{
    internal static SemanticVersion Version { get; } = SemanticVersion.Parse("1.0.0");
    internal static TimeSpan Staleness { get; } = TimeSpan.FromMinutes(2);
    internal const string Module = "Preferences";
    internal static SettingDefinition Title { get; } = new(Module, "Title", Version, ValueKind.Text, "default", Staleness, new(maxLength: 100));
    internal static SettingDefinition PageSize { get; } = new(Module, "PageSize", Version, ValueKind.Integer, "20", Staleness, new(minimum: 1, maximum: 100));
    internal static SettingDefinition Secret { get; } = new(Module, "Connection", Version, ValueKind.SecretReference, null, Staleness);
    internal static FeatureDefinition Preview { get; } = new(Module, "Preview", Version, false, Staleness);
    internal static IReadOnlyList<ScopedValueDefinition> All { get; } = [Title, PageSize, Secret, Preview];
    internal static ModuleManifest Manifest { get; } = new(Module, Version, definitions: All.Select(definition => definition.Declaration));
    internal static ValueDefinitionRegistry Registry() => new([Manifest], All);
}

/// <summary>Deterministic elapsed and UTC time; live timers retain the platform implementation.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long elapsed;
    private readonly DateTimeOffset origin = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero).AddTicks(7);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref elapsed);
    public override DateTimeOffset GetUtcNow() => origin.AddTicks(GetTimestamp());
    internal void Advance(TimeSpan duration) => Interlocked.Add(ref elapsed, duration.Ticks);
}
