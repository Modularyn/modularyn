using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Modules.Identity.Application.Contracts;

/// <summary>
/// The client source a verification is counted against, as the adapter that received the attempt observed it, such as a
/// client address reduced to its blocking unit. Attempts from the same source share one per-source limit.
/// </summary>
public static class ClientSources
{
    public const int MaximumLength = 200;

    /// <summary>
    /// The source of an attempt whose adapter observed no client, such as a connection without an address. It stands for
    /// any number of clients, so it has no per-source counter: only the subject's limits apply to it, and one such client
    /// can neither block nor delay another.
    /// </summary>
    public const string Unknown = "unknown";

    public static bool IsUnknown(string source) => string.Equals(source, Unknown, StringComparison.Ordinal);

    public static bool IsValid(string? source) =>
        !string.IsNullOrEmpty(source) && source.Length <= MaximumLength && !source.Any(char.IsControl);

    public static string Require(string source, string parameterName) => IsValid(source) ? source
        : throw new ArgumentException("A client source is 1 to 200 characters without control characters.", parameterName);
}

/// <summary>A password sign-in attempt from a client source.</summary>
public sealed class PasswordSignInRequest
{
    public PasswordSignInRequest(string userName, SecretText password, string source)
    {
        UserName = userName ?? throw new ArgumentNullException(nameof(userName));
        Password = password ?? throw new ArgumentNullException(nameof(password));
        Source = ClientSources.Require(source, nameof(source));
    }

    public string UserName { get; }
    public SecretText Password { get; }
    public string Source { get; }

    public override string ToString() => nameof(PasswordSignInRequest);
}

public enum SignInStatus
{
    Succeeded = 1,

    /// <summary>The same outcome for an unknown account, a disabled account and a wrong password.</summary>
    Failed = 2,

    /// <summary>Refused before any verification because the source is blocked or the account's delay has not elapsed.</summary>
    Throttled = 3
}

public sealed class SignInResult
{
    private SignInResult(SignInStatus status, IssuedSession? session, DateTimeOffset? retryAt)
    {
        Status = status;
        Session = session;
        RetryAt = retryAt;
    }

    public static SignInResult Failed { get; } = new(SignInStatus.Failed, null, null);

    public SignInStatus Status { get; }
    public IssuedSession? Session { get; }

    /// <summary>When a throttled attempt may be made again.</summary>
    public DateTimeOffset? RetryAt { get; }

    public static SignInResult Succeeded(IssuedSession session) => new(SignInStatus.Succeeded, session ?? throw new ArgumentNullException(nameof(session)), null);

    public static SignInResult Throttled(DateTimeOffset retryAt) => new(SignInStatus.Throttled, null, retryAt);

    public override string ToString() => Status.ToString();
}

/// <summary>
/// Local password sign-in (TM-G02-015, TM-G02-016). Every attempt is limited per source and per account before any
/// verification, failures by others delay the owner but never lock them out, and unknown accounts, disabled accounts and
/// wrong passwords are answered the same way. Success always issues a new session with a new token.
/// </summary>
public interface ISignInService
{
    Task<SignInResult> SignInWithPasswordAsync(PasswordSignInRequest request, CancellationToken cancellationToken);
}

public enum AccountCreationStatus
{
    Created = 1,
    UserNameUnavailable = 2,
    InvalidUserName = 3,
    PasswordPolicy = 4
}

public sealed record AccountCreationResult(AccountCreationStatus Status, Guid? AccountId);

public enum PasswordChangeStatus
{
    Changed = 1,
    CurrentPasswordRejected = 2,
    PasswordPolicy = 3,
    AccountUnavailable = 4,
    Throttled = 5
}

public sealed record PasswordChangeResult(PasswordChangeStatus Status, DateTimeOffset? RetryAt);

/// <summary>Local account administration. Each security-relevant change ends the account's sessions at their next request.</summary>
public interface IAccountService
{
    Task<AccountCreationResult> CreateLocalAccountAsync(string userName, SecretText password, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the password after verifying the current one, which counts against the same per-account and per-source limits
    /// as sign-in. Success ends every session of the account, including the caller's; the owner signs in again.
    /// </summary>
    Task<PasswordChangeResult> ChangePasswordAsync(Guid accountId, SecretText currentPassword, SecretText newPassword, string source,
        CancellationToken cancellationToken);

    /// <summary>Disables the account and ends its sessions. False when the account does not exist.</summary>
    Task<bool> DisableAsync(Guid accountId, CancellationToken cancellationToken);

    Task<bool> EnableAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a change to the account's security-relevant state made elsewhere, such as a role removal that must not wait for
    /// sessions to expire: the security stamp rotates and every session ends. False when the account does not exist.
    /// </summary>
    Task<bool> SecurityStateChangedAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// Global accounts' tenant memberships (the master design's shared account with explicit memberships). Removing a
/// membership never deletes the account, and takes effect when the next tenant context is established. Who may add or
/// remove a member is a permission that authorization supplies; no endpoint exposes these operations.
/// </summary>
public interface ITenantMemberships
{
    Task<bool> AddMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken);

    Task<bool> RemoveMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken);

    /// <summary>True only for an existing, enabled account that is currently a member of the tenant.</summary>
    Task<bool> IsMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken);
}
