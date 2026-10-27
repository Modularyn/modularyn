using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.HttpApi.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class LeakTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "TM-G02-055 no password, verifier, session token, recovery code or seed reaches a log, a response or an exception")]
    public async Task TM_G02_055_No_credential_reaches_a_log_a_response_or_an_exception()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        await using var server = await IdentityTestServer.StartAsync(database);
        var accounts = server.Get<IAccountService>();
        var password = Generated.Password();
        var wrong = Generated.Password();
        var replacement = Generated.Password();
        var account = (await accounts.CreateLocalAccountAsync("admin-1", new SecretText(password), Cancellation)).AccountId!.Value;
        var seen = new List<string>();
        async Task Record(HttpResponseMessage response) =>
            seen.Add(response.StatusCode + " " + string.Join("|", response.Headers.Where(header => header.Key != "Set-Cookie").Select(header => header.Key + "=" + string.Join(",", header.Value)))
                + " " + await response.Content.ReadAsStringAsync(Cancellation));

        var browser = server.Browser();
        await Record(await browser.SignInAsync("admin-1", wrong));
        await Record(await browser.SignInAsync("unknown-1", password));
        await Record(await browser.SignInAsync("admin-1", password));
        var token = browser.Session!;
        await Record(await browser.SendAsync(HttpMethod.Get, IdentityEndpoints.CurrentUserPath));
        await Record(await browser.SendAsync(HttpMethod.Get, IdentityEndpoints.CurrentUserPath, delegation: Guid.NewGuid()));
        var verifier = await database.ScalarAsync<string>("SELECT \"PasswordHash\" FROM " + database.Table("Accounts"));

        var codes = await server.Get<IRecoveryCodes>().GenerateAsync(account, Cancellation);
        Assert.Equal(CredentialVerification.Accepted, await server.Get<IRecoveryCodes>().ConsumeAsync(account, codes.Codes[0], "203.0.113.4", Cancellation));
        var seed = RandomNumberGenerator.GetBytes(20);
        await server.Get<IAuthenticatorSeeds>().StoreAsync(account, seed, Cancellation);
        Assert.Equal(CredentialVerification.Accepted, await server.Get<IAuthenticatorSeeds>().VerifyAsync(account, presented => presented.Span.SequenceEqual(seed), "203.0.113.4", Cancellation));
        Assert.Equal(PasswordChangeStatus.CurrentPasswordRejected,
            (await accounts.ChangePasswordAsync(account, new SecretText(wrong), new SecretText(replacement), "203.0.113.4", Cancellation)).Status);
        Assert.Equal(PasswordChangeStatus.Changed,
            (await accounts.ChangePasswordAsync(account, new SecretText(password), new SecretText(replacement), "203.0.113.4", Cancellation)).Status);
        await Record(await browser.Copy().SendAsync(HttpMethod.Get, IdentityEndpoints.CurrentUserPath));
        var stale = server.Request(HttpMethod.Post, IdentityEndpoints.SignOutPath);
        stale.Headers.Add("Cookie", SessionCookie.Name + "=" + token);
        await Record(await server.SendAsync(stale));

        // Exceptions raised by refusals carry no credential either.
        var exceptions = new List<string>();
        try { await server.Get<IAuthenticatorSeeds>().StoreAsync(account, seed.AsMemory(0, 4), Cancellation); }
        catch (ArgumentException exception) { exceptions.Add(exception.ToString()); }
        try { new DelegationRequest(account, account, ["item.*"], password, null, server.Clock.Now.AddHours(1)).ToString(); }
        catch (ArgumentException exception) { exceptions.Add(exception.ToString()); }

        var raw = Base64Url.DecodeFromChars(token);
        var secrets = new Dictionary<string, string>
        {
            ["password"] = password, ["wrong password"] = wrong, ["new password"] = replacement, ["verifier"] = verifier, ["session token"] = token,
            ["session secret"] = Convert.ToHexStringLower(raw), ["session hash"] = Convert.ToHexStringLower(SHA256.HashData(raw)),
            ["recovery code"] = codes.Codes[1].Reveal(), ["seed"] = Convert.ToHexStringLower(seed), ["seed base64"] = Convert.ToBase64String(seed)
        };
        var logs = server.Logs.Entries;
        Assert.NotEmpty(logs);
        foreach (var (name, secret) in secrets)
        {
            Assert.All(logs, entry => Assert.DoesNotContain(secret, entry, StringComparison.OrdinalIgnoreCase));
            Assert.All(seen, response => Assert.DoesNotContain(secret, response, StringComparison.OrdinalIgnoreCase));
            Assert.All(exceptions, exception => Assert.DoesNotContain(secret, exception, StringComparison.OrdinalIgnoreCase));
        }
        Assert.Contains(seen, response => response.StartsWith("OK", StringComparison.Ordinal));
        output.WriteLine($"Searched {logs.Count} log entries at Trace, {seen.Count} responses and {exceptions.Count} exceptions for {secrets.Count} credential forms: none found.");
        Assert.Equal(HttpStatusCode.OK, (await server.Browser().SignInAsync("admin-1", replacement)).StatusCode);
    }
}
