using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace Modularyn.Framework.Api.Contracts;

/// <summary>Deterministic, repository-owned generation. Runtime transport and custom code live outside generated files.</summary>
public static class ApiGenerator
{
    public static string OpenApi(ApiInventory inventory)
    {
        inventory.Validate();
        var schemas = new JsonObject();
        foreach (var model in inventory.Models)
        {
            var properties = new JsonObject();
            foreach (var field in model.Fields) properties[Camel(field.Name)] = Schema(field.Type, field.Nullable);
            schemas[model.Name] = new JsonObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = new JsonArray(model.Fields.Select(field => (JsonNode?)JsonValue.Create(Camel(field.Name))).ToArray())
            };
        }
        schemas["Problem"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("type", "title", "status", "code", "correlationId"),
            ["properties"] = new JsonObject
            {
                ["type"] = Schema("string"), ["title"] = Schema("string"), ["status"] = Schema("integer"),
                ["code"] = Schema("string"), ["correlationId"] = Schema("string"), ["operationId"] = Schema("uuid", true),
                ["errors"] = new JsonObject { ["anyOf"] = new JsonArray(
                    new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "array", ["items"] = Schema("string") } },
                    new JsonObject { ["type"] = "null" }) }
            }
        };
        var paths = new JsonObject();
        foreach (var endpoint in inventory.Endpoints)
        foreach (var version in endpoint.Versioned ? Enumerable.Range(inventory.OldestVersion, inventory.CurrentVersion - inventory.OldestVersion + 1) : new[] { 1 })
        {
            var route = endpoint.Route.Replace("{version}", version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var parameters = new JsonArray();
            foreach (var parameter in Parameters(endpoint))
                parameters.Add(new JsonObject { ["name"] = parameter, ["in"] = "path", ["required"] = true, ["schema"] = Schema("string") });
            if (endpoint.Idempotent)
                parameters.Add(new JsonObject { ["name"] = FrameworkApi.OperationHeader, ["in"] = "header", ["required"] = true, ["schema"] = Schema("uuid") });
            var responses = new JsonObject
            {
                [endpoint.SuccessStatus.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["description"] = "Authoritative response",
                    ["content"] = endpoint.SuccessStatus == 204 ? new JsonObject()
                        : endpoint.ResponseSchema is { } schema ? InlineContent(endpoint.ResponseMediaType, schema) : Content(endpoint.Response, false),
                    ["headers"] = Headers(endpoint)
                }
            };
            foreach (var status in endpoint.ErrorStatuses ?? new[] { 400, 401, 403, 404, 409, 413, 415, 426, 500, 503 })
            {
                var content = Content("Problem", true);
                if (endpoint.LegacyErrors)
                    content["application/json"] = new JsonObject { ["schema"] = new JsonObject
                    {
                        ["type"] = "object", ["required"] = new JsonArray("code"),
                        ["properties"] = new JsonObject { ["code"] = Schema("string") }
                    } };
                if (endpoint.ResponseMediaType == "text/plain" && endpoint.ResponseSchema is { } healthSchema)
                    content = InlineContent("text/plain", healthSchema);
                responses[status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    { ["description"] = "Declared failure", ["content"] = content, ["headers"] = Headers(endpoint) };
            }
            responses["default"] = new JsonObject
            {
                ["description"] = "Transport or endpoint refusal", ["content"] = Content("Problem", true), ["headers"] = Headers(endpoint)
            };
            var security = endpoint.Authentication == "none" ? new JsonArray()
                : new JsonArray(new JsonObject { [endpoint.Authentication] = new JsonArray() });
            if (endpoint.OptionalAuthentication && security.Count > 0) security.Add(new JsonObject());
            var operation = new JsonObject
            {
                ["operationId"] = endpoint.Name + "V" + version, ["deprecated"] = endpoint.Deprecated,
                ["x-modularyn-authorization"] = endpoint.Authorization, ["x-modularyn-permission"] = endpoint.Permission,
                ["x-modularyn-client-visible"] = endpoint.ClientVisible, ["x-modularyn-version"] = version,
                ["x-modularyn-idempotent"] = endpoint.Idempotent, ["parameters"] = parameters, ["responses"] = responses,
                ["security"] = security
            };
            if (endpoint.Request is not null || endpoint.RequestSchema is not null)
                operation["requestBody"] = new JsonObject { ["required"] = true, ["content"] = endpoint.RequestSchema is { } requestSchema
                    ? InlineContent("application/json", requestSchema) : Content(endpoint.Request, false) };
            if (paths[route] is not JsonObject path) paths[route] = path = new JsonObject();
            path[endpoint.Method.ToLowerInvariant()] = operation;
        }
        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject { ["title"] = "Modularyn API", ["version"] = inventory.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ["x-modularyn-oldest-version"] = inventory.OldestVersion, ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = schemas,
                ["securitySchemes"] = new JsonObject
                {
                    ["explicitCredential"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "Authorization",
                        ["description"] = "Explicit credential obtained for this origin from the host's credential provider." },
                    ["session"] = new JsonObject { ["type"] = "apiKey", ["in"] = "cookie", ["name"] = "__Host-Modularyn-Session" }
                }
            }
        }.ToJsonString(ApiInventory.Json) + "\n";
    }

    public static string DotNet(ApiInventory inventory, string namespaceName, string className)
    {
        inventory.Validate();
        if (!Regex.IsMatch(namespaceName, @"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)*$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(className, "^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)) throw new ArgumentException("Use C# identifiers.");
        var output = new StringBuilder("// Generated from the endpoint inventory by ApiGenerator.\n#nullable enable\nusing System.Text.Json;\nusing Modularyn.Framework.Sdk.Client;\n\nnamespace ")
            .Append(namespaceName).Append(";\n\n");
        foreach (var model in inventory.Models)
            output.Append("public sealed record ").Append(model.Name).Append('(').Append(string.Join(", ", model.Fields.Select(field =>
                CsType(field.Type) + (field.Nullable ? "?" : "") + " " + field.Name))).Append(");\n");
        output.Append("\npublic sealed class ").Append(className).Append("(ApiClient transport)\n{\n")
            .Append("    public const int OldestVersion = ").Append(inventory.OldestVersion).Append(";\n")
            .Append("    public const int CurrentVersion = ").Append(inventory.CurrentVersion).Append(";\n");
        foreach (var endpoint in inventory.Endpoints.Where(endpoint => endpoint.ClientVisible))
        {
            var arguments = Parameters(endpoint).Select(parameter => "string " + parameter).ToList();
            if (endpoint.Request is not null) arguments.Add(endpoint.Request + " body");
            if (endpoint.Idempotent) arguments.Add("Guid operationId");
            arguments.Add("CancellationToken cancellationToken = default");
            output.Append("    public Task<").Append(endpoint.Response).Append("> ").Append(endpoint.Name).Append("Async(")
                .Append(string.Join(", ", arguments)).Append(") =>\n        transport.SendAsync<").Append(endpoint.Response).Append(">(")
                .Append(Quote(endpoint.Method)).Append(", ").Append(Quote(endpoint.Route)).Append(", new Dictionary<string, string> { ")
                .Append(string.Join(", ", Parameters(endpoint).Select(parameter => "[" + Quote(parameter) + "] = " + parameter)))
                .Append(" }, ").Append(endpoint.Request is null ? "null" : "body").Append(", ")
                .Append(endpoint.Idempotent ? "operationId" : "null").Append(", cancellationToken);\n");
        }
        return output.Append("}\n").ToString();
    }

    public static string TypeScript(ApiInventory inventory, string className, string runtimeImport = "./runtime.ts")
    {
        inventory.Validate();
        if (!Regex.IsMatch(className, "^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(runtimeImport, @"^\.?\.?/[A-Za-z0-9/._-]+\.ts$", RegexOptions.CultureInvariant)) throw new ArgumentException("Use static TypeScript names.");
        var output = new StringBuilder("// Generated from the endpoint inventory by ApiGenerator.\nimport { ApiClient, validate } from ")
            .Append(Quote(runtimeImport)).Append(";\n\n");
        foreach (var model in inventory.Models)
        {
            output.Append("export interface ").Append(model.Name).Append(" {\n");
            foreach (var field in model.Fields)
                output.Append("  ").Append(Camel(field.Name)).Append(": ").Append(TsType(field.Type))
                    .Append(field.Nullable ? " | null" : "").Append(";\n");
            output.Append("}\n");
        }
        output.Append("\nconst models = ").Append(JsonSerializer.Serialize(inventory.Models, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .Append(";\n\nexport class ").Append(className).Append(" {\n")
            .Append("  static readonly oldestVersion = ").Append(inventory.OldestVersion).Append(";\n")
            .Append("  static readonly currentVersion = ").Append(inventory.CurrentVersion).Append(";\n")
            .Append("  private readonly transport: ApiClient;\n")
            .Append("  constructor(transport: ApiClient) { this.transport = transport; }\n");
        foreach (var endpoint in inventory.Endpoints.Where(endpoint => endpoint.ClientVisible))
        {
            var arguments = Parameters(endpoint).Select(parameter => parameter + ": string").ToList();
            if (endpoint.Request is not null) arguments.Add("body: " + endpoint.Request);
            if (endpoint.Idempotent) arguments.Add("operationId: string");
            arguments.Add("signal?: AbortSignal");
            output.Append("  async ").Append(Camel(endpoint.Name)).Append('(').Append(string.Join(", ", arguments))
                .Append("): Promise<").Append(endpoint.Response).Append("> {\n    const result = await this.transport.send(")
                .Append(Quote(endpoint.Method)).Append(", ").Append(Quote(endpoint.Route)).Append(", { ")
                .Append(string.Join(", ", Parameters(endpoint))).Append(" }, ").Append(endpoint.Request is null ? "undefined" : "body")
                .Append(", ").Append(endpoint.Idempotent ? "operationId" : "undefined").Append(", signal, value => validate(value, ")
                .Append(Quote(endpoint.Response!)).Append(", models));\n    return result as ").Append(endpoint.Response).Append(";\n  }\n");
        }
        return output.Append("}\n").ToString();
    }

    private static string[] Parameters(ApiEndpoint endpoint) => Regex.Matches(endpoint.Route, @"\{([a-z][a-zA-Z0-9]*)\}")
        .Select(match => match.Groups[1].Value).Where(value => value != "version").Distinct(StringComparer.Ordinal).ToArray();
    private static string Camel(string value) => char.ToLowerInvariant(value[0]) + value[1..];
    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static string CsType(string type) => type switch { "uuid" => "Guid", "integer" => "int", "boolean" => "bool", "values" => "IReadOnlyDictionary<string, JsonElement>", _ => type };
    private static string TsType(string type) => type switch { "uuid" => "string", "integer" => "number", "boolean" => "boolean", "values" => "Record<string, string | boolean | null>", _ => type };

    private static JsonNode Schema(string type, bool nullable = false)
    {
        JsonNode schema = type switch
        {
            "string" or "boolean" => new JsonObject { ["type"] = type },
            "integer" => new JsonObject { ["type"] = "integer", ["format"] = "int32" },
            "uuid" => new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            "values" => new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = new JsonArray("string", "boolean", "null") } },
            _ => new JsonObject { ["$ref"] = "#/components/schemas/" + type }
        };
        return nullable ? new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) } : schema;
    }

    private static JsonObject Content(string? model, bool problem) => model is null ? new JsonObject() : new JsonObject
    {
        [problem ? "application/problem+json" : "application/json"] = new JsonObject { ["schema"] = Schema(model) }
    };

    private static JsonObject InlineContent(string mediaType, JsonElement schema) => new()
    {
        [mediaType] = new JsonObject { ["schema"] = JsonNode.Parse(schema.GetRawText()) }
    };

    private static JsonObject Headers(ApiEndpoint endpoint)
    {
        var headers = new JsonObject { [FrameworkApi.CorrelationHeader] = new JsonObject { ["schema"] = Schema("string") } };
        if (endpoint.Versioned) headers[FrameworkApi.VersionHeader] = new JsonObject { ["schema"] = Schema("string") };
        if (endpoint.Idempotent) headers[FrameworkApi.OperationHeader] = new JsonObject { ["schema"] = Schema("uuid") };
        return headers;
    }
}
