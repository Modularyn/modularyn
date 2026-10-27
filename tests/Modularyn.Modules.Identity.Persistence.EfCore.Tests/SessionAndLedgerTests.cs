using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;
using Modularyn.Modules.Identity.Testing;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.Persistence.EfCore.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class SessionAndLedgerTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static async Task<(DateTimeOffset? Ended, int? Reason, DateTimeOffset LastSeen)> RecordAsync(IdentityDatabase database, Guid session)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(Cancellation);
        await using var command = new NpgsqlCommand("SELECT \"EndedAt\", \"EndReason\", \"LastSeenAt\" FROM " + database.Table("Sessions") + " WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", session);
        await using var reader = await command.ExecuteReaderAsync(Cancellation);
        Assert.True(await reader.ReadAsync(Cancellation));
        return (reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    [Fact(DisplayName = "TM-G02-016 PostgreSQL keeps each session as a server-side record holding only the SHA-256 of its token")]
    public async Task TM_G02_016_PostgreSql_keeps_only_the_hash_of_each_session_token()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database);
        var (account, password) = await identity.AccountAsync("editor-1");
        var session = (await identity.SignInAsync("editor-1", password)).Session!;
        var token = session.Token.Reveal();
        var raw = Base64Url.DecodeFromChars(token);
        var dump = await database.DumpAsync();
        Assert.All(dump, value =>
        {
            Assert.DoesNotContain(token, value, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexStringLower(raw), value, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.ASCII.GetBytes(token)), value, StringComparison.Ordinal);
        });
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Sessions")
            + " WHERE \"Id\" = @id AND \"AccountId\" = @account AND \"TokenHash\" = @hash AND \"EndedAt\" IS NULL AND \"AbsoluteExpiresAt\" = @expires",
            new NpgsqlParameter("id", session.SessionId), new NpgsqlParameter("account", account), new NpgsqlParameter("hash", SHA256.HashData(raw)),
            new NpgsqlParameter("expires", session.ExpiresAt)));
        output.WriteLine($"Session {session.SessionId:D} is stored with its token's SHA-256 only; the token appears in none of {dump.Count} stored values.");
    }

    [Fact(DisplayName = "TM-G02-016 on PostgreSQL, idle and absolute limits end sessions at their edges and record the reason")]
    public async Task TM_G02_016_Idle_and_absolute_limits_end_sessions_at_their_edges_and_record_the_reason()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database);
        var (_, password) = await identity.AccountAsync("editor-1");
        var sessions = identity.Get<ISessionService>();
        var idle = TimeSpan.FromMinutes(20);
        var absolute = TimeSpan.FromHours(8);

        var idleSession = (await identity.SignInAsync("editor-1", password)).Session!;
        var activeSession = (await identity.SignInAsync("editor-1", password)).Session!;
        identity.Clock.Advance(idle - TimeSpan.FromSeconds(1));
        Assert.NotNull(await sessions.ValidateAsync(idleSession.Token, Cancellation));
        Assert.NotNull(await sessions.ValidateAsync(activeSession.Token, Cancellation));
        Assert.Equal(identity.Clock.Now, (await RecordAsync(database, idleSession.SessionId)).LastSeen);
        identity.Clock.Advance(idle);
        Assert.Null(await sessions.ValidateAsync(idleSession.Token, Cancellation));
        Assert.Equal((identity.Clock.Now, (int)SessionEndReason.IdleTimeout), ((await RecordAsync(database, idleSession.SessionId)).Ended, (await RecordAsync(database, idleSession.SessionId)).Reason));
        Assert.Null(await sessions.ValidateAsync(activeSession.Token, Cancellation));

        // A session kept active below its idle timeout ends at its absolute deadline.
        var kept = (await identity.SignInAsync("editor-1", password)).Session!;
        var deadline = kept.ExpiresAt;
        Assert.Equal(kept.AuthenticatedAt + absolute, deadline);
        while (identity.Clock.Now + idle - TimeSpan.FromMinutes(1) < deadline - TimeSpan.FromSeconds(1))
        {
            identity.Clock.Advance(idle - TimeSpan.FromMinutes(1));
            Assert.NotNull(await sessions.ValidateAsync(kept.Token, Cancellation));
        }
        identity.Clock.Set(deadline - TimeSpan.FromSeconds(1));
        Assert.NotNull(await sessions.ValidateAsync(kept.Token, Cancellation));
        identity.Clock.Set(deadline + TimeSpan.FromSeconds(1));
        Assert.Null(await sessions.ValidateAsync(kept.Token, Cancellation));
        var record = await RecordAsync(database, kept.SessionId);
        Assert.Equal((identity.Clock.Now, (int)SessionEndReason.AbsoluteLifetime), (record.Ended, record.Reason));
        output.WriteLine($"Idle edge {idle}, absolute edge {absolute}: each request inside a limit succeeded and the first past it was refused with its reason recorded.");
    }

    [Fact(DisplayName = "TM-G02-016 under a clock with 100 ns ticks, stored times equal the times in memory, so every limit decides the same before and after a round trip")]
    public async Task TM_G02_016_Under_a_sub_microsecond_clock_limits_decide_the_same_before_and_after_a_round_trip()
    {
        // Ticks that PostgreSQL's microseconds cannot hold, as a Linux clock reports them, on every platform.
        var start = new DateTimeOffset(2026, 10, 27, 0, 3, 53, TimeSpan.Zero).AddTicks(9_999_996);
        var clock = new ManualClock(start);
        var limit = TimeSpan.FromMinutes(15);
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database, new()
        {
            ["Modularyn:Identity:Sessions:IdleTimeout"] = "00:15:00",
            ["Modularyn:Identity:Sessions:AbsoluteLifetime"] = "00:30:00"
        }, clock);
        var sessions = identity.Get<ISessionService>();
        var (_, password) = await identity.AccountAsync("editor-1");
        var idle = (await identity.SignInAsync("editor-1", password)).Session!;
        var idleEdge = (await identity.SignInAsync("editor-1", password)).Session!;
        var absolute = (await identity.SignInAsync("editor-1", password)).Session!;

        // What the module holds in memory is what the database keeps: whole microseconds, no later than the clock.
        foreach (var issued in new[] { idle, idleEdge, absolute })
        {
            var stored = (await sessions.DescribeAsync(issued.SessionId, Cancellation))!;
            Assert.Equal((issued.AuthenticatedAt, issued.AuthenticatedAt, issued.ExpiresAt), (stored.AuthenticatedAt, stored.LastSeenAt, stored.ExpiresAt));
            Assert.Equal(issued.AuthenticatedAt, (await RecordAsync(database, issued.SessionId)).LastSeen);
            Assert.True(StoredTime.IsNormalized(issued.AuthenticatedAt) && StoredTime.IsNormalized(issued.ExpiresAt));
            Assert.InRange(start - issued.AuthenticatedAt, TimeSpan.Zero, TimeSpan.FromTicks(9));
        }

        // The idle limit, one tick either side of the edge the issued session implies.
        clock.Set(idle.AuthenticatedAt + limit - TimeSpan.FromTicks(1));
        Assert.NotNull(await sessions.ValidateAsync(idle.Token, Cancellation));
        clock.Set(idleEdge.AuthenticatedAt + limit);
        Assert.Null(await sessions.ValidateAsync(idleEdge.Token, Cancellation));
        Assert.Equal(SessionEndCause.IdleTimeout, (await sessions.DescribeAsync(idleEdge.SessionId, Cancellation))!.EndCause);

        // The absolute limit, at the deadline the issued session reported, after activity kept it from idling.
        foreach (var minutes in new[] { 14, 28 })
        {
            clock.Set(absolute.AuthenticatedAt + TimeSpan.FromMinutes(minutes));
            Assert.NotNull(await sessions.ValidateAsync(absolute.Token, Cancellation));
        }
        clock.Set(absolute.ExpiresAt - TimeSpan.FromTicks(1));
        Assert.NotNull(await sessions.ValidateAsync(absolute.Token, Cancellation));
        clock.Set(absolute.ExpiresAt);
        Assert.Null(await sessions.ValidateAsync(absolute.Token, Cancellation));
        Assert.Equal(SessionEndCause.AbsoluteLifetime, (await sessions.DescribeAsync(absolute.SessionId, Cancellation))!.EndCause);

        // A delegation's window comes from the caller: the stored grant starts no earlier and ends no later than requested,
        // and it is pending, active and expired at the same instants before and after the round trip.
        clock.Set(start + TimeSpan.FromHours(1));
        var (subjectId, subjectPassword) = await identity.AccountAsync("user-1");
        var (actorId, actorPassword) = await identity.AccountAsync("support-1");
        var subject = (await sessions.ValidateAsync((await identity.SignInAsync("user-1", subjectPassword)).Session!.Token, Cancellation))!.ToPrincipal();
        var actor = (await sessions.ValidateAsync((await identity.SignInAsync("support-1", actorPassword)).Session!.Token, Cancellation))!.ToPrincipal();
        var notBefore = clock.Now + TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(3);
        var expiresAt = clock.Now + TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(7);
        var delegations = identity.Get<IDelegationService>();
        var grant = (await delegations.IssueAsync(new DelegationRequest(actorId, subjectId, ["item.read"], "Support case", null, expiresAt, notBefore), subject, Cancellation)).GrantId!.Value;
        var view = (await delegations.DescribeAsync(grant, Cancellation))!;
        Assert.Equal((StoredTime.Ceiling(notBefore), StoredTime.Floor(expiresAt)), (view.NotBefore, view.ExpiresAt));
        Assert.True(StoredTime.IsNormalized(view.IssuedAt));
        clock.Set(view.NotBefore - TimeSpan.FromTicks(1));
        Assert.Null(await delegations.ResolveAsync(grant, actor, Cancellation));
        clock.Set(view.NotBefore);
        var delegated = await delegations.ResolveAsync(grant, actor, Cancellation);
        Assert.NotNull(delegated);
        clock.Set(view.ExpiresAt - TimeSpan.FromTicks(1));
        Assert.NotNull(await delegations.ResolveAsync(grant, actor, Cancellation));
        Assert.True(delegated!.IsWithinCeiling("item.read", null, clock.Now));
        clock.Set(view.ExpiresAt);
        Assert.Null(await delegations.ResolveAsync(grant, actor, Cancellation));
        Assert.False(delegated.IsWithinCeiling("item.read", null, clock.Now));

        // An account delay ends at the retry time the module reports, which is whole microseconds.
        clock.Set(start + TimeSpan.FromHours(2));
        await identity.AccountAsync("admin-1");
        for (var failure = 0; failure < 3; failure++)
            Assert.Equal(SignInStatus.Failed, (await identity.SignInAsync("admin-1", Generated.Password(), "198.51.100." + failure)).Status);
        var throttled = await identity.SignInAsync("admin-1", Generated.Password(), "198.51.100.10");
        Assert.Equal(SignInStatus.Throttled, throttled.Status);
        Assert.True(StoredTime.IsNormalized(throttled.RetryAt!.Value));
        clock.Set(throttled.RetryAt.Value - TimeSpan.FromTicks(1));
        Assert.Equal(SignInStatus.Throttled, (await identity.SignInAsync("admin-1", Generated.Password(), "198.51.100.11")).Status);
        clock.Set(throttled.RetryAt.Value);
        Assert.Equal(SignInStatus.Failed, (await identity.SignInAsync("admin-1", Generated.Password(), "198.51.100.12")).Status);

        // A time that bypassed the module's clock is refused when it would be written, never truncated silently, and the
        // record is left as it was.
        var store = identity.Get<ISessionStore>();
        var raw = start + TimeSpan.FromHours(3);
        var unnormalized = new SessionRecord(Guid.NewGuid(), SessionToken.Create().ComputeHash(), idle.AccountId, raw, raw, raw + limit, "stamp", null, null);
        Assert.Contains("whole microseconds", (await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(unnormalized, Cancellation))).ToString());
        Assert.Null(await store.FindAsync(unnormalized.Id, Cancellation));
        Assert.Contains("whole microseconds", (await Assert.ThrowsAnyAsync<Exception>(() =>
            store.EndAsync(idle.SessionId, SessionEndReason.Revoked, raw, Cancellation))).ToString());
        Assert.Null((await RecordAsync(database, idle.SessionId)).Ended);
        output.WriteLine("Sessions, a delegation and an account delay decided their limits at the instants the module reported, before and after PostgreSQL.");
    }

    [Fact(DisplayName = "TM-G02-015 on PostgreSQL, concurrent attempts from two hosts never verify more than the per-account and per-source limits allow")]
    public async Task TM_G02_015_Concurrent_attempts_never_verify_more_than_the_limits_allow()
    {
        // Two service providers stand for two hosts: each admits one attempt per counter, so only the database's row locks
        // decide between them.
        await using var database = await IdentityDatabase.CreateAsync();
        var clock = Testing.ManualClock.StartingNow();
        await using var first = PostgreSqlIdentity.Create(database, new() { ["Modularyn:Identity:Throttling:SourceThreshold"] = "1000" }, clock);
        await using var second = PostgreSqlIdentity.Create(database, new() { ["Modularyn:Identity:Throttling:SourceThreshold"] = "1000" }, clock);
        var (_, password) = await first.AccountAsync("admin-1");
        int Verified() => first.Hasher.Verifications + second.Hasher.Verifications;
        var before = Verified();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            (index % 2 == 0 ? first : second).SignInAsync("admin-1", Generated.Password(), "198.51.100." + index)));
        Assert.InRange(Verified() - before, 1, 3);
        Assert.All(results, result => Assert.NotEqual(SignInStatus.Succeeded, result.Status));
        for (var attempt = 0; attempt < 6; attempt++)
            await (attempt % 2 == 0 ? first : second).SignInAsync("admin-1", Generated.Password(), "198.51.100." + (100 + attempt));
        Assert.Equal(3, Verified() - before);
        Assert.Equal(SignInStatus.Throttled, (await first.SignInAsync("admin-1", password, "198.51.100.200")).Status);
        output.WriteLine("Concurrent and later guesses for one account across two hosts: exactly 3 verified, every other refused unverified.");

        await using var third = PostgreSqlIdentity.Create(database, new() { ["Modularyn:Identity:Throttling:AccountFreeAttempts"] = "50" }, clock);
        await using var fourth = PostgreSqlIdentity.Create(database, new() { ["Modularyn:Identity:Throttling:AccountFreeAttempts"] = "50" }, clock);
        var names = Enumerable.Range(0, 12).Select(index => "user-" + index).ToArray();
        foreach (var name in names) await third.AccountAsync(name);
        int SourceVerified() => third.Hasher.Verifications + fourth.Hasher.Verifications;
        before = SourceVerified();
        await Task.WhenAll(names.Select((name, index) => (index % 2 == 0 ? third : fourth).SignInAsync(name, Generated.Password(), "203.0.113.50")));
        Assert.InRange(SourceVerified() - before, 1, 6);
        foreach (var name in names) await third.SignInAsync(name, Generated.Password(), "203.0.113.50");
        Assert.Equal(6, SourceVerified() - before);
        output.WriteLine("Guesses from one source across 12 accounts and two hosts: exactly 6 verified before the source was blocked.");
    }

    [Fact(DisplayName = "TM-G02-015 on PostgreSQL, a source renewing an account's delay counts its refusals and meets its own longer delay, so the owner signs in")]
    public async Task TM_G02_015_On_PostgreSql_a_source_renewing_a_delay_cannot_keep_the_owner_out()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database, new() { ["Modularyn:Identity:Throttling:SourceThreshold"] = "1000" });
        var (_, password) = await identity.AccountAsync("admin-1");
        // Three free failures from the attacker; the account's delay is then 2 s and the attacker's own 4 s.
        for (var failure = 0; failure < 3; failure++)
            Assert.Equal(SignInStatus.Failed, (await identity.SignInAsync("admin-1", Generated.Password(), "198.51.100.66")).Status);
        identity.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(SignInStatus.Throttled, (await identity.SignInAsync("admin-1", Generated.Password(), "198.51.100.66")).Status);
        Assert.Equal(SignInStatus.Succeeded, (await identity.SignInAsync("admin-1", password, "203.0.113.9")).Status);

        var source = ThrottleKey.ForSource("198.51.100.66");
        var account = ThrottleKey.ForUserName(ThrottleKind.Password, "admin-1");
        async Task<long> FailuresAsync(ThrottleKey key) => await database.ScalarAsync<int>("SELECT \"Failures\" FROM " + database.Table("VerificationThrottle")
            + " WHERE \"Key\" = @key AND \"Kind\" = @kind", new NpgsqlParameter("key", key.Hash.ToArray()), new NpgsqlParameter("kind", (int)key.Kind));
        // The refusal counted against the source, the owner's success cleared the account, and the attacker's own counter stays.
        Assert.Equal(4L, await FailuresAsync(source));
        Assert.Equal(0L, await FailuresAsync(account));
        Assert.Equal(3L, await FailuresAsync(ThrottleKey.ForSourceOnSubject(source, account)));
        output.WriteLine("The attacker's poll at the account's retry instant met its own delay; the owner signed in from another source.");
    }

    [Fact(DisplayName = "TM-G02-015 throttle rows hold hashed keys only, never a typed user name or address")]
    public async Task TM_G02_015_Throttle_rows_hold_hashed_keys_only()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database);
        var typed = Generated.Sentinel();
        Assert.Equal(SignInStatus.Failed, (await identity.SignInAsync(typed, Generated.Password(), "192.0.2.77")).Status);
        // The source's counter, the account's, and the source's own counter on the account, each keyed by a hash.
        Assert.Equal(3L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("VerificationThrottle") + " WHERE \"Failures\" = 1"));
        var source = ThrottleKey.ForSource("192.0.2.77");
        var account = ThrottleKey.ForUserName(ThrottleKind.Password, typed);
        foreach (var key in new[] { source, account, ThrottleKey.ForSourceOnSubject(source, account) })
            Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("VerificationThrottle") + " WHERE \"Key\" = @key",
                new NpgsqlParameter("key", key.Hash.ToArray())));
        Assert.All(await database.DumpAsync(), value =>
        {
            Assert.DoesNotContain(typed, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(typed.ToUpperInvariant())), value, StringComparison.Ordinal);
            Assert.DoesNotContain("192.0.2.77", value, StringComparison.Ordinal);
        });
    }
}
