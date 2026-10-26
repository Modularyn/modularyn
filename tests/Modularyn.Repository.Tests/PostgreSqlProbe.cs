using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Modularyn.Repository.Tests;

// The test database that MODULARYN_TEST_POSTGRESQL names, as an Npgsql keyword connection string. Real-provider
// tests run only against a loopback server, and the password never appears in any message or ToString.
internal sealed class PostgreSqlTarget
{
    public const string Variable = "MODULARYN_TEST_POSTGRESQL";
    public const int DefaultPort = 5432;
    private static readonly string[] Keys = ["Host", "Port", "Database", "Username", "Password"];
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    private PostgreSqlTarget(string host, int port, string database, string username, string password)
    {
        Host = host;
        Port = port;
        Database = database;
        Username = username;
        Password = password;
    }

    public string Host { get; }

    public int Port { get; }

    public string Database { get; }

    public string Username { get; }

    public string Password { get; }

    public static PostgreSqlTarget FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));

    public static PostgreSqlTarget Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"{Variable} is not set. Tests with the trait Requires=PostgreSQL need a "
                + "PostgreSQL 18 test database; create one as docs/ci.md describes, or exclude them with "
                + "--filter \"Requires!=PostgreSQL\".");
        }

        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            // The parser's own message can quote the connection string, so it is not passed on.
            throw new InvalidOperationException($"{Variable} is not a keyword connection string.");
        }

        var unknown = builder.Keys.Cast<string>().Where(key => !Keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"{Variable} may name only {string.Join(", ", Keys)}, not {string.Join(", ", unknown)}.");
        }

        var host = Required(builder, "Host");
        if (!LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{Variable} must name a loopback host ({string.Join(", ", LoopbackHosts)}), "
                + $"not {host}; real-provider tests never run against a remote server.");
        }

        var port = DefaultPort;
        if (builder.TryGetValue("Port", out var value)
            && (value is not string text || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535))
        {
            throw new InvalidOperationException($"{Variable} must give Port as a number from 1 to 65535.");
        }

        return new PostgreSqlTarget(host, port, Required(builder, "Database"), Required(builder, "Username"), Required(builder, "Password"));
    }

    public override string ToString() => $"Host={Host};Port={Port};Database={Database};Username={Username}";

    private static string Required(DbConnectionStringBuilder builder, string key) =>
        builder.TryGetValue(key, out var value) && value is string text && text.Trim().Length > 0
            ? text
            : throw new InvalidOperationException($"{Variable} must name {key}.");
}

// The client side of SCRAM-SHA-256 without channel binding (RFC 5802 and RFC 7677), as PostgreSQL uses it.
internal sealed class ScramSha256
{
    public const string Mechanism = "SCRAM-SHA-256";
    private const int MinimumIterations = 4096;
    private readonly byte[] password;
    private readonly string clientNonce;
    private readonly string clientFirstBare;
    private byte[]? expectedServerSignature;

    // PostgreSQL takes the user from the startup message, so its exchange leaves the SCRAM user name empty.
    public ScramSha256(string password, string clientNonce, string user = "")
    {
        // SASLprep leaves printable ASCII unchanged, so a password in that range needs no normalization.
        if (password.Length == 0 || password.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidOperationException("The test password must be printable ASCII without spaces.");
        }

        if (clientNonce.Length == 0 || clientNonce.Any(character => character is < '!' or > '~' or ','))
        {
            throw new ArgumentException("The client nonce must be printable ASCII without commas.", nameof(clientNonce));
        }

        this.password = Encoding.ASCII.GetBytes(password);
        this.clientNonce = clientNonce;
        clientFirstBare = $"n={user},r={clientNonce}";
    }

    public bool Verified { get; private set; }

    public string ClientFirst => "n,," + clientFirstBare;

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    public string ClientFinal(string serverFirst)
    {
        var attributes = Attributes(serverFirst);
        if (!attributes.TryGetValue('r', out var nonce) || !nonce.StartsWith(clientNonce, StringComparison.Ordinal)
            || nonce.Length == clientNonce.Length)
        {
            throw new InvalidOperationException("The server's SCRAM nonce does not extend the client nonce.");
        }

        if (!attributes.TryGetValue('i', out var count)
            || !int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < MinimumIterations)
        {
            throw new InvalidOperationException($"The server's SCRAM iteration count must be at least {MinimumIterations}.");
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(attributes.GetValueOrDefault('s', ""));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The server's SCRAM salt is not base64.");
        }

        if (salt.Length == 0)
        {
            throw new InvalidOperationException("The server sent no SCRAM salt.");
        }

        var salted = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        var clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);
        var withoutProof = "c=biws,r=" + nonce;
        var authMessage = Encoding.ASCII.GetBytes(clientFirstBare + "," + serverFirst + "," + withoutProof);
        var signature = HMACSHA256.HashData(storedKey, authMessage);
        var proof = new byte[clientKey.Length];
        for (var index = 0; index < proof.Length; index++)
        {
            proof[index] = (byte)(clientKey[index] ^ signature[index]);
        }

        expectedServerSignature = HMACSHA256.HashData(HMACSHA256.HashData(salted, "Server Key"u8), authMessage);
        return withoutProof + ",p=" + Convert.ToBase64String(proof);
    }

    public void VerifyServerFinal(string serverFinal)
    {
        if (expectedServerSignature is null)
        {
            throw new InvalidOperationException("The server finished SCRAM before the client sent its proof.");
        }

        var attributes = Attributes(serverFinal);
        if (attributes.TryGetValue('e', out var error))
        {
            throw new InvalidOperationException($"The server rejected the SCRAM exchange: {error}.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(attributes.GetValueOrDefault('v', ""));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The server's SCRAM signature is not base64.");
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, expectedServerSignature))
        {
            throw new InvalidOperationException("The server's SCRAM signature does not match, so it does not hold the role's verifier.");
        }

        Verified = true;
    }

    private static Dictionary<char, string> Attributes(string message)
    {
        var attributes = new Dictionary<char, string>();
        foreach (var part in message.Split(','))
        {
            if (part.Length < 2 || part[1] != '=' || !attributes.TryAdd(part[0], part[2..]))
            {
                throw new InvalidOperationException("The server sent a malformed SCRAM message.");
            }
        }

        return attributes;
    }
}

// Just enough of the PostgreSQL 3.0 frontend protocol to sign in to the test database and run one query. It sends
// the password only through SCRAM-SHA-256 and verifies the server's signature.
internal static class PostgreSqlProbe
{
    public const string ApplicationName = "Modularyn.Repository.Tests";
    private const int ProtocolVersion = 3 << 16;
    private const int MaximumMessageLength = 1 << 20;
    private const string Query = "SELECT current_database(), current_user, current_setting('server_version_num')";

    public sealed record Session(string Authentication, IReadOnlyDictionary<string, string> Parameters, string Database,
        string User, int ServerVersionNumber);

    public static async Task<Session> ConnectAsync(PostgreSqlTarget target, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        using var socket = await ConnectAsync(target.Host, target.Port, token);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var startup = new List<byte>();
        startup.AddRange(Int32(ProtocolVersion));
        foreach (var (name, value) in new[]
                 {
                     ("user", target.Username), ("database", target.Database), ("application_name", ApplicationName),
                     ("client_encoding", "UTF8")
                 })
        {
            startup.AddRange(CString(name));
            startup.AddRange(CString(value));
        }

        startup.Add(0);
        await SendAsync(stream, null, [.. startup], token);

        string? authentication = null;
        ScramSha256? scram = null;
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        while (true)
        {
            var (type, body) = await ReceiveAsync(stream, token);
            if (type == 'Z')
            {
                break;
            }

            switch (type)
            {
                case 'R':
                    var method = Int32(body, 0);
                    switch (method)
                    {
                        case 0:
                            if (scram is not null && !scram.Verified)
                            {
                                throw new InvalidOperationException("The server accepted the sign-in without proving its SCRAM signature.");
                            }

                            authentication = scram is null ? "trust" : ScramSha256.Mechanism;
                            break;
                        case 10:
                            var mechanisms = CStrings(body, 4);
                            if (!mechanisms.Contains(ScramSha256.Mechanism))
                            {
                                throw new InvalidOperationException($"The server offers {string.Join(", ", mechanisms)}, not "
                                    + $"{ScramSha256.Mechanism}, so the probe sent no password.");
                            }

                            scram = new ScramSha256(target.Password, ScramSha256.NewNonce());
                            var first = Encoding.ASCII.GetBytes(scram.ClientFirst);
                            await SendAsync(stream, 'p', [.. CString(ScramSha256.Mechanism), .. Int32(first.Length), .. first], token);
                            break;
                        case 11 when scram is not null:
                            await SendAsync(stream, 'p', Encoding.ASCII.GetBytes(scram.ClientFinal(Ascii(body, 4))), token);
                            break;
                        case 12 when scram is not null:
                            scram.VerifyServerFinal(Ascii(body, 4));
                            break;
                        default:
                            throw new InvalidOperationException($"The server asked for authentication method {method}; the probe "
                                + $"sends the password only through {ScramSha256.Mechanism}, so it sent nothing.");
                    }

                    break;
                case 'S':
                    var parameter = CStrings(body, 0);
                    if (parameter.Count == 2)
                    {
                        parameters[parameter[0]] = parameter[1];
                    }

                    break;
                case 'E':
                    throw Error(body);
                case 'K' or 'N':
                    break;
                default:
                    throw new InvalidDataException($"Unexpected PostgreSQL message '{type}' during sign-in.");
            }
        }

        if (authentication is null)
        {
            throw new InvalidDataException("The server reported ready before it authenticated the session.");
        }

        await SendAsync(stream, 'Q', CString(Query), token);
        List<string?>? row = null;
        while (true)
        {
            var (type, body) = await ReceiveAsync(stream, token);
            if (type == 'Z')
            {
                break;
            }

            switch (type)
            {
                case 'D':
                    row = DataRow(body);
                    break;
                case 'E':
                    throw Error(body);
                case 'T' or 'C' or 'N' or 'S':
                    break;
                default:
                    throw new InvalidDataException($"Unexpected PostgreSQL message '{type}' in a query result.");
            }
        }

        await SendAsync(stream, 'X', [], token);
        if (row is not [{ } database, { } user, { } version]
            || !int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var versionNumber))
        {
            throw new InvalidDataException("The probe query did not return the database, user and server version.");
        }

        return new Session(authentication, parameters, database, user, versionNumber);
    }

    private static async Task<Socket> ConnectAsync(string host, int port, CancellationToken token)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, token);
        if (addresses.Length == 0 || addresses.Any(address => !IPAddress.IsLoopback(address)))
        {
            throw new InvalidOperationException($"{host} must resolve only to loopback addresses.");
        }

        var failures = new List<string>();
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), token);
                return socket;
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                failures.Add($"{address}: {exception.SocketErrorCode}");
            }
        }

        throw new InvalidOperationException($"Cannot connect to PostgreSQL on port {port} at {host} ({string.Join(", ", failures)}).");
    }

    private static async Task SendAsync(Stream stream, char? type, byte[] body, CancellationToken token)
    {
        var header = new byte[type is null ? 4 : 5];
        if (type is { } code)
        {
            header[0] = (byte)code;
        }

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(header.Length - 4), body.Length + 4);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    private static async Task<(char Type, byte[] Body)> ReceiveAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length is < 4 or > MaximumMessageLength)
        {
            throw new InvalidDataException($"PostgreSQL message '{(char)header[0]}' has an invalid length {length}.");
        }

        var body = new byte[length - 4];
        await stream.ReadExactlyAsync(body, token);
        return ((char)header[0], body);
    }

    private static InvalidOperationException Error(byte[] body)
    {
        var fields = new Dictionary<char, string>();
        var offset = 0;
        while (offset < body.Length && body[offset] != 0)
        {
            var code = (char)body[offset++];
            var end = Array.IndexOf(body, (byte)0, offset);
            if (end < 0)
            {
                break;
            }

            fields[code] = Encoding.UTF8.GetString(body, offset, end - offset);
            offset = end + 1;
        }

        return new InvalidOperationException($"PostgreSQL {fields.GetValueOrDefault('V', fields.GetValueOrDefault('S', "ERROR"))} "
            + $"{fields.GetValueOrDefault('C', "")}: {fields.GetValueOrDefault('M', "no message")}");
    }

    private static List<string?> DataRow(byte[] body)
    {
        var count = BinaryPrimitives.ReadInt16BigEndian(body);
        var values = new List<string?>();
        var offset = 2;
        for (var column = 0; column < count; column++)
        {
            var length = Int32(body, offset);
            offset += 4;
            if (length < 0)
            {
                values.Add(null);
                continue;
            }

            values.Add(Encoding.UTF8.GetString(body, offset, length));
            offset += length;
        }

        return values;
    }

    private static List<string> CStrings(byte[] body, int offset)
    {
        var values = new List<string>();
        while (offset < body.Length && body[offset] != 0)
        {
            var end = Array.IndexOf(body, (byte)0, offset);
            if (end < 0)
            {
                throw new InvalidDataException("A PostgreSQL message string is not terminated.");
            }

            values.Add(Encoding.UTF8.GetString(body, offset, end - offset));
            offset = end + 1;
        }

        return values;
    }

    private static string Ascii(byte[] body, int offset) => Encoding.ASCII.GetString(body, offset, body.Length - offset);

    private static int Int32(byte[] body, int offset) => BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(offset));

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] CString(string value) => [.. Encoding.UTF8.GetBytes(value), 0];
}
