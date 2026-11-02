using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modularyn.Framework.Api.Contracts;

public sealed record ApiField(string Name, string Type, bool Nullable = false);
public sealed record ApiModel(string Name, IReadOnlyList<ApiField> Fields);

/// <summary>
/// Exposure is deliberate. Non-client endpoints are inventoried but never generate client methods. The response media type
/// is declared, never inferred: JSON for every client-visible endpoint, <c>text/html</c> only for a <see cref="Page"/>,
/// and <c>application/octet-stream</c> only for a <see cref="Download"/>, which names the explicit content types it sends.
/// </summary>
/// <param name="Page">A browser page. It answers GET, or POST for its form, and a form declares <see cref="Antiforgery"/>.</param>
/// <param name="Antiforgery">
/// A form post, a page's POST or a form upload, keeps the ingress's antiforgery validation; its endpoint may not disable it.
/// </param>
/// <param name="Download">A binary download, with its disposition and the explicit content types its bytes are sent with.</param>
/// <param name="Upload">An upload's request body, with the explicit media types it accepts, in place of a JSON request.</param>
public sealed record ApiEndpoint(string Name, string Method, string Route, string Authorization, string? Permission,
    bool ClientVisible, string? Request, string? Response, bool Mutation = false, bool Idempotent = false,
    bool Versioned = true, bool Deprecated = false, int SuccessStatus = 200, string ResponseMediaType = ApiMediaTypes.Json,
    JsonElement? RequestSchema = null, JsonElement? ResponseSchema = null, string Authentication = "explicitCredential",
    bool OptionalAuthentication = false, bool LegacyErrors = false, IReadOnlyList<int>? ErrorStatuses = null,
    bool Page = false, bool Antiforgery = false, ApiDownload? Download = null, ApiUpload? Upload = null);

/// <summary>
/// A binary download's declaration: its disposition, <c>attachment</c> or <c>inline</c>, and the explicit content types its
/// bytes are sent with, each a concrete lowercase <c>type/subtype</c> without parameters, and never HTML or JSON.
/// </summary>
public sealed record ApiDownload(string Disposition, IReadOnlyList<string> MediaTypes);

/// <summary>
/// An upload's request body: exactly <c>multipart/form-data</c> for a form upload, which declares antiforgery, or the explicit
/// binary content types it accepts, each a concrete lowercase <c>type/subtype</c> without parameters, and never multipart,
/// form-encoded, HTML or JSON.
/// </summary>
public sealed record ApiUpload(IReadOnlyList<string> MediaTypes);

/// <summary>The media types an inventory declares: responses, a page's form encoding and a form upload's.</summary>
public static class ApiMediaTypes
{
    public const string Json = "application/json";
    public const string PlainText = "text/plain";
    public const string Html = "text/html";
    public const string Binary = "application/octet-stream";
    public const string Form = "application/x-www-form-urlencoded";
    public const string Multipart = "multipart/form-data";
}

public sealed record ApiInventory(int OldestVersion, int CurrentVersion, IReadOnlyList<ApiModel> Models,
    IReadOnlyList<ApiEndpoint> Endpoints)
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Generated contracts are byte-identical on every platform; indented JSON otherwise uses the platform's newline.
        NewLine = "\n",
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
                || endpoint.Authorization is not ("Anonymous" or "Authenticated" or "MemberOnly" or "Permission" or "HostAuthority")
                || endpoint.Authorization == "Authenticated" && (endpoint.Authentication == "none" || endpoint.OptionalAuthentication)
                || (endpoint.Authorization == "Permission") != (endpoint.Permission is not null)
                || endpoint.Request is not null && !names.Contains(endpoint.Request)
                || endpoint.Response is not null && !names.Contains(endpoint.Response)
                || endpoint.Mutation != (endpoint.Method != "GET")
                || endpoint.Idempotent && !endpoint.Mutation
                || endpoint.SuccessStatus is < 200 or > 299
                || endpoint.Authentication is not ("none" or "session" or "explicitCredential")
                // A client-visible endpoint generates client methods, which pin a version; so it is versioned, and a retried
                // mutation is idempotent.
                || endpoint.ClientVisible && (endpoint.Response is null || !endpoint.Versioned || endpoint.Mutation && !endpoint.Idempotent))
                throw new FormatException("An endpoint must declare its complete route, access and client contract.");
            if (!MediaTypeDeclared(endpoint))
                throw new FormatException("An endpoint must declare its response media type: JSON for a client, HTML for a page, and "
                    + "binary for a download with its explicit content types and disposition.");
            if (!RequestDeclared(endpoint))
                throw new FormatException("An endpoint must declare its request body: an upload's explicit media types, either a form "
                    + "upload or binary types, and antiforgery on every form post.");
        }
    }

    // Media types are declared, never inferred from a handler. Generated clients read JSON, so a client-visible endpoint answers
    // JSON. HTML is a page's: a browser surface that a session or no credential reaches, with no JSON models, answering GET, or
    // POST for its form. Binary is a download's: never HTML or JSON, sent with one of its explicit content types and its
    // declared disposition.
    private static bool MediaTypeDeclared(ApiEndpoint endpoint)
    {
        var page = endpoint.ResponseMediaType == ApiMediaTypes.Html;
        var download = endpoint.ResponseMediaType == ApiMediaTypes.Binary;
        if (endpoint.ResponseMediaType is not (ApiMediaTypes.Json or ApiMediaTypes.PlainText or ApiMediaTypes.Html or ApiMediaTypes.Binary)
            || endpoint.ClientVisible && endpoint.ResponseMediaType != ApiMediaTypes.Json
            || endpoint.Page != page || (endpoint.Download is not null) != download)
            return false;
        if (page)
            return endpoint.Method is "GET" or "POST" && !endpoint.Idempotent && endpoint.Authentication is "none" or "session"
                && endpoint.Request is null && endpoint.Response is null && endpoint.ResponseSchema is null
                && (endpoint.RequestSchema is null || endpoint.Method == "POST");
        if (download)
            return endpoint.Response is null && endpoint.ResponseSchema is null && endpoint.Download!.Disposition is "attachment" or "inline"
                && Distinct(endpoint.Download.MediaTypes) && endpoint.Download.MediaTypes.All(Concrete);
        return true;
    }

    // A request body is declared as well: JSON by default, a page's form form-encoded, and an upload's own media types, either
    // exactly multipart/form-data for a form upload or explicit binary types. A form post, which is a page's POST or a form
    // upload, declares antiforgery, and nothing else may. An upload's body is the upload: a POST or PUT with no JSON request,
    // and a page's form uploads only as multipart/form-data.
    private static bool RequestDeclared(ApiEndpoint endpoint)
    {
        var form = endpoint.Upload is { MediaTypes: [ApiMediaTypes.Multipart] };
        if (endpoint.Antiforgery != (endpoint.Page && endpoint.Method == "POST" || form)) return false;
        if (endpoint.Upload is not { } upload) return true;
        return endpoint.Method is "POST" or "PUT" && endpoint.Request is null && endpoint.RequestSchema is null && (form || !endpoint.Page)
            && Distinct(upload.MediaTypes) && (form || upload.MediaTypes.All(type => Concrete(type)
                && !type.StartsWith("multipart/", StringComparison.Ordinal) && type != ApiMediaTypes.Form));
    }

    private static bool Distinct(IReadOnlyList<string>? types) =>
        types is { Count: > 0 } && types.Distinct(StringComparer.Ordinal).Count() == types.Count;

    // An explicit binary content type: concrete, lowercase and without parameters, and neither HTML nor JSON.
    private static bool Concrete(string? type) => type is not null && Regex.IsMatch(type, MediaTypePattern, RegexOptions.CultureInvariant)
        && type is not (ApiMediaTypes.Html or "application/xhtml+xml" or ApiMediaTypes.Json) && !type.EndsWith("+json", StringComparison.Ordinal);

    // RFC 6838's restricted names, in lowercase, with no parameters and no wildcard.
    private const string MediaTypePattern = @"^[a-z0-9][a-z0-9!#$&^_.+-]{0,126}/[a-z0-9][a-z0-9!#$&^_.+-]{0,126}\z";

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

    /// <summary>The request was refused before or without reaching an endpoint, as its status says; nothing more is claimed.</summary>
    public const string Refused = "MAP009";
}
