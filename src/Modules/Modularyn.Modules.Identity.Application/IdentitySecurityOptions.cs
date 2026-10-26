using Microsoft.Extensions.Options;
using Modularyn.Framework.Configuration;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

/// <summary>
/// The identity module's deployment options, bound strictly from <c>Modularyn:Identity</c> and validated before startup.
/// Every value has a safe default and a bounded range; a deployment can tighten a limit but never remove one.
/// </summary>
public sealed class IdentitySecurityOptions
{
    public PasswordPolicyOptions Passwords { get; set; } = new();
    public SessionPolicyOptions Sessions { get; set; } = new();
    public ThrottlePolicyOptions Throttling { get; set; } = new();
    public DelegationPolicyOptions Delegation { get; set; } = new();
    public CredentialKeyOptions CredentialKeys { get; set; } = new();
}

public sealed class PasswordPolicyOptions
{
    /// <summary>From 15 to 128 characters; NIST SP 800-63B recommends at least 15 for a password used as a single factor.</summary>
    public int MinimumLength { get; set; } = 15;

    /// <summary>PBKDF2-HMAC-SHA512 iterations for new verifiers, from 210,000.</summary>
    public int HashIterations { get; set; } = PasswordPolicy.MinimumHashIterations;
}

public sealed class SessionPolicyOptions
{
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(8);
}

public sealed class ThrottlePolicyOptions
{
    public int SourceThreshold { get; set; } = 20;
    public TimeSpan SourceWindow { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SourceBlock { get; set; } = TimeSpan.FromMinutes(15);
    public int AccountFreeAttempts { get; set; } = 5;
    public TimeSpan AccountBaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan AccountMaximumDelay { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan AccountResetAfter { get; set; } = TimeSpan.FromDays(1);
}

public sealed class DelegationPolicyOptions
{
    public TimeSpan MaximumLifetime { get; set; } = TimeSpan.FromHours(8);
}

/// <summary>
/// Versioned keys for credential protection, each a reference to a 32-byte secret in the deployment's secret store. New
/// seeds and passkey tags use <see cref="CurrentVersion"/>; earlier versions stay listed for as long as stored credentials
/// use them. With no keys, seed and passkey operations are refused.
/// </summary>
public sealed class CredentialKeyOptions
{
    public int CurrentVersion { get; set; }
    public List<CredentialKeyReference> Keys { get; set; } = [];
}

public sealed class CredentialKeyReference
{
    public int Version { get; set; }
    public SecretReference? Secret { get; set; }
}

/// <summary>The validated policies, built once from the options.</summary>
public sealed record IdentityPolicies(PasswordPolicy Passwords, SessionPolicy Sessions, ThrottlePolicy Throttling, DelegationPolicy Delegation)
{
    public static IdentityPolicies From(IdentitySecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var throttle = options.Throttling;
        return new(new PasswordPolicy(options.Passwords.MinimumLength, options.Passwords.HashIterations),
            new SessionPolicy(options.Sessions.IdleTimeout, options.Sessions.AbsoluteLifetime),
            new ThrottlePolicy(throttle.SourceThreshold, throttle.SourceWindow, throttle.SourceBlock, throttle.AccountFreeAttempts,
                throttle.AccountBaseDelay, throttle.AccountMaximumDelay, throttle.AccountResetAfter),
            new DelegationPolicy(options.Delegation.MaximumLifetime));
    }
}

internal sealed class IdentitySecurityOptionsValidation(string sectionPath) : IValidateOptions<IdentitySecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentitySecurityOptions options)
    {
        var failures = new List<string>();
        Check(failures, sectionPath + ":Passwords", () => new PasswordPolicy(options.Passwords.MinimumLength, options.Passwords.HashIterations));
        Check(failures, sectionPath + ":Sessions", () => new SessionPolicy(options.Sessions.IdleTimeout, options.Sessions.AbsoluteLifetime));
        var throttle = options.Throttling;
        Check(failures, sectionPath + ":Throttling", () => new ThrottlePolicy(throttle.SourceThreshold, throttle.SourceWindow, throttle.SourceBlock,
            throttle.AccountFreeAttempts, throttle.AccountBaseDelay, throttle.AccountMaximumDelay, throttle.AccountResetAfter));
        Check(failures, sectionPath + ":Delegation", () => new DelegationPolicy(options.Delegation.MaximumLifetime));

        var keys = options.CredentialKeys;
        var at = sectionPath + ":CredentialKeys";
        for (var index = 0; index < keys.Keys.Count; index++)
        {
            var key = keys.Keys[index];
            if (key is null || key.Version < 1) failures.Add($"'{at}:Keys:{index}:Version' must be a positive key version.");
            if (key?.Secret is null) failures.Add($"'{at}:Keys:{index}:Secret' must reference the key in the secret store.");
        }
        var versions = keys.Keys.Where(key => key is not null).Select(key => key.Version).ToArray();
        if (versions.Distinct().Count() != versions.Length) failures.Add($"'{at}:Keys' lists a key version more than once.");
        if (keys.Keys.Count == 0 && keys.CurrentVersion != 0)
            failures.Add($"'{at}:CurrentVersion' names a key version, but no keys are listed.");
        if (keys.Keys.Count != 0 && !versions.Contains(keys.CurrentVersion))
            failures.Add($"'{at}:CurrentVersion' must be one of the listed key versions.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(List<string> failures, string path, Action create)
    {
        try { create(); }
        catch (ArgumentOutOfRangeException exception)
        {
            // The domain messages state the permitted range and never the configured value.
            failures.Add($"'{path}': {exception.Message.Split(" (Parameter", 2)[0]}");
        }
    }
}
