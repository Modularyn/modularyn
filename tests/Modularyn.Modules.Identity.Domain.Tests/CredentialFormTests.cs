using System.Text;
using Xunit;

namespace Modularyn.Modules.Identity.Domain.Tests;

public sealed class CredentialFormTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "TM-G02-057 recovery codes carry eighty bits and match only their own salted verifier")]
    public void TM_G02_057_Recovery_codes_carry_eighty_bits_and_match_only_their_salted_verifier()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => RecoveryCodes.Generate()).ToArray();
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code =>
        {
            Assert.Matches(@"\A[A-HJ-NP-Z2-9]{4}(?:-[A-HJ-NP-Z2-9]{4}){3}\z", code);
            Assert.Equal(code.Replace("-", "", StringComparison.Ordinal), RecoveryCodes.Normalize(code));
        });
        Assert.Equal(32, RecoveryCodes.Alphabet.Distinct().Count());

        var account = Guid.NewGuid();
        var set = Guid.NewGuid();
        var verifier = RecoveryCodes.CreateVerifier(account, set, codes[0], Now);
        var again = RecoveryCodes.CreateVerifier(account, set, codes[0], Now);
        Assert.NotEqual(verifier.Salt, again.Salt);
        Assert.NotEqual(verifier.Verifier, again.Verifier);
        Assert.Equal(RecoveryCodes.Iterations, verifier.Iterations);
        Assert.Equal(-1, verifier.Verifier.AsSpan().IndexOf(Encoding.ASCII.GetBytes(RecoveryCodes.Normalize(codes[0])!)));

        Assert.True(RecoveryCodes.Matches(verifier, RecoveryCodes.Normalize(codes[0].ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal))!));
        Assert.False(RecoveryCodes.Matches(verifier, RecoveryCodes.Normalize(codes[1])!));
        Assert.False(RecoveryCodes.Matches(verifier with { Iterations = 1 }, RecoveryCodes.Normalize(codes[0])!));
        Assert.False(RecoveryCodes.Matches(verifier with { Salt = [] }, RecoveryCodes.Normalize(codes[0])!));
        foreach (var presented in new[] { "", "ABCD", "IIII-OOOO-0000-1111", codes[0] + "A", new string('A', 65) })
            Assert.Null(RecoveryCodes.Normalize(presented));
        Assert.Null(RecoveryCodes.Normalize(null));
    }

    [Fact(DisplayName = "TM-G02-058 the canonical form binds the account, credential, relying party, public key and algorithm")]
    public void TM_G02_058_The_canonical_form_binds_account_credential_relying_party_key_and_algorithm()
    {
        var account = Guid.NewGuid();
        byte[] credential = [1, 2, 3, 4];
        byte[] key = [9, 8, 7];
        var reference = PasskeyCredentialRecord.CanonicalForm(account, credential, "app.example.test", key, -7);
        var variants = new[]
        {
            PasskeyCredentialRecord.CanonicalForm(Guid.NewGuid(), credential, "app.example.test", key, -7),
            PasskeyCredentialRecord.CanonicalForm(account, [1, 2, 3, 5], "app.example.test", key, -7),
            PasskeyCredentialRecord.CanonicalForm(account, credential, "other.example.test", key, -7),
            PasskeyCredentialRecord.CanonicalForm(account, credential, "app.example.test", [9, 8, 6], -7),
            PasskeyCredentialRecord.CanonicalForm(account, credential, "app.example.test", key, -257)
        };
        Assert.All(variants, variant => Assert.NotEqual(reference, variant));
        Assert.Equal(reference, PasskeyCredentialRecord.CanonicalForm(account, credential, "app.example.test", key, -7));

        // Length prefixes keep field boundaries unambiguous.
        Assert.NotEqual(PasskeyCredentialRecord.CanonicalForm(account, [1, 2], "ab.test", [3], -7),
            PasskeyCredentialRecord.CanonicalForm(account, [1], "ab.test", [2, 3], -7));
        Assert.Throws<ArgumentException>(() => PasskeyCredentialRecord.CanonicalForm(account, [], "app.example.test", key, -7));
        Assert.Throws<ArgumentException>(() => PasskeyCredentialRecord.CanonicalForm(account, new byte[1024], "app.example.test", key, -7));
        Assert.Throws<ArgumentException>(() => PasskeyCredentialRecord.CanonicalForm(account, credential, "App.Example.Test", key, -7));
        Assert.Throws<ArgumentException>(() => PasskeyCredentialRecord.CanonicalForm(account, credential, ".example.test", key, -7));
        Assert.Throws<ArgumentException>(() => PasskeyCredentialRecord.CanonicalForm(account, credential, "app.example.test", [], -7));
    }

    [Fact(DisplayName = "TM-G02-056 seed associated data binds the account and the key version")]
    public void TM_G02_056_Seed_associated_data_binds_the_account_and_key_version()
    {
        var account = Guid.NewGuid();
        var data = ProtectedSeed.AssociatedData(account, 1);
        Assert.Equal(data, ProtectedSeed.AssociatedData(account, 1));
        Assert.NotEqual(data, ProtectedSeed.AssociatedData(account, 2));
        Assert.NotEqual(data, ProtectedSeed.AssociatedData(Guid.NewGuid(), 1));
    }
}
