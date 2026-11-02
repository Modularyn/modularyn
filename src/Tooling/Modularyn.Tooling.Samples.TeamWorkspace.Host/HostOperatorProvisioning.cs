using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Domain;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

public enum ProvisioningOutcome
{
    /// <summary>The account is the first host operator, holding every operation of the bootstrap authority but provisioning.</summary>
    Provisioned = 0,

    /// <summary>A host operator exists: an earlier provisioning, another account's unfinished one, or a configured user operator.</summary>
    HostOperatorExists = 1,

    /// <summary>The deployment names no bootstrap authority for this run, or one without host authority or provisioning.</summary>
    BootstrapAuthorityAbsent = 2,

    /// <summary>No enabled account has the identifier.</summary>
    AccountUnavailable = 3
}

/// <summary>
/// The one-time command that makes an existing account the deployment's first host operator (SEC-05's decision). It runs
/// as the declared service identity <see cref="WorkspaceDefinition.Provisioner"/>, which only the provisioning process
/// declares, under the deployment's own authority: the authorization option <c>HostOperators</c>, configured for this run,
/// naming that service with <c>modularyn.authorization.host-authority</c>, <c>workspace.provision-host-operator</c> and each
/// operation the first operator is to hold. Its one input is the account's identifier, never a secret.
/// <para>
/// It refuses while a host operator exists. In one audited unit it claims the provisioning record, whose fixed key lets one
/// run claim it; then it grants the account each bootstrap operation but provisioning through
/// <see cref="HostAuthorityAdministration"/>, every grant committing with its own audit record; then it marks the record
/// complete with a record of what it granted. A run that stopped after its claim resumes for the same account only, so a
/// failure never leaves the deployment without a way to finish, and a completed provisioning is final.
/// </para>
/// </summary>
public static class HostOperatorProvisioning
{
    public const string Command = "provision-host-operator";
    public const string StartedAction = "workspace.host-operator-provisioning";
    public const string ProvisionedAction = "workspace.host-operator-provisioned";
    private const string Reason = "Provision the first host operator";

    /// <summary>
    /// Validates the deployment as startup does, activates its modules without serving requests, provisions, and stops them
    /// again. The application must come from <see cref="TeamWorkspaceHost.BuildProvisioningAsync"/> and must not be running.
    /// </summary>
    public static async Task<ProvisioningOutcome> RunAsync(WebApplication app, Guid account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (account == Guid.Empty) throw new ArgumentException("Name the account by its identifier.", nameof(account));
        app.Services.GetService<IStartupValidator>()?.Validate();
        var modules = app.Services.GetRequiredService<ModuleHost>();
        // Activation checks the deployment lock and migration readiness, as a serving host's startup does.
        await modules.StartAsync(cancellationToken);
        try { return await ProvisionAsync(app.Services, account, cancellationToken); }
        finally { await modules.StopAsync(CancellationToken.None); }
    }

    /// <summary>The fixed message for an outcome. It names no account, operation or configuration value.</summary>
    public static string Describe(ProvisioningOutcome outcome) => outcome switch
    {
        ProvisioningOutcome.Provisioned => "The first host operator is provisioned. Remove the bootstrap authority from the deployment's configuration.",
        ProvisioningOutcome.HostOperatorExists => "Provisioning refused: a host operator already exists.",
        ProvisioningOutcome.BootstrapAuthorityAbsent => "Provisioning refused: the deployment names no bootstrap authority for this run.",
        ProvisioningOutcome.AccountUnavailable => "Provisioning refused: no enabled account has that identifier.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static async Task<ProvisioningOutcome> ProvisionAsync(IServiceProvider services, Guid account, CancellationToken token)
    {
        var configured = services.GetRequiredService<IOptions<AuthorizationSecurityOptions>>().Value.HostOperators;
        // A user the configuration names is a host operator already; provisioning is for a deployment that has none.
        if (configured.Any(entry => entry.Kind == ActorKind.User)) return ProvisioningOutcome.HostOperatorExists;
        var bootstrap = configured.SingleOrDefault(entry => entry.Kind == ActorKind.Service && entry.Subject == WorkspaceDefinition.Provisioner);
        if (bootstrap is null) return ProvisioningOutcome.BootstrapAuthorityAbsent;

        // Each host scope is granted by tenancy only under host authority and after its own audit record.
        var identities = services.GetRequiredService<IServiceIdentityScopes>();
        await using var claiming = identities.Begin(WorkspaceDefinition.Provisioner);
        await using var granting = identities.Begin(WorkspaceDefinition.Provisioner);
        var claim = await HostScopeAsync(claiming, WorkspaceDefinition.ProvisionHostOperator.Id, token);
        var grant = claim is null ? null : await HostScopeAsync(granting, AuthorizationModule.HostAuthorityOperation.Id, token);
        if (claim is null || grant is null) return ProvisioningOutcome.BootstrapAuthorityAbsent;

        if (await services.GetRequiredService<IAccountDirectory>().FindAsync(account, token) is not { Disabled: false })
            return ProvisioningOutcome.AccountUnavailable;
        var first = new ActorReference(ActorKind.User, account.ToString("D"));
        var operations = bootstrap.Operations.Where(operation => operation != WorkspaceDefinition.ProvisionHostOperator.Id)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var module = services.GetRequiredService<WorkspaceComposition>().Workspace;
        var audit = services.GetRequiredService<IAuditTrail>();
        var clock = services.GetRequiredService<TimeProvider>();
        if (!await ClaimAsync(module, audit, clock, claim, first, token)) return ProvisioningOutcome.HostOperatorExists;
        // The bootstrap holds each of these by configuration, and HostAuthorityAdministration refuses any it does not.
        var administration = services.GetRequiredService<HostAuthorityAdministration>();
        foreach (var operation in operations) await administration.GrantAsync(grant, first, operation, token);
        await CompleteAsync(module, audit, clock, claim, first, operations, token);
        return ProvisioningOutcome.Provisioned;
    }

    private static async Task<TenantContext?> HostScopeAsync(ServiceIdentityScope identity, string operation, CancellationToken token)
    {
        var decision = await identity.Services.GetRequiredService<TenantExecution>().EstablishServiceHostAsync(identity.Principal, operation, Reason, token);
        if (decision.Succeeded) return decision.Context;
        // An unavailable seam or audit is not a refusal of authority: the run fails, and can be repeated.
        return decision.Terminal ? null : throw new InvalidOperationException("Host authority could not be decided.");
    }

    /// <summary>
    /// Claims the provisioning record for the account, or finds it already claimed. Only the same account's unfinished
    /// provisioning resumes; a completed one, or another account's, refuses.
    /// </summary>
    private static async Task<bool> ClaimAsync(PostgreSqlModule module, IAuditTrail audit, TimeProvider clock, TenantContext host, ActorReference first,
        CancellationToken token)
    {
        await using (var work = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: token))
        {
            if (await Find(work, token) is { } existing) return Resumes(existing, first);
            work.Context.Add(new HostOperatorProvisioningRecord
            {
                Id = HostOperatorProvisioningRecord.Key, Account = first.Subject, StartedAt = UtcMicroseconds.Now(clock)
            });
            await audit.RecordAsync(work, Entry(StartedAction, host, first), token);
            try
            {
                if ((await work.CompleteAsync(token)).State == UnitOfWorkState.Committed) return true;
            }
            catch (DbUpdateException) { }
        }
        // Another run claimed the record first; its claim decides, and anything else is a failure, not a refusal.
        await using var read = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: token);
        return await Find(read, token) is { } winner ? Resumes(winner, first)
            : throw new InvalidOperationException("The provisioning record did not commit.");
    }

    private static async Task CompleteAsync(PostgreSqlModule module, IAuditTrail audit, TimeProvider clock, TenantContext host, ActorReference first,
        string[] operations, CancellationToken token)
    {
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: token);
        var record = await Find(work, token) ?? throw new InvalidOperationException("The provisioning record is missing.");
        if (record.CompletedAt is not null) return;
        record.CompletedAt = UtcMicroseconds.Now(clock);
        await audit.RecordAsync(work, Entry(ProvisionedAction, host, first) with { Detail = string.Join(' ', operations) }, token);
        if ((await work.CompleteAsync(token)).State != UnitOfWorkState.Committed)
            throw new InvalidOperationException("The provisioning record did not commit.");
    }

    private static Task<HostOperatorProvisioningRecord?> Find(EfCoreUnitOfWork work, CancellationToken token) =>
        work.Context.Set<HostOperatorProvisioningRecord>().SingleOrDefaultAsync(row => row.Id == HostOperatorProvisioningRecord.Key, token);

    private static bool Resumes(HostOperatorProvisioningRecord record, ActorReference first) =>
        record.CompletedAt is null && string.Equals(record.Account, first.Subject, StringComparison.Ordinal);

    private static AuditEntry Entry(string action, TenantContext host, ActorReference first) => AuditEntry.InContext(action, host) with
    {
        Subject = first, ResourceType = "account", ResourceId = first.Subject, OperationId = host.HostGrant!.OperationId, Reason = host.HostGrant.Reason
    };
}
