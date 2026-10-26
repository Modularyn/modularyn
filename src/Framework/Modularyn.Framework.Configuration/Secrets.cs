using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Modularyn.Framework.Configuration;

/// <summary>
/// A reference to a secret held by the deployment's secret store, written <c>secret:&lt;name&gt;</c>. Configuration
/// holds only references; a literal value bound to a reference property is discarded and refused at startup.
/// </summary>
[TypeConverter(typeof(SecretReferenceTypeConverter))]
[JsonConverter(typeof(SecretReferenceJsonConverter))]
public sealed class SecretReference : IEquatable<SecretReference>
{
    public const string Scheme = "secret:";
    private static readonly Regex NamePattern = new(@"\A[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9_-])?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly string? name;

    private SecretReference(string? name) => this.name = name;

    /// <summary>Stands for a configured value that was not a reference; the value itself is not retained.</summary>
    internal static SecretReference Discarded { get; } = new(null);

    public bool IsValid => name is not null;

    /// <summary>The secret's name in the store. Names use ASCII letters, digits, '.', '_' and '-', and never start or end with '.'.</summary>
    public string Name => name ?? throw new InvalidOperationException("The configured value is not a secret reference.");

    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out SecretReference? reference)
    {
        reference = value is not null && value.StartsWith(Scheme, StringComparison.Ordinal) && NamePattern.IsMatch(value[Scheme.Length..])
            ? new SecretReference(value[Scheme.Length..]) : null;
        return reference is not null;
    }

    public static SecretReference Parse(string value) => TryParse(value, out var reference) ? reference
        : throw new FormatException($"The value is not a secret reference of the form '{Scheme}<name>'. The value is not reported.");

    public bool Equals(SecretReference? other) => other is not null && name is not null && string.Equals(name, other.name, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is SecretReference other && Equals(other);
    public override int GetHashCode() => name is null ? 0 : StringComparer.Ordinal.GetHashCode(name);
    public override string ToString() => name is null ? Scheme + "<discarded>" : Scheme + name;
}

internal sealed class SecretReferenceTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);

    // A value that is not a reference is discarded here, so binding never fails with, or retains, a value that may be a secret.
    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string text && SecretReference.TryParse(text, out var reference) ? reference : SecretReference.Discarded;
}

internal sealed class SecretReferenceJsonConverter : JsonConverter<SecretReference>
{
    public override SecretReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && SecretReference.TryParse(reader.GetString(), out var reference) ? reference
            : throw new JsonException($"Expected a secret reference of the form '{SecretReference.Scheme}<name>'.");

    public override void Write(Utf8JsonWriter writer, SecretReference value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>Resolves secret references. Implementations name only the reference when a secret is unavailable.</summary>
public interface ISecretStore
{
    ValueTask<SecretValue> GetAsync(SecretReference reference, CancellationToken cancellationToken);
}

/// <summary>
/// A resolved secret. Formatting, logging, debugging and JSON serialization see only <see cref="Redacted"/>; the
/// value is available only through the explicit reveal methods. Disposing it clears the held bytes.
/// </summary>
[DebuggerDisplay("[secret]")]
[JsonConverter(typeof(SecretValueJsonConverter))]
public sealed class SecretValue : IDisposable
{
    public const string Redacted = "[secret]";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private byte[]? bytes;

    private SecretValue(byte[] bytes) => this.bytes = bytes;

    public static SecretValue FromBytes(ReadOnlySpan<byte> value) => new(value.ToArray());

    internal static SecretValue Own(byte[] value) => new(value);

    public ReadOnlySpan<byte> RevealBytes() => bytes ?? throw new ObjectDisposedException(nameof(SecretValue));

    public string RevealString()
    {
        try
        {
            return StrictUtf8.GetString(RevealBytes());
        }
        catch (DecoderFallbackException)
        {
            // The fallback exception quotes the offending bytes.
            throw new FormatException("The secret is not UTF-8 text. Its bytes are not reported.");
        }
    }

    public override string ToString() => Redacted;

    public void Dispose()
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        bytes = null;
    }
}

internal sealed class SecretValueJsonConverter : JsonConverter<SecretValue>
{
    public override SecretValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Secret values are resolved from the secret store, never deserialized.");

    public override void Write(Utf8JsonWriter writer, SecretValue value, JsonSerializerOptions options) => writer.WriteStringValue(SecretValue.Redacted);
}

/// <summary>A secret could not be resolved. The message names the reference only; there is never an inner exception.</summary>
public sealed class SecretResolutionException(string message) : Exception(message);

public sealed class SecretStoreOptions
{
    /// <summary>The fully qualified directory where the deployment mounts one file per secret, named by the secret.</summary>
    public string? Directory { get; set; }
}

/// <summary>
/// Reads each secret from a file named by the reference in the mounted secret directory. A link is followed only
/// when its final target stays inside the directory. Empty files and files over 64 KiB are refused, and the file's
/// bytes are returned exactly.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    public const int MaximumBytes = 64 * 1024;
    private readonly string directory;

    public FileSecretStore(IOptions<SecretStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Directory
            ?? throw new InvalidOperationException("The secret store directory is not configured.")));
    }

    public async ValueTask<SecretValue> GetAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.IsValid) throw new SecretResolutionException("A discarded literal value is not a secret reference, so nothing was resolved.");
        var name = reference.Name;
        byte[]? buffer = null;
        try
        {
            var file = new FileInfo(Path.Join(directory, name));
            if (file.LinkTarget is not null)
            {
                var target = file.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !Path.GetFullPath(target.FullName).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw Refused(name, "is a link whose target is outside the secret store");
                file = new FileInfo(target.FullName);
            }
            if (!file.Exists) throw Refused(name, "is not present in the secret store");
            if (file.Length == 0) throw Refused(name, "is empty");
            if (file.Length > MaximumBytes) throw Refused(name, $"is larger than {MaximumBytes} bytes");
            await using var stream = new FileStream(file.FullName, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous
            });
            buffer = new byte[stream.Length];
            if (buffer.Length is 0 or > MaximumBytes) throw Refused(name, "changed while it was read");
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (stream.ReadByte() != -1) throw Refused(name, "changed while it was read");
            var value = SecretValue.Own(buffer);
            buffer = null;
            return value;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Refused(name, "is not present in the secret store");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            throw Refused(name, "could not be read from the secret store");
        }
        finally
        {
            if (buffer is not null) CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static SecretResolutionException Refused(string name, string reason) =>
        new($"Secret '{name}' {reason}. Mount the secret through the deployment's secret store.");
}

internal sealed class SecretStoreOptionsValidation(string sectionPath) : IValidateOptions<SecretStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, SecretStoreOptions options)
    {
        var key = sectionPath + ":Directory";
        if (string.IsNullOrWhiteSpace(options.Directory))
            return ValidateOptionsResult.Fail($"'{key}' must name the directory where the deployment mounts its secrets.");
        if (!Path.IsPathFullyQualified(options.Directory))
            return ValidateOptionsResult.Fail($"'{key}' must be a fully qualified directory.");
        if (!System.IO.Directory.Exists(options.Directory))
            return ValidateOptionsResult.Fail($"'{key}' names a directory that does not exist. Mount the secret directory before startup.");
        return ValidateOptionsResult.Success;
    }
}

public static class SecretServiceCollectionExtensions
{
    /// <summary>Registers <see cref="FileSecretStore"/> over the mounted secret directory named by <c>{sectionPath}:Directory</c>.</summary>
    public static IServiceCollection AddModularynSecrets(this IServiceCollection services, IConfiguration configuration, string sectionPath = "Modularyn:Secrets")
    {
        services.AddModularynDeploymentOptions<SecretStoreOptions>(configuration, sectionPath);
        services.AddSingleton<IValidateOptions<SecretStoreOptions>>(new SecretStoreOptionsValidation(sectionPath));
        services.TryAddSingleton<ISecretStore, FileSecretStore>();
        return services;
    }
}
