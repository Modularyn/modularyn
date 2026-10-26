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

    [Fact(DisplayName = "TM-G02-015 on PostgreSQL, concurrent attempts never verify more than the per-account and per-source limits allow")]
    public async Task TM_G02_015_Concurrent_attempts_never_verify_more_than_the_limits_allow()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database, new() { ["Modularyn:Identity:Throttling:SourceThreshold"] = "1000" });
        var (_, password) = await identity.AccountAsync("admin-1");
        var verified = identity.Hasher.Verifications;
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => identity.SignInAsync("admin-1", Generated.Password(), "198.51.100." + index)));
        Assert.Equal(3, identity.Hasher.Verifications - verified);
        Assert.Equal(3, results.Count(result => result.Status == SignInStatus.Failed));
        Assert.Equal(13, results.Count(result => result.Status == SignInStatus.Throttled));
        Assert.Equal(SignInStatus.Throttled, (await identity.SignInAsync("admin-1", password, "198.51.100.200")).Status);
        output.WriteLine($"16 concurrent guesses for one account: {results.Count(result => result.Status == SignInStatus.Failed)} verified, the rest refused unverified.");

        await using var bySource = PostgreSqlIdentity.Create(database, new() { ["Modularyn:Identity:Throttling:AccountFreeAttempts"] = "50" });
        var names = Enumerable.Range(0, 12).Select(index => "user-" + index).ToArray();
        foreach (var name in names) await bySource.AccountAsync(name);
        verified = bySource.Hasher.Verifications;
        var spread = await Task.WhenAll(names.Select(name => bySource.SignInAsync(name, Generated.Password(), "203.0.113.50")));
        Assert.Equal(6, bySource.Hasher.Verifications - verified);
        Assert.Equal(6, spread.Count(result => result.Status == SignInStatus.Throttled));
        output.WriteLine("12 concurrent guesses from one source across 12 accounts: 6 verified before the source was blocked.");
    }

    [Fact(DisplayName = "TM-G02-015 throttle rows hold hashed keys only, never a typed user name or address")]
    public async Task TM_G02_015_Throttle_rows_hold_hashed_keys_only()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database);
        var typed = Generated.Sentinel();
        Assert.Equal(SignInStatus.Failed, (await identity.SignInAsync(typed, Generated.Password(), "192.0.2.77")).Status);
        Assert.Equal(2L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("VerificationThrottle") + " WHERE \"Failures\" = 1"));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("VerificationThrottle") + " WHERE \"Key\" = @key",
            new NpgsqlParameter("key", ThrottleKey.ForUserName(ThrottleKind.Password, typed).Hash.ToArray())));
        Assert.All(await database.DumpAsync(), value =>
        {
            Assert.DoesNotContain(typed, value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(typed.ToUpperInvariant())), value, StringComparison.Ordinal);
            Assert.DoesNotContain("192.0.2.77", value, StringComparison.Ordinal);
        });
    }
}
