using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modularyn.Framework.Security.Abstractions;

/// <summary>
/// Text that is a credential or a secret, such as a password, a session token or a recovery code. Formatting, logging,
/// debugging and JSON serialization see only <see cref="Redacted"/>; the value is available only through <see cref="Reveal"/>.
/// </summary>
[DebuggerDisplay("[secret]")]
[JsonConverter(typeof(SecretTextJsonConverter))]
public sealed class SecretText
{
    public const string Redacted = "[secret]";
    private readonly string value;

    public SecretText(string value) => this.value = value ?? throw new ArgumentNullException(nameof(value));

    public int Length => value.Length;

    public string Reveal() => value;

    public override string ToString() => Redacted;
}

internal sealed class SecretTextJsonConverter : JsonConverter<SecretText>
{
    public override SecretText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new JsonException("Secret text is read by its owning adapter, never deserialized.");

    public override void Write(Utf8JsonWriter writer, SecretText value, JsonSerializerOptions options) => writer.WriteStringValue(SecretText.Redacted);
}
