using System.Text;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

// The stored forms of credentials whose journeys arrive at G-03, exercised through the application services with keys
// read from a real secret-store directory. The PostgreSQL tests repeat them against the real stores.
public sealed class CredentialFormServiceTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static Func<ReadOnlyMemory<byte>, bool> Equals(byte[] expected) => presented => presented.Span.SequenceEqual(expected);

    [Fact(DisplayName = "TM-G02-056 seeds are encrypted under the current key, decrypt only to verify, and survive rotation")]
    public async Task TM_G02_056_Seeds_are_encrypted_under_versioned_keys_and_survive_rotation()
    {
        using var keys = new CredentialKeyDirectory(1, 2);
        var account = Guid.NewGuid();
        var seed = Bytes.Random(20);
        ProtectedSeed stored;
        await using (var before = UnitIdentity.Create(keys.Settings(1, 1)))
        {
            await before.Get<IAuthenticatorSeeds>().StoreAsync(account, seed, Cancellation);
            stored = before.Stores.Seeds[account];
            Assert.Equal(1, stored.KeyVersion);
            Assert.Equal((12, 16, 20), (stored.Nonce.Length, stored.Tag.Length, stored.Ciphertext.Length));
            Assert.NotEqual(seed, stored.Ciphertext);
            Assert.Equal(CredentialVerification.Accepted, await before.Get<IAuthenticatorSeeds>().VerifyAsync(account, Equals(seed), "src-1", Cancellation));
            Assert.Equal(CredentialVerificationStatus.Rejected,
                (await before.Get<IAuthenticatorSeeds>().VerifyAsync(account, Equals(Bytes.Random(20)), "src-1", Cancellation)).Status);
        }

        // After rotation to version 2 the earlier seed still verifies under version 1, and new seeds use version 2.
        await using var after = UnitIdentity.Create(keys.Settings(2, 1, 2));
        after.Stores.Seeds[account] = stored;
        var seeds = after.Get<IAuthenticatorSeeds>();
        Assert.Equal(CredentialVerification.Accepted, await seeds.VerifyAsync(account, Equals(seed), "src-1", Cancellation));
        var second = Guid.NewGuid();
        await seeds.StoreAsync(second, seed, Cancellation);
        Assert.Equal(2, after.Stores.Seeds[second].KeyVersion);
        Assert.NotEqual(stored.Ciphertext, after.Stores.Seeds[second].Ciphertext);

        // The plaintext exists only during the check and is cleared afterwards.
        ReadOnlyMemory<byte> seen = default;
        Assert.Equal(CredentialVerification.Accepted, await seeds.VerifyAsync(account, presented => { seen = presented; return presented.Span.SequenceEqual(seed); },
            "src-1", Cancellation));
        Assert.All(seen.ToArray(), value => Assert.Equal(0, value));

        // An altered ciphertext, a seed moved to another account, a relabeled key version and a missing key never verify.
        after.Stores.Seeds[account] = stored with { Ciphertext = Flip(stored.Ciphertext) };
        Assert.Equal(CredentialVerification.Unavailable, await seeds.VerifyAsync(account, Equals(seed), "src-1", Cancellation));
        var moved = Guid.NewGuid();
        after.Stores.Seeds[moved] = stored with { AccountId = moved };
        Assert.Equal(CredentialVerification.Unavailable, await seeds.VerifyAsync(moved, Equals(seed), "src-1", Cancellation));
        after.Stores.Seeds[account] = stored with { KeyVersion = 2 };
        Assert.Equal(CredentialVerification.Unavailable, await seeds.VerifyAsync(account, Equals(seed), "src-1", Cancellation));
        after.Stores.Seeds[account] = stored;
        keys.Remove(1);
        Assert.Equal(CredentialVerification.Unavailable, await seeds.VerifyAsync(account, _ => true, "src-1", Cancellation));
        Assert.Equal(CredentialVerification.Unavailable, await seeds.VerifyAsync(Guid.NewGuid(), _ => true, "src-1", Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => seeds.StoreAsync(account, Bytes.Random(8), Cancellation));
    }

    [Fact(DisplayName = "TM-G02-056 without a usable key, seeds are neither stored nor verified")]
    public async Task TM_G02_056_Without_a_usable_key_seeds_are_neither_stored_nor_verified()
    {
        await using (var none = UnitIdentity.Create())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => none.Get<IAuthenticatorSeeds>().StoreAsync(Guid.NewGuid(), Bytes.Random(20), Cancellation));
            Assert.Empty(none.Stores.Seeds);
        }
        using var keys = new CredentialKeyDirectory(1);
        keys.Write(1, Bytes.Random(16));
        await using var shortKey = UnitIdentity.Create(keys.Settings(1, 1));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => shortKey.Get<IAuthenticatorSeeds>().StoreAsync(Guid.NewGuid(), Bytes.Random(20), Cancellation));
        Assert.Contains("version 1", failure.Message, StringComparison.Ordinal);
        Assert.Empty(shortKey.Stores.Seeds);
    }

    [Fact(DisplayName = "TM-G02-057 recovery codes are shown once, stored as verifiers, consumed once and invalidated by a new set")]
    public async Task TM_G02_057_Recovery_codes_are_stored_as_verifiers_and_consumed_once()
    {
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits());
        var (account, _) = await identity.AccountAsync("admin-1");
        var codes = identity.Get<IRecoveryCodes>();
        var set = await codes.GenerateAsync(account, Cancellation);
        Assert.Equal(RecoveryCodes.CodesPerSet, set.Codes.Count);
        Assert.Equal(set.Codes.Count, set.Codes.Select(code => code.Reveal()).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(set.Codes[0].Reveal(), set.ToString(), StringComparison.Ordinal);
        foreach (var (verifier, _, _) in identity.Stores.RecoveryCodes.Values)
            foreach (var code in set.Codes)
                Assert.Equal(-1, verifier.Verifier.AsSpan().IndexOf(Encoding.ASCII.GetBytes(RecoveryCodes.Normalize(code.Reveal())!)));
        Assert.Equal(10, await codes.CountUsableAsync(account, Cancellation));

        Assert.Equal(CredentialVerification.Accepted, await codes.ConsumeAsync(account, set.Codes[0], "src-1", Cancellation));
        Assert.Equal(CredentialVerification.Rejected, await codes.ConsumeAsync(account, set.Codes[0], "src-1", Cancellation));
        Assert.Equal(9, await codes.CountUsableAsync(account, Cancellation));

        var renewed = await codes.GenerateAsync(account, Cancellation);
        Assert.Equal(CredentialVerification.Rejected, await codes.ConsumeAsync(account, set.Codes[1], "src-1", Cancellation));
        Assert.Equal(10, await codes.CountUsableAsync(account, Cancellation));
        Assert.Equal(CredentialVerification.Accepted, await codes.ConsumeAsync(account, new SecretText(renewed.Codes[1].Reveal().ToLowerInvariant()), "src-1", Cancellation));

        // Guessing counts against the account's recovery-code limit, separately from its password.
        for (var guess = 0; guess < 3; guess++)
            Assert.Equal(CredentialVerification.Rejected, await codes.ConsumeAsync(account, new SecretText("ABCD-EFGH-JKLM-NPQR"), "src-2", Cancellation));
        var throttled = await codes.ConsumeAsync(account, renewed.Codes[2], "src-3", Cancellation);
        Assert.Equal(CredentialVerificationStatus.Throttled, throttled.Status);
        Assert.Equal(9, await codes.CountUsableAsync(account, Cancellation));
        Assert.Equal(ThrottleCounter.Empty, identity.Stores.Counter(ThrottleKey.ForUserName(ThrottleKind.Password, "admin-1")));
        identity.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(CredentialVerification.Accepted, await codes.ConsumeAsync(account, renewed.Codes[2], "src-3", Cancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => codes.GenerateAsync(Guid.NewGuid(), Cancellation));
    }

    [Fact(DisplayName = "TM-G02-058 passkey records verify only while their integrity tag matches the account, credential, relying party and key")]
    public async Task TM_G02_058_Passkey_records_verify_only_while_their_integrity_tag_matches()
    {
        using var keys = new CredentialKeyDirectory(1);
        await using var identity = UnitIdentity.Create(keys.Settings(1, 1));
        var passkeys = identity.Get<IPasskeyCredentials>();
        var admin = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var adminCredential = Bytes.Random(32);
        var editorCredential = Bytes.Random(32);
        Assert.True(await passkeys.EnrollAsync(new PasskeyEnrollment(admin, adminCredential, "app.example.test", Bytes.Random(77), -7), Cancellation));
        Assert.True(await passkeys.EnrollAsync(new PasskeyEnrollment(editor, editorCredential, "app.example.test", Bytes.Random(77), -7), Cancellation));
        Assert.False(await passkeys.EnrollAsync(new PasskeyEnrollment(editor, adminCredential, "app.example.test", Bytes.Random(77), -7), Cancellation));

        var found = await passkeys.FindForSignInAsync(adminCredential, "app.example.test", Cancellation);
        Assert.Equal(PasskeyLookupStatus.Verified, found.Status);
        Assert.Equal(admin, found.Credential!.AccountId);
        Assert.Equal(PasskeyLookupStatus.NotFound, (await passkeys.FindForSignInAsync(adminCredential, "other.example.test", Cancellation)).Status);
        Assert.Equal(PasskeyLookupStatus.NotFound, (await passkeys.FindForSignInAsync(Bytes.Random(32), "app.example.test", Cancellation)).Status);

        var adminRecord = identity.Stores.Passkeys[Convert.ToHexString(adminCredential)];
        var editorRecord = identity.Stores.Passkeys[Convert.ToHexString(editorCredential)];
        foreach (var altered in new[]
                 {
                     adminRecord with { PublicKey = editorRecord.PublicKey }, adminRecord with { AccountId = editor },
                     adminRecord with { RelyingPartyId = "other.example.test" }, adminRecord with { Algorithm = -257 },
                     adminRecord with { IntegrityTag = Flip(adminRecord.IntegrityTag) }, adminRecord with { KeyVersion = 9 }
                 })
        {
            identity.Stores.Passkeys[Convert.ToHexString(adminCredential)] = altered;
            Assert.Equal(PasskeyLookupStatus.IntegrityFailure, (await passkeys.FindForSignInAsync(adminCredential, "app.example.test", Cancellation)).Status);
        }
        identity.Stores.Passkeys[Convert.ToHexString(adminCredential)] = adminRecord;
        Assert.False(await passkeys.RevokeAsync(editor, adminCredential, Cancellation));
        Assert.True(await passkeys.RevokeAsync(admin, adminCredential, Cancellation));
        Assert.Equal(PasskeyLookupStatus.NotFound, (await passkeys.FindForSignInAsync(adminCredential, "app.example.test", Cancellation)).Status);
    }

    private static byte[] Flip(byte[] value)
    {
        var copy = value.ToArray();
        copy[0] ^= 0x01;
        return copy;
    }
}
