using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

internal sealed class SignInService(IAccountDirectory accounts, VerificationGate gate, SessionService sessions, TimeProvider clock) : ISignInService
{
    public async Task<SignInResult> SignInWithPasswordAsync(PasswordSignInRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Input no account could match is refused before any lookup, identically for every account name.
        if (!UserNames.IsPlausible(request.UserName) || !PasswordPolicy.IsPlausible(request.Password)) return SignInResult.Failed;
        var now = clock.GetUtcNow();
        await using var attempt = await gate.EnterAsync(ThrottleKey.ForSource(request.Source),
            ThrottleKey.ForUserName(ThrottleKind.Password, request.UserName), now, cancellationToken).ConfigureAwait(false);
        if (attempt.RefusedUntil is { } retryAt) return SignInResult.Throttled(retryAt);

        var check = await accounts.CheckPasswordAsync(request.UserName, request.Password, cancellationToken).ConfigureAwait(false);
        if (check is not { Outcome: PasswordCheckOutcome.Verified, Account: { } account })
        {
            await attempt.FailedAsync().ConfigureAwait(false);
            return SignInResult.Failed;
        }
        var session = await sessions.IssueAsync(account, now, cancellationToken).ConfigureAwait(false);
        await attempt.SucceededAsync().ConfigureAwait(false);
        return SignInResult.Succeeded(session);
    }
}

internal sealed class AccountService(IAccountDirectory accounts, VerificationGate gate, SessionService sessions, IdentityPolicies policies,
    TimeProvider clock) : IAccountService
{
    public async Task<AccountCreationResult> CreateLocalAccountAsync(string userName, SecretText password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!UserNames.IsPlausible(userName)) return new(AccountCreationStatus.InvalidUserName, null);
        if (password.Length < policies.Passwords.MinimumLength || password.Length > PasswordPolicy.MaximumLength)
            return new(AccountCreationStatus.PasswordPolicy, null);
        var created = await accounts.CreateAsync(userName, password, cancellationToken).ConfigureAwait(false);
        return new((AccountCreationStatus)(int)created.Outcome, created.AccountId);
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(Guid accountId, SecretText currentPassword, SecretText newPassword, string source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        source = ClientSources.Require(source, nameof(source));
        if (!PasswordPolicy.IsPlausible(currentPassword)) return new(PasswordChangeStatus.CurrentPasswordRejected, null);
        if (newPassword.Length < policies.Passwords.MinimumLength || newPassword.Length > PasswordPolicy.MaximumLength)
            return new(PasswordChangeStatus.PasswordPolicy, null);
        var account = await accounts.FindAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null || account.Disabled) return new(PasswordChangeStatus.AccountUnavailable, null);

        // The current password is a guess like any other, so it shares sign-in's counters.
        var now = clock.GetUtcNow();
        await using var attempt = await gate.EnterAsync(ThrottleKey.ForSource(source),
            ThrottleKey.ForUserName(ThrottleKind.Password, account.UserName), now, cancellationToken).ConfigureAwait(false);
        if (attempt.RefusedUntil is { } retryAt) return new(PasswordChangeStatus.Throttled, retryAt);
        var outcome = await accounts.ChangePasswordAsync(accountId, currentPassword, newPassword, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case PasswordChangeOutcome.CurrentPasswordRejected:
                await attempt.FailedAsync().ConfigureAwait(false);
                return new(PasswordChangeStatus.CurrentPasswordRejected, null);
            case PasswordChangeOutcome.Changed:
                await attempt.SucceededAsync().ConfigureAwait(false);
                // The rotated security stamp already ends every session; ending them here also records why.
                await sessions.EndAllAsync(accountId, SessionEndReason.PasswordChanged, cancellationToken).ConfigureAwait(false);
                return new(PasswordChangeStatus.Changed, null);
            default:
                return new((PasswordChangeStatus)(int)outcome, null);
        }
    }

    public async Task<bool> DisableAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!await accounts.SetDisabledAsync(accountId, true, cancellationToken).ConfigureAwait(false)) return false;
        await sessions.EndAllAsync(accountId, SessionEndReason.AccountDisabled, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<bool> EnableAsync(Guid accountId, CancellationToken cancellationToken) => accounts.SetDisabledAsync(accountId, false, cancellationToken);

    public async Task<bool> SecurityStateChangedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!await accounts.RotateSecurityStampAsync(accountId, cancellationToken).ConfigureAwait(false)) return false;
        await sessions.EndAllAsync(accountId, SessionEndReason.SecurityStateChanged, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

internal sealed class TenantMembershipService(ITenantMembershipStore store, TimeProvider clock) : ITenantMemberships
{
    public Task<bool> AddMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty ? throw new ArgumentException("Name the tenant by its identifier.", nameof(tenantId))
            : store.AddAsync(accountId, tenantId, clock.GetUtcNow(), cancellationToken);

    public Task<bool> RemoveMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken) => store.RemoveAsync(accountId, tenantId, cancellationToken);

    public Task<bool> IsMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty ? Task.FromResult(false) : store.IsMemberAsync(accountId, tenantId, cancellationToken);
}
