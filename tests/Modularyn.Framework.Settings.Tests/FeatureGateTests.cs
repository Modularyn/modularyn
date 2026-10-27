using Modularyn.Framework.Features;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Settings.Tests;

[Collection(SettingsPostgreSqlCollection.Name)]
public sealed class FeatureGateTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task An_operation_is_enabled_only_while_both_the_flag_and_the_gate_allow_it()
    {
        await using var database = await SettingsDatabase.CreateAsync();
        var a = await database.EnterAsync(database.TenantA);
        var gate = new CountingGate { Allows = true };
        var features = new FeatureService(database.Settings, gate);

        // The flag is off by default, so an allowing gate cannot enable the operation and is not asked.
        Assert.False(await features.IsEnabledAsync(a, TestDefinitions.Preview, Cancellation));
        Assert.Empty(gate.Seen);

        var version = await database.Settings.SetAsync(a, TestDefinitions.Preview, ValueSource.Tenant, "true",
            cancellationToken: Cancellation);
        Assert.True(await features.IsEnabledAsync(a, TestDefinitions.Preview, Cancellation));
        // An entitlement or permission withdrawn while the flag stays on refuses at once: the gate's outcome is not cached.
        gate.Allows = false;
        Assert.False(await features.IsEnabledAsync(a, TestDefinitions.Preview, Cancellation));
        gate.Allows = true;
        Assert.True(await features.IsEnabledAsync(a, TestDefinitions.Preview, Cancellation));
        Assert.Equal(3, gate.Seen.Count);
        Assert.All(gate.Seen, seen =>
        {
            Assert.Same(a, seen.Context);
            Assert.Same(TestDefinitions.Preview, seen.Feature);
        });

        // Turning the flag off refuses although the gate still allows.
        await database.Settings.SetAsync(a, TestDefinitions.Preview, ValueSource.Tenant, "false", version, Cancellation);
        Assert.False(await features.IsEnabledAsync(a, TestDefinitions.Preview, Cancellation));
        Assert.Equal(3, gate.Seen.Count);
    }

    private sealed class CountingGate : IFeatureGate
    {
        internal bool Allows { get; set; }
        internal List<(TenantContext Context, FeatureDefinition Feature)> Seen { get; } = [];

        public ValueTask<bool> AllowsAsync(TenantContext context, FeatureDefinition feature, CancellationToken cancellationToken)
        {
            Seen.Add((context, feature));
            return ValueTask.FromResult(Allows);
        }
    }
}
