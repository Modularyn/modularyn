using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Modules.Identity.Application;

public static class IdentityRetentionJob
{
    public const string Kind = "identity.retention";
    public static PermissionDefinition Permission(SemanticVersion version) =>
        new(IdentityModule.Id, IdentityRetentionPolicy.Permission, version, PermissionFreshness.Current);

    /// <summary>Register on the durable worker; the callback always runs inside AuthorizedWorkRunner.</summary>
    public static JobDefinition Create(IIdentityRetentionStore store, IServiceScopeFactory scopes, int batchSize = 100)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return new(Kind, IdentityRetentionPolicy.Permission, async (execution, token) =>
        {
            await execution.CheckpointAsync(token).ConfigureAwait(false);
            await using var scope = scopes.CreateAsyncScope();
            var host = await scope.ServiceProvider.GetRequiredService<TenantExecution>().EstablishServiceHostAsync(execution.Principal,
                IdentityRetentionPolicy.OperationId, "Remove expired identity retention rows", token).ConfigureAwait(false);
            if (!host.Succeeded) throw new InvalidOperationException("Identity retention host authority was refused: " + host.Code);
            var result = await store.PruneAsync(host.Context!, batchSize, token).ConfigureAwait(false);
            if (!result.Committed) throw new InvalidOperationException("Identity retention lost a concurrent write; retry the bounded batch.");
        });
    }
}
