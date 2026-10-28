using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

/// <summary>
/// The authority cache over SEC-04's tenant cache, made unavailable on demand: while it is down every read and every
/// invalidation of it fails, as an outage of a cache would make them.
/// </summary>
internal sealed class OutageCache(IAuthorityCache inner) : IAuthorityCache
{
    internal bool Available { get; set; } = true;
    internal int Failures;

    public Task<AuthoritySnapshot> GetOrCreateAsync(TenantContext context, ActorReference subject, TimeSpan bound,
        Func<CancellationToken, Task<AuthoritySnapshot>> fill, CancellationToken cancellationToken) =>
        Available ? inner.GetOrCreateAsync(context, subject, bound, fill, cancellationToken) : Fail<Task<AuthoritySnapshot>>();

    public void InvalidateTenant(Guid tenantId)
    {
        if (!Available) Fail<bool>();
        inner.InvalidateTenant(tenantId);
    }

    public void InvalidateHostPrincipal(string operationId, ActorReference actor)
    {
        if (!Available) Fail<bool>();
        inner.InvalidateHostPrincipal(operationId, actor);
    }

    private T Fail<T>()
    {
        Interlocked.Increment(ref Failures);
        throw new InvalidOperationException("The cache is unavailable.");
    }
}

/// <summary>
/// AC-003-R02: authorization decisions recover across a cache outage and a restart. During the outage decisions follow
/// the current grants; once it ends, no entry built before a revocation authorizes anything; after a restart, decisions
/// and the bootstrap view reflect the current grants and session version.
/// </summary>
public sealed partial class G02AC003Tests
{
    private static AuthorizationHost WithOutage(AuthorizationDatabase database, TestClock clock) => AuthorizationHost.Create(database, clock, services =>
        services.Replace(ServiceDescriptor.Singleton<IAuthorityCache>(provider => new OutageCache(new TenantAuthorityCache(provider.GetRequiredService<TenantCache>())))));

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_R02_CacheOutageAndRestartKeepCurrentAuthorization_Integration()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = WithOutage(database, clock);
        var cache = (OutageCache)host.Get<IAuthorityCache>();
        var tenant = await host.TenantAsync("Tenant A");
        var admin = await host.UserAsync("admin-1", tenant);
        var user = await host.UserAsync("user-1", tenant);
        await host.OwnerAsync(admin, tenant);
        await using var owner = await host.EnterAsync(admin, tenant);
        var administration = host.Get<AuthorityAdministration>();
        await administration.GrantPermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Read, Cancellation);
        await using var unit = await host.EnterAsync(user, tenant);
        Assert.Equal((true, AuthorizationSource.Store), Outcome(await host.AuthorizeAsync(unit, Items.Read)));
        Assert.Equal((true, AuthorizationSource.Cache), Outcome(await host.AuthorizeAsync(unit, Items.Read)));
        var warmKey = host.Get<TenantCache>().KeyFor(unit.Context, TenantAuthorityCache.Entry(user.Actor, AuthorizationHost.Bound));

        // The cache goes down. The revocation commits; its invalidation fails and is logged, and the change stands.
        cache.Available = false;
        var revokedAt = clock.Now;
        Assert.True(await administration.RevokePermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Read, Cancellation));
        Assert.True(cache.Failures >= 1);

        // During the outage the decision follows the current grant: refused, read from the store.
        var during = await host.AuthorizeAsync(unit, Items.Read);
        Assert.Equal((false, AuthorizationCodes.NotHeld, AuthorizationSource.Store), (during.Permitted, during.Code, during.Source));

        // Availability returns without a restart. The entry built before the revocation is still there, unreachable by no
        // invalidation, yet it authorizes nothing: the cache is distrusted until every such entry has expired.
        cache.Available = true;
        Assert.Equal(warmKey, host.Get<TenantCache>().KeyFor(unit.Context, TenantAuthorityCache.Entry(user.Actor, AuthorizationHost.Bound)));
        clock.Advance(TimeSpan.FromSeconds(1));
        var after = await host.AuthorizeAsync(unit, Items.Read);
        Assert.Equal((false, AuthorizationCodes.NotHeld, AuthorizationSource.Store), (after.Permitted, after.Code, after.Source));
        clock.Set(revokedAt + AuthorizationHost.Bound + TimeSpan.FromSeconds(1));
        var settled = await host.AuthorizeAsync(unit, Items.Read);
        Assert.Equal((false, AuthorizationCodes.NotHeld), (settled.Permitted, settled.Code));
        Assert.Equal(AuthorizationCodes.NotHeld, (await host.AuthorizeAsync(unit, Items.Read)).Code);
        output.WriteLine($"Outage from {revokedAt:O}: the revocation committed, its invalidation failed {cache.Failures} time(s), and every read "
            + $"during and after the outage was refused; entry {warmKey} never authorized again.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_R02_CacheOutageAndRestartKeepCurrentAuthorization_Isolation()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = WithOutage(database, clock);
        var cache = (OutageCache)host.Get<IAuthorityCache>();
        var tenantA = await host.TenantAsync("Tenant A");
        var tenantB = await host.TenantAsync("Tenant B");
        var admin = await host.UserAsync("admin-1", tenantA, tenantB);
        var user = await host.UserAsync("user-1", tenantA, tenantB);
        var administration = host.Get<AuthorityAdministration>();
        var owners = new Dictionary<Guid, Unit>();
        var units = new Dictionary<Guid, Unit>();
        var (_, principal) = await host.SignInAsync(user);
        foreach (var tenant in new[] { tenantA, tenantB })
        {
            await host.OwnerAsync(admin, tenant);
            owners[tenant] = await host.EnterAsync(admin, tenant);
            await administration.GrantPermissionAsync(owners[tenant].Context, owners[tenant].Principal, user.Actor, Items.Read, Cancellation);
            units[tenant] = await host.EnterAsync(user, tenant, principal);
            Assert.True((await host.AuthorizeAsync(units[tenant], Items.Read)).Permitted);
        }
        // One principal, two tenants: their entries never share a key.
        Assert.NotEqual(host.Get<TenantCache>().KeyFor(units[tenantA].Context, TenantAuthorityCache.Entry(user.Actor, AuthorizationHost.Bound)),
            host.Get<TenantCache>().KeyFor(units[tenantB].Context, TenantAuthorityCache.Entry(user.Actor, AuthorizationHost.Bound)));

        cache.Available = false;
        await administration.RevokePermissionAsync(owners[tenantA].Context, owners[tenantA].Principal, user.Actor, Items.Read, Cancellation);
        // During the outage each tenant's decision follows its own current grants.
        Assert.Equal(AuthorizationCodes.NotHeld, (await host.AuthorizeAsync(units[tenantA], Items.Read)).Code);
        Assert.True((await host.AuthorizeAsync(units[tenantB], Items.Read)).Permitted);
        cache.Available = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AuthorizationCodes.NotHeld, (await host.AuthorizeAsync(units[tenantA], Items.Read)).Code);
        Assert.True((await host.AuthorizeAsync(units[tenantB], Items.Read)).Permitted);
        clock.Advance(AuthorizationHost.Bound);
        Assert.Equal(AuthorizationCodes.NotHeld, (await host.AuthorizeAsync(units[tenantA], Items.Read)).Code);
        var tenantBDecision = await host.AuthorizeAsync(units[tenantB], Items.Read);
        Assert.True(tenantBDecision.Permitted);
        // Visibility follows the same per-tenant decision during and after the outage.
        foreach (var tenant in new[] { tenantA, tenantB })
            await ItemWork.AddAsync(database.ItemsModule, owners[tenant].Context, user.Id.ToString("D"), "item-1");
        Assert.Equal(0, (await ItemWork.ListAsync(host.Get<IPermissionEvaluator>(), database.ItemsModule, units[tenantA].Context, principal, Cancellation)).Count);
        Assert.Equal(1, (await ItemWork.ListAsync(host.Get<IPermissionEvaluator>(), database.ItemsModule, units[tenantB].Context, principal, Cancellation)).Count);
        foreach (var held in owners.Values.Concat(units.Values)) await held.DisposeAsync();
        output.WriteLine("Through and after the outage, tenant A's revoked read was refused and tenant B's permitted.");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_R02_CacheOutageAndRestartKeepCurrentAuthorization_Recovery()
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var other = AuthorizationHost.Create(database, clock);
        var tenant = await other.TenantAsync("Tenant A");
        var admin = await other.UserAsync("admin-1", tenant);
        var user = await other.UserAsync("user-1", tenant);
        await other.OwnerAsync(admin, tenant);
        await using var owner = await other.EnterAsync(admin, tenant);
        var administration = other.Get<AuthorityAdministration>();
        await administration.GrantPermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Read, Cancellation);
        await administration.GrantPermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Update, Cancellation);
        var (_, principal) = await other.SignInAsync(user);

        await using (var first = AuthorizationHost.Create(database, clock))
        {
            await using var unit = await first.EnterAsync(user, tenant, principal);
            Assert.True((await first.AuthorizeAsync(unit, Items.Read)).Permitted);
            Assert.Equal([Items.Read, Items.Update], (await first.Get<IPermissionEvaluator>().DescribeAsync(unit.Context, unit.Principal, Cancellation))!.Permissions
                .Order(StringComparer.Ordinal));
            // Revoked in another process; this one's warm entry would still serve until B, but the host now restarts.
            await administration.RevokePermissionAsync(owner.Context, owner.Principal, user.Actor, Items.Read, Cancellation);
            Assert.True((await first.AuthorizeAsync(unit, Items.Read)).Permitted);
        }
        output.WriteLine("Restarted the host with a warm entry that predates the revocation.");
        await using var restarted = AuthorizationHost.Create(database, clock);
        await using (var unit = await restarted.EnterAsync(user, tenant, principal))
        {
            var read = await restarted.AuthorizeAsync(unit, Items.Read);
            Assert.Equal((false, AuthorizationCodes.NotHeld, AuthorizationSource.Store), (read.Permitted, read.Code, read.Source));
            var bootstrap = await restarted.Get<IPermissionEvaluator>().DescribeAsync(unit.Context, unit.Principal, Cancellation);
            Assert.Equal([Items.Update], bootstrap!.Permissions);
            Assert.True((await restarted.AuthorizeAsync(unit, Items.Update)).Permitted);
        }

        // The session version too: sessions revoked while the host restarts end every current-profile decision, and the
        // next sign-in sees the current grants.
        Assert.True(await other.Get<ISessionService>().RevokeAllAsync(IdentityAuthority.Host(await other.HostAsync(IdentityHostOperations.SessionsId)), user.Id,
            Cancellation) >= 1);
        await using var again = AuthorizationHost.Create(database, clock);
        await using (var stale = await again.EnterAsync(user, tenant, principal))
            Assert.Equal(AuthorizationCodes.PrincipalNotCurrent, (await again.AuthorizeAsync(stale, Items.Update, current: true)).Code);
        await using var current = await again.EnterAsync(user, tenant);
        Assert.True((await again.AuthorizeAsync(current, Items.Update, current: true)).Permitted);
        Assert.Equal([Items.Update], (await again.Get<IPermissionEvaluator>().DescribeAsync(current.Context, current.Principal, Cancellation))!.Permissions);
        output.WriteLine("After each restart the decisions and the bootstrap view showed only the current grants, and the revoked session was refused.");
    }
}
