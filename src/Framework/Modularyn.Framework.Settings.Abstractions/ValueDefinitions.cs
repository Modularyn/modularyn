using System.Globalization;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Settings.Abstractions;

public enum ValueKind { Text, Boolean, Integer, SecretReference }
public enum ValueCategory { Setting, Feature }
public enum ValueSource { Default, Global, Tenant, User }

/// <summary>Declarative validation, shared by defaults, writes and reads. No submitted value enters a diagnostic.</summary>
public sealed class ValueValidation
{
    public ValueValidation(int maxLength = 4096, long? minimum = null, long? maximum = null,
        IEnumerable<string>? allowedValues = null)
    {
        if (maxLength is < 1 or > 65536 || minimum > maximum)
            throw new ArgumentException("The value validation bounds are invalid.");
        var values = allowedValues?.ToArray() ?? [];
        if (values.Any(value => value is null || value.Length > maxLength)
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Allowed values must be distinct and fit the declared length.");
        MaxLength = maxLength;
        Minimum = minimum;
        Maximum = maximum;
        AllowedValues = Array.AsReadOnly(values);
    }

    public int MaxLength { get; }
    public long? Minimum { get; }
    public long? Maximum { get; }
    public IReadOnlyList<string> AllowedValues { get; }

    public bool Accepts(ValueKind kind, string value)
    {
        if (value.Length > MaxLength || AllowedValues.Count != 0 && !AllowedValues.Contains(value, StringComparer.Ordinal)) return false;
        return kind switch
        {
            ValueKind.Text => true,
            ValueKind.Boolean => value is "true" or "false",
            ValueKind.Integer => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                && number.ToString(CultureInfo.InvariantCulture) == value && (Minimum is null || number >= Minimum)
                && (Maximum is null || number <= Maximum),
            // The runtime validates references through the configuration contract before they can be persisted.
            ValueKind.SecretReference => false,
            _ => false
        };
    }
}

/// <summary>
/// A module contract bound to an identically named and versioned manifest definition. Secret definitions have no
/// literal default or allowed-value list: an explicit global reference supplies them. Values use ordinal identity.
/// </summary>
public abstract class ScopedValueDefinition
{
    protected ScopedValueDefinition(string moduleId, string name, SemanticVersion version, ValueCategory category,
        ValueKind kind, string? defaultValue, TimeSpan maxStaleness, ValueValidation? validation = null, bool clientVisible = false)
    {
        ModuleId = ValidName(moduleId);
        Name = ValidName(name);
        Version = version ?? throw new ArgumentNullException(nameof(version));
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(category)) throw new ArgumentException("Declare a supported value type and category.");
        if (maxStaleness <= TimeSpan.Zero || maxStaleness > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(maxStaleness), "Declare a positive staleness bound of at most one day.");
        Validation = validation ?? new();
        if (kind == ValueKind.SecretReference)
        {
            if (defaultValue is not null || validation is not null || clientVisible)
                throw new ValueValidationException();
        }
        else if (defaultValue is null || !Validation.Accepts(kind, defaultValue)) throw new ValueValidationException();
        if (kind != ValueKind.Integer && (Validation.Minimum is not null || Validation.Maximum is not null))
            throw new ArgumentException("Numeric bounds apply only to integer definitions.");
        if (Validation.AllowedValues.Any(value => !Validation.Accepts(kind, value))) throw new ValueValidationException();
        ClientVisible = clientVisible;
        Category = category;
        Kind = kind;
        DefaultValue = defaultValue;
        MaxStaleness = maxStaleness;
    }

    public string ModuleId { get; }
    public string Name { get; }
    public SemanticVersion Version { get; }
    public ValueCategory Category { get; }
    public ValueKind Kind { get; }
    public bool ClientVisible { get; }
    public bool IsSecret => Kind == ValueKind.SecretReference;
    public string? DefaultValue { get; }
    public TimeSpan MaxStaleness { get; }
    public ValueValidation Validation { get; }
    public string AssetName => (Category == ValueCategory.Setting ? "setting." : "feature.") + Name;
    public ModuleAsset Declaration => new(AssetName, Version);

    private static string ValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name != name.Trim()
            || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
            throw new ArgumentException("Definition identities use 1 to 160 ASCII letters, digits, '.', '-' or '_'.");
        return name;
    }
}

public sealed class SettingDefinition(string moduleId, string name, SemanticVersion version, ValueKind kind,
    string? defaultValue, TimeSpan maxStaleness, ValueValidation? validation = null, bool clientVisible = false)
    : ScopedValueDefinition(moduleId, name, version, ValueCategory.Setting, kind, defaultValue, maxStaleness, validation, clientVisible);

/// <summary>Validation never retains a submitted value or an inner exception.</summary>
public sealed class ValueValidationException() : Exception("The value does not satisfy its declared type or validation rules.");

public static class ValuePrecedence
{
    public static IReadOnlyList<ValueSource> HighestFirst { get; } = Array.AsReadOnly(new[]
        { ValueSource.User, ValueSource.Tenant, ValueSource.Global, ValueSource.Default });

    /// <summary>A null override is absent; an empty string is a value.</summary>
    public static (string? Value, ValueSource Source) Resolve(string? user, string? tenant, string? global, string? defaultValue) =>
        user is not null ? (user, ValueSource.User) : tenant is not null ? (tenant, ValueSource.Tenant)
        : global is not null ? (global, ValueSource.Global) : (defaultValue, ValueSource.Default);
}
