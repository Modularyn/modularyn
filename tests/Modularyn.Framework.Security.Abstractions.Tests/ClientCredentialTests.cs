using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Modularyn.Framework.Security.Abstractions.Tests;

// TM-G02-013 is CODE-06's control; these tests cover the credential-provider seam SEC-03 supplies for its clients.
public sealed class ClientCredentialTests
{
    private static string Secret() => "token-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    [Fact(DisplayName = "TM-G02-013 a provider supplies credentials only to its configured origins, never to discovered or redirected ones")]
    public async Task TM_G02_013_A_provider_supplies_credentials_only_to_its_configured_origins()
    {
        var secret = Secret();
        var consulted = 0;
        var provider = new OriginBoundCredentialProvider([EndpointOrigin.Parse("https://api.example.test")], (_, _) =>
        {
            consulted++;
            return ValueTask.FromResult(new ClientCredential("Bearer", new SecretText(secret)));
        });
        var cancellation = TestContext.Current.CancellationToken;

        Assert.True(EndpointOrigin.TryFromRequestUri(new Uri("https://API.example.test/items/1?page=2"), out var configured));
        var credential = await provider.GetCredentialAsync(configured, cancellation);
        Assert.Equal(secret, credential!.Value.Reveal());
        Assert.Equal(1, consulted);

        // A redirect or discovery document can name any of these; none receives the credential, and the source is not consulted.
        foreach (var target in new[] { "https://api.example.test:8443/items", "https://other.example.test/items", "https://api.example.test.evil.test/",
                     "https://evil.test/?u=https://api.example.test" })
        {
            Assert.True(EndpointOrigin.TryFromRequestUri(new Uri(target), out var other));
            Assert.Null(await provider.GetCredentialAsync(other, cancellation));
        }
        Assert.Equal(1, consulted);
        foreach (var target in new[] { "http://api.example.test/items", "https://user:pass@api.example.test/", "https://api.example.test./" })
            Assert.False(EndpointOrigin.TryFromRequestUri(new Uri(target), out _));
        Assert.False(EndpointOrigin.TryFromRequestUri(new Uri("/items", UriKind.Relative), out _));
    }

    [Fact(DisplayName = "TM-G02-013 credentials and secret text are redacted from formatting and serialization")]
    public void TM_G02_013_Credentials_and_secret_text_are_redacted_from_formatting_and_serialization()
    {
        var secret = Secret();
        var text = new SecretText(secret);
        var credential = new ClientCredential("Bearer", text);
        Assert.Equal(SecretText.Redacted, text.ToString());
        Assert.Equal("Bearer " + SecretText.Redacted, credential.ToString());
        Assert.DoesNotContain(secret, $"{credential} {text}", StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(new { credential, text });
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecretText>("\"" + secret + "\""));
        Assert.Throws<ArgumentException>(() => new ClientCredential("Bearer token", text));
        Assert.Throws<ArgumentException>(() => new ClientCredential("Bearer", new SecretText("")));
    }

    [Fact(DisplayName = "TM-G02-013 configured origins are exact HTTPS origins")]
    public void TM_G02_013_Configured_origins_are_exact_https_origins()
    {
        Assert.Equal("https://api.example.test", EndpointOrigin.Parse("https://api.example.test").Value);
        Assert.Equal("https://api.example.test", EndpointOrigin.Parse("https://api.example.test/").Value);
        Assert.Equal("https://api.example.test:8443", EndpointOrigin.Parse("https://api.example.test:8443").Value);
        Assert.Equal(EndpointOrigin.Parse("https://api.example.test"), EndpointOrigin.Parse("https://api.example.test:443"));
        foreach (var value in new[] { "http://api.example.test", "https://api.example.test/v1", "https://api.example.test?x=1", "https://*.example.test",
                     "https://user@api.example.test", "https://api.example.test.", " https://api.example.test", "https://api.ex\u00e4mple.test", "api.example.test",
                     "https:api.example.test", "", "https://api.example.test#f" })
            Assert.Throws<FormatException>(() => EndpointOrigin.Parse(value));
        Assert.Throws<ArgumentException>(() => new OriginBoundCredentialProvider([], (_, _) => ValueTask.FromResult(new ClientCredential("Bearer", new SecretText("x")))));
    }
}
