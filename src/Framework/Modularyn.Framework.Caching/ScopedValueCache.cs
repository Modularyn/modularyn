using System.Collections.Concurrent;
using System.Globalization;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Caching;

/// <summary>
/// Process-local caching over TenantCache. Global generations make every affected tenant's old entries unreachable;
/// tenant and principal invalidations use tenancy's generations. No cross-process coordination is claimed.
/// </summary>
public sealed class ScopedValueCache(TenantCache cache)
{
    private readonly ConcurrentDictionary<string, long> generations = new(StringComparer.Ordinal);

    public Task<T> GetOrCreateAsync<T>(TenantContext tenant, string module, string version, string name,
        TimeSpan maxStaleness, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken = default)
    {
        RequireTenant(tenant);
        return cache.GetOrCreateAsync(tenant, new TenantCacheEntry(module, version, name, maxStaleness, true,
            generations.GetValueOrDefault(module).ToString(CultureInfo.InvariantCulture)), factory, cancellationToken);
    }

    public void InvalidateGlobal(string module) => generations.AddOrUpdate(module, 1, (_, generation) => checked(generation + 1));
    public void InvalidateTenant(Guid tenant) => cache.InvalidateTenant(tenant);
    public void InvalidateUser(Guid tenant, ActorReference actor) => cache.InvalidatePrincipal(tenant, actor);

    public static void RequireTenant(TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (!tenant.IsActive || tenant.Scope != TenantScope.Tenant)
            throw new InvalidOperationException("Effective values require an active tenant context.");
    }
}
