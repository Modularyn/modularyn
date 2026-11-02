using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Api;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Events;
using Modularyn.Framework.Features;
using Modularyn.Framework.Features.Abstractions;
using Modularyn.Framework.Hosting;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Localization;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Persistence.EfCore;
using Modularyn.Modules.Files.Persistence.FileSystem;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.HttpApi;
using Modularyn.Modules.Identity.Persistence.EfCore;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

public static class TeamWorkspaceHost
{
    public const long MaximumAttachmentBytes = 10L * 1024 * 1024;
    public static readonly string[] ReleasedMediaTypes = ["image/png", "image/jpeg", "text/plain"];

    /// <summary>
    /// The host for <see cref="HostOperatorProvisioning"/>: the serving host's composition, plus the provisioning service
    /// identity, which no serving host declares. Its required permission ceiling is never exercised: it enters no tenant.
    /// </summary>
    public static Task<WebApplication> BuildProvisioningAsync(IConfiguration configuration,
        Action<WebApplicationBuilder>? configure = null, CancellationToken cancellationToken = default) =>
        BuildAsync(configuration, builder =>
        {
            builder.Services.AddModularynServiceIdentity(WorkspaceDefinition.Provisioner, [WorkspaceDefinition.Read]);
            configure?.Invoke(builder);
        }, cancellationToken);

    public static async Task<WebApplication> BuildAsync(IConfiguration configuration,
        Action<WebApplicationBuilder>? configure = null, CancellationToken cancellationToken = default)
    {
        var environment = configuration["DOTNET_ENVIRONMENT"] ?? Environments.Production;
        var seed = configuration.GetValue<bool>("Workspace:DevelopmentSeed");
        if (seed && environment is not ("Development" or "Testing"))
            throw new InvalidOperationException("Development seed is refused outside Development and Testing.");
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment, ContentRootPath = AppContext.BaseDirectory, ApplicationName = typeof(TeamWorkspaceHost).Assembly.GetName().Name
        });
        builder.Configuration.AddConfiguration(configuration);
        builder.WebHost.UseKestrel().UseUrls(configuration["ASPNETCORE_URLS"] ?? "http://127.0.0.1:8080");
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateScopes = true; options.ValidateOnBuild = true; });
        builder.Logging.AddConsole();
        // Request URI, headers, body and exceptions are not access-log fields. Modularyn's logger also redacts secret forms.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        builder.Services.AddModularynSecrets(configuration);
        builder.Services.AddModularynDataProtection(configuration);
        builder.Services.AddModularynIngress(configuration);

        var secretDirectory = Required(configuration, "Modularyn:Secrets:Directory");
        if (!Path.IsPathFullyQualified(secretDirectory) || !System.IO.Directory.Exists(secretDirectory))
            throw new InvalidOperationException("Declare an existing secret directory.");
        var secrets = new FileSecretStore(Options.Create(new SecretStoreOptions { Directory = secretDirectory }));
        using var database = await secrets.GetAsync(SecretReference.Parse(Required(configuration, "Workspace:Database")), cancellationToken);
        WorkspaceComposition composition;
        try { composition = new(new PostgreSqlPersistenceOptions(database.RevealString()), configuration["Workspace:SchemaPrefix"] ?? "team_"); }
        catch (Exception) { throw new InvalidOperationException("Workspace persistence configuration is invalid."); }
        var services = builder.Services;
        services.AddSingleton(composition);
        services.AddModularynPostgreSqlPersistence(composition.Modules);
        services.AddModularynTenancy(composition.Directory);
        services.AddModularynIdentity(configuration);
        services.AddModularynIdentityPostgreSql(composition.Identity);
        services.AddModularynIdentityHttp();
        services.AddModularynAuthorization(configuration, composition.Authorization, composition.Permissions);
        services.AddModularynAudit(composition.Audit);
        services.AddModularynSettings(composition.Settings, new ValueDefinitionRegistry(composition.Manifests,
            [WorkspaceDefinition.Title, WorkspaceDefinition.Digests]));
        services.AddModularynFeatures();
        services.AddScoped<IFeatureGate, DigestFeatureGate>();
        services.AddModularynLocalization(new ModuleLocalization(composition.Manifests, [WorkspaceDefinition.Text]));
        services.AddModularynServiceIdentity(WorkspaceDefinition.Worker,
            [WorkspaceDefinition.Write, WorkspaceDefinition.Read, WorkspaceDefinition.Digest, ScheduleWorker.SchedulePermission, FilePermissions.Release]);
        // This declares a service's ceiling. Deployment must separately grant the dispatch host operation.
        services.AddSingleton<IFileReleasePolicy>(new AllowlistFileReleasePolicy(ReleasedMediaTypes, MaximumAttachmentBytes));
        var blobRoot = Required(configuration, "Workspace:BlobDirectory");
        services.AddSingleton<IFiles>(provider => new FileService(new FileMetadataStore(composition.Files, provider.GetRequiredService<IAuditTrail>()),
            new FileSystemBlobStore(blobRoot, MaximumAttachmentBytes, readSlots: 1), provider.GetRequiredService<IPermissionEvaluator>(), provider.GetRequiredService<TimeProvider>(),
            new FilesPolicy(uploadTimeout: TimeSpan.FromSeconds(25), maximumGrantLifetime: TimeSpan.FromMinutes(2)),
            provider.GetRequiredService<IFileReleasePolicy>()));
        services.AddSingleton(provider => new EventStore(composition.Workspace, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IAuditTrail>()));
        services.AddSingleton(provider => new JobStore(composition.Workspace, provider.GetRequiredService<TimeProvider>(), new JobPolicy(), provider.GetRequiredService<IAuditTrail>()));
        services.AddSingleton(provider => new WorkspaceService(composition.Workspace, provider.GetRequiredService<EventStore>(), provider.GetRequiredService<JobStore>(),
            provider.GetRequiredService<IPermissionEvaluator>(), provider.GetRequiredService<IAuditTrail>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<WorkspaceHandlers>();
        services.AddSingleton<FileSteps>();
        services.AddSingleton<WorkspaceDispatcher>();
        var lockPath = Required(configuration, "Workspace:DeploymentLock");
        services.AddModularynDeploymentTrust(lockPath, AppContext.BaseDirectory);
        services.AddModularynModules(composition.Environment,
            composition.Manifests.Select(manifest => ModuleRegistration.Create<ComposedModule>(manifest)));
        services.AddHostedService<WorkspaceWorker>();
        configure?.Invoke(builder);
        var app = builder.Build();
        app.UseRouting();
        app.UseModularynApi();
        app.UseModularynIngress();
        app.UseModularynTenancy();
        app.UseAuthorization();
        app.UseModularynRequests();
        app.MapModularynIdentityEndpoints();
        app.MapModularynHealthChecks();
        app.MapModularynBootstrap();
        WorkspaceEndpoints.Map(app, seed);
        return app;
    }

    public static async Task MigrateAsync(WebApplication app, CancellationToken token)
    {
        app.Services.GetService<IStartupValidator>()?.Validate();
        var composition = app.Services.GetRequiredService<WorkspaceComposition>();
        // Readiness is checked on activation. Application is a separate operation, retaining trust and all definition validators.
        var validators = app.Services.GetServices<ICompositionValidator>().Where(value => value is not MigrationActivationValidator);
        var result = await new ModuleComposer(validators).ComposeAsync(new(composition.Manifests, composition.Environment), token);
        if (!result.Succeeded) throw new InvalidOperationException("Composition refused migration.");
        var migrated = await app.Services.GetRequiredService<PostgreSqlMigrationCoordinator>().ApplyAsync(result.Plan!, cancellationToken: token);
        if (migrated.Diagnostics.Count != 0) throw new InvalidOperationException("Migration did not complete.");
    }

    private static string Required(IConfiguration configuration, string name) =>
        !string.IsNullOrWhiteSpace(configuration[name]) ? configuration[name]! : throw new InvalidOperationException("Configure " + name + ".");
}

// The modules' migration validators own durable readiness; these registrations own no additional external resources.
public sealed class ComposedModule : IModule
{
    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StartAsync(ModuleContext context, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StopAsync(ModuleContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class DigestFeatureGate(IPermissionEvaluator evaluator, IExecutionPrincipalAccessor principal) : IFeatureGate
{
    public async ValueTask<bool> AllowsAsync(TenantContext context, FeatureDefinition feature, CancellationToken cancellationToken) =>
        feature == WorkspaceDefinition.Digests && (await evaluator.AuthorizeAsync(new(context, principal.Current, WorkspaceDefinition.Digest)
            { RequireCurrent = true }, cancellationToken)).Permitted;
}
