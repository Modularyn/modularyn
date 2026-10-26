using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Modularyn.Framework.Modularity.Abstractions;

/// <summary>A SemVer 2.0.0 value. Build metadata affects identity, but never precedence.</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly string[] core;
    private readonly string[] prerelease;

    private SemanticVersion(string value, Match match)
    {
        Value = value;
        core = [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value];
        prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
    }

    public string Value { get; }
    public bool IsPrerelease => prerelease.Length != 0;

    public static SemanticVersion Parse(string value) => TryParse(value, out var version)
        ? version : throw new FormatException($"'{value}' is not a SemVer 2.0.0 version.");

    public static bool TryParse(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (value is null) return false;
        var match = Pattern.Match(value);
        if (!match.Success || match.Groups[4].Value.Split('.').Any(part =>
                IsNumeric(part) && part.Length > 1 && part[0] == '0')) return false;
        version = new SemanticVersion(value, match);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < core.Length; i++)
        {
            var comparison = CompareNumeric(core[i], other.core[i]);
            if (comparison != 0) return comparison;
        }
        if (prerelease.Length == 0) return other.prerelease.Length == 0 ? 0 : 1;
        if (other.prerelease.Length == 0) return -1;
        for (var i = 0; i < Math.Min(prerelease.Length, other.prerelease.Length); i++)
        {
            var left = prerelease[i];
            var right = other.prerelease[i];
            var leftNumeric = IsNumeric(left);
            var rightNumeric = IsNumeric(right);
            var comparison = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1)
                : StringComparer.Ordinal.Compare(left, right);
            if (comparison != 0) return comparison;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }

    public bool Equals(SemanticVersion? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
    private static int CompareNumeric(string left, string right) => left.Length != right.Length
        ? left.Length.CompareTo(right.Length) : StringComparer.Ordinal.Compare(left, right);
}
