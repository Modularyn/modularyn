using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Tenancy.Tests;
using Modularyn.Modules.Identity.Application.Contracts;
using Xunit;

namespace Modularyn.Modules.Identity.Testing;

/// <summary>
/// Identity administration for tests: real host-scope contexts, which SEC-04's tenancy runtime establishes under test
/// stand-ins for authorization's host authority and audit, one for each of identity's host operations. The stand-ins are
/// scaffolding for identity's own decisions; the authorization tests exercise the real elevated policy.
/// </summary>
internal sealed class IdentityAdministrator : IAsyncDisposable
{
    internal const string Subject = "identity-admin";
    private readonly ServiceProvider services;
    private readonly List<AsyncServiceScope> scopes = [];
    private readonly Dictionary<string, IdentityAuthority> authorities = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly MemoryDirectory directory = new();
    private readonly TestSeams seams = new();

    public IdentityAdministrator()
    {
        foreach (var operation in new[] { IdentityHostOperations.Accounts, IdentityHostOperations.Memberships, IdentityHostOperations.Sessions,
                     IdentityHostOperations.Credentials })
            seams.Authority.Grant(Subject, operation);
        services = UnitServices.Build(directory, seams);
    }

    /// <summary>An identity administrator's authority in an audited host scope granted for the operation, active until disposal.</summary>
    public async Task<IdentityAuthority> HostAsync(string operationId)
    {
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (authorities.TryGetValue(operationId, out var existing)) return existing;
            var (scope, context) = await Units.EnterHostAsync(services, Subject, operationId);
            scopes.Add(scope);
            return authorities[operationId] = IdentityAuthority.Host(context);
        }
        finally { gate.Release(); }
    }

    /// <summary>A member's tenant-scope context, offered as if it were authority: identity must refuse it.</summary>
    public async Task<IdentityAuthority> TenantMemberAsync()
    {
        var tenant = directory.Add("Tenant " + Guid.NewGuid().ToString("N"));
        seams.Membership.Add(Subject, tenant);
        var (scope, context) = await Units.EnterAsync(services, Subject, tenant);
        scopes.Add(scope);
        return IdentityAuthority.Host(context);
    }

    /// <summary>A host scope granted for the operation whose unit has already ended.</summary>
    public async Task<IdentityAuthority> EndedHostAsync(string operationId)
    {
        var (scope, context) = await Units.EnterHostAsync(services, Subject, operationId);
        await scope.DisposeAsync();
        return IdentityAuthority.Host(context);
    }

    public Task<IdentityAuthority> AccountsAsync() => HostAsync(IdentityHostOperations.AccountsId);
    public Task<IdentityAuthority> MembershipsAsync() => HostAsync(IdentityHostOperations.MembershipsId);
    public Task<IdentityAuthority> SessionsAsync() => HostAsync(IdentityHostOperations.SessionsId);
    public Task<IdentityAuthority> CredentialsAsync() => HostAsync(IdentityHostOperations.CredentialsId);

    public async ValueTask DisposeAsync()
    {
        foreach (var scope in scopes) await scope.DisposeAsync();
        await services.DisposeAsync();
        gate.Dispose();
    }
}
