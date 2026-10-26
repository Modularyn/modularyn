using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

public sealed class OptionsTests
{
    private static async Task<OptionsValidationException> RefusedAsync(Dictionary<string, string?> settings, bool stores = true)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddModularynIdentity(builder.Configuration);
        if (stores)
        {
            var fakes = new InMemoryIdentityStores();
            builder.Services.AddSingleton<IAccountDirectory>(fakes).AddSingleton<ISessionStore>(fakes).AddSingleton<IVerificationLedger>(fakes)
                .AddSingleton<IDelegationStore>(fakes).AddSingleton<IAuthenticatorSeedStore>(fakes).AddSingleton<IRecoveryCodeStore>(fakes)
                .AddSingleton<IPasskeyCredentialStore>(fakes).AddSingleton<ITenantMembershipStore>(fakes);
        }
        using var host = builder.Build();
        return await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact(DisplayName = "TM-G02-048 identity limits outside their declared ranges refuse startup, naming the key and never the value")]
    public async Task TM_G02_048_Identity_limits_outside_their_ranges_refuse_startup()
    {
        (string Key, string Value)[] cases =
        [
            ("Modularyn:Identity:Passwords:MinimumLength", "9"),
            ("Modularyn:Identity:Passwords:HashIterations", "100000"),
            ("Modularyn:Identity:Sessions:IdleTimeout", "00:00:10"),
            ("Modularyn:Identity:Sessions:AbsoluteLifetime", "3.00:00:00"),
            ("Modularyn:Identity:Throttling:SourceThreshold", "2"),
            ("Modularyn:Identity:Throttling:AccountMaximumDelay", "05:00:00"),
            ("Modularyn:Identity:Delegation:MaximumLifetime", "30.00:00:00"),
            ("Modularyn:Identity:CredentialKeys:CurrentVersion", "3"),
            // Durations finer than the microseconds the database keeps.
            ("Modularyn:Identity:Sessions:IdleTimeout", "00:20:00.0000005"),
            ("Modularyn:Identity:Throttling:SourceWindow", "00:15:00.0000003"),
            ("Modularyn:Identity:Delegation:MaximumLifetime", "08:00:00.0000001")
        ];
        foreach (var (key, value) in cases)
        {
            var refused = await RefusedAsync(new() { [key] = value });
            var section = key[..key.LastIndexOf(':')];
            Assert.Contains(refused.Failures, failure => failure.Contains(section, StringComparison.Ordinal));
            Assert.DoesNotContain(refused.Failures, failure => failure.Contains(value, StringComparison.Ordinal));
        }
        var unknown = await RefusedAsync(new() { ["Modularyn:Identity:Sessions:Lifetime"] = "01:00:00" });
        Assert.Contains(unknown.Failures, failure => failure.Contains("Lifetime", StringComparison.Ordinal));
        var duplicate = await RefusedAsync(new()
        {
            ["Modularyn:Identity:CredentialKeys:CurrentVersion"] = "1",
            ["Modularyn:Identity:CredentialKeys:Keys:0:Version"] = "1", ["Modularyn:Identity:CredentialKeys:Keys:0:Secret"] = "secret:key-a",
            ["Modularyn:Identity:CredentialKeys:Keys:1:Version"] = "1", ["Modularyn:Identity:CredentialKeys:Keys:1:Secret"] = "secret:key-b"
        });
        Assert.Contains(duplicate.Failures, failure => failure.Contains("more than once", StringComparison.Ordinal));
        var literal = Testing.Generated.Sentinel();
        var pasted = await RefusedAsync(new()
        {
            ["Modularyn:Identity:CredentialKeys:CurrentVersion"] = "1",
            ["Modularyn:Identity:CredentialKeys:Keys:0:Version"] = "1", ["Modularyn:Identity:CredentialKeys:Keys:0:Secret"] = literal
        });
        Assert.DoesNotContain(pasted.Failures, failure => failure.Contains(literal, StringComparison.Ordinal));
        Assert.Contains(pasted.Failures, failure => failure.Contains("CredentialKeys:Keys:0:Secret", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "TM-G02-048 a host without the identity stores refuses startup")]
    public async Task TM_G02_048_A_host_without_the_identity_stores_refuses_startup()
    {
        var refused = await RefusedAsync([], stores: false);
        Assert.Contains(refused.Failures, failure => failure.Contains(nameof(ISessionStore), StringComparison.Ordinal));
    }

    [Fact(DisplayName = "TM-G02-015 the declared defaults are the documented limits")]
    public void TM_G02_015_The_declared_defaults_are_the_documented_limits()
    {
        var policies = IdentityPolicies.From(new IdentitySecurityOptions());
        Assert.Equal((15, 210_000), (policies.Passwords.MinimumLength, policies.Passwords.HashIterations));
        Assert.Equal((TimeSpan.FromMinutes(20), TimeSpan.FromHours(8)), (policies.Sessions.IdleTimeout, policies.Sessions.AbsoluteLifetime));
        Assert.Equal((20, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)), (policies.Throttling.SourceThreshold, policies.Throttling.SourceWindow, policies.Throttling.SourceBlock));
        Assert.Equal((5, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15), TimeSpan.FromDays(1)), (policies.Throttling.SubjectFreeAttempts,
            policies.Throttling.SubjectBaseDelay, policies.Throttling.SubjectMaximumDelay, policies.Throttling.SubjectResetAfter));
        Assert.Equal(TimeSpan.FromHours(8), policies.Delegation.MaximumLifetime);

        // The contract's end causes mirror the domain's end reasons one to one.
        Assert.Equal(Enum.GetNames<SessionEndReason>(), Enum.GetNames<SessionEndCause>());
        Assert.Equal(Enum.GetValues<SessionEndReason>().Select(value => (int)value), Enum.GetValues<SessionEndCause>().Select(value => (int)value));
        Assert.Equal(Enum.GetNames<AccountCreationOutcome>(), Enum.GetNames<AccountCreationStatus>());
        Assert.Equal(Enum.GetValues<PasswordChangeOutcome>().Select(value => (int)value), Enum.GetValues<PasswordChangeStatus>().Select(value => (int)value).Take(4));
        Assert.Equal(Persistence.EfCore.IdentityPersistence.ModuleId, IdentityModule.Id);
    }
}
