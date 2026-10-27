using System.Globalization;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Settings.Abstractions;

namespace Modularyn.Framework.Settings;

/// <summary>Effective value with provenance. Diagnostics and serialization use a masked display for a secret reference.</summary>
public sealed class ResolvedValue
{
    private readonly object? value;

    internal ResolvedValue(ScopedValueDefinition definition, string? text, ValueSource source, Guid? version, DateTimeOffset? updatedAt)
    {
        Kind = definition.Kind;
        Source = source;
        Version = version;
        UpdatedAt = updatedAt;
        value = text is null ? null : definition.Kind switch
        {
            ValueKind.Boolean => text == "true",
            ValueKind.Integer => long.Parse(text, CultureInfo.InvariantCulture),
            ValueKind.SecretReference => SecretReference.Parse(text),
            _ => text
        };
    }

    public ValueKind Kind { get; }
    public ValueSource Source { get; }
    public Guid? Version { get; }
    public DateTimeOffset? UpdatedAt { get; }
    public bool IsSet => value is not null;
    public string? DisplayValue => Kind == ValueKind.SecretReference ? SecretValue.Redacted
        : value is bool boolean ? (boolean ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture);

    public T Get<T>() => value is T typed ? typed : throw new InvalidOperationException("Read the value through its declared type, after checking that it is set.");
    public override string ToString() => $"{Kind} from {Source}: {DisplayValue}";
}

/// <summary>The single normalization point for timestamps written to PostgreSQL and returned to memory.</summary>
public static class SettingsTimestamp
{
    public static DateTimeOffset Normalize(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}

public sealed class ValueConflictException() : Exception("The override changed. Read its current version before retrying.");
public sealed class ValueUnavailableException() : Exception("The scoped value could not be resolved. No fallback was cached.");
