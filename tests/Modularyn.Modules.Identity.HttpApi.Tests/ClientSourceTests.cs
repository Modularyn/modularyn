using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
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

    [Fact(DisplayName = "TM-G02-015 the first sign-in on a connection with no client address warns once that per-source limits are off")]
    public void TM_G02_015_A_connection_with_no_client_address_warns_once_that_per_source_limits_are_off()
    {
        using var logs = new CapturedLogs();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var observer = new ClientSourceObserver(loggers.CreateLogger<ClientSourceObserver>());
        var addressed = new DefaultHttpContext();
        addressed.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        Assert.Equal("203.0.113.7", observer.SourceOf(addressed.Connection));
        Assert.Empty(logs.Entries);

        // A Unix-socket listener behind a proxy, say: no address, so the attempt belongs to the unknown source. The first
        // warns, naming what is lost and the remedy; later ones, from any number of clients, add nothing to the log.
        var unaddressed = new DefaultHttpContext();
        Assert.Null(unaddressed.Connection.RemoteIpAddress);
        for (var attempt = 0; attempt < 3; attempt++) Assert.Equal(ClientSources.Unknown, observer.SourceOf(unaddressed.Connection));
        var warning = Assert.Single(logs.Entries);
        Assert.Contains("Warning", warning, StringComparison.Ordinal);
        Assert.Contains("no per-source limits", warning, StringComparison.Ordinal);
    }
}
