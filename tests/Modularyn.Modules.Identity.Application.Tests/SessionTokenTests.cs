using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

public sealed class SessionTokenTests
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    [Fact]
    public void TM_G02_016_A_token_whose_last_character_sets_unused_bits_is_refused_without_an_exception()
    {
        var canonical = SessionToken.Create().Reveal();
        // A 32-byte secret leaves the final character's two low bits unused, and its canonical encoding keeps them zero.
        var last = Alphabet.IndexOf(canonical[^1]);
        Assert.Equal(0, last & 3);
        var crafted = canonical[..^1] + Alphabet[last | 1];

        Assert.False(SessionToken.TryParse(crafted, out var token));
        Assert.Null(token);
        Assert.True(SessionToken.TryParse(canonical, out var parsed));
        Assert.Equal(canonical, parsed.Reveal());
    }
}
