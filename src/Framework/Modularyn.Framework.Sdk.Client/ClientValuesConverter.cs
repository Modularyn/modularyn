using System.Text.Json.Serialization;
using System.Text.Json;

namespace Modularyn.Framework.Sdk.Client;

internal sealed class ClientValuesConverter : JsonConverter<IReadOnlyDictionary<string, JsonElement>>
{
    public override IReadOnlyDictionary<string, JsonElement> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                throw new JsonException();
            values[property.Name] = property.Value.Clone();
        }
        return values;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, JsonElement> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (name, item) in value)
        {
            if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) throw new JsonException();
            writer.WritePropertyName(name);
            item.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
