using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Modularyn.Framework.Configuration.Tests")]

namespace Modularyn.Framework.Configuration;

public static class DeploymentOptionsServiceCollectionExtensions
{
    /// <summary>
    /// Binds deployment options from one configuration section and validates them before the host starts any hosted
    /// service, so an invalid section refuses startup before module activation. Binding is strict: a key the options do
    /// not declare, or a value that cannot be converted, is a validation failure whose message never contains a
    /// configured value. Secret settings are <see cref="SecretReference"/> properties and must hold references.
    /// Deployment options take effect only at startup; a later configuration reload does not change them.
    /// </summary>
    public static OptionsBuilder<TOptions> AddModularynDeploymentOptions<TOptions>(this IServiceCollection services,
        IConfiguration configuration, string sectionPath) where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IConfigureOptions<TOptions>) && descriptor.ImplementationInstance is StrictBinding<TOptions>))
            throw new InvalidOperationException($"Deployment options '{typeof(TOptions).Name}' are already bound. Bind each deployment options type from one section.");
        services.AddSingleton<IConfigureOptions<TOptions>>(new StrictBinding<TOptions>(configuration.GetSection(sectionPath)));
        services.AddSingleton<IValidateOptions<TOptions>>(new SecretReferenceValidation<TOptions>(sectionPath));
        // No change-token source is registered, and binding reads a copy of the section taken at first use, so neither
        // IOptionsMonitor nor IOptionsSnapshot observes a later reload of a deployment section.
        return services.AddOptions<TOptions>().ValidateDataAnnotations().ValidateOnStart();
    }
}

internal sealed class StrictBinding<TOptions> : IConfigureOptions<TOptions> where TOptions : class
{
    private const string UnknownKeysPrefix = "'ErrorOnUnknownConfiguration' was set on the provided BinderOptions, but the following properties were not found on the instance of ";
    private static readonly Regex UnknownKeys = new(@"\A[A-Za-z0-9_.`+]+: ('[^'\r\n]*'(?:, '[^'\r\n]*')*)\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ConversionFailure = new(@"(?s)\AFailed to convert configuration value .* at '(?<path>[^'\r\n]+)' to type '(?<type>[^'\r\n]+)'\.\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly string path;
    private readonly Lazy<IConfigurationSection> frozen;

    internal StrictBinding(IConfigurationSection section)
    {
        path = section.Path;
        frozen = new(() => new ConfigurationBuilder().AddInMemoryCollection(section.AsEnumerable()).Build().GetSection(path));
    }

    public void Configure(TOptions options)
    {
        try
        {
            frozen.Value.Bind(options, binder =>
            {
                binder.ErrorOnUnknownConfiguration = true;
                binder.BindNonPublicProperties = false;
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Binder messages can quote the configured value, which may be a secret pasted in the wrong place.
            throw new OptionsValidationException(Options.DefaultName, typeof(TOptions), [Describe(path, exception.Message)]);
        }
    }

    internal static string Describe(string path, string message)
    {
        if (message.StartsWith(UnknownKeysPrefix, StringComparison.Ordinal)
            && UnknownKeys.Match(message[UnknownKeysPrefix.Length..]) is { Success: true } unknown)
            return $"Configuration section '{path}' contains keys that its options do not declare: {unknown.Groups[1].Value}. Remove or correct them.";
        if (ConversionFailure.Match(message) is { Success: true } conversion)
            return $"The configuration value at '{conversion.Groups["path"].Value}' cannot be converted to {conversion.Groups["type"].Value}. The value is not reported.";
        return $"Configuration section '{path}' cannot be bound to {typeof(TOptions).Name}. The values are not reported.";
    }
}

/// <summary>Refuses every <see cref="SecretReference"/> in the options graph that was configured with a literal value.</summary>
internal sealed class SecretReferenceValidation<TOptions>(string sectionPath) : IValidateOptions<TOptions> where TOptions : class
{
    private const int MaximumDepth = 16;

    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        Walk(options, sectionPath, 0, new HashSet<object>(ReferenceEqualityComparer.Instance), failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Walk(object? value, string path, int depth, HashSet<object> visited, List<string> failures)
    {
        if (value is null) return;
        if (value is SecretReference reference)
        {
            if (!reference.IsValid)
                failures.Add($"'{path}' must be a secret reference of the form '{SecretReference.Scheme}<name>'. The configured value was discarded and is not reported.");
            return;
        }
        var type = value.GetType();
        if (depth >= MaximumDepth || type.IsPrimitive || type.IsEnum || value is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or Uri
            || !visited.Add(value)) return;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) Walk(entry.Value, path + ":" + entry.Key, depth + 1, visited, failures);
            return;
        }
        if (value is IEnumerable items)
        {
            var index = 0;
            foreach (var item in items) Walk(item, path + ":" + index++, depth + 1, visited, failures);
            return;
        }
        if (type.Namespace is { } space && (space.StartsWith("System", StringComparison.Ordinal) || space.StartsWith("Microsoft", StringComparison.Ordinal))) return;
        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch (System.Reflection.TargetInvocationException)
            {
                failures.Add($"'{path}:{property.Name}' could not be inspected for secret references.");
                continue;
            }
            Walk(child, path + ":" + property.Name, depth + 1, visited, failures);
        }
    }
}
