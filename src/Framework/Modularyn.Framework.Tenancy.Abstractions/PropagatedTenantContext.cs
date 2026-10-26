using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Modularyn.Framework.Tenancy.Abstractions;

/// <summary>
/// The propagation contract for events, jobs and file processing: the references a producer captures from its validated
/// context, carried explicitly beside the payload and revalidated by the receiver before any work. It holds references
/// only, never a credential, token, request object or payload value, and a tenant identifier alone authorizes nothing.
/// </summary>
public sealed class PropagatedTenantContext
{
    public const string FormatName = "modularyn.tenant-context";
    public const int FormatVersion = 1;
    public const int MaxJsonLength = 4096;
    public const int MaxOperationLength = 200;
    public const int MaxReasonLength = 500;

    private static readonly string[] TenantProperties = ["format", "formatVersion", "scope", "tenant", "actor", "producerUnit", "capturedAt"];
    private static readonly string[] HostProperties = ["format", "formatVersion", "scope", "operation", "reason", "actor", "producerUnit", "capturedAt"];

    private PropagatedTenantContext(TenantScope scope, Guid? tenantId, string? operationId, string? reason, ActorReference actor,
        Guid producerUnitId, DateTimeOffset capturedAt)
    {
        Scope = scope;
        TenantId = tenantId;
        OperationId = operationId;
        Reason = reason;
        Actor = actor;
        ProducerUnitId = producerUnitId;
        CapturedAt = capturedAt;
    }

    public TenantScope Scope { get; }
    public Guid? TenantId { get; }
    public string? OperationId { get; }
    public string? Reason { get; }
    public ActorReference Actor { get; }
    public Guid ProducerUnitId { get; }
    public DateTimeOffset CapturedAt { get; }

    /// <summary>Captures the references of a context whose unit is still active.</summary>
    public static PropagatedTenantContext Capture(TenantContext context, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsActive)
            throw new InvalidOperationException("Capture the tenant context while its unit of work is active.");
        var at = TruncateToMicroseconds((clock ?? TimeProvider.System).GetUtcNow());
        return context.Scope == TenantScope.Tenant
            ? new(TenantScope.Tenant, context.TenantId, null, null, context.Actor, context.UnitId, at)
            : new(TenantScope.Host, null, context.HostGrant!.OperationId, context.HostGrant.Reason, context.Actor, context.UnitId, at);
    }

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("scope", Scope == TenantScope.Tenant ? "tenant" : "host");
            if (Scope == TenantScope.Tenant) writer.WriteString("tenant", TenantIdentifier.Format(TenantId!.Value));
            else
            {
                writer.WriteString("operation", OperationId);
                writer.WriteString("reason", Reason);
            }
            writer.WriteStartObject("actor");
            writer.WriteString("kind", Actor.Kind == ActorKind.User ? "user" : "service");
            writer.WriteString("subject", Actor.Subject);
            writer.WriteEndObject();
            writer.WriteString("producerUnit", ProducerUnitId.ToString("D"));
            writer.WriteString("capturedAt", CapturedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Parses format 1 strictly: exactly the properties of its scope, each once, with canonical values. Anything else,
    /// including an unknown field or version, is refused rather than repaired.
    /// </summary>
    public static bool TryParse(string? json, out PropagatedTenantContext? value)
    {
        value = null;
        if (string.IsNullOrEmpty(json) || json.Length > MaxJsonLength) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Properties(root, out var properties)) return false;
            if (!Text(properties, "format", out var format) || format != FormatName
                || !properties.TryGetValue("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number
                || version.GetRawText() != "1" || !Text(properties, "scope", out var scope)) return false;
            var expected = scope switch { "tenant" => TenantProperties, "host" => HostProperties, _ => null };
            if (expected is null || !properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected)) return false;
            if (!ParseActor(properties["actor"], out var actor)
                || !Text(properties, "producerUnit", out var unitText) || !Guid.TryParseExact(unitText, "D", out var unit)
                || unit == Guid.Empty || unit.ToString("D") != unitText
                || !Text(properties, "capturedAt", out var atText)
                || !DateTimeOffset.TryParseExact(atText, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) return false;
            if (scope == "tenant")
            {
                if (!Text(properties, "tenant", out var tenantText) || !TenantIdentifier.TryParse(tenantText, out var tenant)) return false;
                value = new(TenantScope.Tenant, tenant, null, null, actor!, unit, at);
                return true;
            }
            if (!Text(properties, "operation", out var operation) || !ValidOperation(operation)
                || !Text(properties, "reason", out var reason) || !ValidReason(reason)) return false;
            value = new(TenantScope.Host, null, operation, reason, actor!, unit, at);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool ValidOperation(string? value) => !string.IsNullOrEmpty(value) && value.Length <= MaxOperationLength
        && value.All(character => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_');

    internal static bool ValidReason(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxReasonLength
        && value == value.Trim() && !value.Any(char.IsControl);

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private static bool Properties(JsonElement element, out Dictionary<string, JsonElement> properties)
    {
        properties = new(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!properties.TryAdd(property.Name, property.Value)) return false;
        return true;
    }

    private static bool Text(Dictionary<string, JsonElement> properties, string name, out string text)
    {
        text = "";
        if (!properties.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        text = element.GetString()!;
        return true;
    }

    private static bool ParseActor(JsonElement element, out ActorReference? actor)
    {
        actor = null;
        if (element.ValueKind != JsonValueKind.Object || !Properties(element, out var properties)
            || !properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["kind", "subject"])
            || !Text(properties, "kind", out var kind) || !Text(properties, "subject", out var subject)) return false;
        var actorKind = kind switch { "user" => ActorKind.User, "service" => ActorKind.Service, _ => (ActorKind?)null };
        if (actorKind is null || string.IsNullOrWhiteSpace(subject) || subject.Length > ActorReference.MaxSubjectLength
            || subject != subject.Trim() || subject.Any(char.IsControl)) return false;
        actor = new(actorKind.Value, subject);
        return true;
    }
}
