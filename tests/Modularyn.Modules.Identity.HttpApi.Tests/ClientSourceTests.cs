using System.Net;
using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Modules.Identity.HttpApi.Tests;

public sealed class ClientSourceTests
{
    [Fact(DisplayName = "TM-G02-015 a per-source limit counts an IPv4 address alone and an IPv6 address by its /64 network")]
    public void TM_G02_015_A_per_source_limit_counts_an_IPv4_address_and_an_IPv6_network()
    {
        Assert.Equal("203.0.113.7", ClientSourceAddress.From(IPAddress.Parse("203.0.113.7")));
        Assert.Equal("203.0.113.7", ClientSourceAddress.From(IPAddress.Parse("::ffff:203.0.113.7")));
        Assert.Equal("2001:db8:1:2::/64", ClientSourceAddress.From(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd")));
        Assert.Equal(ClientSourceAddress.From(IPAddress.Parse("2001:db8:1:2::1")), ClientSourceAddress.From(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(ClientSourceAddress.From(IPAddress.Parse("2001:db8:1:2::1")), ClientSourceAddress.From(IPAddress.Parse("2001:db8:1:3::1")));
        // A connection without an address is the unknown source, which has no per-source counter of its own.
        Assert.Equal(ClientSources.Unknown, ClientSourceAddress.From(null));
        Assert.True(ClientSources.IsUnknown(ClientSourceAddress.From(null)));
    }
}
