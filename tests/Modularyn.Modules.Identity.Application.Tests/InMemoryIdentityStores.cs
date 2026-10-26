using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

/// <summary>
/// In-memory stand-ins for the identity stores, for unit tests of the application services. They are scaffolding, not
/// provider evidence; the PostgreSQL tests exercise the real stores. Every password verification is counted.
/// </summary>
internal sealed class InMemoryIdentityStores : IAccountDirectory, ISessionStore, IVerificationLedger, IDelegationStore, IAuthenticatorSeedStore,
    IRecoveryCodeStore, IPasskeyCredentialStore, ITenantMembershipStore
{
    private readonly HashSet<(Guid Account, Guid Tenant)> memberships = [];

    private readonly object gate = new();
    private readonly SemaphoreSlim ledger = new(1, 1);
    private readonly Dictionary<Guid, Account> accounts = [];
    private readonly Dictionary<Guid, SessionRecord> sessions = [];
    private readonly Dictionary<ThrottleKey, ThrottleCounter> counters = [];
    private readonly Dictionary<Guid, DelegationGrant> delegations = [];
    private int verifications;

    public Dictionary<Guid, ProtectedSeed> Seeds { get; } = [];
    public Dictionary<Guid, (RecoveryCodeVerifier Verifier, DateTimeOffset? Consumed, DateTimeOffset? Invalidated)> RecoveryCodes { get; } = [];
    public Dictionary<string, PasskeyCredentialRecord> Passkeys { get; } = new(StringComparer.Ordinal);

    public int Verifications => Volatile.Read(ref verifications);

    public IReadOnlyCollection<SessionRecord> Sessions
    {
        get { lock (gate) return sessions.Values.ToArray(); }
    }

    public ThrottleCounter Counter(ThrottleKey key)
    {
        lock (gate) return counters.GetValueOrDefault(key) ?? ThrottleCounter.Empty;
    }

    private sealed class Account
    {
        public required Guid Id { get; init; }
        public required string UserName { get; init; }
        public required string Password { get; set; }
        public required string Stamp { get; set; }
        public bool Disabled { get; set; }
        public AccountSecurityState State => new(Id, UserName, Stamp, Disabled);
    }

    public Task<AccountCreation> CreateAsync(string userName, SecretText password, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (accounts.Values.Any(account => UserNames.Normalize(account.UserName) == UserNames.Normalize(userName)))
                return Task.FromResult(new AccountCreation(AccountCreationOutcome.UserNameUnavailable, null));
            var account = new Account { Id = Guid.NewGuid(), UserName = userName, Password = password.Reveal(), Stamp = Guid.NewGuid().ToString("N") };
            accounts[account.Id] = account;
            return Task.FromResult(new AccountCreation(AccountCreationOutcome.Created, account.Id));
        }
    }

    public Task<PasswordCheck> CheckPasswordAsync(string userName, SecretText password, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref verifications);
        lock (gate)
        {
            var account = accounts.Values.SingleOrDefault(row => UserNames.Normalize(row.UserName) == UserNames.Normalize(userName));
            return Task.FromResult(account is { Disabled: false } && account.Password == password.Reveal()
                ? new PasswordCheck(PasswordCheckOutcome.Verified, account.State) : PasswordCheck.Rejected);
        }
    }

    public Task<AccountSecurityState?> FindAsync(Guid accountId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(accounts.TryGetValue(accountId, out var account) ? account.State : null);
    }

    public Task<PasswordChangeOutcome> ChangePasswordAsync(Guid accountId, SecretText currentPassword, SecretText newPassword, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref verifications);
        lock (gate)
        {
            if (!accounts.TryGetValue(accountId, out var account) || account.Disabled) return Task.FromResult(PasswordChangeOutcome.AccountUnavailable);
            if (account.Password != currentPassword.Reveal()) return Task.FromResult(PasswordChangeOutcome.CurrentPasswordRejected);
            account.Password = newPassword.Reveal();
            account.Stamp = Guid.NewGuid().ToString("N");
            return Task.FromResult(PasswordChangeOutcome.Changed);
        }
    }

    public Task<bool> SetDisabledAsync(Guid accountId, bool disabled, CancellationToken cancellationToken) => Change(accountId, account => account.Disabled = disabled);

    public Task<bool> RotateSecurityStampAsync(Guid accountId, CancellationToken cancellationToken) => Change(accountId, _ => { });

    private Task<bool> Change(Guid accountId, Action<Account> change)
    {
        lock (gate)
        {
            if (!accounts.TryGetValue(accountId, out var account)) return Task.FromResult(false);
            change(account);
            account.Stamp = Guid.NewGuid().ToString("N");
            return Task.FromResult(true);
        }
    }

    Task ISessionStore.CreateAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        lock (gate) sessions.Add(session.Id, session);
        return Task.CompletedTask;
    }

    public Task<SessionLookup?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(Lookup(sessions.Values.SingleOrDefault(row => row.TokenHash.AsSpan().SequenceEqual(tokenHash))));
    }

    Task<SessionLookup?> ISessionStore.FindAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(Lookup(sessions.GetValueOrDefault(sessionId)));
    }

    private SessionLookup? Lookup(SessionRecord? session) =>
        session is null ? null : new SessionLookup(session, accounts.TryGetValue(session.AccountId, out var account) ? account.State : null);

    public Task<bool> TouchAsync(Guid sessionId, DateTimeOffset now, DateTimeOffset idleCutoff, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var row) || row.EndedAt is not null || row.LastSeenAt <= idleCutoff || row.AbsoluteExpiresAt <= now)
                return Task.FromResult(false);
            sessions[sessionId] = row with { LastSeenAt = now };
            return Task.FromResult(true);
        }
    }

    public Task<bool> EndAsync(Guid sessionId, SessionEndReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var row) || row.EndedAt is not null) return Task.FromResult(false);
            sessions[sessionId] = row with { EndedAt = now, EndReason = reason };
            return Task.FromResult(true);
        }
    }

    public Task<int> EndAllAsync(Guid accountId, SessionEndReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var active = sessions.Values.Where(row => row.AccountId == accountId && row.EndedAt is null).ToArray();
            foreach (var row in active) sessions[row.Id] = row with { EndedAt = now, EndReason = reason };
            return Task.FromResult(active.Length);
        }
    }

    public async Task<IVerificationLedgerEntry> AcquireAsync(IReadOnlyCollection<ThrottleKey> keys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await ledger.WaitAsync(cancellationToken);
        lock (gate) return new Entry(this, keys.Distinct().ToDictionary(key => key, key => counters.GetValueOrDefault(key) ?? ThrottleCounter.Empty));
    }

    private sealed class Entry(InMemoryIdentityStores owner, Dictionary<ThrottleKey, ThrottleCounter> held) : IVerificationLedgerEntry
    {
        private bool released;

        public ThrottleCounter Get(ThrottleKey key) => held[key];

        public void Set(ThrottleKey key, ThrottleCounter counter)
        {
            _ = held[key];
            held[key] = counter;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            lock (owner.gate) foreach (var (key, counter) in held) owner.counters[key] = counter;
            Release();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        private void Release()
        {
            if (released) return;
            released = true;
            owner.ledger.Release();
        }
    }

    Task IDelegationStore.CreateAsync(DelegationGrant grant, CancellationToken cancellationToken)
    {
        lock (gate) delegations.Add(grant.Id, grant);
        return Task.CompletedTask;
    }

    Task<DelegationGrant?> IDelegationStore.FindAsync(Guid grantId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(delegations.GetValueOrDefault(grantId));
    }

    public Task<bool> RevokeAsync(Guid grantId, PrincipalReference revokedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!delegations.TryGetValue(grantId, out var grant) || grant.RevokedAt is not null) return Task.FromResult(false);
            var authority = grant.Authority;
            delegations[grantId] = DelegationGrant.Restore(grant.Id, grant.Actor, grant.Subject, authority.Scope, authority.Purpose, authority.Tenant,
                authority.NotBefore, authority.ExpiresAt, grant.IssuedBy, grant.IssuedAt, now, revokedBy);
            return Task.FromResult(true);
        }
    }

    public Task SaveAsync(ProtectedSeed seed, CancellationToken cancellationToken)
    {
        lock (gate) Seeds[seed.AccountId] = seed;
        return Task.CompletedTask;
    }

    Task<ProtectedSeed?> IAuthenticatorSeedStore.FindAsync(Guid accountId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(Seeds.GetValueOrDefault(accountId));
    }

    Task<bool> IAuthenticatorSeedStore.RemoveAsync(Guid accountId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(Seeds.Remove(accountId));
    }

    public Task ReplaceAsync(Guid accountId, IReadOnlyList<RecoveryCodeVerifier> verifiers, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            foreach (var (id, row) in RecoveryCodes.Where(pair => pair.Value.Verifier.AccountId == accountId && pair.Value.Consumed is null && pair.Value.Invalidated is null).ToArray())
                RecoveryCodes[id] = row with { Invalidated = now };
            foreach (var verifier in verifiers) RecoveryCodes[verifier.Id] = (verifier, null, null);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RecoveryCodeVerifier>> FindUsableAsync(Guid accountId, CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<RecoveryCodeVerifier>>(RecoveryCodes.Values
                .Where(row => row.Verifier.AccountId == accountId && row.Consumed is null && row.Invalidated is null).Select(row => row.Verifier).ToArray());
    }

    public Task<bool> ConsumeAsync(Guid verifierId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!RecoveryCodes.TryGetValue(verifierId, out var row) || row.Consumed is not null || row.Invalidated is not null) return Task.FromResult(false);
            RecoveryCodes[verifierId] = row with { Consumed = now };
            return Task.FromResult(true);
        }
    }

    public Task<bool> AddAsync(PasskeyCredentialRecord record, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(Passkeys.TryAdd(Convert.ToHexString(record.CredentialId), record));
    }

    Task<PasskeyCredentialRecord?> IPasskeyCredentialStore.FindAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(Passkeys.GetValueOrDefault(Convert.ToHexString(credentialId)));
    }

    public Task<bool> RemoveAsync(Guid accountId, byte[] credentialId, CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult(Passkeys.TryGetValue(Convert.ToHexString(credentialId), out var record) && record.AccountId == accountId
                && Passkeys.Remove(Convert.ToHexString(credentialId)));
    }

    public Task<bool> AddAsync(Guid accountId, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(accounts.ContainsKey(accountId) && memberships.Add((accountId, tenantId)));
    }

    public Task<bool> RemoveAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(memberships.Remove((accountId, tenantId)));
    }

    public Task<bool> IsMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(memberships.Contains((accountId, tenantId)) && accounts.TryGetValue(accountId, out var account) && !account.Disabled);
    }
}

/// <summary>The identity application over the in-memory stores, with a manual clock and, optionally, credential keys.</summary>
internal sealed class UnitIdentity : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private UnitIdentity(ServiceProvider provider, InMemoryIdentityStores stores, ManualClock clock)
    {
        this.provider = provider;
        Stores = stores;
        Clock = clock;
    }

    public InMemoryIdentityStores Stores { get; }
    public ManualClock Clock { get; }

    public static UnitIdentity Create(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var stores = new InMemoryIdentityStores();
        var clock = ManualClock.StartingNow();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddModularynIdentity(configuration);
        if (configuration["Modularyn:Secrets:Directory"] is not null) services.AddModularynSecrets(configuration);
        services.AddSingleton<IAccountDirectory>(stores);
        services.AddSingleton<ISessionStore>(stores);
        services.AddSingleton<IVerificationLedger>(stores);
        services.AddSingleton<IDelegationStore>(stores);
        services.AddSingleton<IAuthenticatorSeedStore>(stores);
        services.AddSingleton<IRecoveryCodeStore>(stores);
        services.AddSingleton<IPasskeyCredentialStore>(stores);
        services.AddSingleton<ITenantMembershipStore>(stores);
        configure?.Invoke(services);
        return new UnitIdentity(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }), stores, clock);
    }

    /// <summary>Limits small enough to reach in a test; every other value is the module's default.</summary>
    public static Dictionary<string, string?> TestLimits(Dictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Modularyn:Identity:Throttling:SourceThreshold"] = "6",
            ["Modularyn:Identity:Throttling:AccountFreeAttempts"] = "3",
            ["Modularyn:Identity:Throttling:AccountBaseDelay"] = "00:00:02",
            ["Modularyn:Identity:Throttling:AccountMaximumDelay"] = "00:00:16"
        };
        foreach (var (key, value) in more ?? []) settings[key] = value;
        return settings;
    }

    public T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    public async Task<(Guid Id, string Password)> AccountAsync(string userName)
    {
        var password = Generated.Password();
        var created = await Get<IAccountService>().CreateLocalAccountAsync(userName, new SecretText(password), TestContext.Current.CancellationToken);
        Assert.Equal(AccountCreationStatus.Created, created.Status);
        return (created.AccountId!.Value, password);
    }

    public async Task<(IssuedSession Session, ExecutionPrincipal Principal)> SignInAsync(string userName, string password, string source = "203.0.113.1")
    {
        var result = await Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(userName, new SecretText(password), source),
            TestContext.Current.CancellationToken);
        Assert.Equal(SignInStatus.Succeeded, result.Status);
        var active = await Get<ISessionService>().ValidateAsync(result.Session!.Token, TestContext.Current.CancellationToken);
        return (result.Session, active!.ToPrincipal());
    }

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}

internal static class Bytes
{
    public static byte[] Random(int length) => RandomNumberGenerator.GetBytes(length);
}
