using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Persistence.EfCore;

/// <summary>
/// ASP.NET Core Identity's EF Core user store, exposed only as a user, password and security-stamp store. UserManager
/// therefore reports e-mail, lockout, two-factor, token, authenticator-key, recovery-code and passkey support as absent,
/// and none of their plaintext storage paths can run.
/// </summary>
internal sealed class IdentityAccountStore(UserOnlyStore<IdentityAccount, IdentityModuleContext, Guid> inner)
    : IUserPasswordStore<IdentityAccount>, IUserSecurityStampStore<IdentityAccount>
{
    public Task<string> GetUserIdAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.GetUserIdAsync(user, cancellationToken);
    public Task<string?> GetUserNameAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.GetUserNameAsync(user, cancellationToken);
    public Task SetUserNameAsync(IdentityAccount user, string? userName, CancellationToken cancellationToken) => inner.SetUserNameAsync(user, userName, cancellationToken);
    public Task<string?> GetNormalizedUserNameAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.GetNormalizedUserNameAsync(user, cancellationToken);
    public Task SetNormalizedUserNameAsync(IdentityAccount user, string? normalizedName, CancellationToken cancellationToken) =>
        inner.SetNormalizedUserNameAsync(user, normalizedName, cancellationToken);
    public Task<IdentityResult> CreateAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.CreateAsync(user, cancellationToken);
    public Task<IdentityResult> UpdateAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.UpdateAsync(user, cancellationToken);
    public Task<IdentityResult> DeleteAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.DeleteAsync(user, cancellationToken);
    public Task<IdentityAccount?> FindByIdAsync(string userId, CancellationToken cancellationToken) => inner.FindByIdAsync(userId, cancellationToken);
    public Task<IdentityAccount?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => inner.FindByNameAsync(normalizedUserName, cancellationToken);
    public Task SetPasswordHashAsync(IdentityAccount user, string? passwordHash, CancellationToken cancellationToken) => inner.SetPasswordHashAsync(user, passwordHash, cancellationToken);
    public Task<string?> GetPasswordHashAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.GetPasswordHashAsync(user, cancellationToken);
    public Task<bool> HasPasswordAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.HasPasswordAsync(user, cancellationToken);
    public Task SetSecurityStampAsync(IdentityAccount user, string stamp, CancellationToken cancellationToken) => inner.SetSecurityStampAsync(user, stamp, cancellationToken);
    public Task<string?> GetSecurityStampAsync(IdentityAccount user, CancellationToken cancellationToken) => inner.GetSecurityStampAsync(user, cancellationToken);
    public void Dispose() => inner.Dispose();
}

/// <summary>
/// Local accounts over ASP.NET Core Identity's UserManager and EF Core store, each operation in its own module unit of
/// work. Password verifiers are PBKDF2-HMAC-SHA512 in Identity's version 3 format, which records the algorithm, iteration
/// count and salt length in every verifier. UserManager receives no logger and no service provider, so it logs nothing
/// and registers no token providers.
/// </summary>
internal sealed class IdentityAccountDirectory : IAccountDirectory
{
    private static readonly IdentityErrorDescriber Describer = new();
    private static readonly IdentityAccount Probe = new() { Id = Guid.Empty, UserName = "probe", SecurityStamp = "probe" };
    private readonly PostgreSqlModule module;
    private readonly IPasswordHasher<IdentityAccount> hasher;
    private readonly IOptions<IdentityOptions> options;
    private readonly string unknownAccountVerifier;

    public IdentityAccountDirectory(PostgreSqlModule module, IPasswordHasher<IdentityAccount> hasher, PasswordPolicy policy)
    {
        this.module = module ?? throw new ArgumentNullException(nameof(module));
        this.hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        ArgumentNullException.ThrowIfNull(policy);
        var identity = new IdentityOptions();
        identity.Password.RequiredLength = policy.MinimumLength;
        identity.Password.RequireDigit = false;
        identity.Password.RequireLowercase = false;
        identity.Password.RequireUppercase = false;
        identity.Password.RequireNonAlphanumeric = false;
        identity.Password.RequiredUniqueChars = 1;
        identity.User.RequireUniqueEmail = false;
        identity.Lockout.AllowedForNewUsers = false;
        identity.Stores.ProtectPersonalData = false;
        options = Options.Create(identity);
        // An unknown account is verified against a verifier made under the current policy, so it costs what a known one costs.
        // It is made here, before any attempt, so that no attempt pays for making it.
        unknownAccountVerifier = hasher.HashPassword(Probe, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    private UserManager<IdentityAccount> Manager(EfCoreUnitOfWork work) =>
        new(new IdentityAccountStore(new UserOnlyStore<IdentityAccount, IdentityModuleContext, Guid>((IdentityModuleContext)work.Context, Describer)),
            options, hasher, [new UserValidator<IdentityAccount>(Describer)], [new PasswordValidator<IdentityAccount>(Describer)],
            new UpperInvariantLookupNormalizer(), Describer, null!, NullLogger<UserManager<IdentityAccount>>.Instance);

    public async Task<AccountCreation> CreateAsync(string userName, SecretText password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var manager = Manager(work);
        var account = new IdentityAccount { Id = Guid.NewGuid(), UserName = userName };
        IdentityResult result;
        try
        {
            result = await manager.CreateAsync(account, password.Reveal()).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent creation took the name between validation and insert; the unique index refused this one.
            return new(AccountCreationOutcome.UserNameUnavailable, null);
        }
        if (!result.Succeeded) return new(CreationFailure(result), null);
        Committed(await work.CompleteAsync(cancellationToken).ConfigureAwait(false));
        return new(AccountCreationOutcome.Created, account.Id);
    }

    public async Task<PasswordCheck> CheckPasswordAsync(string userName, SecretText password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var manager = Manager(work);
        var account = await manager.FindByNameAsync(userName).ConfigureAwait(false);
        if (account?.PasswordHash is not { } verifier)
        {
            hasher.VerifyHashedPassword(Probe, unknownAccountVerifier, password.Reveal());
            return PasswordCheck.Rejected;
        }
        var result = Verify(account, verifier, password);
        if (result == PasswordVerificationResult.Failed || account.Disabled) return PasswordCheck.Rejected;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // Replace a verifier below the policy without rotating the security stamp, which would end the owner's sessions.
            account.PasswordHash = hasher.HashPassword(account, password.Reveal());
            if ((await manager.UpdateAsync(account).ConfigureAwait(false)).Succeeded)
                await work.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        return new(PasswordCheckOutcome.Verified, State(account));
    }

    public async Task<AccountSecurityState?> FindAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        var account = await work.Context.Set<IdentityAccount>().AsNoTracking().Where(row => row.Id == accountId)
            .Select(row => new { row.Id, row.UserName, row.SecurityStamp, row.Disabled }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return account is null ? null : new AccountSecurityState(account.Id, account.UserName!, account.SecurityStamp!, account.Disabled);
    }

    public async Task<PasswordChangeOutcome> ChangePasswordAsync(Guid accountId, SecretText currentPassword, SecretText newPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var manager = Manager(work);
        var account = await manager.FindByIdAsync(accountId.ToString("D")).ConfigureAwait(false);
        if (account is null || account.Disabled) return PasswordChangeOutcome.AccountUnavailable;
        var result = await manager.ChangePasswordAsync(account, currentPassword.Reveal(), newPassword.Reveal()).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return result.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.PasswordMismatch))
                ? PasswordChangeOutcome.CurrentPasswordRejected
                : CreationFailure(result) == AccountCreationOutcome.PasswordPolicy ? PasswordChangeOutcome.PasswordPolicy
                : throw Unexpected(result);
        }
        Committed(await work.CompleteAsync(cancellationToken).ConfigureAwait(false));
        return PasswordChangeOutcome.Changed;
    }

    public Task<bool> SetDisabledAsync(Guid accountId, bool disabled, CancellationToken cancellationToken) =>
        RotateAsync(accountId, account => account.Disabled = disabled, cancellationToken);

    public Task<bool> RotateSecurityStampAsync(Guid accountId, CancellationToken cancellationToken) => RotateAsync(accountId, _ => { }, cancellationToken);

    private async Task<bool> RotateAsync(Guid accountId, Action<IdentityAccount> change, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var manager = Manager(work);
        var account = await manager.FindByIdAsync(accountId.ToString("D")).ConfigureAwait(false);
        if (account is null) return false;
        change(account);
        var result = await manager.UpdateSecurityStampAsync(account).ConfigureAwait(false);
        if (!result.Succeeded) throw Unexpected(result);
        Committed(await work.CompleteAsync(cancellationToken).ConfigureAwait(false));
        return true;
    }

    private PasswordVerificationResult Verify(IdentityAccount account, string verifier, SecretText password)
    {
        try
        {
            return hasher.VerifyHashedPassword(account, verifier, password.Reveal());
        }
        catch (FormatException)
        {
            // A verifier that is not in a recognized format never verifies.
            return PasswordVerificationResult.Failed;
        }
    }

    private static AccountSecurityState State(IdentityAccount account) =>
        new(account.Id, account.UserName!, account.SecurityStamp!, account.Disabled);

    private static AccountCreationOutcome CreationFailure(IdentityResult result)
    {
        var codes = result.Errors.Select(error => error.Code).ToArray();
        if (codes.Contains(nameof(IdentityErrorDescriber.DuplicateUserName), StringComparer.Ordinal)) return AccountCreationOutcome.UserNameUnavailable;
        if (codes.Contains(nameof(IdentityErrorDescriber.InvalidUserName), StringComparer.Ordinal)) return AccountCreationOutcome.InvalidUserName;
        if (codes.Length > 0 && codes.All(code => code.StartsWith("Password", StringComparison.Ordinal))) return AccountCreationOutcome.PasswordPolicy;
        throw Unexpected(result);
    }

    // Identity error codes name the rule that failed; their descriptions, which can quote the user name, are not used.
    private static InvalidOperationException Unexpected(IdentityResult result) =>
        new("The account store refused the change: " + string.Join(", ", result.Errors.Select(error => error.Code)) + ".");

    private static void Committed(UnitOfWorkResult result)
    {
        if (result.State != UnitOfWorkState.Committed) throw new InvalidOperationException("The account change was not committed.");
    }
}
