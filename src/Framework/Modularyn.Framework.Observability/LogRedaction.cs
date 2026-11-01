using System.Collections;
using Microsoft.Extensions.Logging;

namespace Modularyn.Framework.Observability;

/// <summary>
/// The host's logger factory, wrapped so that no log event carries a recognized secret form (<see cref="SecretForms"/>)
/// to any provider: its message, each structured value, each scope value and its exception are scrubbed before a provider
/// sees them. An event with no such form reaches the providers unchanged.
/// </summary>
internal sealed class RedactingLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose() => inner.Dispose();
}

internal sealed class RedactingLogger(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        RedactedState.Leaks(state, null, out var values) ? inner.BeginScope(new RedactedState(SecretForms.Scrub(state.ToString() ?? ""), values)) : inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!inner.IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        var leaks = RedactedState.Leaks(state, exception, out var values);
        if (!leaks && !SecretForms.Contains(message))
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }
        inner.Log(logLevel, eventId, new RedactedState(SecretForms.Scrub(message), values), exception is null ? null : new RedactedException(exception),
            static (scrubbed, _) => scrubbed.ToString());
    }
}

/// <summary>A log event's state with every structured value scrubbed; its text is the scrubbed message.</summary>
internal sealed class RedactedState : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly string message;
    private readonly KeyValuePair<string, object?>[] values;

    internal RedactedState(string message, IReadOnlyList<KeyValuePair<string, object?>>? source)
    {
        this.message = message;
        values = source is null ? [] : source.Select(pair => new KeyValuePair<string, object?>(pair.Key, Scrub(pair.Value))).ToArray();
    }

    /// <summary>True when the state's values or the exception hold a secret form; the values are returned for scrubbing.</summary>
    internal static bool Leaks<TState>(TState state, Exception? exception, out IReadOnlyList<KeyValuePair<string, object?>>? values)
    {
        values = state as IReadOnlyList<KeyValuePair<string, object?>>;
        if (values is null && state is IEnumerable<KeyValuePair<string, object?>> pairs) values = pairs.ToArray();
        return (values?.Any(pair => pair.Value is not null && SecretForms.Contains(Text(pair.Value))) ?? false)
            || (values is null && state is not null && SecretForms.Contains(state.ToString()))
            || (exception is not null && SecretForms.Contains(exception.ToString()));
    }

    private static object? Scrub(object? value) => value switch
    {
        null => null,
        string text => SecretForms.Scrub(text),
        _ when SecretForms.Contains(Text(value)) => SecretForms.Scrub(Text(value)),
        _ => value
    };

    private static string Text(object value) => value is IEnumerable enumerable and not string
        ? string.Join(", ", enumerable.Cast<object?>()) : value.ToString() ?? "";

    public KeyValuePair<string, object?> this[int index] => values[index];
    public int Count => values.Length;
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => message;
}

/// <summary>
/// Stands in for an exception whose text holds a secret form: it keeps the original type's name and the scrubbed message
/// and stack text, and nothing else, not even the original as an inner exception.
/// </summary>
internal sealed class RedactedException(Exception original) : Exception(SecretForms.Scrub(original.GetType().FullName + ": " + original.Message))
{
    private readonly string text = SecretForms.Scrub(original.ToString());

    public override string? StackTrace => null;

    public override string ToString() => text;
}
