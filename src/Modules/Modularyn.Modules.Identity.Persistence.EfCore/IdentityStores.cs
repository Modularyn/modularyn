using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Persistence.EfCore;

/// <summary>
/// Opens the module's units of work: a transaction for every change, an autocommitted connection for reads. Every query
/// is ordinary LINQ over the module context and every change a tracked write; there is no raw SQL and no bulk write.
/// </summary>
internal abstract class IdentityStore(PostgreSqlModule module)
{
    // A concurrent writer can win a race a bounded number of times before an operation that retries gives up.
    protected const int Attempts = 4;

    protected PostgreSqlModule Module { get; } = module ?? throw new ArgumentNullException(nameof(module));

    protected async Task<T> ReadAsync<T>(Func<IdentityModuleContext, Task<T>> read, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, UnitOfWorkMode.Suppress, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await read((IdentityModuleContext)work.Context).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one transactional change. When a concurrency token shows that another writer changed a row first, nothing is
    /// committed and <paramref name="conflicted"/> is returned; any other failure to commit is an error.
    /// </summary>
    protected async Task<T> WriteAsync<T>(Func<IdentityModuleContext, Task<T>> write, T conflicted, CancellationToken cancellationToken)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = await write((IdentityModuleContext)work.Context).ConfigureAwait(false);
        var outcome = await work.CompleteAsync(cancellationToken).ConfigureAwait(false);
        if (outcome.State == UnitOfWorkState.Committed) return result;
        if (outcome.Conflicts.Count > 0) return conflicted;
        throw new InvalidOperationException("The identity change was not committed.");
    }
}

/// <summary>
/// Session records. The record has no concurrency token, so parallel requests of one session never conflict; each write
/// changes only its own columns, so recording activity never undoes an end that committed meanwhile.
/// </summary>
internal sealed class SessionStore(PostgreSqlModule module) : IdentityStore(module), ISessionStore
{
    public Task CreateAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return WriteAsync(context =>
        {
            context.Add(new SessionRow
            {
                Id = session.Id, TokenHash = session.TokenHash.ToArray(), AccountId = session.AccountId, AuthenticatedAt = session.AuthenticatedAt.ToUniversalTime(),
                LastSeenAt = session.LastSeenAt.ToUniversalTime(), AbsoluteExpiresAt = session.AbsoluteExpiresAt.ToUniversalTime(),
                SecurityStamp = session.SecurityStamp
            });
            return Task.FromResult(true);
        }, false, cancellationToken);
    }

    public Task<SessionLookup?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        return ReadAsync(context => LookupAsync(context, context.Set<SessionRow>().Where(row => row.TokenHash == tokenHash), cancellationToken), cancellationToken);
    }

    public Task<SessionLookup?> FindAsync(Guid sessionId, CancellationToken cancellationToken) =>
        ReadAsync(context => LookupAsync(context, context.Set<SessionRow>().Where(row => row.Id == sessionId), cancellationToken), cancellationToken);

    public Task<bool> TouchAsync(Guid sessionId, DateTimeOffset now, DateTimeOffset idleCutoff, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        var cutoff = idleCutoff.ToUniversalTime();
        return WriteAsync(async context =>
        {
            var row = await context.Set<SessionRow>().SingleOrDefaultAsync(row => row.Id == sessionId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.EndedAt is not null || row.LastSeenAt <= cutoff || row.AbsoluteExpiresAt <= at) return false;
            row.LastSeenAt = at;
            return true;
        }, false, cancellationToken);
    }

    public Task<bool> EndAsync(Guid sessionId, SessionEndReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        return WriteAsync(async context =>
        {
            var row = await context.Set<SessionRow>().SingleOrDefaultAsync(row => row.Id == sessionId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.EndedAt is not null) return false;
            (row.EndedAt, row.EndReason) = (at, (int)reason);
            return true;
        }, false, cancellationToken);
    }

    public Task<int> EndAllAsync(Guid accountId, SessionEndReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        return WriteAsync(async context =>
        {
            var active = await context.Set<SessionRow>().Where(row => row.AccountId == accountId && row.EndedAt == null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in active) (row.EndedAt, row.EndReason) = (at, (int)reason);
            return active.Count;
        }, 0, cancellationToken);
    }

    private static async Task<SessionLookup?> LookupAsync(IdentityModuleContext context, IQueryable<SessionRow> sessions, CancellationToken cancellationToken)
    {
        var found = await (from session in sessions.AsNoTracking()
                           join account in context.Set<IdentityAccount>().AsNoTracking() on session.AccountId equals account.Id into accounts
                           from account in accounts.DefaultIfEmpty()
                           select new
                           {
                               Session = session,
                               AccountId = (Guid?)account!.Id,
                               account.UserName,
                               account.SecurityStamp,
                               Disabled = (bool?)account.Disabled
                           }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (found is null) return null;
        var row = found.Session;
        var record = new SessionRecord(row.Id, row.TokenHash, row.AccountId, row.AuthenticatedAt, row.LastSeenAt, row.AbsoluteExpiresAt, row.SecurityStamp,
            row.EndedAt, row.EndReason is { } reason && Enum.IsDefined((SessionEndReason)reason) ? (SessionEndReason)reason : row.EndedAt is null ? null : SessionEndReason.Revoked);
        var state = found.AccountId is { } id && found.UserName is { } name && found.SecurityStamp is { } stamp
            ? new AccountSecurityState(id, name, stamp, found.Disabled ?? true) : null;
        return new SessionLookup(record, state);
    }
}

/// <summary>
/// Throttle counters, one row per key. Acquiring an entry writes each counter's row, in a fixed order, which takes the
/// row's lock until the entry ends; the counter is then read again, so it reflects every attempt that held the lock before.
/// A counter's first attempt inserts it, and a concurrent first attempt that loses the insert retries and waits on the lock.
/// </summary>
internal sealed class VerificationLedger(PostgreSqlModule module) : IVerificationLedger
{
    private const int Attempts = 4;

    public async Task<IVerificationLedgerEntry> AcquireAsync(IReadOnlyCollection<ThrottleKey> keys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        // A fixed order means two attempts that share counters always lock them in the same sequence.
        var ordered = keys.Distinct().OrderBy(key => Convert.ToHexString(key.Hash), StringComparer.Ordinal).ToArray();
        var at = now.ToUniversalTime();
        for (var attempt = 1; ; attempt++)
        {
            var work = await EfCoreUnitOfWork.BeginAsync(module, UnitOfWorkMode.New, cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                var rows = new Dictionary<ThrottleKey, ThrottleRow>();
                foreach (var key in ordered)
                {
                    var hash = key.Hash.ToArray();
                    var context = work.Context;
                    var row = await context.Set<ThrottleRow>().SingleOrDefaultAsync(row => row.Key == hash, cancellationToken).ConfigureAwait(false);
                    if (row is null)
                    {
                        row = new ThrottleRow { Key = hash, Kind = (int)key.Kind, UpdatedAt = at };
                        context.Add(row);
                        await work.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        row.UpdatedAt = at;
                        context.Entry(row).Property(counter => counter.UpdatedAt).IsModified = true;
                        await work.FlushAsync(cancellationToken).ConfigureAwait(false);
                        // Read the counter as the previous holder of the lock committed it. An ordinary query, not
                        // EntityEntry.Reload, whose internal query ignores every filter and is refused by the query guard.
                        var committed = await context.Set<ThrottleRow>().AsNoTracking().SingleAsync(counter => counter.Key == hash, cancellationToken).ConfigureAwait(false);
                        context.Entry(row).CurrentValues.SetValues(committed);
                        context.Entry(row).OriginalValues.SetValues(committed);
                    }
                    if (row.Kind != (int)key.Kind || row.Failures < 0) throw new InvalidOperationException("A throttle counter is inconsistent with its key.");
                    rows[key] = row;
                }
                return new Entry(work, rows, at);
            }
            catch (DbUpdateException) when (attempt < Attempts)
            {
                // Another attempt inserted the same counter first; it exists now, so the next try locks it.
                await work.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                await work.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private sealed class Entry(EfCoreUnitOfWork work, Dictionary<ThrottleKey, ThrottleRow> rows, DateTimeOffset now) : IVerificationLedgerEntry
    {
        public ThrottleCounter Get(ThrottleKey key) => rows.TryGetValue(key, out var row)
            ? new ThrottleCounter(row.Failures, row.WindowStartedAt, row.LastFailureAt, row.BlockedUntil)
            : throw new ArgumentException("The counter was not acquired by this entry.", nameof(key));

        public void Set(ThrottleKey key, ThrottleCounter counter)
        {
            ArgumentNullException.ThrowIfNull(counter);
            if (!rows.TryGetValue(key, out var row)) throw new ArgumentException("The counter was not acquired by this entry.", nameof(key));
            row.Failures = counter.Failures;
            row.WindowStartedAt = counter.WindowStartedAt?.ToUniversalTime();
            row.LastFailureAt = counter.LastFailureAt?.ToUniversalTime();
            row.BlockedUntil = counter.BlockedUntil?.ToUniversalTime();
            row.UpdatedAt = now;
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if ((await work.CompleteAsync(cancellationToken).ConfigureAwait(false)).State != UnitOfWorkState.Committed)
                throw new InvalidOperationException("The throttle counters were not committed.");
        }

        public ValueTask DisposeAsync() => work.DisposeAsync();
    }
}

/// <summary>Delegation records. Revocation is a tracked write under the row's concurrency token, so it happens once.</summary>
internal sealed class DelegationStore(PostgreSqlModule module) : IdentityStore(module), IDelegationStore
{
    public Task CreateAsync(DelegationGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var authority = grant.Authority;
        return WriteAsync(context =>
        {
            context.Add(new DelegationRow
            {
                Id = grant.Id, ActorId = grant.Actor.Id, ActorKind = (int)grant.Actor.Kind, SubjectId = grant.Subject.Id, SubjectKind = (int)grant.Subject.Kind,
                Scope = string.Join(' ', authority.Scope.Order(StringComparer.Ordinal)), Purpose = authority.Purpose, Tenant = authority.Tenant,
                IssuedById = grant.IssuedBy.Id, IssuedByKind = (int)grant.IssuedBy.Kind, IssuedAt = grant.IssuedAt.ToUniversalTime(),
                NotBefore = authority.NotBefore.ToUniversalTime(), ExpiresAt = authority.ExpiresAt.ToUniversalTime()
            });
            return Task.FromResult(true);
        }, false, cancellationToken);
    }

    public Task<DelegationGrant?> FindAsync(Guid grantId, CancellationToken cancellationToken) => ReadAsync(async context =>
    {
        var row = await context.Set<DelegationRow>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == grantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        try
        {
            return DelegationGrant.Restore(row.Id, new(row.ActorId, (IdentityKind)row.ActorKind), new(row.SubjectId, (IdentityKind)row.SubjectKind),
                row.Scope.Split(' '), row.Purpose, row.Tenant, row.NotBefore, row.ExpiresAt, new(row.IssuedById, (IdentityKind)row.IssuedByKind), row.IssuedAt,
                row.RevokedAt, row.RevokedById is null || row.RevokedByKind is null ? null : new(row.RevokedById, (IdentityKind)row.RevokedByKind.Value));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // A stored delegation that does not validate never resolves.
            return null;
        }
    }, cancellationToken);

    public Task<bool> RevokeAsync(Guid grantId, PrincipalReference revokedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revokedBy);
        var at = now.ToUniversalTime();
        return WriteAsync(async context =>
        {
            var row = await context.Set<DelegationRow>().SingleOrDefaultAsync(row => row.Id == grantId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.RevokedAt is not null) return false;
            (row.RevokedAt, row.RevokedById, row.RevokedByKind) = (at, revokedBy.Id, (int)revokedBy.Kind);
            return true;
        }, false, cancellationToken);
    }
}

internal sealed class AuthenticatorSeedStore(PostgreSqlModule module) : IdentityStore(module), IAuthenticatorSeedStore
{
    public async Task SaveAsync(ProtectedSeed seed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                if (await WriteAsync(async context =>
                    {
                        var earlier = await context.Set<AuthenticatorSeedRow>().SingleOrDefaultAsync(row => row.AccountId == seed.AccountId, cancellationToken)
                            .ConfigureAwait(false);
                        if (earlier is not null) context.Remove(earlier);
                        context.Add(new AuthenticatorSeedRow
                        {
                            AccountId = seed.AccountId, KeyVersion = seed.KeyVersion, Nonce = seed.Nonce.ToArray(), Ciphertext = seed.Ciphertext.ToArray(),
                            Tag = seed.Tag.ToArray(), CreatedAt = seed.CreatedAt.ToUniversalTime()
                        });
                        return true;
                    }, false, cancellationToken).ConfigureAwait(false)) return;
            }
            catch (DbUpdateException) when (attempt < Attempts)
            {
                // A concurrent store of the same account's seed committed first; the next try replaces it.
            }
        }
        throw new InvalidOperationException("The seed could not be stored against concurrent changes.");
    }

    public Task<ProtectedSeed?> FindAsync(Guid accountId, CancellationToken cancellationToken) => ReadAsync(async context =>
    {
        var row = await context.Set<AuthenticatorSeedRow>().AsNoTracking().SingleOrDefaultAsync(row => row.AccountId == accountId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new ProtectedSeed(row.AccountId, row.KeyVersion, row.Nonce, row.Ciphertext, row.Tag, row.CreatedAt);
    }, cancellationToken);

    public Task<bool> RemoveAsync(Guid accountId, CancellationToken cancellationToken) => WriteAsync(async context =>
    {
        var row = await context.Set<AuthenticatorSeedRow>().SingleOrDefaultAsync(row => row.AccountId == accountId, cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        context.Remove(row);
        return true;
    }, false, cancellationToken);
}

/// <summary>
/// Recovery-code verifiers. Each row has a concurrency token, so of any number of concurrent consumptions of one code,
/// and of a consumption racing a replacement of its set, exactly one write commits.
/// </summary>
internal sealed class RecoveryCodeStore(PostgreSqlModule module) : IdentityStore(module), IRecoveryCodeStore
{
    public async Task ReplaceAsync(Guid accountId, IReadOnlyList<RecoveryCodeVerifier> verifiers, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        if (verifiers.Any(verifier => verifier.AccountId != accountId)) throw new ArgumentException("Every verifier belongs to the account.", nameof(verifiers));
        var at = now.ToUniversalTime();
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var replaced = await WriteAsync(async context =>
            {
                foreach (var row in await context.Set<RecoveryCodeRow>().Where(row => row.AccountId == accountId && row.ConsumedAt == null && row.InvalidatedAt == null)
                             .ToListAsync(cancellationToken).ConfigureAwait(false))
                    row.InvalidatedAt = at;
                context.AddRange(verifiers.Select(verifier => new RecoveryCodeRow
                {
                    Id = verifier.Id, AccountId = verifier.AccountId, SetId = verifier.SetId, Salt = verifier.Salt.ToArray(), Verifier = verifier.Verifier.ToArray(),
                    Iterations = verifier.Iterations, CreatedAt = verifier.CreatedAt.ToUniversalTime()
                }));
                return true;
            }, false, cancellationToken).ConfigureAwait(false);
            // A code consumed while the set was being replaced changed a row first; the next try invalidates what is left.
            if (replaced) return;
        }
        throw new InvalidOperationException("The recovery codes could not be replaced against concurrent changes.");
    }

    public Task<IReadOnlyList<RecoveryCodeVerifier>> FindUsableAsync(Guid accountId, CancellationToken cancellationToken) => ReadAsync(async context =>
    {
        var rows = await context.Set<RecoveryCodeRow>().AsNoTracking().Where(row => row.AccountId == accountId && row.ConsumedAt == null && row.InvalidatedAt == null)
            .OrderBy(row => row.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return (IReadOnlyList<RecoveryCodeVerifier>)Array.AsReadOnly(rows.Select(row =>
            new RecoveryCodeVerifier(row.Id, row.AccountId, row.SetId, row.Salt, row.Verifier, row.Iterations, row.CreatedAt)).ToArray());
    }, cancellationToken);

    public Task<bool> ConsumeAsync(Guid verifierId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var at = now.ToUniversalTime();
        return WriteAsync(async context =>
        {
            var row = await context.Set<RecoveryCodeRow>().SingleOrDefaultAsync(row => row.Id == verifierId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.ConsumedAt is not null || row.InvalidatedAt is not null) return false;
            row.ConsumedAt = at;
            return true;
        }, false, cancellationToken);
    }
}

internal sealed class PasskeyCredentialStore(PostgreSqlModule module) : IdentityStore(module), IPasskeyCredentialStore
{
    public async Task<bool> AddAsync(PasskeyCredentialRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            return await WriteAsync(async context =>
            {
                var credentialId = record.CredentialId.ToArray();
                if (await context.Set<PasskeyCredentialRow>().AnyAsync(row => row.CredentialId == credentialId, cancellationToken).ConfigureAwait(false)) return false;
                context.Add(new PasskeyCredentialRow
                {
                    Id = record.Id, AccountId = record.AccountId, CredentialId = credentialId, RelyingPartyId = record.RelyingPartyId,
                    PublicKey = record.PublicKey.ToArray(), Algorithm = record.Algorithm, CreatedAt = record.CreatedAt.ToUniversalTime(),
                    KeyVersion = record.KeyVersion, IntegrityTag = record.IntegrityTag.ToArray()
                });
                return true;
            }, false, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent enrollment of the same credential identifier was stored first.
            return false;
        }
    }

    public Task<PasskeyCredentialRecord?> FindAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        return ReadAsync(async context =>
        {
            var row = await context.Set<PasskeyCredentialRow>().AsNoTracking().SingleOrDefaultAsync(row => row.CredentialId == credentialId, cancellationToken)
                .ConfigureAwait(false);
            return row is null ? null : new PasskeyCredentialRecord(row.Id, row.AccountId, row.CredentialId, row.RelyingPartyId, row.PublicKey, row.Algorithm,
                row.CreatedAt, row.KeyVersion, row.IntegrityTag);
        }, cancellationToken);
    }

    public Task<bool> RemoveAsync(Guid accountId, byte[] credentialId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        return WriteAsync(async context =>
        {
            var row = await context.Set<PasskeyCredentialRow>().SingleOrDefaultAsync(row => row.AccountId == accountId && row.CredentialId == credentialId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null) return false;
            context.Remove(row);
            return true;
        }, false, cancellationToken);
    }
}

/// <summary>Global accounts' tenant memberships, read afresh for every check.</summary>
internal sealed class TenantMembershipStore(PostgreSqlModule module) : IdentityStore(module), ITenantMembershipStore
{
    public async Task<bool> AddAsync(Guid accountId, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await WriteAsync(async context =>
            {
                if (!await context.Set<IdentityAccount>().AnyAsync(account => account.Id == accountId, cancellationToken).ConfigureAwait(false)
                    || await context.Set<TenantMembershipRow>().AnyAsync(row => row.AccountId == accountId && row.TenantId == tenantId, cancellationToken)
                        .ConfigureAwait(false)) return false;
                context.Add(new TenantMembershipRow { AccountId = accountId, TenantId = tenantId, AddedAt = now.ToUniversalTime() });
                return true;
            }, false, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent addition of the same membership, or a removal of the account, committed first.
            return false;
        }
    }

    public Task<bool> RemoveAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken) => WriteAsync(async context =>
    {
        var row = await context.Set<TenantMembershipRow>().SingleOrDefaultAsync(row => row.AccountId == accountId && row.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return false;
        context.Remove(row);
        return true;
    }, false, cancellationToken);

    public Task<bool> IsMemberAsync(Guid accountId, Guid tenantId, CancellationToken cancellationToken) => ReadAsync(context =>
        (from membership in context.Set<TenantMembershipRow>()
         join account in context.Set<IdentityAccount>() on membership.AccountId equals account.Id
         where membership.AccountId == accountId && membership.TenantId == tenantId && !account.Disabled
         select membership).AnyAsync(cancellationToken), cancellationToken);
}
