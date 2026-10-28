using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.Persistence.EfCore.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class PasswordVerifierTests(ITestOutputHelper output)
{
    private const int Policy = 210_000;

    private sealed record Verifier(byte Format, string Algorithm, int Iterations, int SaltLength, int Length, string Digest);

    // Reads the parameters ASP.NET Core Identity records in a verifier; the verifier itself is reported only as a digest.
    private static Verifier Describe(string stored)
    {
        var bytes = Convert.FromBase64String(stored);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        if (bytes[0] == 0x00) return new(0, "PBKDF2-HMAC-SHA1", 1000, 16, bytes.Length, digest);
        var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1));
        return new(bytes[0], prf switch { 0 => "PBKDF2-HMAC-SHA1", 1 => "PBKDF2-HMAC-SHA256", 2 => "PBKDF2-HMAC-SHA512", _ => "unknown" },
            (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5)), (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9)), bytes.Length, digest);
    }

    private static async Task<(string Hash, string Stamp)> StoredAsync(IdentityDatabase database, Guid account)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT \"PasswordHash\", \"SecurityStamp\" FROM " + database.Table("Accounts") + " WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", account);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetString(0), reader.GetString(1));
    }

    private static Task SetVerifierAsync(IdentityDatabase database, Guid account, string verifier) =>
        database.ExecuteAsync("UPDATE " + database.Table("Accounts") + " SET \"PasswordHash\" = @hash WHERE \"Id\" = @id",
            new NpgsqlParameter("hash", verifier), new NpgsqlParameter("id", account));

    /// <summary>An Identity version 3 verifier with the SHA-256 PRF, as earlier releases produced, which current policy supersedes.</summary>
    private static string Sha256Verifier(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Policy, HashAlgorithmName.SHA256, 32);
        var bytes = new byte[13 + salt.Length + subkey.Length];
        bytes[0] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), Policy);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(9), (uint)salt.Length);
        salt.CopyTo(bytes, 13);
        subkey.CopyTo(bytes, 13 + salt.Length);
        return Convert.ToBase64String(bytes);
    }

    [Fact(DisplayName = "TM-G02-055 verifiers are salted PBKDF2-HMAC-SHA512 at the policy cost, with algorithm and parameters recorded in each")]
    public async Task TM_G02_055_Verifiers_are_salted_adaptive_hashes_with_their_parameters_recorded()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database);
        var password = Generated.Password();
        var first = await identity.Get<IAccountService>().CreateLocalAccountAsync(await identity.Administrator.AccountsAsync(), "account-1", new Framework.Security.Abstractions.SecretText(password), TestContext.Current.CancellationToken);
        var second = await identity.Get<IAccountService>().CreateLocalAccountAsync(await identity.Administrator.AccountsAsync(), "account-2", new Framework.Security.Abstractions.SecretText(password), TestContext.Current.CancellationToken);
        var (hashA, _) = await StoredAsync(database, first.AccountId!.Value);
        var (hashB, _) = await StoredAsync(database, second.AccountId!.Value);
        var described = Describe(hashA);
        Assert.Equal(new Verifier(0x01, "PBKDF2-HMAC-SHA512", Policy, 16, 13 + 16 + 32, described.Digest), described);
        Assert.NotEqual(hashA, hashB);
        Assert.NotEqual(Convert.FromBase64String(hashA).AsSpan(13, 16).ToArray(), Convert.FromBase64String(hashB).AsSpan(13, 16).ToArray());
        Assert.All((await database.DumpAsync()), value => Assert.DoesNotContain(password, value, StringComparison.Ordinal));
        Assert.All((await database.DumpAsync()), value => Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(password)), value, StringComparison.Ordinal));
        output.WriteLine($"Two accounts with one password: {described} and {Describe(hashB)}.");
    }

    [Fact(DisplayName = "TM-G02-055 a sign-in upgrades a verifier below policy without ending sessions, and a wrong password changes nothing")]
    public async Task TM_G02_055_A_sign_in_upgrades_a_verifier_below_policy_and_a_wrong_password_changes_nothing()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var identity = PostgreSqlIdentity.Create(database);
        var accounts = new Dictionary<string, (Guid Id, string Password)>();
        foreach (var name in new[] { "account-1", "account-2", "account-3", "account-4" }) accounts[name] = await identity.AccountAsync(name);
        var oneStepBelow = new PasswordHasher<IdentityAccount>(Options.Create(new PasswordHasherOptions { IterationCount = Policy - 1 }));
        var identityV2 = new PasswordHasher<IdentityAccount>(Options.Create(new PasswordHasherOptions { CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2 }));
        await SetVerifierAsync(database, accounts["account-2"].Id, oneStepBelow.HashPassword(new IdentityAccount(), accounts["account-2"].Password));
        await SetVerifierAsync(database, accounts["account-3"].Id, identityV2.HashPassword(new IdentityAccount(), accounts["account-3"].Password));
        await SetVerifierAsync(database, accounts["account-4"].Id, Sha256Verifier(accounts["account-4"].Password));
        var before = new Dictionary<string, (string Hash, string Stamp)>();
        foreach (var (name, account) in accounts) before[name] = await StoredAsync(database, account.Id);
        foreach (var name in accounts.Keys) output.WriteLine($"{name} before: {Describe(before[name].Hash)}.");

        // A session issued before the upgrade must survive it: an upgrade is not a security change.
        var earlier = (await identity.SignInAsync("account-1", accounts["account-1"].Password)).Session!;
        foreach (var name in accounts.Keys)
        {
            Assert.Equal(SignInStatus.Failed, (await identity.SignInAsync(name, Generated.Password(), "203.0.113." + name[^1])).Status);
            Assert.Equal(before[name], await StoredAsync(database, accounts[name].Id));
        }

        var sessions = new Dictionary<string, IssuedSession>();
        foreach (var name in accounts.Keys)
        {
            var result = await identity.SignInAsync(name, accounts[name].Password, "198.51.100." + name[^1]);
            Assert.Equal(SignInStatus.Succeeded, result.Status);
            sessions[name] = result.Session!;
        }
        Assert.Equal(before["account-1"], await StoredAsync(database, accounts["account-1"].Id));
        foreach (var name in new[] { "account-2", "account-3", "account-4" })
        {
            var after = await StoredAsync(database, accounts[name].Id);
            Assert.NotEqual(before[name].Hash, after.Hash);
            Assert.Equal(before[name].Stamp, after.Stamp);
            var described = Describe(after.Hash);
            Assert.Equal(("PBKDF2-HMAC-SHA512", Policy, 16), (described.Algorithm, described.Iterations, described.SaltLength));
            output.WriteLine($"{name} after its sign-in: {described}.");
        }
        foreach (var session in sessions.Values.Append(earlier))
            Assert.NotNull(await identity.Get<ISessionService>().ValidateAsync(session.Token, TestContext.Current.CancellationToken));

        // The upgraded verifiers keep verifying, and are not upgraded again.
        var upgraded = await StoredAsync(database, accounts["account-3"].Id);
        Assert.Equal(SignInStatus.Succeeded, (await identity.SignInAsync("account-3", accounts["account-3"].Password, "198.51.100.33")).Status);
        Assert.Equal(upgraded, await StoredAsync(database, accounts["account-3"].Id));
    }
}
