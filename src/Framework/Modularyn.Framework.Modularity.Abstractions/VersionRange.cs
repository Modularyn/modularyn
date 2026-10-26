namespace Modularyn.Framework.Modularity.Abstractions;

/// <summary>An explicit interval using SemVer precedence, including prereleases within its bounds.</summary>
public sealed class VersionRange
{
    public VersionRange(SemanticVersion? minimum, bool includeMinimum, SemanticVersion? maximum, bool includeMaximum)
    {
        if ((minimum is null && includeMinimum) || (maximum is null && includeMaximum))
            throw new ArgumentException("An unbounded edge must be exclusive.");
        if (minimum is not null && maximum is not null &&
            (minimum.CompareTo(maximum) > 0 || (minimum.CompareTo(maximum) == 0 && !(includeMinimum && includeMaximum))))
            throw new ArgumentException("The version interval must contain at least one version.");
        Minimum = minimum;
        Maximum = maximum;
        IncludeMinimum = includeMinimum;
        IncludeMaximum = includeMaximum;
    }

    public SemanticVersion? Minimum { get; }
    public SemanticVersion? Maximum { get; }
    public bool IncludeMinimum { get; }
    public bool IncludeMaximum { get; }
    public static VersionRange Any { get; } = new(null, false, null, false);
    public static VersionRange Exact(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(version, true, version, true);
    }

    public bool Contains(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return (Minimum is null || (IncludeMinimum ? version.CompareTo(Minimum) >= 0 : version.CompareTo(Minimum) > 0))
            && (Maximum is null || (IncludeMaximum ? version.CompareTo(Maximum) <= 0 : version.CompareTo(Maximum) < 0));
    }

    public override string ToString() => $"{(IncludeMinimum ? '[' : '(')}{Minimum}, {Maximum}{(IncludeMaximum ? ']' : ')')}";
}
