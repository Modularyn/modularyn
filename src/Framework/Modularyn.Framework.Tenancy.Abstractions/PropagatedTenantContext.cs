using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Modularyn.Framework.Tenancy.Abstractions;

/// <summary>
/// The propagation contract for events, jobs and file processing: the references a producer captures from its validated
/// context, carried explicitly beside the payload and revalidated by the receiver before any work. It holds references
/// only, never a credential, token, request object or payload value, and a tenant identifier alone authorizes nothing.
/// </summary>
/// <remarks>
/// A stored reference is format 2: its store binds it to the row or item it travels with and seals it with the host's
/// key ring (<see cref="IPropagationSeal"/>), in the producing unit. Only a reference captured in process or opened
/// under its seal and expected binding is verified, and tenancy establishes no context from any other, so a reference
/// written by anything but its store's capture, altered, or moved to another row, even in the same tenant, never runs.
/// </remarks>
public sealed class PropagatedTenantContext
{
    public const string FormatName = "modularyn.tenant-context";
    public const int FormatVersion = 2;
    public const int MaxJsonLength = 4096;
    public const int MaxOperationLength = 200;
    public const int MaxReasonLength = 500;
    private const int MaxSealLength = 1024;

    private static readonly string[] TenantProperties = ["format", "formatVersion", "scope", "tenant", "actor", "producerUnit", "capturedAt", "binding", "seal"];
    private static readonly string[] HostProperties = ["format", "formatVersion", "scope", "operation", "reason", "actor", "producerUnit", "capturedAt", "binding", "seal"];

    private readonly IPropagationSeal? sealer;

    private PropagatedTenantContext(TenantScope scope, Guid? tenantId, string? operationId, string? reason, ActorReference actor,
        Guid producerUnitId, DateTimeOffset capturedAt, IPropagationSeal? sealer = null, bool verified = false, PropagationBinding? binding = null)
    {
        Scope = scope;
        TenantId = tenantId;
        OperationId = operationId;
        Reason = reason;
        Actor = actor;
        ProducerUnitId = producerUnitId;
        CapturedAt = capturedAt;
        this.sealer = sealer;
        IsVerified = verified;
        Binding = binding;
    }

    public TenantScope Scope { get; }
    public Guid? TenantId { get; }
    public string? OperationId { get; }
    public string? Reason { get; }
    public ActorReference Actor { get; }
    public Guid ProducerUnitId { get; }
    public DateTimeOffset CapturedAt { get; }

    /// <summary>
    /// True for a reference captured in process from a live context, or opened under its seal and expected binding; only
    /// such a reference establishes a context. A parsed reference is for inspection and is never verified.
    /// </summary>
    public bool IsVerified { get; }

    /// <summary>The binding a stored reference carries; null for one captured in process and not yet stored.</summary>
    public PropagationBinding? Binding { get; }

    /// <summary>Captures the references of a context whose unit is still active.</summary>
    public static PropagatedTenantContext Capture(TenantContext context, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsActive)
            throw new InvalidOperationException("Capture the tenant context while its unit of work is active.");
        var at = UtcMicroseconds.Now(clock ?? TimeProvider.System);
        return context.Scope == TenantScope.Tenant
            ? new(TenantScope.Tenant, context.TenantId, null, null, context.Actor, context.UnitId, at, context.PropagationSeal, verified: true)
            : new(TenantScope.Host, null, context.HostGrant!.OperationId, context.HostGrant.Reason, context.Actor, context.UnitId, at,
                context.PropagationSeal, verified: true);
    }

    /// <summary>
    /// The stored form, bound to <paramref name="binding"/> and sealed with the seal of the unit it was captured from. Only
    /// a reference captured in process is stored; one with no seal attached, because the host has none, is refused.
    /// </summary>
    public string ToJson(PropagationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsVerified || Binding is not null)
            throw new InvalidOperationException("Store only a reference captured from its producing unit; a stored reference is never stored again.");
        if (sealer is not { IsAvailable: true })
            throw new InvalidOperationException("Stored references are sealed with the host's declared key ring, and none is available, so nothing is stored.");
        var seal = sealer.Seal(Write(binding, null));
        return Encoding.UTF8.GetString(Write(binding, seal));
    }

    /// <summary>
    /// Opens a stored reference: it parses strictly, carries exactly <paramref name="expected"/> and bears this host's
    /// seal over its content and binding. Only then is it verified; anything else is refused rather than repaired.
    /// </summary>
    public static bool TryOpen(string? json, PropagationBinding expected, IPropagationSeal seal, out PropagatedTenantContext? value)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(seal);
        value = null;
        if (!TryRead(json, out var parsed, out var sealText) || parsed!.Binding != expected
            || !seal.Verify(parsed.Write(expected, null), sealText!)) return false;
        value = new(parsed.Scope, parsed.TenantId, parsed.OperationId, parsed.Reason, parsed.Actor, parsed.ProducerUnitId, parsed.CapturedAt,
            seal, verified: true, binding: expected);
        return true;
    }

    /// <summary>
    /// Parses a stored reference strictly, for inspection only, such as naming a replayed event's producer: exactly the
    /// properties of format 2 and its scope, each once, with canonical values. The result is never verified, so no context
    /// is established from it.
    /// </summary>
    public static bool TryParse(string? json, out PropagatedTenantContext? value) => TryRead(json, out value, out _);

    private static bool TryRead(string? json, out PropagatedTenantContext? value, out string? seal)
    {
        value = null;
        seal = null;
        if (string.IsNullOrEmpty(json) || json.Length > MaxJsonLength) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Properties(root, out var properties)) return false;
            if (!Text(properties, "format", out var format) || format != FormatName
                || !properties.TryGetValue("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number
                || version.GetRawText() != "2" || !Text(properties, "scope", out var scope)) return false;
            var expected = scope switch { "tenant" => TenantProperties, "host" => HostProperties, _ => null };
            if (expected is null || !properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected)) return false;
            if (!ParseActor(properties["actor"], out var actor)
                || !Text(properties, "producerUnit", out var unitText) || !Guid.TryParseExact(unitText, "D", out var unit)
                || unit == Guid.Empty || unit.ToString("D") != unitText
                || !Text(properties, "capturedAt", out var atText)
                || !DateTimeOffset.TryParseExact(atText, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                || !ParseBinding(properties["binding"], out var binding)
                || !Text(properties, "seal", out var sealText) || sealText.Length is 0 or > MaxSealLength) return false;
            if (scope == "tenant")
            {
                if (!Text(properties, "tenant", out var tenantText) || !TenantIdentifier.TryParse(tenantText, out var tenant)) return false;
                value = new(TenantScope.Tenant, tenant, null, null, actor!, unit, at, binding: binding);
            }
            else
            {
                if (!Text(properties, "operation", out var operation) || !ValidOperation(operation)
                    || !Text(properties, "reason", out var reason) || !ValidReason(reason)) return false;
                value = new(TenantScope.Host, null, operation, reason, actor!, unit, at, binding: binding);
            }
            seal = sealText;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The canonical content the seal covers, and with the seal the stored form: written here alone, so the same values
    // always produce the same bytes.
    private byte[] Write(PropagationBinding binding, string? seal)
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
            writer.WriteStartObject("binding");
            writer.WriteString("purpose", binding.Purpose);
            writer.WriteString("key", binding.Key);
            writer.WriteEndObject();
            if (seal is not null) writer.WriteString("seal", seal);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    internal static bool ValidOperation(string? value) => !string.IsNullOrEmpty(value) && value.Length <= MaxOperationLength
        && value.All(character => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_');

    internal static bool ValidReason(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxReasonLength
        && value == value.Trim() && !value.Any(char.IsControl);

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

    private static bool ParseBinding(JsonElement element, out PropagationBinding? binding)
    {
        binding = null;
        if (element.ValueKind != JsonValueKind.Object || !Properties(element, out var properties)
            || !properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["purpose", "key"])
            || !Text(properties, "purpose", out var purpose) || !Text(properties, "key", out var key)) return false;
        try
        {
            binding = new(purpose, key);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
