using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Caching;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

// SEC-05's gate on settings and feature management, with a stand-in for the evaluator; the authorization tests repeat it
// with the real one. And the report of a fill that overruns its staleness bound.
[Collection(SettingsPostgreSqlCollection.Name)]
public sealed class ManagementTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task TM_G02_024_Tenant_and_user_overrides_change_only_with_their_management_permission()
    {
        await using var database = await SettingsDatabase.CreateAsync();
        var a = await database.EnterAsync(database.TenantA);
        var principal = SettingsDatabase.PrincipalOf(a);
        database.Permissions.Revoke("user-a", database.TenantA, SettingsModule.TenantOverridesPermission);
        database.Permissions.Revoke("user-a", database.TenantA, SettingsModule.UserOverridesPermission);
        var before = database.Permissions.Decisions;

        // Without the permission no override is written or reset, at tenant or user scope; the member still reads its effective value.
        var refused = await Assert.ThrowsAsync<ValueAccessDeniedException>(() =>
            database.Settings.SetAsync(a, principal, TestDefinitions.Title, ValueSource.Tenant, "tenant", cancellationToken: Cancellation));
        Assert.Equal(AuthorizationCodes.NotHeld, refused.Code);
        Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<ValueAccessDeniedException>(() =>
            database.Settings.SetAsync(a, principal, TestDefinitions.Title, ValueSource.User, "user", cancellationToken: Cancellation))).Code);
        Assert.Equal(AuthorizationCodes.Unauthenticated, (await Assert.ThrowsAsync<ValueAccessDeniedException>(() =>
            database.Settings.SetAsync(a, null, TestDefinitions.Title, ValueSource.Tenant, "tenant", cancellationToken: Cancellation))).Code);
        Assert.Equal(3, database.Permissions.Decisions - before);
        Assert.Empty(await database.InventoryAsync());
        Assert.Equal(ValueSource.Default, (await database.Settings.ResolveAsync(a, TestDefinitions.Title, Cancellation)).Source);

        // Granted, the tenant write is made; the user write still needs its own permission.
        database.Permissions.Grant("user-a", database.TenantA, SettingsModule.TenantOverridesPermission);
        var version = await database.Settings.SetAsync(a, principal, TestDefinitions.Title, ValueSource.Tenant, "tenant", cancellationToken: Cancellation);
        Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<ValueAccessDeniedException>(() =>
            database.Settings.SetAsync(a, principal, TestDefinitions.Title, ValueSource.User, "user", cancellationToken: Cancellation))).Code);
        // A reset needs the permission too; revoked, it is refused and the override stays.
        database.Permissions.Revoke("user-a", database.TenantA, SettingsModule.TenantOverridesPermission);
        Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<ValueAccessDeniedException>(() =>
            database.Settings.ResetAsync(a, principal, TestDefinitions.Title, ValueSource.Tenant, version, Cancellation))).Code);
        Assert.Equal(ValueSource.Tenant, (await database.Settings.ResolveAsync(a, TestDefinitions.Title, Cancellation)).Source);

        // A user override is the acting user's own, so none is written under a delegation, which acts for someone else.
        database.Permissions.Grant("user-a", database.TenantA, SettingsModule.UserOverridesPermission);
        var delegated = ExecutionPrincipal.ForDelegation(new PrincipalReference("user-a", IdentityKind.User), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new PrincipalReference("user-b", IdentityKind.User), new DelegatedAuthority(Guid.NewGuid(), [SettingsModule.UserOverridesPermission], "Preferences",
                database.TenantA, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10)));
        Assert.Equal(AuthorizationCodes.OutsideCeiling, (await Assert.ThrowsAsync<ValueAccessDeniedException>(() =>
            database.Settings.SetAsync(a, delegated, TestDefinitions.Title, ValueSource.User, "user", cancellationToken: Cancellation))).Code);

        // Feature management goes through the same gate.
        var features = new Features.FeatureService(database.Settings);
        database.Permissions.Revoke("user-a", database.TenantA, SettingsModule.TenantOverridesPermission);
        Assert.Equal(AuthorizationCodes.NotHeld, (await Assert.ThrowsAsync<ValueAccessDeniedException>(() =>
            features.SetAsync(a, principal, TestDefinitions.Preview, ValueSource.Tenant, true, cancellationToken: Cancellation))).Code);

        // With no evaluator registered, tenant and user management is refused; global management still needs only its host grant.
        var unguarded = new SettingsService(database.Module, database.Definitions, database.Services.GetRequiredService<ScopedValueCache>(),
            database.Services.GetRequiredService<TenantWorkRunner>(), database.Clock);
        Assert.Equal(AuthorizationCodes.Unavailable, (await Assert.ThrowsAsync<ValueAccessDeniedException>(() =>
            unguarded.SetAsync(a, principal, TestDefinitions.PageSize, ValueSource.Tenant, "30", cancellationToken: Cancellation))).Code);
        var host = await database.HostAsync();
        Assert.NotEqual(Guid.Empty, await unguarded.SetAsync(host, null, TestDefinitions.PageSize, ValueSource.Global, "30", cancellationToken: Cancellation));
    }

    [Fact]
    public async Task TM_G02_034_A_fill_that_overruns_its_staleness_bound_is_reported_unavailable()
    {
        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        var tenant = directory.Add("Tenant");
        seams.Membership.Add("user", tenant);
        var slow = new SlowMembership(seams.Membership);
        await using var services = UnitServices.Build(directory, seams, collection =>
        {
            collection.AddSingleton<ITenantMembership>(slow);
            collection.AddSingleton<ScopedValueCache>();
        });
        var quick = new SettingDefinition(TestDefinitions.Module, "Quick", TestDefinitions.Version, ValueKind.Text, "default", TimeSpan.FromMilliseconds(200));
        var registry = new ValueDefinitionRegistry([new ModuleManifest(TestDefinitions.Module, TestDefinitions.Version, definitions: [quick.Declaration])], [quick]);
        // No connection is opened: the fill is still revalidating its tenant when its staleness bound ends it.
        var settings = new SettingsService(SettingsModule.Create(new PostgreSqlPersistenceOptions("Host=localhost;Database=unused")), registry,
            services.GetRequiredService<ScopedValueCache>(), services.GetRequiredService<TenantWorkRunner>(), TimeProvider.System);
        var (scope, context) = await Units.EnterAsync(services, "user", tenant);
        await using var held = scope;
        slow.Hanging = true;
        // Reported as the value being unavailable, exactly, rather than as a cancellation the caller never asked for.
        await Assert.ThrowsAsync<ValueUnavailableException>(() => settings.ResolveAsync(context, quick, Cancellation));
        Assert.Equal(1, slow.Calls);
        Assert.False(Cancellation.IsCancellationRequested);
    }

    /// <summary>Membership that, while hanging, answers only when the caller's token cancels the call.</summary>
    private sealed class SlowMembership(ITenantMembership inner) : ITenantMembership
    {
        internal bool Hanging { get; set; }
        internal int Calls;

        public async ValueTask<bool> IsMemberAsync(ActorReference actor, Guid tenantId, CancellationToken cancellationToken)
        {
            if (Hanging) Interlocked.Increment(ref Calls);
            if (Hanging) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await inner.IsMemberAsync(actor, tenantId, cancellationToken);
        }
    }
}
