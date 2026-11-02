using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

// The evidence schema uses the same closed JSON Schema subset as the repository contracts. Keep this test-local
// evaluator independent of the writer's object model: unknown rules, duplicate properties and remote references refuse.
internal static class EvidenceSchema
{
    private static readonly HashSet<string> Keywords = new(
        ["$schema", "$id", "$defs", "$ref", "title", "description", "type", "const", "enum", "oneOf", "properties",
         "required", "additionalProperties", "items", "minItems", "maxItems", "uniqueItems", "minimum", "maximum",
         "minLength", "maxLength", "pattern"], StringComparer.Ordinal);

    internal static void Validate(string record, string schema)
    {
        using var value = JsonDocument.Parse(record, new JsonDocumentOptions { AllowDuplicateProperties = false });
        using var definition = JsonDocument.Parse(schema, new JsonDocumentOptions { AllowDuplicateProperties = false });
        Require(definition.RootElement.GetProperty("$id").GetString() == "urn:modularyn:evidence-record:1", "evidence schema identity");
        Require(definition.RootElement.GetProperty("$schema").GetString() == "https://json-schema.org/draft/2020-12/schema", "evidence schema draft");
        Inspect(definition.RootElement);
        Validate(value.RootElement, definition.RootElement, definition.RootElement);
    }

    private static void Inspect(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        Require(schema.ValueKind == JsonValueKind.Object, "schema shape");
        foreach (var property in schema.EnumerateObject())
        {
            Require(Keywords.Contains(property.Name), "unsupported schema keyword");
            switch (property.Name)
            {
                case "$ref":
                    Require(property.Value.GetString()!.StartsWith("#/$defs/", StringComparison.Ordinal), "local schema reference required");
                    break;
                case "$defs":
                case "properties":
                    foreach (var child in property.Value.EnumerateObject()) Inspect(child.Value);
                    break;
                case "items":
                case "additionalProperties": Inspect(property.Value); break;
                case "oneOf": foreach (var child in property.Value.EnumerateArray()) Inspect(child); break;
            }
        }
    }

    private static void Validate(JsonElement value, JsonElement schema, JsonElement root)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        Require(schema.ValueKind != JsonValueKind.False, "schema prohibits value");
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var key = reference.GetString()![8..];
            Require(!key.Contains('/') && root.GetProperty("$defs").TryGetProperty(key, out _), "unresolved schema reference");
            Validate(value, root.GetProperty("$defs").GetProperty(key), root);
        }
        if (schema.TryGetProperty("const", out var constant)) Require(JsonElement.DeepEquals(value, constant), "schema constant");
        if (schema.TryGetProperty("enum", out var choices))
            Require(choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(value, choice)), "schema enum");
        if (schema.TryGetProperty("oneOf", out var alternatives))
        {
            var matches = 0;
            foreach (var alternative in alternatives.EnumerateArray())
            {
                try { Validate(value, alternative, root); matches++; }
                catch (EvidenceValidationException) { }
            }
            Require(matches == 1, "schema oneOf");
        }
        if (schema.TryGetProperty("type", out var type))
            Require(type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == decimal.Truncate(number),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => throw new EvidenceValidationException("unsupported schema type")
            }, "schema type");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray()) Require(value.TryGetProperty(name.GetString()!, out _), "required evidence property");
            foreach (var property in value.EnumerateObject())
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property.Name, out var child))
                    Validate(property.Value, child, root);
                else if (schema.TryGetProperty("additionalProperties", out var additional)) Validate(property.Value, additional, root);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            Bound(items.Length, schema, "minItems", "maxItems");
            if (schema.TryGetProperty("items", out var itemSchema))
                foreach (var item in items) Validate(item, itemSchema, root);
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
                for (var index = 0; index < items.Length; index++)
                    for (var other = index + 1; other < items.Length; other++)
                        Require(!JsonElement.DeepEquals(items[index], items[other]), "schema duplicate item");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            Bound(text.EnumerateRunes().Count(), schema, "minLength", "maxLength");
            if (schema.TryGetProperty("pattern", out var pattern))
                Require(Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "schema pattern");
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            Require(value.TryGetDecimal(out var number), "schema number");
            Bound(number, schema, "minimum", "maximum");
        }
    }

    private static void Bound(decimal number, JsonElement schema, string minimum, string maximum)
    {
        if (schema.TryGetProperty(minimum, out var low)) Require(number >= low.GetDecimal(), "schema lower bound");
        if (schema.TryGetProperty(maximum, out var high)) Require(number <= high.GetDecimal(), "schema upper bound");
    }

    internal static void Require(bool condition, string code)
    {
        if (!condition) throw new EvidenceValidationException(code);
    }
}

internal sealed class EvidenceValidationException(string code) : Exception("Evidence refused: " + code + ".");
