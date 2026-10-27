using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Features;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

public sealed partial class G02AC003Tests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_P02_PrecedenceAndInvalidationKeepTenantValuesSeparate_Integration()
    {
        await using var database = await SettingsDatabase.CreateAsync();
        var settings = database.Settings;
        var a = await database.EnterAsync(database.TenantA);
        var b = await database.EnterAsync(database.TenantB);
        var other = await database.EnterAsync(database.TenantA, "user-b");
        var host = await database.HostAsync();
        var features = new FeatureService(settings);

        foreach (var definition in new ScopedValueDefinition[] { TestDefinitions.Title, TestDefinitions.Preview })
        {
            var flag = definition is FeatureDefinition;
            var globalValue = flag ? "true" : "global";
            var globalReplacement = flag ? "false" : "global replaced";
            var tenantValue = flag ? "false" : "tenant";
            var tenantReplacement = flag ? "true" : "tenant replaced";
            var userValue = flag ? "true" : "";
            await Expect(a, ValueSource.Default, definition.DefaultValue!);
            await Expect(b, ValueSource.Default, definition.DefaultValue!);
            var globalVersion = await settings.SetAsync(host, definition, ValueSource.Global, globalValue, cancellationToken: Cancellation);
            await Expect(a, ValueSource.Global, globalValue);
            var bGlobal = await Expect(b, ValueSource.Global, globalValue);
            var tenantVersion = await settings.SetAsync(a, definition, ValueSource.Tenant, tenantValue, cancellationToken: Cancellation);
            await Expect(a, ValueSource.Tenant, tenantValue);
            await Expect(other, ValueSource.Tenant, tenantValue);
            Assert.Same(bGlobal, await Expect(b, ValueSource.Global, globalValue));
            var userVersion = await settings.SetAsync(a, definition, ValueSource.User, userValue, cancellationToken: Cancellation);
            var aUser = await Expect(a, ValueSource.User, userValue);
            await Expect(other, ValueSource.Tenant, tenantValue);
            Assert.Same(bGlobal, await Expect(b, ValueSource.Global, globalValue));

            globalVersion = await settings.SetAsync(host, definition, ValueSource.Global, globalReplacement, globalVersion, Cancellation);
            Assert.NotSame(aUser, await Expect(a, ValueSource.User, userValue));
            await Expect(other, ValueSource.Tenant, tenantValue);
            await Expect(b, ValueSource.Global, globalReplacement);
            tenantVersion = await settings.SetAsync(a, definition, ValueSource.Tenant, tenantReplacement, tenantVersion, Cancellation);
            await Expect(a, ValueSource.User, userValue);
            await Expect(other, ValueSource.Tenant, tenantReplacement);
            await Expect(b, ValueSource.Global, globalReplacement);

            userVersion = await settings.SetAsync(a, definition, ValueSource.User, globalValue, userVersion, Cancellation);
            await Expect(a, ValueSource.User, globalValue);
            await Expect(other, ValueSource.Tenant, tenantReplacement);
            await Expect(b, ValueSource.Global, globalReplacement);

            await settings.ResetAsync(a, definition, ValueSource.User, userVersion, Cancellation);
            await Expect(a, ValueSource.Tenant, tenantReplacement);
            await settings.ResetAsync(a, definition, ValueSource.Tenant, tenantVersion, Cancellation);
            await Expect(a, ValueSource.Global, globalReplacement);
            await settings.ResetAsync(host, definition, ValueSource.Global, globalVersion, Cancellation);
            await Expect(a, ValueSource.Default, definition.DefaultValue!);
            await Expect(b, ValueSource.Default, definition.DefaultValue!);

            async Task<ResolvedValue> Expect(TenantContext context, ValueSource source, string value)
            {
                var resolved = await settings.ResolveAsync(context, definition, Cancellation);
                Assert.Equal(source, resolved.Source);
                Assert.Equal(value, resolved.DisplayValue);
                if (flag) Assert.Equal(value == "true", resolved.Get<bool>());
                else Assert.Equal(value, resolved.Get<string>());
                Assert.Equal(source == ValueSource.Default, resolved.Version is null);
                return resolved;
            }
        }
        await features.SetAsync(a, TestDefinitions.Preview, ValueSource.Tenant, true, cancellationToken: Cancellation);
        Assert.True((await features.ResolveAsync(a, TestDefinitions.Preview, Cancellation)).Get<bool>());
        Assert.False(await features.IsEnabledAsync(a, TestDefinitions.Preview, Cancellation));
        Assert.Single(await database.InventoryAsync());
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task Invalid_values_stale_versions_and_rollbacks_preserve_the_committed_override_and_cache()
    {
        await using var database = await SettingsDatabase.CreateAsync();
        var a = await database.EnterAsync(database.TenantA);
        var settings = database.Settings;
        var definition = TestDefinitions.PageSize;
        var version = await settings.SetAsync(a, definition, ValueSource.Tenant, "30", cancellationToken: Cancellation);
        var cached = await settings.ResolveAsync(a, definition, Cancellation);
        Assert.Equal(30L, cached.Get<long>());
        Assert.Equal(SettingsTimestamp.Normalize(database.Clock.GetUtcNow()), cached.UpdatedAt);
        Assert.Equal(0, cached.UpdatedAt!.Value.Ticks % 10);
        foreach (var invalid in new[] { "0", "101", "01", "1.0", "9223372036854775808" })
            await Assert.ThrowsAsync<ValueValidationException>(() => settings.SetAsync(a, definition, ValueSource.Tenant, invalid, version, Cancellation));
        await Assert.ThrowsAsync<ValueConflictException>(() => settings.SetAsync(a, definition, ValueSource.Tenant, "40", cancellationToken: Cancellation));
        await Assert.ThrowsAsync<ValueConflictException>(() => settings.SetAsync(a, definition, ValueSource.Tenant, "40", Guid.NewGuid(), Cancellation));
        await Assert.ThrowsAsync<ValueConflictException>(() => settings.ResetAsync(a, definition, ValueSource.Tenant, Guid.NewGuid(), Cancellation));
        Assert.Same(cached, await settings.ResolveAsync(a, definition, Cancellation));

        await using (var work = await EfCoreUnitOfWork.BeginAsync(database.Module, a, cancellationToken: Cancellation))
        {
            var row = await ((SettingsContext)work.Context).TenantValues.SingleAsync(Cancellation);
            row.Value = "90";
            await work.FlushAsync(Cancellation);
            await work.RollbackAsync(Cancellation);
        }
        Assert.Equal(version, (await settings.ReadOverrideAsync(a, definition, ValueSource.Tenant, Cancellation))!.Version);
        Assert.Same(cached, await settings.ResolveAsync(a, definition, Cancellation));
        database.Clock.Advance(TimeSpan.FromTicks(23));
        var updatedVersion = await settings.SetAsync(a, definition, ValueSource.Tenant, "40", version, Cancellation);
        Assert.NotEqual(version, updatedVersion);
        var updated = await settings.ResolveAsync(a, definition, Cancellation);
        Assert.Equal(40L, updated.Get<long>());
        Assert.Equal(SettingsTimestamp.Normalize(database.Clock.GetUtcNow()), updated.UpdatedAt);
        Assert.Equal(updated.UpdatedAt, (await settings.ReadOverrideAsync(a, definition, ValueSource.Tenant, Cancellation))!.UpdatedAt);
        await Assert.ThrowsAsync<ValueConflictException>(() => settings.ResetAsync(a, definition, ValueSource.Tenant, version, Cancellation));
        Assert.Same(updated, await settings.ResolveAsync(a, definition, Cancellation));
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task Independent_caches_observe_remote_changes_within_the_bound_and_restart_reads_committed_state()
    {
        await using var database = await SettingsDatabase.CreateAsync();
        var a = await database.EnterAsync(database.TenantA);
        var host = await database.HostAsync();
        var definition = TestDefinitions.Title;
        var version = await database.Settings.SetAsync(host, definition, ValueSource.Global, "first", cancellationToken: Cancellation);
        var cached = await database.Settings.ResolveAsync(a, definition, Cancellation);
        await using var remoteServices = database.NewServices();
        var remote = remoteServices.GetRequiredService<SettingsService>();
        await remote.SetAsync(host, definition, ValueSource.Global, "second", version, Cancellation);
        Assert.Same(cached, await database.Settings.ResolveAsync(a, definition, Cancellation));
        Assert.Equal("second", (await remote.ResolveAsync(a, definition, Cancellation)).Get<string>());
        database.Clock.Advance(TestDefinitions.Staleness - TimeSpan.FromTicks(1));
        Assert.Equal("first", (await database.Settings.ResolveAsync(a, definition, Cancellation)).Get<string>());
        database.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal("second", (await database.Settings.ResolveAsync(a, definition, Cancellation)).Get<string>());
        await using var restarted = database.NewServices();
        Assert.Equal("second", (await restarted.GetRequiredService<SettingsService>().ResolveAsync(a, definition, Cancellation)).Get<string>());
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task A_database_outage_does_not_cache_a_default_and_recovery_reads_the_store()
    {
        await using var database = await SettingsDatabase.CreateAsync();
        var a = await database.EnterAsync(database.TenantA);
        var definition = TestDefinitions.Title;
        await database.Settings.SetAsync(a, definition, ValueSource.Tenant, "committed", cancellationToken: Cancellation);
        await database.Settings.ResolveAsync(a, definition, Cancellation);
        database.Clock.Advance(TestDefinitions.Staleness);
        await database.ExecuteAsync("ALTER TABLE " + database.Table("TenantValues") + " RENAME TO \"UnavailableValues\"");
        try
        {
            var exception = await Assert.ThrowsAsync<ValueUnavailableException>(() => database.Settings.ResolveAsync(a, definition, Cancellation));
            Assert.Null(exception.InnerException);
        }
        finally
        {
            await database.ExecuteAsync("ALTER TABLE " + database.Table("UnavailableValues") + " RENAME TO \"TenantValues\"");
        }
        Assert.Equal("committed", (await database.Settings.ResolveAsync(a, definition, Cancellation)).Get<string>());
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task Invalid_persisted_values_are_refused_after_expiry_instead_of_becoming_defaults()
    {
        await using var database = await SettingsDatabase.CreateAsync();
        var a = await database.EnterAsync(database.TenantA);
        var version = await database.Settings.SetAsync(a, TestDefinitions.PageSize, ValueSource.Tenant, "30", cancellationToken: Cancellation);
        await database.Settings.ResolveAsync(a, TestDefinitions.PageSize, Cancellation);
        // Independent corruption of this fixture's schema exercises validation on the actual provider read path.
        await database.ExecuteAsync("UPDATE " + database.Table("TenantValues") + " SET \"Value\" = @value",
            new Npgsql.NpgsqlParameter("value", "invalid integer"));
        database.Clock.Advance(TestDefinitions.Staleness);
        await Assert.ThrowsAsync<ValueUnavailableException>(() => database.Settings.ResolveAsync(a, TestDefinitions.PageSize, Cancellation));
        await database.Settings.SetAsync(a, TestDefinitions.PageSize, ValueSource.Tenant, "40", version, Cancellation);
        Assert.Equal(40L, (await database.Settings.ResolveAsync(a, TestDefinitions.PageSize, Cancellation)).Get<long>());
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task Host_tenant_user_and_ended_context_boundaries_remain_enforced()
    {
        await using var database = await SettingsDatabase.CreateAsync();
        var a = await database.EnterAsync(database.TenantA);
        var host = await database.HostAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Settings.SetAsync(a, TestDefinitions.Title, ValueSource.Global, "refused", cancellationToken: Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Settings.SetAsync(host, TestDefinitions.Title, ValueSource.Tenant, "refused", cancellationToken: Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Settings.SetAsync(a, TestDefinitions.Title, ValueSource.Default, "refused", cancellationToken: Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Settings.ResolveAsync(host, TestDefinitions.Title, Cancellation));
        var (directoryScope, directoryHost) = await Units.EnterHostAsync(database.Services, "host-1", TenantDirectoryAdministration.OperationId);
        await using (directoryScope)
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.Settings.SetAsync(directoryHost, TestDefinitions.Title,
                ValueSource.Global, "refused", cancellationToken: Cancellation));
        await using var serviceScope = database.Services.CreateAsyncScope();
        var serviceExecution = serviceScope.ServiceProvider.GetRequiredService<TenantExecution>();
        var serviceDecision = await serviceExecution.EstablishAsync(TenantResolutionRequest.ForMember(TestIdentity.Service("worker"),
            [Candidates.Header(database.TenantA)]), Cancellation);
        Assert.True(serviceDecision.Succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Settings.SetAsync(serviceDecision.Context!, TestDefinitions.Title, ValueSource.User,
            "refused", cancellationToken: Cancellation));
        await database.Settings.SetAsync(a, TestDefinitions.Title, ValueSource.User, "user only", cancellationToken: Cancellation);
        Assert.Equal("default", (await database.Settings.ResolveAsync(serviceDecision.Context!, TestDefinitions.Title, Cancellation)).Get<string>());

        var (endedScope, ended) = await Units.EnterAsync(database.Services, "user-a", database.TenantA);
        await database.Settings.ResolveAsync(ended, TestDefinitions.Title, Cancellation);
        await endedScope.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Settings.ResolveAsync(ended, TestDefinitions.Title, Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Settings.SetAsync(ended, TestDefinitions.Title, ValueSource.Tenant, "refused", cancellationToken: Cancellation));

        using var model = database.Module.CreateContext();
        foreach (var entity in model.Model.GetEntityTypes())
        {
            Assert.True(typeof(ITenantScoped).IsAssignableFrom(entity.ClrType) || typeof(IHostScoped).IsAssignableFrom(entity.ClrType));
            if (!typeof(ITenantScoped).IsAssignableFrom(entity.ClrType)) continue;
            Assert.All(entity.GetKeys(), key => Assert.Contains(key.Properties, property => property.Name == "TenantId"));
            Assert.All(entity.GetIndexes(), index => Assert.Contains(index.Properties, property => property.Name == "TenantId"));
        }
    }
}
