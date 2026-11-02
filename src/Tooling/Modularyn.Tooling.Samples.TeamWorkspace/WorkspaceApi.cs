using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Settings;
using Modularyn.Modules.Files.Application.Contracts;

namespace Modularyn.Tooling.Samples.TeamWorkspace;

public static class WorkspaceApi
{
    public const string Prefix = "/api/v{version}/t/{tenant}";
    public static ApiInventory Inventory { get; } = Create();
    public static ApiEndpoint Endpoint(string name) => Inventory.Endpoints.Single(item => item.Name == name);

    private static ApiInventory Create()
    {
        var models = new ApiModel[]
        {
            new("CreateProject", [new("Name", "string")]),
            new("ProjectView", [new("Id", "uuid"), new("Name", "string"), new("Version", "uuid")]),
            new("CreateNote", [new("ProjectId", "uuid"), new("Text", "string")]),
            new("ReviseNote", [new("NoteId", "uuid"), new("Version", "uuid"), new("Text", "string")]),
            new("NoteView", [new("Id", "uuid"), new("ProjectId", "uuid"), new("Text", "string"), new("Revision", "integer"), new("Version", "uuid")]),
            new("SubscribeDigest", [new("TimeZone", "string"), new("LocalTime", "string")]),
            new("IdentityView", [new("Id", "uuid")]),
            new("AttachmentReference", [new("FileId", "uuid"), new("VersionId", "uuid")])
        };
        var endpoints = new ApiEndpoint[]
        {
            Page("SignInPage", "/sign-in", "GET", null),
            Page("NewNotePage", "/t/{tenant}/notes/new", "GET", WorkspaceDefinition.Write),
            Page("NewNoteForm", "/t/{tenant}/notes/new", "POST", WorkspaceDefinition.Write),
            new("SignInScript", "GET", "/assets/sign-in.js", "Anonymous", null, false, null, null, Versioned: false,
                ResponseMediaType: ApiMediaTypes.Binary, Authentication: "none", Download: new("inline", ["text/javascript"])),
            Json("CreateProject", "POST", "/projects", WorkspaceDefinition.Write, "CreateProject", "ProjectView", true),
            Json("GetProjects", "GET", "/projects", WorkspaceDefinition.Read),
            Json("CreateNote", "POST", "/notes", WorkspaceDefinition.Write, "CreateNote", "NoteView", true),
            Json("ReviseNote", "PUT", "/notes", WorkspaceDefinition.Write, "ReviseNote", "NoteView", true),
            Json("GetRevisions", "GET", "/notes/{note}/revisions", WorkspaceDefinition.Read),
            Json("GetActivity", "GET", "/activity", WorkspaceDefinition.Read),
            Json("SubscribeDigest", "POST", "/digests", WorkspaceDefinition.Digest, "SubscribeDigest", "IdentityView", true),
            Json("GetDigests", "GET", "/digests", WorkspaceDefinition.Digest),
            Json("UploadAttachment", "POST", "/attachments/{note}", FilePermissions.Upload) with
                { Upload = new(["application/octet-stream", "image/png", "image/jpeg", "text/plain"]) },
            Json("GetAttachments", "GET", "/attachments", FilePermissions.Read),
            Json("GetFileVersions", "GET", "/files/{file}/versions", FilePermissions.Read),
            Json("QueueRelease", "POST", "/releases", FilePermissions.Release, "AttachmentReference", "IdentityView", true),
            Json("IssueDownloadGrant", "POST", "/grants", FilePermissions.Grant, "AttachmentReference"),
            Json("DownloadAttachment", "GET", "/download", FilePermissions.Read) with
                { ResponseMediaType = ApiMediaTypes.Binary, Download = new("attachment", ["image/png", "image/jpeg", "text/plain"]) },
            Json("GetContract", "GET", "/contract", WorkspaceDefinition.Read),
            Json("GetLocalization", "GET", "/localization", WorkspaceDefinition.Read),
            Json("SetTitle", "PUT", "/title", SettingsModule.TenantOverridesPermission),
            Json("SetDigestFeature", "PUT", "/digest-feature", SettingsModule.TenantOverridesPermission),
            Json("DefineMemberRole", "POST", "/member-role", AuthorizationModule.ManagePermission),
            Json("AssignMemberRole", "POST", "/members/{member}/role", AuthorizationModule.ManagePermission),
            Json("ReadAudit", "GET", "/audit", "modularyn.audit.read"),
            Json("ReadJobHistory", "GET", "/jobs", "modularyn.diagnostics.read"),
            Json("ReadEventQuarantine", "GET", "/events", "modularyn.diagnostics.read"),
            Json("DevelopmentSeed", "POST", "/development-seed", WorkspaceDefinition.Write),
            new("CreateTenant", "POST", "/api/v{version}/host/tenants", "HostAuthority", null, false, null, null,
                Mutation: true, Authentication: "session"),
            new("AddMembership", "POST", "/api/v{version}/host/memberships", "HostAuthority", null, false, null, null,
                Mutation: true, Authentication: "session")
        };
        var result = new ApiInventory(1, 2, models, endpoints);
        result.Validate();
        return result;
    }

    private static ApiEndpoint Json(string name, string method, string path, string permission, string? request = null,
        string? response = null, bool client = false) => new(name, method, Prefix + path, "Permission", permission, client, request, response,
            Mutation: method != "GET", Idempotent: client && method != "GET", Authentication: "session");
    private static ApiEndpoint Page(string name, string route, string method, string? permission) => new(name, method, route,
        permission is null ? "Anonymous" : "Permission", permission, false, null, null, Mutation: method == "POST", Versioned: false,
        ResponseMediaType: ApiMediaTypes.Html, Authentication: permission is null ? "none" : "session", Page: true, Antiforgery: method == "POST");
}
