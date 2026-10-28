using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modularyn.Framework.Api.Contracts;

public sealed record ApiField(string Name, string Type, bool Nullable = false);
public sealed record ApiModel(string Name, IReadOnlyList<ApiField> Fields);

/// <summary>Exposure is deliberate. Non-client endpoints are inventoried but never generate client methods.</summary>
public sealed record ApiEndpoint(string Name, string Method, string Route, string Authorization, string? Permission,
    bool ClientVisible, string? Request, string? Response, bool Mutation = false, bool Idempotent = false,
    bool Versioned = true, bool Deprecated = false, int SuccessStatus = 200, string ResponseMediaType = "application/json",
    JsonElement? RequestSchema = null, JsonElement? ResponseSchema = null, string Authentication = "explicitCredential",
    bool OptionalAuthentication = false, bool LegacyErrors = false, IReadOnlyList<int>? ErrorStatuses = null);

public sealed record ApiInventory(int OldestVersion, int CurrentVersion, IReadOnlyList<ApiModel> Models,
    IReadOnlyList<ApiEndpoint> Endpoints)
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ApiInventory Read(string json)
    {
        var inventory = JsonSerializer.Deserialize<ApiInventory>(json, Json) ?? throw new FormatException("An API inventory is required.");
        inventory.Validate();
        return inventory;
    }

    public void Validate()
    {
        if (OldestVersion < 1 || CurrentVersion < OldestVersion || CurrentVersion - OldestVersion > 20)
            throw new FormatException("Declare a bounded, contiguous API compatibility window.");
        var names = Models.Select(model => model.Name).ToHashSet(StringComparer.Ordinal);
        bool Type(string type) => type is "string" or "uuid" or "integer" or "boolean" or "values" || names.Contains(type);
        bool Name(string name) => Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);
        if (names.Count != Models.Count || Models.Any(model => !Name(model.Name) || model.Fields.Count == 0
            || model.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != model.Fields.Count
            || model.Fields.Any(field => !Name(field.Name) || !Type(field.Type))))
            throw new FormatException("Model identities and fields must be unique and use declared types.");
        if (Endpoints.Select(endpoint => endpoint.Name).Distinct(StringComparer.Ordinal).Count() != Endpoints.Count
            || Endpoints.Select(endpoint => (endpoint.Method, endpoint.Route)).Distinct().Count() != Endpoints.Count)
            throw new FormatException("Endpoint names and bindings must be unique.");
        foreach (var endpoint in Endpoints)
        {
            if (!Name(endpoint.Name) || endpoint.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")
                || !Regex.IsMatch(endpoint.Route, @"^/[A-Za-z0-9/{}._-]+$", RegexOptions.CultureInvariant)
                || endpoint.Route.Contains("..", StringComparison.Ordinal)
                || endpoint.Versioned != endpoint.Route.Contains("{version}", StringComparison.Ordinal)
                || endpoint.Authorization is not ("Anonymous" or "MemberOnly" or "Permission" or "HostAuthority")
                || (endpoint.Authorization == "Permission") != (endpoint.Permission is not null)
                || endpoint.Request is not null && !names.Contains(endpoint.Request)
                || endpoint.Response is not null && !names.Contains(endpoint.Response)
                || endpoint.Mutation != (endpoint.Method != "GET")
                || endpoint.Idempotent && !endpoint.Mutation
                || endpoint.SuccessStatus is < 200 or > 299
                || endpoint.ResponseMediaType is not ("application/json" or "text/plain")
                || endpoint.Authentication is not ("none" or "session" or "explicitCredential")
                || endpoint.ClientVisible && (endpoint.Response is null || endpoint.Mutation && !endpoint.Idempotent))
                throw new FormatException("An endpoint must declare its complete route, access and client contract.");
        }
    }

    public int Negotiate(int oldest, int current)
    {
        if (oldest < 1 || current < oldest) throw new InvalidOperationException("The client compatibility window is invalid.");
        var selected = Math.Min(current, CurrentVersion);
        return selected >= Math.Max(oldest, OldestVersion) ? selected
            : throw new InvalidOperationException("The API compatibility windows do not overlap.");
    }
}

public static class FrameworkApi
{
    public const string VersionHeader = "Modularyn-Api-Version";
    public const string CorrelationHeader = "Modularyn-Correlation";
    public const string OperationHeader = "Idempotency-Key";
    public static ApiInventory Inventory { get; } = Load();
    public static ApiEndpoint Endpoint(string name) => Inventory.Endpoints.Single(endpoint => endpoint.Name == name);

    private static ApiInventory Load()
    {
        using var stream = typeof(FrameworkApi).Assembly.GetManifestResourceStream(
            "Modularyn.Framework.Api.Contracts.endpoint-inventory.json")!;
        using var reader = new StreamReader(stream);
        return ApiInventory.Read(reader.ReadToEnd());
    }
}

public sealed record ApiProblem(string Type, string Title, int Status, string Code, string CorrelationId,
    Guid? OperationId = null, IReadOnlyDictionary<string, string[]>? Errors = null);

public static class ApiCodes
{
    public const string Validation = "MAP001";
    public const string Conflict = "MAP002";
    public const string Version = "MAP003";
    public const string NotFound = "MAP004";
    public const string OperationConflict = "MAP005";
    public const string OutcomeUnknown = "MAP006";
    public const string Unavailable = "MAP007";
    public const string Protocol = "MAP008";
}
