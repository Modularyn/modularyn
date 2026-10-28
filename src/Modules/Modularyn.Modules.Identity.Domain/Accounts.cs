using System.Text;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Identity.Domain;

/// <summary>The security-relevant state of a local account. A change to the security stamp ends every session issued before it.</summary>
public sealed record AccountSecurityState(Guid AccountId, string UserName, string SecurityStamp, bool Disabled)
{
    public PrincipalReference Principal => new(AccountId.ToString("D"), IdentityKind.User);
}

public enum PasswordCheckOutcome
{
    Verified = 1,

    /// <summary>The account is unknown, disabled or has another password. These are deliberately indistinguishable.</summary>
    Rejected = 2
}

public sealed record PasswordCheck(PasswordCheckOutcome Outcome, AccountSecurityState? Account)
{
    public static PasswordCheck Rejected { get; } = new(PasswordCheckOutcome.Rejected, null);
}

public enum AccountCreationOutcome
{
    Created = 1,
    UserNameUnavailable = 2,
    InvalidUserName = 3,
    PasswordPolicy = 4
}

public sealed record AccountCreation(AccountCreationOutcome Outcome, Guid? AccountId);

public enum PasswordChangeOutcome
{
    Changed = 1,
    CurrentPasswordRejected = 2,
    PasswordPolicy = 3,
    AccountUnavailable = 4
}

public sealed record PasswordPolicy
{
    /// <summary>The fewest PBKDF2-HMAC-SHA512 iterations a verifier may use, after OWASP's guidance for that function.</summary>
    public const int MinimumHashIterations = 210_000;
    public const int MaximumHashIterations = 5_000_000;
    public const int MinimumAllowedLength = 15;
    public const int MaximumLength = 256;

    public PasswordPolicy(int minimumLength, int hashIterations)
    {
        if (minimumLength is < MinimumAllowedLength or > 128)
            throw new ArgumentOutOfRangeException(nameof(minimumLength), $"A password policy requires from {MinimumAllowedLength} to 128 characters.");
        if (hashIterations is < MinimumHashIterations or > MaximumHashIterations)
            throw new ArgumentOutOfRangeException(nameof(hashIterations), $"Password verifiers use from {MinimumHashIterations} to {MaximumHashIterations} iterations.");
        MinimumLength = minimumLength;
        HashIterations = hashIterations;
    }

    public int MinimumLength { get; }
    public int HashIterations { get; }

    /// <summary>Input that no policy could accept is refused before any account is read or any hash is computed.</summary>
    public static bool IsPlausible(SecretText? password) => password is { Length: > 0 and <= MaximumLength };
}

public static class UserNames
{
    public const int MaximumLength = 256;

    /// <summary>
    /// The characters a user name may contain, which account creation enforces through ASP.NET Core Identity's user
    /// validator. They are ASCII, so a name's comparison form is the same with or without the globalization libraries:
    /// in globalization-invariant mode, Unicode normalization leaves a string unchanged.
    /// </summary>
    public const string AllowedCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";

    /// <summary>The comparison form of a user name, as ASP.NET Core Identity's default normalizer produces it.</summary>
    public static string Normalize(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        return userName.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    /// <summary>Whether a typed name could belong to an account: from 1 to 256 of the allowed characters.</summary>
    public static bool IsPlausible(string? userName) =>
        !string.IsNullOrEmpty(userName) && userName.Length <= MaximumLength && userName.All(character => AllowedCharacters.Contains(character));
}

/// <summary>
/// Local accounts. The persistence adapter implements it over ASP.NET Core Identity and its EF Core store; the
/// application never sees a password verifier.
/// </summary>
public interface IAccountDirectory
{
    Task<AccountCreation> CreateAsync(string userName, SecretText password, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies a password. An unknown or disabled account costs the same verification as a known one and is rejected the
    /// same way. After a successful verification, a verifier below the current policy is replaced by one at the policy,
    /// without changing the account's security stamp.
    /// </summary>
    Task<PasswordCheck> CheckPasswordAsync(string userName, SecretText password, CancellationToken cancellationToken);

    Task<AccountSecurityState?> FindAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Verifies the current password, applies the policy to the new one, stores its verifier and rotates the security stamp.</summary>
    Task<PasswordChangeOutcome> ChangePasswordAsync(Guid accountId, SecretText currentPassword, SecretText newPassword, CancellationToken cancellationToken);

    /// <summary>Disables or enables the account and rotates its security stamp. False when the account does not exist.</summary>
    Task<bool> SetDisabledAsync(Guid accountId, bool disabled, CancellationToken cancellationToken);

    /// <summary>Rotates the security stamp, ending every session issued before the change. False when the account does not exist.</summary>
    Task<bool> RotateSecurityStampAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// Global accounts' memberships of tenants. A membership is a host-level record linking one account to one tenant: tenancy
/// reads it to establish a tenant context, before any tenant context exists, and reads it afresh every time. It decides who
/// may establish a tenant context, so it is authority data: it changes only in the audited host scope the caller
/// passes, never in an owner-bound unit.
/// </summary>
public interface ITenantMembershipStore
{
    /// <summary>Adds the membership. False when it exists already or the account does not exist.</summary>
    Task<bool> AddAsync(TenantContext host, Guid accountId, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(TenantContext host, Guid accountId, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>True only for an existing, enabled account that is currently a member of the tenant.</summary>
    Task<bool> IsMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken);
}
