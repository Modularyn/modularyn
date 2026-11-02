using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Features;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Localization;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Identity.Application;
using Modularyn.Modules.Identity.Application.Contracts;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

public static class WorkspaceEndpoints
{
    public const string MemberRole = "workspace-member";
    public static readonly string[] MemberPermissions = [WorkspaceDefinition.Read, WorkspaceDefinition.Write, WorkspaceDefinition.Digest,
        ScheduleWorker.SchedulePermission, FilePermissions.Upload, FilePermissions.Read, FilePermissions.Grant, FilePermissions.Release];

    public static void Map(WebApplication app, bool developmentSeed)
    {
        Map("SignInPage", c => Task.FromResult(WorkspacePages.SignIn(c)));
        Map("SignInScript", c =>
        {
            c.Response.Headers.ContentDisposition = "inline";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            c.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.FromResult(Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "sign-in.js"), "text/javascript"));
        });
        Map("NewNotePage", WorkspacePages.NewNoteAsync);
        Map("NewNoteForm", WorkspacePages.CreateNoteAsync);
        Map("CreateProject", async c => Results.Json(await Service(c).CreateProjectAsync(Tenant(c), Principal(c), ApiEndpoints.OperationId(c), Version(c),
            await ApiEndpoints.ReadRequestAsync<CreateProject>(c), c.RequestAborted)));
        Map("GetProjects", async c => Results.Json(await Service(c).ProjectsAsync(Tenant(c), Principal(c), c.RequestAborted)));
        Map("CreateNote", async c => Results.Json(await Service(c).CreateNoteAsync(Tenant(c), Principal(c), ApiEndpoints.OperationId(c), Version(c),
            await ApiEndpoints.ReadRequestAsync<CreateNote>(c), c.RequestAborted)));
        Map("ReviseNote", async c => Results.Json(await Service(c).ReviseAsync(Tenant(c), Principal(c), ApiEndpoints.OperationId(c), Version(c),
            await ApiEndpoints.ReadRequestAsync<ReviseNote>(c), c.RequestAborted)));
        Map("GetRevisions", async c => Results.Json(await Service(c).RevisionsAsync(Tenant(c), Principal(c), Id(c, "note"), c.RequestAborted)));
        Map("GetActivity", async c => Results.Json(await Service(c).ActivityAsync(Tenant(c), Principal(c), c.RequestAborted)));
        Map("SubscribeDigest", async c =>
        {
            if (!await c.RequestServices.GetRequiredService<FeatureService>().IsEnabledAsync(Tenant(c), WorkspaceDefinition.Digests, c.RequestAborted))
                throw new ApiException(ApiCodes.Refused, 403);
            return Results.Json(await Service(c).SubscribeAsync(Tenant(c), Principal(c), ApiEndpoints.OperationId(c), Version(c),
                await ApiEndpoints.ReadRequestAsync<SubscribeDigest>(c), c.RequestAborted));
        });
        Map("GetDigests", async c => Results.Json(await Service(c).DigestsAsync(Tenant(c), Principal(c), c.RequestAborted)));
        Map("UploadAttachment", UploadAsync);
        Map("GetAttachments", async c => Results.Json(await Steps(c).RunAsync(c, FilePermissions.Read,
            (files, tenant, principal, ct) => files.ListFilesAsync(tenant, principal, "notes", cancellationToken: ct))));
        Map("GetFileVersions", async c => Results.Json(await Steps(c).RunAsync(c, FilePermissions.Read,
            (files, tenant, principal, ct) => files.ListAsync(tenant, principal, Id(c, "file"), ct))));
        Map("QueueRelease", async c => Results.Json(await Service(c).QueueReleaseAsync(Tenant(c), Principal(c), ApiEndpoints.OperationId(c), Version(c),
            await ApiEndpoints.ReadRequestAsync<AttachmentReference>(c), c.RequestAborted)));
        Map("IssueDownloadGrant", async c =>
        {
            var request = await ApiEndpoints.ReadRequestAsync<AttachmentReference>(c);
            var issued = await Steps(c).RunAsync(c, FilePermissions.Grant, (files, tenant, principal, ct) =>
                files.IssueGrantAsync(tenant, principal, request.FileId, request.VersionId, FileGrantPurpose.Download, TimeSpan.FromMinutes(2), ct));
            // Only this uncached JSON response reveals the secret. Never place it in a link, receipt, log or trace.
            return Results.Json(new { grantId = issued.Grant.GrantId, expiresAt = issued.Grant.ExpiresAt, token = issued.Token.Reveal() });
        });
        Map("DownloadAttachment", async c => { await Steps(c).DownloadAsync(c); return Results.Empty; });
        Map("GetContract", c =>
        {
            var source = new CompositeEndpointDataSource(((IEndpointRouteBuilder)app).DataSources);
            return Task.FromResult(Results.Text(ApiGenerator.OpenApi(ApiEndpoints.Describe(source)), ApiMediaTypes.Json));
        });
        Map("GetLocalization", c =>
        {
            var requested = c.Request.Query["culture"];
            if (requested.Count > 1) throw new ApiException(ApiCodes.Validation, 400);
            try
            {
                return Task.FromResult(Results.Json(c.RequestServices.GetRequiredService<ModuleLocalization>()
                    .Bootstrap(WorkspaceDefinition.Id, "workspace", requested.Count == 0 ? "en" : requested[0]!)));
            }
            catch (ArgumentException) { throw new ApiException(ApiCodes.Validation, 400); }
        });
        Map("SetTitle", async c =>
        {
            var request = await ApiEndpoints.ReadRequestAsync<TitleChange>(c);
            var version = await c.RequestServices.GetRequiredService<SettingsService>().SetAsync(Tenant(c), Principal(c), WorkspaceDefinition.Title,
                ValueSource.Tenant, request.Title, request.Version, c.RequestAborted);
            return Results.Json(new { version });
        });
        Map("SetDigestFeature", async c =>
        {
            var request = await ApiEndpoints.ReadRequestAsync<FeatureChange>(c);
            var version = await c.RequestServices.GetRequiredService<FeatureService>().SetAsync(Tenant(c), Principal(c), WorkspaceDefinition.Digests,
                ValueSource.Tenant, request.Enabled, request.Version, c.RequestAborted);
            return Results.Json(new { version });
        });
        Map("DefineMemberRole", async c => Results.Json(new { changed = await c.RequestServices.GetRequiredService<AuthorityAdministration>()
            .DefineRoleAsync(Tenant(c), Principal(c), MemberRole, MemberPermissions, [], c.RequestAborted) }));
        Map("AssignMemberRole", async c => Results.Json(new { changed = await c.RequestServices.GetRequiredService<AuthorityAdministration>()
            .AssignRoleAsync(Tenant(c), Principal(c), new ActorReference(ActorKind.User, Id(c, "member").ToString("D")), MemberRole, c.RequestAborted) }));
        Map("ReadAudit", async c => Results.Json(await c.RequestServices.GetRequiredService<AuditReader>()
            .ReadAsync(Tenant(c), Principal(c), new AuditQuery(), c.RequestAborted)));
        Map("ReadJobHistory", async c => Results.Json(await Service(c).Jobs.HistoryAsync(Tenant(c), Principal(c),
            c.RequestServices.GetRequiredService<IPermissionEvaluator>(), cancellationToken: c.RequestAborted)));
        Map("ReadEventQuarantine", async c => Results.Json(await Service(c).Events.QuarantineAsync(Tenant(c), Principal(c),
            c.RequestServices.GetRequiredService<IPermissionEvaluator>(), cancellationToken: c.RequestAborted)));
        if (developmentSeed) Map("DevelopmentSeed", async c =>
        {
            // Requires an already provisioned member with write permission. It creates no identity or authority.
            var project = await Service(c).CreateProjectAsync(Tenant(c), Principal(c), Guid.NewGuid(), 1, new("Team handbook"), c.RequestAborted);
            var note = await Service(c).CreateNoteAsync(Tenant(c), Principal(c), Guid.NewGuid(), 1,
                new(project.Id, "Record the decisions for this project here."), c.RequestAborted);
            return Results.Json(new { project, note });
        });
        MapHost("CreateTenant", TenantDirectoryAdministration.OperationId, async c =>
        {
            var request = await ApiEndpoints.ReadRequestAsync<TenantCreation>(c);
            return Results.Json(new IdentityView(await c.RequestServices.GetRequiredService<TenantDirectoryAdministration>()
                .CreateTenantAsync(Tenant(c), request.Name, c.RequestAborted)));
        });
        MapHost("AddMembership", IdentityHostOperations.MembershipsId, async c =>
        {
            var request = await ApiEndpoints.ReadRequestAsync<MembershipChange>(c);
            return Results.Json(new { changed = await c.RequestServices.GetRequiredService<ITenantMemberships>()
                .AddMemberAsync(IdentityAuthority.Host(Tenant(c)), request.AccountId, request.TenantId, c.RequestAborted) });
        });

        void Map(string name, Func<HttpContext, Task<IResult>> handler)
        {
            var contract = WorkspaceApi.Endpoint(name);
            var mapped = app.MapMethods(contract.Route, [contract.Method], async (HttpContext context) =>
            {
                try { return await handler(context); }
                catch (ValueValidationException) { throw new ApiException(ApiCodes.Validation, 400); }
                catch (ValueConflictException) { throw new ApiException(ApiCodes.Conflict, 409); }
                catch (ValueUnavailableException) { throw new ApiException(ApiCodes.Unavailable, 503); }
                catch (ValueAccessDeniedException exception)
                {
                    throw new ApiException(exception.Code,
                        exception.Code is AuthorizationCodes.Unavailable or AuthorizationCodes.AuditUnavailable ? 503 : 403);
                }
            });
            if (contract.Permission is { } permission) mapped.RequireTenantMember().RequirePermission(permission);
            else mapped.AllowAnonymous();
            mapped.WithApiContract(contract, WorkspaceApi.Inventory);
        }
        void MapHost(string name, string operation, Func<HttpContext, Task<IResult>> handler)
        {
            var contract = WorkspaceApi.Endpoint(name);
            app.MapMethods(contract.Route, [contract.Method], async (HttpContext context) =>
            {
                try { return await handler(context); }
                catch (Exception exception) when (HostRefusal(exception) is { } refusal) { throw refusal; }
            }).RequireHostAuthority(operation, "Administer workspace directory").WithApiContract(contract, WorkspaceApi.Inventory);
        }
    }

    /// <summary>
    /// The problem a host endpoint answers for a refusal of directory or identity administration: invalid input is a
    /// validation failure, a change that did not commit or could not be audited is unavailable, and anything else they
    /// refuse is forbidden.
    /// Every other exception keeps the API wrapper's answer.
    /// </summary>
    public static ApiException? HostRefusal(Exception exception) => exception switch
    {
        ArgumentException => new ApiException(ApiCodes.Validation, 400),
        TenancyException { Code: "MTN022" or "MTN023" } refused => new ApiException(refused.Code, 503),
        TenancyException refused => new ApiException(refused.Code, 403),
        IdentityAuthorityException { Code: IdentityAuthorityCodes.AuditUnavailable } refused => new ApiException(refused.Code, 503),
        IdentityAuthorityException refused => new ApiException(refused.Code, 403),
        _ => null
    };

    private static async Task<IResult> UploadAsync(HttpContext c)
    {
        var contract = WorkspaceApi.Endpoint("UploadAttachment");
        var media = c.Request.ContentType?.Split(';')[0].Trim();
        if (!contract.Upload!.MediaTypes.Contains(media ?? "", StringComparer.Ordinal)) throw new ApiException(ApiCodes.Validation, 415);
        if (c.Request.ContentLength is not { } length || length < 0 || length > TeamWorkspaceHost.MaximumAttachmentBytes)
            throw new ApiException(ApiCodes.Validation, 413);
        var note = Id(c, "note");
        if ((await Service(c).RevisionsAsync(Tenant(c), Principal(c), note, c.RequestAborted)).Count == 0)
            throw new ApiException(ApiCodes.NotFound, 404);
        if (c.Request.Headers["X-File-Name"] is not [{ } name] || c.Request.Headers["X-Content-SHA256"] is not [{ } hash])
            throw new ApiException(ApiCodes.Validation, 400);
        var request = new FileUploadRequest(null, name, "notes", note.ToString("D"), length, hash);
        return Results.Json(await Steps(c).RunAsync(c, FilePermissions.Upload,
            (files, tenant, principal, ct) => files.UploadAsync(tenant, principal, request, c.Request.Body, ct)));
    }

    internal static WorkspaceService Service(HttpContext c) => c.RequestServices.GetRequiredService<WorkspaceService>();
    internal static TenantContext Tenant(HttpContext c) => c.RequestServices.GetRequiredService<TenantExecution>().Context!;
    internal static ExecutionPrincipal Principal(HttpContext c) => c.RequestServices.GetRequiredService<IExecutionPrincipalAccessor>().Current!;
    private static FileSteps Steps(HttpContext c) => c.RequestServices.GetRequiredService<FileSteps>();
    private static int Version(HttpContext c) => int.Parse((string)c.Request.RouteValues["version"]!, CultureInfo.InvariantCulture);
    private static Guid Id(HttpContext c, string name) => Guid.TryParseExact(c.Request.RouteValues[name]?.ToString(), "D", out var id) && id != Guid.Empty
        ? id : throw new ApiException(ApiCodes.Validation, 400);

    public sealed record TitleChange(string Title, Guid? Version);
    public sealed record FeatureChange(bool Enabled, Guid? Version);
    public sealed record TenantCreation(string Name);
    public sealed record MembershipChange(Guid AccountId, Guid TenantId);
}
