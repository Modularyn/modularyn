using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Tenancy;

/// <summary>
/// One cache entry's declared identity: the owning module, its schema version, the entry name and its parts, whether it
/// varies by principal, and how stale it may become. An authorization-sensitive entry must vary by principal.
/// </summary>
public sealed record TenantCacheEntry
{
    public static readonly TimeSpan MaxAllowedStaleness = TimeSpan.FromDays(1);

    public TenantCacheEntry(string module, string schemaVersion, string name, TimeSpan maxStaleness, bool principalScoped,
        params string[] parts)
    {
        Module = Name(module, nameof(module));
        SchemaVersion = Name(schemaVersion, nameof(schemaVersion));
        EntryName = Name(name, nameof(name));
        if (maxStaleness <= TimeSpan.Zero || maxStaleness > MaxAllowedStaleness)
            throw new ArgumentOutOfRangeException(nameof(maxStaleness), "Declare a staleness limit greater than zero and at most one day.");
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Any(part => part is null)) throw new ArgumentException("Key parts cannot be null.", nameof(parts));
        MaxStaleness = maxStaleness;
        PrincipalScoped = principalScoped;
        Parts = Array.AsReadOnly(parts.ToArray());
    }

    public string Module { get; }
    public string SchemaVersion { get; }
    public string EntryName { get; }
    public TimeSpan MaxStaleness { get; }
    public bool PrincipalScoped { get; }
    public IReadOnlyList<string> Parts { get; }

    private static string Name(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > 200 || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Use at most 200 characters with no surrounding whitespace or control characters.", parameter);
        return value;
    }
}

/// <summary>
/// A cache whose keys are built only from a validated context and a declared entry: tenant (or host), module, schema
/// version, principal for principal-scoped entries, entry name and parts, each length-prefixed so no two distinct
/// identities share a key. Entries expire at their declared staleness limit; invalidating a tenant or a principal makes
/// their earlier entries unreachable at once. Concurrent misses for one key share a single factory call, whose failure
/// is not cached. The store is this process's memory: during an outage of a dependency the factory fails and nothing is
/// cached, and it coordinates nothing across processes.
/// </summary>
public sealed class TenantCache : IDisposable
{
    private const string KeyFormat = "modularyn.tenant-cache:1";
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    private readonly ConcurrentDictionary<string, long> generations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Flight> flights = new(StringComparer.Ordinal);
    private readonly TimeProvider time;

    public TenantCache(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    /// <summary>The key for an entry in a context; exposed so evidence can record it. The context must be active.</summary>
    public string KeyFor(TenantContext context, TenantCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entry);
        if (!context.IsActive) throw new InvalidOperationException("A cache entry is read or built only while its context is active.");
        var scope = ScopeKey(context);
        var key = new StringBuilder(KeyFormat);
        Append(key, "scope", scope);
        Append(key, "scope-generation", Generation(scope));
        Append(key, "module", entry.Module);
        Append(key, "schema", entry.SchemaVersion);
        if (entry.PrincipalScoped)
        {
            var principal = PrincipalKey(scope, context.Actor);
            Append(key, "principal", principal);
            Append(key, "principal-generation", Generation(principal));
        }
        Append(key, "entry", entry.EntryName);
        Append(key, "parts", entry.Parts.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var part in entry.Parts) Append(key, "part", part);
        return key.ToString();
    }

    public async Task<T> GetOrCreateAsync<T>(TenantContext context, TenantCacheEntry entry, Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        var key = KeyFor(context, entry);
        if (cache.TryGetValue(key, out Stored? hit))
        {
            if (time.GetElapsedTime(hit!.StartedAt) < entry.MaxStaleness) return Unwrap<T>(hit);
            cache.Remove(key);
        }
        var candidate = new Flight();
        candidate.Value = new(() => ProduceAsync(key, candidate, entry, factory), LazyThreadSafetyMode.ExecutionAndPublication);
        var flight = flights.GetOrAdd(key, candidate);
        var result = await flight.Value!.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!context.IsActive) throw new InvalidOperationException("The cache context ended while the value was read.");
        if (time.GetElapsedTime(result.StartedAt) >= entry.MaxStaleness)
            throw new InvalidOperationException("The cache factory exceeded the entry's declared staleness bound.");
        return Unwrap<T>(result);
    }

    /// <summary>Makes every entry of the tenant unreachable, for grant, capability and setting changes.</summary>
    public void InvalidateTenant(Guid tenantId) => Bump("tenant/" + TenantIdentifier.Format(tenantId));

    /// <summary>Makes the principal's entries in the tenant unreachable, for a change to that principal's grants or session.</summary>
    public void InvalidatePrincipal(Guid tenantId, ActorReference actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        Bump(PrincipalKey("tenant/" + TenantIdentifier.Format(tenantId), actor));
    }

    public void Dispose() => cache.Dispose();

    private async Task<Stored> ProduceAsync<T>(string key, Flight flight, TenantCacheEntry entry, Func<CancellationToken, Task<T>> factory)
    {
        try
        {
            var startedAt = time.GetTimestamp();
            // The shared factory runs independently of any one waiter's cancellation.
            var value = await factory(CancellationToken.None).ConfigureAwait(false);
            var stored = new Stored(typeof(T), value, startedAt);
            // Time spent obtaining the value counts toward its staleness, too.
            var remaining = entry.MaxStaleness - time.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero)
                cache.Set(key, stored, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = remaining });
            return stored;
        }
        finally { flights.TryRemove(new KeyValuePair<string, Flight>(key, flight)); }
    }

    private static T Unwrap<T>(Stored stored) => stored.Type == typeof(T)
        ? (T)stored.Value!
        : throw new InvalidOperationException("A cache entry was declared with another value type.");

    private static string ScopeKey(TenantContext context) =>
        context.Scope == TenantScope.Tenant ? "tenant/" + TenantIdentifier.Format(context.TenantId!.Value) : "host";

    private static string PrincipalKey(string scope, ActorReference actor) =>
        scope + "/" + (actor.Kind == ActorKind.User ? "user" : "service") + "/" + actor.Subject.Length.ToString(CultureInfo.InvariantCulture) + ":" + actor.Subject;

    private string Generation(string key) => generations.GetValueOrDefault(key).ToString(CultureInfo.InvariantCulture);

    private void Bump(string key) => generations.AddOrUpdate(key, 1, (_, generation) => generation + 1);

    private static void Append(StringBuilder key, string name, string value) =>
        key.Append('|').Append(name).Append('=').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private sealed record Stored(Type Type, object? Value, long StartedAt);

    private sealed class Flight
    {
        internal Lazy<Task<Stored>>? Value { get; set; }
    }
}
