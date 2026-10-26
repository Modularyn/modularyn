using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Xunit;

namespace Modularyn.Modules.Identity.Persistence.EfCore.Tests;

/// <summary>Counts every verification and hash ASP.NET Core Identity's hasher performs, without changing what it computes.</summary>
internal sealed class CountingPasswordHasher(int iterations) : IPasswordHasher<IdentityAccount>
{
    private readonly PasswordHasher<IdentityAccount> inner = new(Options.Create(new PasswordHasherOptions { IterationCount = iterations }));
    private int verifications;
    private int hashes;

    public int Verifications => Volatile.Read(ref verifications);
    public int Hashes => Volatile.Read(ref hashes);

    public string HashPassword(IdentityAccount user, string password)
    {
        Interlocked.Increment(ref hashes);
        return inner.HashPassword(user, password);
    }

    public PasswordVerificationResult VerifyHashedPassword(IdentityAccount user, string hashedPassword, string providedPassword)
    {
        Interlocked.Increment(ref verifications);
        return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}

/// <summary>The identity application over its real PostgreSQL stores in one test schema, with a manual clock.</summary>
internal sealed class PostgreSqlIdentity : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private PostgreSqlIdentity(ServiceProvider provider, ManualClock clock, CountingPasswordHasher hasher)
    {
        this.provider = provider;
        Clock = clock;
        Hasher = hasher;
    }

    public ManualClock Clock { get; }
    public CountingPasswordHasher Hasher { get; }

    public static PostgreSqlIdentity Create(IdentityDatabase database, Dictionary<string, string?>? settings = null, ManualClock? clock = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Modularyn:Identity:Throttling:SourceThreshold"] = "6",
            ["Modularyn:Identity:Throttling:AccountFreeAttempts"] = "3",
            ["Modularyn:Identity:Throttling:AccountBaseDelay"] = "00:00:02",
            ["Modularyn:Identity:Throttling:AccountMaximumDelay"] = "00:00:16"
        };
        foreach (var (key, value) in settings ?? []) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        clock ??= ManualClock.StartingNow();
        var hasher = new CountingPasswordHasher(IdentityPolicies.From(new IdentitySecurityOptions()).Passwords.HashIterations);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IPasswordHasher<IdentityAccount>>(hasher);
        services.AddModularynIdentity(configuration);
        if (configuration["Modularyn:Secrets:Directory"] is not null) services.AddModularynSecrets(configuration);
        services.AddModularynIdentityPostgreSql(database.Module);
        return new PostgreSqlIdentity(services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }), clock, hasher);
    }

    public T Get<T>() where T : notnull => provider.GetRequiredService<T>();

    public async Task<(Guid Id, string Password)> AccountAsync(string userName)
    {
        var password = Generated.Password();
        var created = await Get<IAccountService>().CreateLocalAccountAsync(userName, new SecretText(password), TestContext.Current.CancellationToken);
        Assert.Equal(AccountCreationStatus.Created, created.Status);
        return (created.AccountId!.Value, password);
    }

    public Task<Application.Contracts.SignInResult> SignInAsync(string userName, string password, string source = "203.0.113.1") =>
        Get<ISignInService>().SignInWithPasswordAsync(new PasswordSignInRequest(userName, new SecretText(password), source), TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
