namespace Modularyn.Framework.Tenancy.Abstractions;

/// <summary>
/// Data that belongs to exactly one tenant. The persistence adapter registers the tenant filter for it, derives the
/// tenant on insert from the unit of work's validated context, refuses a change of tenant, and requires the tenant in
/// its keys, indexes and references. Implement the member as a public mapped property.
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

/// <summary>
/// Data that belongs to the host rather than to any tenant, such as the tenant directory. Every root entity of a module
/// context declares either this or <see cref="ITenantScoped"/>; an undeclared entity is refused. A tenant unit of work
/// can read host-scoped data but cannot change it.
/// </summary>
public interface IHostScoped;

/// <summary>Claim types that tenancy reads from an authenticated principal.</summary>
public static class TenantClaimTypes
{
    /// <summary>A tenant candidate carried by the principal: one canonical tenant identifier.</summary>
    public const string Tenant = "modularyn.tenant";
}

/// <summary>The one textual form of a tenant identifier: a lowercase, hyphenated GUID that is not empty.</summary>
public static class TenantIdentifier
{
    public static string Format(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant identifier cannot be empty.", nameof(tenantId));
        return tenantId.ToString("D");
    }

    /// <summary>Accepts only the canonical form. Letter case, braces, whitespace and other variants are refused, never normalized.</summary>
    public static bool TryParse(string? value, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        if (value is null || value.Length != 36 || !Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty
            || !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal)) return false;
        tenantId = parsed;
        return true;
    }
}
