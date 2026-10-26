using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class PostgreSqlEnvironmentTests(ITestOutputHelper output)
{
    // The trait that selects tests needing PostgreSQL: the real-provider job runs exactly these, and every other job
    // excludes them. ToolPinContract requires the workflow filters to use these values.
    public const string RequiresTrait = "Requires";
    public const string PostgreSql = "PostgreSQL";

    [Fact]
    [Trait(RequiresTrait, PostgreSql)]
    public async Task Test_database_is_postgresql_18_reached_by_an_unprivileged_owner()
    {
        var target = PostgreSqlTarget.FromEnvironment();
        var session = await PostgreSqlProbe.ConnectAsync(target, TestContext.Current.CancellationToken);
        output.WriteLine($"{target}: {session.Authentication} sign-in, PostgreSQL {session.Parameters.GetValueOrDefault("server_version")}, "
            + $"server_version_num {session.ServerVersionNumber}, encoding {session.Parameters.GetValueOrDefault("server_encoding")}");
        Assert.Equal(target.Database, session.Database);
        Assert.Equal(target.Username, session.User);
        Assert.InRange(session.ServerVersionNumber, 180000, 189999);
        Assert.Equal("off", session.Parameters.GetValueOrDefault("is_superuser"));
        Assert.Equal("UTF8", session.Parameters.GetValueOrDefault("server_encoding"));
    }

    [Fact]
    public void The_postgresql_probe_carries_the_trait_that_the_real_provider_job_selects()
    {
        var probe = typeof(PostgreSqlEnvironmentTests).GetMethod(nameof(Test_database_is_postgresql_18_reached_by_an_unprivileged_owner))!;
        var trait = Assert.Single(probe.GetCustomAttributes<TraitAttribute>());
        Assert.Equal((RequiresTrait, PostgreSql), (trait.Name, trait.Value));
        // Only the probe needs PostgreSQL in this assembly; every other test here runs on all platforms.
        var marked = typeof(PostgreSqlEnvironmentTests).Assembly.GetTypes().SelectMany(type => type.GetMethods())
            .Where(method => method.GetCustomAttributes<TraitAttribute>().Any(attribute => attribute.Name == RequiresTrait)).ToList();
        Assert.Equal(new[] { probe }, marked);
    }

    [Fact]
    public void Scram_matches_the_rfc_7677_example_exchange()
    {
        // RFC 7677, section 3: user 'user', password 'pencil'.
        var scram = new ScramSha256("pencil", "rOprNGfwEbeRWgbNEkqO", "user");
        Assert.Equal("n,,n=user,r=rOprNGfwEbeRWgbNEkqO", scram.ClientFirst);
        var final = scram.ClientFinal("r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096");
        Assert.Equal("c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,p=dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=", final);
        Assert.False(scram.Verified);
        scram.VerifyServerFinal("v=6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=");
        Assert.True(scram.Verified);
    }

    [Theory]
    [InlineData("r=rOprNGfwEbeRWgbNEkqO,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096", "does not extend the client nonce")]
    [InlineData("r=other%hvYDpWUa2RaTCAfuxFIlj,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096", "does not extend the client nonce")]
    [InlineData("r=rOprNGfwEbeRWgbNEkqO%hvYD,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4095", "at least 4096")]
    [InlineData("r=rOprNGfwEbeRWgbNEkqO%hvYD,s=*,i=4096", "salt is not base64")]
    [InlineData("r=rOprNGfwEbeRWgbNEkqO%hvYD,i=4096", "sent no SCRAM salt")]
    [InlineData("r=rOprNGfwEbeRWgbNEkqO%hvYD,r=x,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096", "malformed SCRAM message")]
    public void Scram_rejects_an_invalid_server_first_message(string serverFirst, string expected)
    {
        var scram = new ScramSha256("pencil", "rOprNGfwEbeRWgbNEkqO");
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => scram.ClientFinal(serverFirst)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v=7rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=", "does not match")]
    [InlineData("e=invalid-proof", "rejected the SCRAM exchange")]
    [InlineData("v=*", "signature is not base64")]
    public void Scram_rejects_a_server_that_cannot_prove_the_verifier(string serverFinal, string expected)
    {
        var scram = new ScramSha256("pencil", "rOprNGfwEbeRWgbNEkqO", "user");
        scram.ClientFinal("r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096");
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => scram.VerifyServerFinal(serverFinal)).Message, StringComparison.Ordinal);
        Assert.False(scram.Verified);
    }

    [Theory]
    [InlineData("pass word")]
    [InlineData("pässword")]
    [InlineData("")]
    public void Scram_accepts_only_printable_ascii_passwords(string password)
    {
        Assert.Throws<InvalidOperationException>(() => new ScramSha256(password, ScramSha256.NewNonce()));
    }

    [Fact]
    public void Connection_string_names_a_loopback_test_database()
    {
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var target = PostgreSqlTarget.Parse($"Host=127.0.0.1;Port=55432;Database=modularyn_test_a;Username=modularyn_test_a;Password={password}");
        Assert.Equal(("127.0.0.1", 55432, "modularyn_test_a", "modularyn_test_a", password),
            (target.Host, target.Port, target.Database, target.Username, target.Password));
        Assert.Equal(PostgreSqlTarget.DefaultPort, PostgreSqlTarget.Parse($"host=localhost;database=d;username=u;password={password}").Port);
        Assert.Equal("::1", PostgreSqlTarget.Parse($"Host=::1;Database=d;Username=u;Password={password}").Host);
        Assert.DoesNotContain(password, target.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "is not set")]
    [InlineData(" ", "is not set")]
    [InlineData("Host=db.example.com;Database=d;Username=u;Password=p", "must name a loopback host")]
    [InlineData("Host=10.0.0.1;Database=d;Username=u;Password=p", "must name a loopback host")]
    [InlineData("Host=localhost;Database=d;Username=u;Password=p;SSL Mode=Disable", "may name only")]
    [InlineData("Server=localhost;Database=d;Username=u;Password=p", "may name only")]
    [InlineData("Host=localhost;Database=d;Username=u", "must name Password")]
    [InlineData("Host=localhost;Username=u;Password=p", "must name Database")]
    [InlineData("Host=localhost;Database=d;Password=p", "must name Username")]
    [InlineData("Database=d;Username=u;Password=p", "must name Host")]
    [InlineData("Host=localhost;Port=0;Database=d;Username=u;Password=p", "Port as a number")]
    [InlineData("Host=localhost;Port=65536;Database=d;Username=u;Password=p", "Port as a number")]
    [InlineData("Host=localhost;Port=+5432;Database=d;Username=u;Password=p", "Port as a number")]
    [InlineData("Host=localhost;Database=d;Username=u;Password='p", "not a keyword connection string")]
    public void Connection_string_outside_the_convention_is_rejected(string? connectionString, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSqlTarget.Parse(connectionString));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Contains(PostgreSqlTarget.Variable, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Probe_never_sends_the_password_outside_scram(int method)
    {
        // A stand-in server that asks for a cleartext (3) or MD5 (5) password; the probe must send nothing more.
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var target = PostgreSqlTarget.Parse($"Host=127.0.0.1;Port={port};Database=probe;Username=probe;Password={password}");
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            var stream = client.GetStream();
            var length = new byte[4];
            await stream.ReadExactlyAsync(length, token);
            await stream.ReadExactlyAsync(new byte[BinaryPrimitives.ReadInt32BigEndian(length) - 4], token);
            byte[] request = method == 5 ? [(byte)'R', 0, 0, 0, 12, 0, 0, 0, 5, 1, 2, 3, 4] : [(byte)'R', 0, 0, 0, 8, 0, 0, 0, 3];
            await stream.WriteAsync(request, token);
            using var received = new MemoryStream();
            await stream.CopyToAsync(received, token);
            return received.ToArray();
        }, token);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PostgreSqlProbe.ConnectAsync(target, token));
        Assert.Contains($"authentication method {method}", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, error.Message, StringComparison.Ordinal);
        var afterStartup = await server;
        Assert.Empty(afterStartup);
        Assert.DoesNotContain(password, Encoding.ASCII.GetString(afterStartup), StringComparison.Ordinal);
    }
}
