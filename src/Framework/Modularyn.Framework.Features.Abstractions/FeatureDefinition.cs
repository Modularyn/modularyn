using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Settings.Abstractions;

namespace Modularyn.Framework.Features.Abstractions;

/// <summary>A runtime flag; it neither changes installed composition nor grants authority.</summary>
public sealed class FeatureDefinition(string moduleId, string name, SemanticVersion version, bool defaultValue, TimeSpan maxStaleness, bool clientVisible = false)
    : ScopedValueDefinition(moduleId, name, version, ValueCategory.Feature, ValueKind.Boolean,
        defaultValue ? "true" : "false", maxStaleness, clientVisible: clientVisible);
