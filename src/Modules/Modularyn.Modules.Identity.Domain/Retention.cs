using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Modules.Identity.Domain;

public sealed record IdentityRetentionResult(int ThrottleRows, int Sessions, bool Committed);

/// <summary>Bounded, idempotent retention in an explicit audited host scope; never deletes authority records.</summary>
public interface IIdentityRetentionStore
{
    Task<IdentityRetentionResult> PruneAsync(TenantContext host, int limit, CancellationToken cancellationToken);
}

public static class IdentityRetentionPolicy
{
    public const string OperationId = "modularyn.identity.retention";
    public const string Permission = "identity.retention";
    // Covers every permitted throttle policy, including the source-account counter's double reset period.
    public static readonly TimeSpan ThrottleRetention = TimeSpan.FromDays(60);
    public static readonly TimeSpan EndedSessionRetention = TimeSpan.FromDays(30);
}
