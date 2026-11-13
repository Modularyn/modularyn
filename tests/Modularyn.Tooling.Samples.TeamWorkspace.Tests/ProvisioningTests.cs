using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Persistence.EfCore.Tests;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Repository.Tests;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;
using Npgsql;
using Xunit;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

/// <summary>
/// The one-time host-operator provisioning command through the real host, its module activation, identity, authorization,
/// tenancy's host scopes and audit, on PostgreSQL. Each test builds the provisioning host several times over one database,
/// as successive runs of the command with different deployment configuration.
/// </summary>
[Collection(TeamWorkspacePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class ProvisioningTests
{
    private static readonly string[] Granted =
        [AuthorizationModule.HostAuthorityOperation.Id, TenantDirectoryAdministration.OperationId, IdentityHostOperations.MembershipsId];
    private static readonly Operator Bootstrap = new(ActorKind.Service, WorkspaceDefinition.Provisioner,
        [.. Granted, WorkspaceDefinition.ProvisionHostOperator.Id]);

    [Fact]
    public async Task The_first_host_operator_is_provisioned_once_under_the_deployments_bootstrap_authority()
    {
        var token = TestContext.Current.CancellationToken;
        await using var deployment = await ProvisioningDeployment.StartAsync(token);

        // A user that the configuration names is a host operator already.
        await deployment.BuildAsync([Bootstrap, new(ActorKind.User, Guid.NewGuid().ToString("D"), [TenantDirectoryAdministration.OperationId])], token);
        var first = await deployment.AccountAsync(token);
        Check.That(await deployment.ProvisionAsync(first, token) == ProvisioningOutcome.HostOperatorExists, "configured-user-operator");

        // Without the deployment's bootstrap authority, or with one that lacks host authority, nothing is granted.
        await deployment.BuildAsync([], token);
        Check.That(await deployment.ProvisionAsync(first, token) == ProvisioningOutcome.BootstrapAuthorityAbsent, "no-bootstrap-authority");
        await deployment.BuildAsync([Bootstrap with { Operations = [TenantDirectoryAdministration.OperationId, WorkspaceDefinition.ProvisionHostOperator.Id] }], token);
        Check.That(await deployment.ProvisionAsync(first, token) == ProvisioningOutcome.BootstrapAuthorityAbsent, "bootstrap-without-host-authority");
        await deployment.BuildAsync([Bootstrap with { Operations = [.. Granted] }], token);
        Check.That(await deployment.ProvisionAsync(first, token) == ProvisioningOutcome.BootstrapAuthorityAbsent, "bootstrap-without-provisioning");
        Check.That((await deployment.HeldAsync(first, token)).Length == 0, "nothing-granted-without-bootstrap");

        await deployment.BuildAsync([Bootstrap], token);
        Check.That(await deployment.ProvisionAsync(Guid.NewGuid(), token) == ProvisioningOutcome.AccountUnavailable, "unknown-account");
        var disabled = await deployment.AccountAsync(token, disable: true);
        Check.That(await deployment.ProvisionAsync(disabled, token) == ProvisioningOutcome.AccountUnavailable, "disabled-account");
        Check.That((await deployment.AuditAsync(HostOperatorProvisioning.StartedAction, token)).Count == 0, "refusals-change-nothing");

        Check.That(await deployment.ProvisionAsync(first, token) == ProvisioningOutcome.Provisioned, "provisioned");
        Check.That((await deployment.HeldAsync(first, token)).SequenceEqual(Granted.Order(StringComparer.Ordinal)), "first-operator-holds-the-bootstrap-operations");
        var started = await deployment.AuditAsync(HostOperatorProvisioning.StartedAction, token);
        var provisioned = await deployment.AuditAsync(HostOperatorProvisioning.ProvisionedAction, token);
        var grants = await deployment.AuditAsync(AuditActions.Of(AuthorityChangeKind.HostAuthorityGranted), token);
        Check.That(started is [{ Subject.Subject: var startedFor, Actor.Subject: WorkspaceDefinition.Provisioner }] && startedFor == first.ToString("D"), "claim-audited");
        Check.That(provisioned is [{ Subject.Subject: var provisionedFor, Detail: var detail }] && provisionedFor == first.ToString("D")
            && detail == string.Join(' ', Granted.Order(StringComparer.Ordinal)), "completion-audited");
        Check.That(grants.Count == Granted.Length && grants.All(record => record.Subject?.Subject == first.ToString("D")), "each-grant-audited");

        // A second run refuses, for the same account or another, and changes nothing.
        Check.That(await deployment.ProvisionAsync(first, token) == ProvisioningOutcome.HostOperatorExists, "second-run-refused");
        var second = await deployment.AccountAsync(token);
        Check.That(await deployment.ProvisionAsync(second, token) == ProvisioningOutcome.HostOperatorExists, "another-account-refused");
        Check.That((await deployment.HeldAsync(second, token)).Length == 0, "no-second-operator");
        Check.That((await deployment.AuditAsync(AuditActions.Of(AuthorityChangeKind.HostAuthorityGranted), token)).Count == Granted.Length, "no-further-grants");

        // A serving host never declares the provisioning identity, so a bootstrap entry left in its configuration is inert.
        await deployment.BuildAsync([Bootstrap], token, serving: true);
        Check.That(!await deployment.ProvisionerDeclaredAsync(), "serving-host-cannot-act-as-provisioner");
    }

    [Fact]
    public async Task A_provisioning_that_stopped_after_its_claim_resumes_only_for_its_own_account()
    {
        var token = TestContext.Current.CancellationToken;
        await using var deployment = await ProvisioningDeployment.StartAsync(token);
        await deployment.BuildAsync([Bootstrap], token);
        var first = await deployment.AccountAsync(token);
        var other = await deployment.AccountAsync(token);
        // The state a run leaves when it stops after claiming the record and before its grants complete.
        await deployment.ClaimDirectlyAsync(first, token);

        Check.That(await deployment.ProvisionAsync(other, token) == ProvisioningOutcome.HostOperatorExists, "unfinished-claim-refuses-another-account");
        Check.That((await deployment.HeldAsync(other, token)).Length == 0, "other-account-holds-nothing");
        Check.That(await deployment.ProvisionAsync(first, token) == ProvisioningOutcome.Provisioned, "unfinished-claim-resumes");
        Check.That((await deployment.HeldAsync(first, token)).SequenceEqual(Granted.Order(StringComparer.Ordinal)), "resumed-grants");
        Check.That((await deployment.AuditAsync(HostOperatorProvisioning.ProvisionedAction, token)).Count == 1, "resumed-completion-audited");
        Check.That(await deployment.ProvisionAsync(first, token) == ProvisioningOutcome.HostOperatorExists, "completed-is-final");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task Two_concurrent_provisioning_hosts_grant_exactly_one_account_and_audit_one_completion()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var deployment = await ProvisioningDeployment.StartAsync(token);
        await deployment.BuildAsync([Bootstrap], token);
        var first = await deployment.AccountAsync(token);
        var second = await deployment.AccountAsync(token);
        var outcomes = await deployment.ProvisionConcurrentlyAsync(first, second, token);
        Assert.Single(outcomes, outcome => outcome == ProvisioningOutcome.Provisioned);
        Assert.Single(outcomes, outcome => outcome == ProvisioningOutcome.HostOperatorExists);
        var winner = outcomes[0] == ProvisioningOutcome.Provisioned ? first : second;
        var loser = winner == first ? second : first;
        Assert.Equal(Granted.Order(StringComparer.Ordinal), await deployment.HeldAsync(winner, token));
        Assert.Empty(await deployment.HeldAsync(loser, token));
        var started = Assert.Single(await deployment.AuditAsync(HostOperatorProvisioning.StartedAction, token));
        var completed = Assert.Single(await deployment.AuditAsync(HostOperatorProvisioning.ProvisionedAction, token));
        Assert.Equal(winner.ToString("D"), started.Subject!.Subject);
        Assert.Equal(winner.ToString("D"), completed.Subject!.Subject);
        var grants = await deployment.AuditAsync(AuditActions.Of(AuthorityChangeKind.HostAuthorityGranted), token);
        Assert.Equal(Granted.Length, grants.Count);
        Assert.All(grants, record => Assert.Equal(winner.ToString("D"), record.Subject!.Subject));
        Assert.Equal(ProvisioningOutcome.HostOperatorExists, await deployment.ProvisionAsync(loser, token));
        Assert.Equal(ProvisioningOutcome.HostOperatorExists, await deployment.ProvisionAsync(winner, token));
        Assert.Single(await deployment.AuditAsync(HostOperatorProvisioning.ProvisionedAction, token));
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task A_refusal_after_host_scope_acquisition_retains_both_tenancy_audit_entries()
    {
        var token = TestContext.Current.CancellationToken;
        await using var deployment = await ProvisioningDeployment.StartAsync(token);
        await deployment.BuildAsync([Bootstrap], token);
        Assert.Equal(ProvisioningOutcome.AccountUnavailable, await deployment.ProvisionAsync(Guid.NewGuid(), token));
        var scopes = (await deployment.AuditAsync(AuditActions.HostScopeGranted, token))
            .Where(record => record.Actor.Subject == WorkspaceDefinition.Provisioner).ToArray();
        Assert.Equal(2, scopes.Length);
        Assert.Equal(new[] { AuthorizationModule.HostAuthorityOperation.Id, WorkspaceDefinition.ProvisionHostOperator.Id }.Order(StringComparer.Ordinal),
            scopes.Select(record => record.OperationId).Order(StringComparer.Ordinal));
        Assert.Empty(await deployment.AuditAsync(HostOperatorProvisioning.StartedAction, token));
        Assert.Empty(await deployment.AuditAsync(HostOperatorProvisioning.ProvisionedAction, token));
        Assert.Empty(await deployment.AuditAsync(AuditActions.Of(AuthorityChangeKind.HostAuthorityGranted), token));
    }
}

internal sealed record Operator(ActorKind Kind, string Subject, string[] Operations);

/// <summary>
/// One deployment of the sample over one isolated set of schemas, built again for each run of the provisioning command. A
/// fixture service, which only these tests declare and configure, creates accounts and reads host audit.
/// </summary>
internal sealed class ProvisioningDeployment : IAsyncDisposable
{
    private readonly PostgreSqlTestLock databaseLock;
    private readonly string connectionString;
    private readonly string directory;
    private readonly string prefix = "tw_" + Guid.NewGuid().ToString("N")[..24] + "_";
    private readonly string fixture = "fixture-" + Guid.NewGuid().ToString("N");
    private readonly CapturedLogs logs = new();
    private WebApplication? app;
    private Func<CancellationToken, Task<WebApplication>>? buildSibling;
    private bool migrated;

    private ProvisioningDeployment(string connectionString, PostgreSqlTestLock databaseLock, string directory)
    {
        this.connectionString = connectionString;
        this.databaseLock = databaseLock;
        this.directory = directory;
    }

    internal static async Task<ProvisioningDeployment> StartAsync(CancellationToken token)
    {
        var session = await PostgreSqlProbe.ConnectAsync(PostgreSqlTarget.FromEnvironment(), token);
        Check.That(session.ServerVersionNumber is >= 180000 and < 190000 && session.Parameters.GetValueOrDefault("is_superuser") == "off", "database-role-and-version");
        var connection = Environment.GetEnvironmentVariable(PostgreSqlTarget.Variable)!;
        var deployment = await PostgreSqlTestLock.HoldAsync(connection, held => new ProvisioningDeployment(connection, held, RunDirectory.NewPath()), token);
        try
        {
            RunDirectory.Create(deployment.directory);
            foreach (var name in new[] { "keys", "secrets", "blobs" }) KeyRingDirectory.Create(Path.Combine(deployment.directory, name));
            var secret = Path.Combine(deployment.directory, "secrets", "database");
            await File.WriteAllTextAsync(secret, connection, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await File.WriteAllTextAsync(Path.Combine(deployment.directory, "deployment-lock.json"), DeploymentArtifacts.Describe().ToJson(), token);
            return deployment;
        }
        catch { await deployment.DisposeAsync(); throw; }
    }

    /// <summary>Builds the provisioning host as a new run of the command would, with these host operators and the fixture's.</summary>
    internal async Task BuildAsync(IReadOnlyList<Operator> operators, CancellationToken token, bool serving = false)
    {
        if (app is not null) await app.DisposeAsync();
        app = null;
        var config = new Dictionary<string, string?>
        {
            ["DOTNET_ENVIRONMENT"] = "Testing", ["Workspace:Database"] = "secret:database", ["Workspace:SchemaPrefix"] = prefix,
            ["Workspace:BlobDirectory"] = Path.Combine(directory, "blobs"), ["Workspace:DeploymentLock"] = Path.Combine(directory, "deployment-lock.json"),
            ["Modularyn:Secrets:Directory"] = Path.Combine(directory, "secrets"),
            ["Modularyn:DataProtection:ApplicationName"] = "Modularyn.TeamWorkspace.Provisioning",
            ["Modularyn:DataProtection:Directory"] = Path.Combine(directory, "keys"),
            ["Modularyn:Ingress:PublicOrigins:0"] = "https://127.0.0.1:8443", ["Modularyn:Ingress:HttpsPort"] = "8443"
        };
        Operator[] all = [new(ActorKind.Service, fixture, [IdentityHostOperations.AccountsId, AuditModule.HostReadOperation.Id]), .. operators];
        for (var index = 0; index < all.Length; index++)
        {
            config[$"Modularyn:Authorization:HostOperators:{index}:Kind"] = all[index].Kind.ToString();
            config[$"Modularyn:Authorization:HostOperators:{index}:Subject"] = all[index].Subject;
            for (var operation = 0; operation < all[index].Operations.Length; operation++)
                config[$"Modularyn:Authorization:HostOperators:{index}:Operations:{operation}"] = all[index].Operations[operation];
        }
        void Configure(WebApplicationBuilder builder)
        {
            builder.Services.AddModularynServiceIdentity(fixture, [WorkspaceDefinition.Read]);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        buildSibling = ct => TeamWorkspaceHost.BuildProvisioningAsync(configuration, Configure, ct);
        app = serving ? await TeamWorkspaceHost.BuildAsync(configuration, Configure, token)
            : await TeamWorkspaceHost.BuildProvisioningAsync(configuration, Configure, token);
        if (!migrated) await TeamWorkspaceHost.MigrateAsync(app, token);
        migrated = true;
    }

    internal async Task<bool> ProvisionerDeclaredAsync()
    {
        try
        {
            await using var scope = app!.Services.GetRequiredService<IServiceIdentityScopes>().Begin(WorkspaceDefinition.Provisioner);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    internal Task<ProvisioningOutcome> ProvisionAsync(Guid account, CancellationToken token) => HostOperatorProvisioning.RunAsync(app!, account, token);

    internal async Task<ProvisioningOutcome[]> ProvisionConcurrentlyAsync(Guid first, Guid second, CancellationToken token)
    {
        await using var sibling = await buildSibling!(token);
        Assert.NotSame(app!.Services, sibling.Services);
        // Activation's migration-readiness check holds the exclusive migration lock, and an activation that finds it held is
        // refused with MDC202 for the operator to retry (the persistence README). Both hosts therefore activate first, one
        // after the other, so each run's own activation repeats a completed one.
        await app.Services.GetRequiredService<ModuleHost>().StartAsync(token);
        await sibling.Services.GetRequiredService<ModuleHost>().StartAsync(token);
        // Started together, the second run would otherwise reach its claim only after the first had completed. A SHARE lock on
        // the provisioning table lets both runs find no record but holds both inserts, so the two claims are simultaneous: once
        // the lock is released, one insert commits and the other fails on the record's key, the losing insert of a real race.
        var table = $"\"{prefix}workspace\".\"{WorkspaceDefinition.ProvisioningTable}\"";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var command = new NpgsqlCommand($"LOCK TABLE {table} IN SHARE MODE", connection, transaction))
            await command.ExecuteNonQueryAsync(token);
        var runs = new[] { HostOperatorProvisioning.RunAsync(app, first, token), HostOperatorProvisioning.RunAsync(sibling, second, token) };
        await using (var waiting = new NpgsqlCommand(
            $"SELECT count(*) FROM pg_locks WHERE relation = '{table}'::regclass AND mode = 'RowExclusiveLock' AND NOT granted", connection, transaction))
        {
            while ((long)(await waiting.ExecuteScalarAsync(token))! < 2)
            {
                Check.That(!runs.Any(run => run.IsCompleted), "both-runs-reach-their-claim");
                await Task.Delay(TimeSpan.FromMilliseconds(20), token);
            }
        }
        await transaction.CommitAsync(token);
        return await Task.WhenAll(runs);
    }

    internal async Task<Guid> AccountAsync(CancellationToken token, bool disable = false)
    {
        await using var scope = app!.Services.GetRequiredService<IServiceIdentityScopes>().Begin(fixture);
        var decision = await scope.Services.GetRequiredService<TenantExecution>().EstablishServiceHostAsync(scope.Principal,
            IdentityHostOperations.AccountsId, "Create provisioning test accounts", token);
        Check.That(decision.Succeeded, "fixture-host-scope");
        var accounts = app.Services.GetRequiredService<IAccountService>();
        var created = await accounts.CreateLocalAccountAsync(IdentityAuthority.Host(decision.Context!), "operator-" + Guid.NewGuid().ToString("N"),
            new SecretText("pw-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24))), token);
        Check.That(created.Status == AccountCreationStatus.Created, "create-account");
        if (disable) Check.That(await accounts.DisableAsync(IdentityAuthority.Host(decision.Context!), created.AccountId!.Value, token), "disable-account");
        return created.AccountId!.Value;
    }

    /// <summary>The host operations the account holds now, read as tenancy reads them for each host scope.</summary>
    internal async Task<string[]> HeldAsync(Guid account, CancellationToken token)
    {
        var authority = app!.Services.GetRequiredService<IHostAuthority>();
        var held = new List<string>();
        foreach (var operation in app.Services.GetRequiredService<WorkspaceComposition>().Permissions.HostOperations.Select(item => item.Operation.Id))
            if (await authority.AuthorizeAsync(new(ActorKind.User, account.ToString("D")), operation, token) is not null) held.Add(operation);
        return [.. held.Order(StringComparer.Ordinal)];
    }

    internal async Task<IReadOnlyList<AuditRecord>> AuditAsync(string action, CancellationToken token)
    {
        await using var scope = app!.Services.GetRequiredService<IServiceIdentityScopes>().Begin(fixture);
        var decision = await scope.Services.GetRequiredService<TenantExecution>().EstablishServiceHostAsync(scope.Principal,
            AuditModule.HostReadOperation.Id, "Read provisioning test audit", token);
        Check.That(decision.Succeeded, "fixture-audit-scope");
        return await app.Services.GetRequiredService<AuditReader>().ReadAsync(decision.Context!, scope.Principal,
            new AuditQuery { Action = action, Limit = AuditQuery.MaximumLimit }, token);
    }

    /// <summary>Writes the record a run leaves when it stops after its claim, as the database would hold it.</summary>
    internal async Task ClaimDirectlyAsync(Guid account, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(
            $"INSERT INTO \"{prefix}workspace\".\"{WorkspaceDefinition.ProvisioningTable}\" (\"Id\", \"Account\", \"StartedAt\") VALUES ($1, $2, $3)", connection);
        command.Parameters.AddWithValue(HostOperatorProvisioningRecord.Key);
        command.Parameters.AddWithValue(account.ToString("D"));
        command.Parameters.AddWithValue(UtcMicroseconds.Now(TimeProvider.System));
        Check.That(await command.ExecuteNonQueryAsync(token) == 1, "direct-claim");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (app is not null) await app.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            foreach (var module in new WorkspaceComposition(new PostgreSqlPersistenceOptions(connectionString), prefix).Modules.Modules.Reverse())
            {
                await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS \"" + module.Schema + "\" CASCADE", connection);
                await command.ExecuteNonQueryAsync(timeout.Token);
            }
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            finally { await databaseLock.DisposeAsync(); }
        }
    }
}
