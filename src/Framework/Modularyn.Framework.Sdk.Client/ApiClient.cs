using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Framework.Sdk.Client;

/// <summary>
/// Static transport. It owns a transport with cookies, redirects and ambient credentials disabled. Each request obtains
/// an explicit credential anew for its exact origin. It never retries a mutation or a challenge automatically.
/// </summary>
public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32,
        Converters = { new ClientValuesConverter() }
    };
    private readonly HttpClient http;
    private readonly IClientCredentialProvider credentials;
    private readonly EndpointOrigin origin;

    public ApiClient(EndpointOrigin origin, IClientCredentialProvider credentials, int version = 2, SocketsHttpHandler? handler = null)
    {
        this.origin = origin ?? throw new ArgumentNullException(nameof(origin));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        handler ??= new();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        handler.Credentials = null;
        handler.PreAuthenticate = false;
        handler.UseProxy = false;
        http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    }

    public int Version { get; }

    public static int NegotiateVersion(int clientOldest, int clientCurrent, int serverOldest, int serverCurrent)
    {
        if (clientOldest < 1 || serverOldest < 1 || clientCurrent < clientOldest || serverCurrent < serverOldest)
            throw new ArgumentException("A version window must be valid.");
        var selected = Math.Min(clientCurrent, serverCurrent);
        return selected >= Math.Max(clientOldest, serverOldest) ? selected
            : throw new CompatibilityFailure(new ApiProblem("urn:modularyn:problem:" + ApiCodes.Version,
                "The API compatibility windows do not overlap.", 426, ApiCodes.Version, ""));
    }

    public async Task<T> SendAsync<T>(string method, string template, IReadOnlyDictionary<string, string> parameters,
        object? body, Guid? operationId, CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (method != "GET" && (operationId is null || operationId == Guid.Empty))
            throw new ArgumentException("A mutation requires a stable operation identity.", nameof(operationId));
        var path = template.Replace("{version}", Version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        foreach (var (name, value) in parameters)
        {
            if (string.IsNullOrEmpty(value) || value is "." or ".." || value.Contains('/') || value.Contains('\\'))
                throw new ArgumentException("A route argument is one nonempty path segment.", nameof(parameters));
            path = path.Replace("{" + name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('{') || path.Contains('}'))
            throw new ArgumentException("The endpoint binding is incomplete.", nameof(template));
        var target = new Uri(new Uri(origin.Value), path);
        if (!EndpointOrigin.TryFromRequestUri(target, out var actual) || !actual.Equals(origin))
            throw new ArgumentException("The endpoint binding must stay on its configured origin.", nameof(template));
        using var request = new HttpRequestMessage(new HttpMethod(method), target);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (operationId is { } id) request.Headers.Add(FrameworkApi.OperationHeader, id.ToString("D"));
        var credential = await credentials.GetCredentialAsync(actual, cancellationToken).ConfigureAwait(false);
        if (credential is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue(credential.Scheme, credential.Value.Reveal());
        cancellationToken.ThrowIfCancellationRequested();
        HttpResponseMessage response;
        try
        {
            // Buffer to completion under cancellation. Once received, parsing cannot turn the observed outcome into cancellation.
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (operationId is not null && exception is HttpRequestException or OperationCanceledException)
        { throw new OutcomeUnknownFailure(operationId.Value); }
        using (response)
        {
            var correlation = response.Headers.TryGetValues(FrameworkApi.CorrelationHeader, out var correlations) ? SingleHeader(correlations) ?? "" : "";
            var bytes = await response.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
            JsonElement? value = null;
            try { if (bytes.Length > 0) value = JsonSerializer.Deserialize<JsonElement>(bytes, Json); }
            catch (JsonException) { }
            var status = (int)response.StatusCode;
            ApiProblem Problem(string code) => new("urn:modularyn:problem:" + code, "The request could not be completed.", status, code, correlation, operationId);
            if (!response.IsSuccessStatusCode)
            {
                var code = value is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty("code", out var property)
                    && property.ValueKind == JsonValueKind.String ? property.GetString()! : status switch
                    { 400 => ApiCodes.Validation, 404 => ApiCodes.NotFound, 409 => ApiCodes.Conflict, 426 => ApiCodes.Version, 503 => ApiCodes.Unavailable, _ => ApiCodes.Protocol };
                ApiProblem problem;
                try { problem = value?.Deserialize<ApiProblem>(Json) ?? Problem(code); }
                catch (JsonException) { problem = Problem(code); }
                // Transport status and correlation remain authoritative even when a peer supplied malformed problem fields.
                problem = problem with { Status = status, Code = code, CorrelationId = correlation, OperationId = operationId };
                throw Failure(problem);
            }
            if (response.Headers.TryGetValues(FrameworkApi.VersionHeader, out var versions) is false
                || SingleHeader(versions) != Version.ToString(CultureInfo.InvariantCulture))
                throw new CompatibilityFailure(Problem(ApiCodes.Version), value);
            try
            {
                return value?.Deserialize<T>(Json) ?? throw new JsonException();
            }
            catch (JsonException) { throw new ProtocolFailure(Problem(ApiCodes.Protocol), value); }
        }
    }

    private static ApiFailure Failure(ApiProblem problem) => problem.Status switch
    {
        401 => new AuthenticationFailure(problem), 403 => new AuthorizationFailure(problem),
        _ => problem.Code switch
        {
            ApiCodes.Validation => new ValidationFailure(problem), ApiCodes.Conflict => new ConcurrencyFailure(problem),
            ApiCodes.Version => new CompatibilityFailure(problem), ApiCodes.OperationConflict => new OperationConflictFailure(problem),
            ApiCodes.NotFound => new MissingFailure(problem), ApiCodes.Unavailable => new AvailabilityFailure(problem),
            ApiCodes.OutcomeUnknown => new OutcomeUnknownFailure(problem),
            ApiCodes.Protocol => new ProtocolFailure(problem),
            _ => problem.Status == 404 ? new MissingFailure(problem) : problem.Status == 503 ? new AvailabilityFailure(problem) : new ApiFailure(problem)
        }
    };

    private static string? SingleHeader(IEnumerable<string> values)
    {
        var copy = values.Take(2).ToArray();
        return copy.Length == 1 ? copy[0] : null;
    }

    public void Dispose() => http.Dispose();
}
