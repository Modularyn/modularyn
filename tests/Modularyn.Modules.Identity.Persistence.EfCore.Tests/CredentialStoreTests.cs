using System.Text;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;
using Modularyn.Modules.Identity.Testing;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.Persistence.EfCore.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class CredentialStoreTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    // RFC 4648 base32, the encoding authenticator applications show seeds in.
    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var text = new StringBuilder();
        for (var bit = 0; bit < data.Length * 8; bit += 5)
        {
            var value = 0;
            for (var offset = 0; offset < 5; offset++)
                value = value << 1 | (bit + offset < data.Length * 8 && (data[(bit + offset) / 8] & (0x80 >> ((bit + offset) % 8))) != 0 ? 1 : 0);
            text.Append(alphabet[value]);
        }
        return text.ToString();
    }

    [Fact(DisplayName = "TM-G02-056 PostgreSQL holds only ciphertext seeds tagged with key versions, readable only through the secret store")]
    public async Task TM_G02_056_PostgreSql_holds_only_ciphertext_seeds_tagged_with_key_versions()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        using var keys = new CredentialKeyDirectory(1, 2);
        var seed = Encoding.ASCII.GetBytes(Generated.Sentinel()[..20]);
        Guid account;
        await using (var before = PostgreSqlIdentity.Create(database, keys.Settings(1, 1)))
        {
            account = (await before.AccountAsync("admin-1")).Id;
            await before.Get<IAuthenticatorSeeds>().StoreAsync(account, seed, Cancellation);
        }
        var dump = await database.DumpAsync();
        foreach (var form in new[] { Encoding.ASCII.GetString(seed), Base32(seed), Convert.ToHexStringLower(seed), Convert.ToBase64String(seed) })
            Assert.All(dump, value => Assert.DoesNotContain(form, value, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT \"KeyVersion\" FROM " + database.Table("AuthenticatorSeeds") + " WHERE \"AccountId\" = @id",
            new NpgsqlParameter("id", account)));
        output.WriteLine($"The stored seed appears in none of {dump.Count} values as text, base32, hexadecimal or base64; its row records key version 1.");

        // After rotation to version 2, the version-1 seed still verifies through the secret store, and new seeds use version 2.
        await using var after = PostgreSqlIdentity.Create(database, keys.Settings(2, 1, 2));
        var seeds = after.Get<IAuthenticatorSeeds>();
        Assert.Equal(CredentialVerification.Accepted, await seeds.VerifyAsync(account, presented => presented.Span.SequenceEqual(seed), "203.0.113.9", Cancellation));
        var second = (await after.AccountAsync("admin-2")).Id;
        await seeds.StoreAsync(second, seed, Cancellation);
        Assert.Equal(2, await database.ScalarAsync<int>("SELECT \"KeyVersion\" FROM " + database.Table("AuthenticatorSeeds") + " WHERE \"AccountId\" = @id",
            new NpgsqlParameter("id", second)));

        // An edited ciphertext or a row moved to another account never decrypts; with the key gone, nothing can be read.
        await database.ExecuteAsync("UPDATE " + database.Table("AuthenticatorSeeds") + " SET \"Ciphertext\" = set_byte(\"Ciphertext\", 0, get_byte(\"Ciphertext\", 0) # 1)"
            + " WHERE \"AccountId\" = @id", new NpgsqlParameter("id", account));
        Assert.Equal(CredentialVerification.Unavailable, await seeds.VerifyAsync(account, _ => true, "203.0.113.9", Cancellation));
        var third = (await after.AccountAsync("admin-3")).Id;
        await database.ExecuteAsync("INSERT INTO " + database.Table("AuthenticatorSeeds") + " SELECT @to, \"KeyVersion\", \"Nonce\", \"Ciphertext\", \"Tag\", \"CreatedAt\" FROM "
            + database.Table("AuthenticatorSeeds") + " WHERE \"AccountId\" = @from", new NpgsqlParameter("to", third), new NpgsqlParameter("from", second));
        Assert.Equal(CredentialVerification.Unavailable, await seeds.VerifyAsync(third, _ => true, "203.0.113.9", Cancellation));
        keys.Remove(2);
        Assert.Equal(CredentialVerification.Unavailable, await seeds.VerifyAsync(second, _ => true, "203.0.113.9", Cancellation));
    }

    [Fact(DisplayName = "TM-G02-057 PostgreSQL holds recovery-code verifiers only, accepts a code once under concurrency and invalidates a replaced set")]
    public async Task TM_G02_057_PostgreSql_accepts_a_recovery_code_once_and_invalidates_a_replaced_set()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database);
        var (account, _) = await identity.AccountAsync("admin-1");
        var codes = identity.Get<IRecoveryCodes>();
        var set = await codes.GenerateAsync(account, Cancellation);
        var dump = await database.DumpAsync();
        foreach (var code in set.Codes)
        {
            var normalized = RecoveryCodes.Normalize(code.Reveal())!;
            Assert.All(dump, value =>
            {
                Assert.DoesNotContain(normalized, value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(code.Reveal(), value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.ASCII.GetBytes(normalized)), value, StringComparison.Ordinal);
            });
        }
        Assert.Equal(10L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("RecoveryCodes") + " WHERE \"AccountId\" = @id AND \"Iterations\" = @iterations"
            + " AND length(\"Salt\") = 16 AND length(\"Verifier\") = 32", new NpgsqlParameter("id", account), new NpgsqlParameter("iterations", RecoveryCodes.Iterations)));

        // Eight concurrent attempts with one code, each from its own source: exactly one is accepted and the code is consumed once.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => codes.ConsumeAsync(account, set.Codes[0], "198.51.100." + index, Cancellation)));
        Assert.Equal(1, attempts.Count(result => result.Status == CredentialVerificationStatus.Accepted));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("RecoveryCodes") + " WHERE \"ConsumedAt\" IS NOT NULL"));

        // The store's conditional write alone also accepts one of many concurrent consumptions of the same verifier.
        var usable = await identity.Get<IRecoveryCodeStore>().FindUsableAsync(account, Cancellation);
        var direct = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => identity.Get<IRecoveryCodeStore>().ConsumeAsync(usable[0].Id, identity.Clock.Now, Cancellation)));
        Assert.Equal(1, direct.Count(consumed => consumed));

        await codes.GenerateAsync(account, Cancellation);
        Assert.Equal(8L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("RecoveryCodes") + " WHERE \"InvalidatedAt\" IS NOT NULL"));
        identity.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CredentialVerification.Rejected, await codes.ConsumeAsync(account, set.Codes[2], "198.51.100.99", Cancellation));
        output.WriteLine("One of eight concurrent attempts consumed the code; a replaced set's codes were invalidated and refused.");
    }

    [Fact(DisplayName = "TM-G02-058 passkey records altered in PostgreSQL fail integrity verification before they are trusted")]
    public async Task TM_G02_058_Passkey_records_altered_in_PostgreSql_fail_integrity_verification()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        using var keys = new CredentialKeyDirectory(1);
        await using var identity = PostgreSqlIdentity.Create(database, keys.Settings(1, 1));
        var (admin, _) = await identity.AccountAsync("admin-1");
        var (editor, _) = await identity.AccountAsync("editor-1");
        var passkeys = identity.Get<IPasskeyCredentials>();
        var adminCredential = Bytes(32);
        var editorCredential = Bytes(32);
        Assert.True(await passkeys.EnrollAsync(new PasskeyEnrollment(admin, adminCredential, "app.example.test", Bytes(77), -7), Cancellation));
        Assert.True(await passkeys.EnrollAsync(new PasskeyEnrollment(editor, editorCredential, "app.example.test", Bytes(77), -7), Cancellation));
        Assert.False(await passkeys.EnrollAsync(new PasskeyEnrollment(editor, adminCredential, "app.example.test", Bytes(77), -7), Cancellation));
        Assert.Equal(PasskeyLookupStatus.Verified, (await passkeys.FindForSignInAsync(adminCredential, "app.example.test", Cancellation)).Status);

        var table = database.Table("PasskeyCredentials");
        var alterations = new (string Description, string Change)[]
        {
            ("public key replaced with editor-1's", "\"PublicKey\" = (SELECT \"PublicKey\" FROM " + table + " WHERE \"AccountId\" = @editor)"),
            ("record rebound to editor-1's account", "\"AccountId\" = @editor"),
            ("relying party changed", "\"RelyingPartyId\" = 'other.example.test'"),
            ("algorithm changed", "\"Algorithm\" = -257")
        };
        foreach (var (description, change) in alterations)
        {
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(Cancellation);
                await using var transaction = await connection.BeginTransactionAsync(Cancellation);
                await using (var alter = new NpgsqlCommand("UPDATE " + table + " SET " + change + " WHERE \"CredentialId\" = @credential", connection, transaction))
                {
                    alter.Parameters.AddWithValue("editor", editor);
                    alter.Parameters.AddWithValue("credential", adminCredential);
                    Assert.Equal(1, await alter.ExecuteNonQueryAsync(Cancellation));
                }
                await transaction.CommitAsync(Cancellation);
            }
            Assert.Equal(PasskeyLookupStatus.IntegrityFailure, (await passkeys.FindForSignInAsync(adminCredential, "app.example.test", Cancellation)).Status);
            Assert.Equal(PasskeyLookupStatus.IntegrityFailure, (await passkeys.FindForSignInAsync(adminCredential, "other.example.test", Cancellation)).Status);
            output.WriteLine($"Altered record ({description}) failed integrity verification.");
            // Restore the enrolled record for the next alteration.
            Assert.True(await passkeys.RevokeAsync(admin, adminCredential, Cancellation) || await passkeys.RevokeAsync(editor, adminCredential, Cancellation));
            Assert.True(await passkeys.EnrollAsync(new PasskeyEnrollment(admin, adminCredential, "app.example.test", Bytes(77), -7), Cancellation));
        }
        Assert.Equal(PasskeyLookupStatus.Verified, (await passkeys.FindForSignInAsync(editorCredential, "app.example.test", Cancellation)).Status);
        Assert.True(await passkeys.RevokeAsync(admin, adminCredential, Cancellation));
        Assert.Equal(PasskeyLookupStatus.NotFound, (await passkeys.FindForSignInAsync(adminCredential, "app.example.test", Cancellation)).Status);
    }

    [Fact(DisplayName = "TM-G02-025 tenant memberships on PostgreSQL are account records read afresh for every check")]
    public async Task TM_G02_025_Tenant_memberships_are_account_records_read_afresh()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database);
        var (account, _) = await identity.AccountAsync("member-1");
        var memberships = identity.Get<ITenantMemberships>();
        var tenant = Guid.NewGuid();
        Assert.True(await memberships.AddMemberAsync(account, tenant, Cancellation));
        Assert.False(await memberships.AddMemberAsync(account, tenant, Cancellation));
        Assert.True(await memberships.IsMemberAsync(account, tenant, Cancellation));
        var added = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => memberships.AddMemberAsync(account, Guid.Parse("00000000-0000-0000-0000-00000000000a"), Cancellation)));
        Assert.Equal(1, added.Count(result => result));

        // A change made directly in the store is seen at the next check: nothing is cached.
        await database.ExecuteAsync("DELETE FROM " + database.Table("TenantMemberships") + " WHERE \"TenantId\" = @tenant", new NpgsqlParameter("tenant", tenant));
        Assert.False(await memberships.IsMemberAsync(account, tenant, Cancellation));
        Assert.True(await memberships.AddMemberAsync(account, tenant, Cancellation));
        Assert.True(await identity.Get<IAccountService>().DisableAsync(account, Cancellation));
        Assert.False(await memberships.IsMemberAsync(account, tenant, Cancellation));
        Assert.True(await identity.Get<IAccountService>().EnableAsync(account, Cancellation));
        Assert.True(await memberships.RemoveMemberAsync(account, tenant, Cancellation));
        Assert.False(await memberships.IsMemberAsync(account, tenant, Cancellation));
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Accounts")));
    }

    private static byte[] Bytes(int length) => System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
}
