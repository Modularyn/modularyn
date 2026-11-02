using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;
using FileVersionInfo = Modularyn.Modules.Files.Application.Contracts.FileVersionInfo;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

public static class SmokeScenario
{
    public static async Task RunAsync(CancellationToken token)
    {
        var traces = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => traces.Enqueue(string.Join(" ", activity.TagObjects.Select(pair => pair.Key + "=" + pair.Value)))
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = await SmokeHost.StartAsync(token);
        var tenant = await host.TenantAsync(token);
        var otherTenant = await host.TenantAsync(token);
        var owner = await host.UserAsync(tenant, true, token);
        var member = await host.UserAsync(tenant, false, token);
        var prefix = "/api/v1/t/" + tenant.ToString("D");
        using var ownerClient = host.Client();
        using var client = host.Client();
        using var anonymous = host.Client();
        using (var health = await anonymous.GetAsync("/health/ready", token)) Check.That(health.IsSuccessStatusCode, "host-ready");
        var signInPage = await anonymous.GetStringAsync("/sign-in", token);
        Check.That(signInPage.Contains("src=\"/assets/sign-in.js\"", StringComparison.Ordinal)
            && !signInPage.Contains("<script>", StringComparison.Ordinal), "external-sign-in-script");
        using (var script = await anonymous.GetAsync("/assets/sign-in.js", token))
            Check.That(script.Content.Headers.ContentType?.MediaType == "text/javascript", "script-media");
        using (var refused = await anonymous.GetAsync(prefix + "/projects", token)) Check.That(refused.StatusCode == HttpStatusCode.Unauthorized, "anonymous-refused");
        await SignInAsync(ownerClient, owner, token);
        await SignInAsync(client, member, token);
        using (var denied = await client.GetAsync(prefix + "/projects", token)) Check.That(denied.StatusCode == HttpStatusCode.Forbidden, "permission-default-deny");
        using (var role = await SendAsync(ownerClient, HttpMethod.Post, prefix + "/member-role", new { }, token)) Success(role, "define-role");
        using (var role = await SendAsync(ownerClient, HttpMethod.Post, prefix + "/members/" + member.Id.ToString("D") + "/role", new { }, token)) Success(role, "assign-role");
        await PresentationAsync(ownerClient, client, prefix, token);
        var project = await JsonAsync<ProjectView>(client, HttpMethod.Post, prefix + "/projects", new CreateProject("Shared project"), token);
        var operation = Guid.NewGuid();
        var request = new CreateNote(project.Id, "A first note");
        var note = await JsonAsync<NoteView>(client, HttpMethod.Post, prefix + "/notes", request, token, operation);
        var replay = await JsonAsync<NoteView>(client, HttpMethod.Post, prefix + "/notes", request, token, operation);
        Check.That(note == replay, "idempotent-note-retry");
        var revision = await JsonAsync<NoteView>(client, HttpMethod.Put, prefix + "/notes", new ReviseNote(note.Id, note.Version, "A second revision"), token);
        Check.That(revision.Revision == 2 && revision.Version != note.Version, "note-version");
        using (var stale = await SendAsync(client, HttpMethod.Put, prefix + "/notes", new ReviseNote(note.Id, note.Version, "Stale text"), token))
            Check.That(stale.StatusCode == HttpStatusCode.Conflict, "stale-note-refused");
        using (var foreign = await client.GetAsync("/api/v1/t/" + otherTenant.ToString("D") + "/projects", token))
            Check.That(foreign.StatusCode == HttpStatusCode.NotFound, "tenant-isolation");
        await FormAsync(client, tenant, project.Id, token);
        var revisions = await client.GetFromJsonAsync<NoteRevision[]>(prefix + "/notes/" + note.Id.ToString("D") + "/revisions", token);
        Check.That(revisions is { Length: 2 } && revisions.All(item => item.CreatedAt.UtcTicks % 10 == 0), "revision-history-and-time");

        var content = Encoding.UTF8.GetBytes("An attachment in quarantine.\n");
        // A member's release only asks the allowlist policy to judge the member's own file: plain text is released.
        var upload = await UploadAsync(client, prefix, note.Id, content, "notes.txt", token);
        Check.That(upload.Quarantine == FileQuarantineState.Pending, "upload-quarantined");
        var reference = new AttachmentReference(upload.FileId, upload.VersionId);
        using (var pending = await SendAsync(client, HttpMethod.Post, prefix + "/grants", reference, token))
            Check.That(pending.StatusCode == HttpStatusCode.NotFound, "quarantine-unreadable");
        await JsonAsync<IdentityView>(client, HttpMethod.Post, prefix + "/releases", reference, token);
        await host.Services.GetRequiredService<WorkspaceDispatcher>().RunAsync(token);
        var versions = await client.GetFromJsonAsync<FileVersionInfo[]>(prefix + "/files/" + upload.FileId.ToString("D") + "/versions", token);
        Check.That(versions is [{ Quarantine: FileQuarantineState.Released }], "allowlist-release");
        var grant = await JsonAsync<GrantResponse>(client, HttpMethod.Post, prefix + "/grants", reference, token);
        using (var missing = await client.GetAsync(prefix + "/download", token)) Check.That(missing.StatusCode == HttpStatusCode.BadRequest, "header-required");
        using (var download = await DownloadAsync(client, prefix, grant.Token, token))
        {
            Success(download, "download");
            Check.That((await download.Content.ReadAsByteArrayAsync(token)).SequenceEqual(content), "download-integrity");
            Check.That(download.Content.Headers.ContentDisposition?.DispositionType == "attachment"
                && download.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "download-headers");
        }
        await DeadlineAsync(host, member, tenant, grant.Token, token);
        using (var next = await DownloadAsync(client, prefix, grant.Token, token)) Success(next, "download-slot-released");

        // PDF passes the upload detector but is outside this deployment's release allowlist. The allowlist policy leaves a
        // version it does not release pending in quarantine, never rejected, which would be final.
        var pdf = await UploadAsync(client, prefix, note.Id, Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n"), "document.pdf", token);
        var rejected = new AttachmentReference(pdf.FileId, pdf.VersionId);
        await JsonAsync<IdentityView>(client, HttpMethod.Post, prefix + "/releases", rejected, token);
        await host.Services.GetRequiredService<WorkspaceDispatcher>().RunAsync(token);
        var refusedVersions = await client.GetFromJsonAsync<FileVersionInfo[]>(prefix + "/files/" + pdf.FileId.ToString("D") + "/versions", token);
        Check.That(refusedVersions is [{ Quarantine: FileQuarantineState.Pending }], "allowlist-refusal");
        using (var refused = await SendAsync(client, HttpMethod.Post, prefix + "/grants", rejected, token))
            Check.That(refused.StatusCode == HttpStatusCode.NotFound, "rejected-content-unreadable");

        // An owner releases its own attachment the same way; Files authorizes a release for the file's creator.
        var ownerUpload = await UploadAsync(ownerClient, prefix, note.Id, content, "owner.txt", token);
        await JsonAsync<IdentityView>(ownerClient, HttpMethod.Post, prefix + "/releases", new AttachmentReference(ownerUpload.FileId, ownerUpload.VersionId), token);
        await host.Services.GetRequiredService<WorkspaceDispatcher>().RunAsync(token);
        var ownerVersions = await ownerClient.GetFromJsonAsync<FileVersionInfo[]>(prefix + "/files/" + ownerUpload.FileId.ToString("D") + "/versions", token);
        Check.That(ownerVersions is [{ Quarantine: FileQuarantineState.Released }], "owner-releases-own-file");

        await host.Services.GetRequiredService<WorkspaceDispatcher>().RunAsync(token);
        var activity = await client.GetFromJsonAsync<ActivityItem[]>(prefix + "/activity", token);
        Check.That(activity!.Count(item => item.NoteId == note.Id) == 2, "durable-activity");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var due = TimeZoneInfo.ConvertTime(host.Clock.GetUtcNow(), zone).AddMinutes(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        await JsonAsync<IdentityView>(client, HttpMethod.Post, prefix + "/digests", new SubscribeDigest(zone.Id, due), token);
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        await host.Services.GetRequiredService<WorkspaceDispatcher>().RunAsync(token);
        var digests = await client.GetFromJsonAsync<MemberDigest[]>(prefix + "/digests", token);
        Check.That(digests is { Length: > 0 } && digests.All(item => item.Member == member.Id.ToString("D") && item.CreatedAt.UtcTicks % 10 == 0), "member-zone-digest");
        await host.Services.GetRequiredService<WorkspaceDispatcher>().RunAsync(token);
        var repeated = await client.GetFromJsonAsync<MemberDigest[]>(prefix + "/digests", token);
        Check.That(repeated!.Length == digests!.Length, "digest-once-per-occurrence");
        host.Clock.Advance(TimeSpan.FromMinutes(3));
        using (var expired = await DownloadAsync(client, prefix, grant.Token, token))
            Check.That(expired.StatusCode == HttpStatusCode.NotFound, "expired-grant");
        using (var contract = await client.GetAsync(prefix + "/contract", token)) Success(contract, "active-openapi-inventory");
        await HostAdministrationAsync(host, ownerClient, client, owner, member, token);
        using (var audit = await ownerClient.GetAsync(prefix + "/audit", token))
        {
            Success(audit, "audit-access");
            var text = await audit.Content.ReadAsStringAsync(token);
            Check.That(text.Contains("workspace.note-revised", StringComparison.Ordinal), "audited-note-write");
            Check.That(!text.Contains(grant.Token, StringComparison.Ordinal), "grant-not-in-audit");
        }
        // ASP.NET Core's request logs are Information and stay out; its warnings, such as the refused form post's missing
        // antiforgery token, are kept.
        var aspNetCore = host.Logs.Entries.Where(entry => entry.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)).ToArray();
        Check.That(aspNetCore.All(entry => entry.Level >= LogLevel.Warning), "no-aspnetcore-information-log");
        Check.That(aspNetCore.Any(entry => entry.Category.StartsWith("Microsoft.AspNetCore.Antiforgery", StringComparison.Ordinal)), "aspnetcore-warnings-kept");
        Check.That(!host.Logs.Lines.Concat(traces).Any(line => line.Contains(grant.Token, StringComparison.Ordinal)
            || line.Contains(FileSteps.GrantHeader, StringComparison.OrdinalIgnoreCase)
            || line.Contains(member.Password, StringComparison.Ordinal) || line.Contains(owner.Password, StringComparison.Ordinal)), "secret-log-and-trace-canaries");
    }

    private static async Task SignInAsync(HttpClient client, SmokeUser user, CancellationToken token)
    {
        using var result = await SendAsync(client, HttpMethod.Post, "/identity/sign-in", new { userName = user.Name, password = user.Password, returnUrl = "/" }, token);
        Success(result, "identity-sign-in");
        using var current = await client.GetAsync("/identity/current-user", token);
        Success(current, "identity-session");
    }

    // The host endpoints run only in a host scope that the caller's own host authority grants: owning a tenant confers none.
    private static async Task HostAdministrationAsync(SmokeHost host, HttpClient ownerClient, HttpClient memberClient, SmokeUser owner, SmokeUser member,
        CancellationToken token)
    {
        using (var refused = await SendAsync(ownerClient, HttpMethod.Post, "/api/v1/host/tenants", new WorkspaceEndpoints.TenantCreation("Refused tenant"), token))
            Check.That(refused.StatusCode == HttpStatusCode.Forbidden, "host-tenants-refused-without-host-authority");
        using (var refused = await SendAsync(memberClient, HttpMethod.Post, "/api/v1/host/memberships",
            new WorkspaceEndpoints.MembershipChange(member.Id, Guid.NewGuid()), token))
            Check.That(refused.StatusCode == HttpStatusCode.Forbidden, "host-memberships-refused-without-host-authority");
        await host.GrantHostAuthorityAsync(owner.Id, [TenantDirectoryAdministration.OperationId, IdentityHostOperations.MembershipsId], token);
        // Invalid input is a validation failure, not an outcome the caller cannot know.
        using (var invalid = await SendAsync(ownerClient, HttpMethod.Post, "/api/v1/host/tenants", new WorkspaceEndpoints.TenantCreation(" Padded name "), token))
            Check.That(invalid.StatusCode == HttpStatusCode.BadRequest, "host-tenant-name-validated");
        using (var invalid = await SendAsync(ownerClient, HttpMethod.Post, "/api/v1/host/memberships",
            new WorkspaceEndpoints.MembershipChange(member.Id, Guid.Empty), token))
            Check.That(invalid.StatusCode == HttpStatusCode.BadRequest, "host-membership-tenant-validated");
        var created = await JsonAsync<IdentityView>(ownerClient, HttpMethod.Post, "/api/v1/host/tenants", new WorkspaceEndpoints.TenantCreation("Created tenant"), token);
        var projects = "/api/v1/t/" + created.Id.ToString("D") + "/projects";
        using (var before = await memberClient.GetAsync(projects, token)) Check.That(before.StatusCode == HttpStatusCode.NotFound, "created-tenant-not-yet-joined");
        await JsonAsync<JsonElement>(ownerClient, HttpMethod.Post, "/api/v1/host/memberships", new WorkspaceEndpoints.MembershipChange(member.Id, created.Id), token);
        using (var after = await memberClient.GetAsync(projects, token))
            Check.That(after.StatusCode == HttpStatusCode.Forbidden, "membership-added-confers-no-permission");
    }

    private static async Task PresentationAsync(HttpClient owner, HttpClient member, string prefix, CancellationToken token)
    {
        var before = await member.GetFromJsonAsync<JsonElement>(prefix + "/configuration", token);
        Check.That(before.GetProperty("settings").GetProperty(WorkspaceDefinition.Id + ":title").GetString() == "Team workspace", "bootstrap-default");
        using (var refused = await SendAsync(member, HttpMethod.Put, prefix + "/title", new WorkspaceEndpoints.TitleChange("Member title", null), token))
            Check.That(refused.StatusCode == HttpStatusCode.Forbidden, "setting-management-permission");
        await JsonAsync<JsonElement>(owner, HttpMethod.Put, prefix + "/title", new WorkspaceEndpoints.TitleChange("Shared handbook", null), token);
        var after = await member.GetFromJsonAsync<JsonElement>(prefix + "/configuration", token);
        Check.That(after.GetProperty("settings").GetProperty(WorkspaceDefinition.Id + ":title").GetString() == "Shared handbook", "setting-cache-invalidation");
        using (var stale = await SendAsync(owner, HttpMethod.Put, prefix + "/title", new WorkspaceEndpoints.TitleChange("Stale title", Guid.NewGuid()), token))
            Check.That(stale.StatusCode == HttpStatusCode.Conflict, "setting-version-conflict");
        var disabled = await JsonAsync<JsonElement>(owner, HttpMethod.Put, prefix + "/digest-feature", new WorkspaceEndpoints.FeatureChange(false, null), token);
        using (var refused = await SendAsync(member, HttpMethod.Post, prefix + "/digests", new SubscribeDigest("America/Chicago", "09:00"), token))
            Check.That(refused.StatusCode == HttpStatusCode.Forbidden, "feature-gate");
        await JsonAsync<JsonElement>(owner, HttpMethod.Put, prefix + "/digest-feature",
            new WorkspaceEndpoints.FeatureChange(true, disabled.GetProperty("version").GetGuid()), token);
        var localized = await member.GetFromJsonAsync<JsonElement>(prefix + "/localization?culture=es-MX", token);
        var saved = localized.GetProperty("strings").GetProperty("Saved");
        Check.That(saved.GetProperty("value").GetString() == "Nota guardada" && saved.GetProperty("sourceCulture").GetString() == "es", "localization-fallback");
    }

    private static async Task FormAsync(HttpClient client, Guid tenant, Guid project, CancellationToken token)
    {
        var route = "/t/" + tenant.ToString("D") + "/notes/new";
        using (var denied = await client.PostAsync(route, new FormUrlEncodedContent(new Dictionary<string, string> { ["text"] = "Cross-site form" }), token))
            Check.That(denied.StatusCode == HttpStatusCode.BadRequest, "form-without-antiforgery");
        var page = await client.GetStringAsync(route, token);
        var match = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        Check.That(match.Success, "form-token");
        using var response = await client.PostAsync(route, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value), ["project"] = project.ToString("D"),
            ["operation"] = Guid.NewGuid().ToString("D"), ["text"] = "A browser form note"
        }), token);
        Success(response, "authorized-form");
        Check.That(response.Content.Headers.ContentType?.MediaType == "text/html", "form-html");
    }

    private static async Task<FileVersionInfo> UploadAsync(HttpClient client, string prefix, Guid note, byte[] content, string name, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, prefix + "/attachments/" + note.ToString("D")) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Add("X-File-Name", name);
        request.Headers.Add("X-Content-SHA256", Convert.ToHexStringLower(SHA256.HashData(content)));
        request.Headers.Add("RequestVerificationToken", await AntiforgeryAsync(client, token));
        using var response = await client.SendAsync(request, token);
        Success(response, "attachment-upload");
        return (await response.Content.ReadFromJsonAsync<FileVersionInfo>(token))!;
    }

    private static Task<HttpResponseMessage> DownloadAsync(HttpClient client, string prefix, string grant, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, prefix + "/download");
        request.Headers.Add(FileSteps.GrantHeader, grant);
        return SendDownloadAsync();
        async Task<HttpResponseMessage> SendDownloadAsync()
        {
            using (request) return await client.SendAsync(request, token);
        }
    }

    private static async Task DeadlineAsync(SmokeHost host, SmokeUser member, Guid tenant, string grant, CancellationToken token)
    {
        await using var scope = await host.EnterAsync(member, tenant, token);
        var response = new BlockedResponse();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = token };
        context.Request.Headers[FileSteps.GrantHeader] = grant;
        context.Response.Body = response;
        await host.Services.GetRequiredService<FileSteps>().DownloadAsync(context).WaitAsync(TimeSpan.FromSeconds(20), token);
        Check.That(response.Cancelled, "stalled-download-deadline");
    }

    private static async Task<string> AntiforgeryAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync("/identity/antiforgery", token);
        Success(response, "antiforgery-issue");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object body, CancellationToken token, Guid? operation = null)
    {
        using var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", await AntiforgeryAsync(client, token));
        request.Headers.Add(FrameworkApi.OperationHeader, (operation ?? Guid.NewGuid()).ToString("D"));
        return await client.SendAsync(request, token);
    }

    private static async Task<T> JsonAsync<T>(HttpClient client, HttpMethod method, string route, object body, CancellationToken token, Guid? operation = null)
    {
        using var response = await SendAsync(client, method, route, body, token, operation);
        Success(response, "json-operation");
        return (await response.Content.ReadFromJsonAsync<T>(token))!;
    }
    private static void Success(HttpResponseMessage response, string check) => Check.That(response.IsSuccessStatusCode, check);
    private sealed class GrantResponse
    {
        public Guid GrantId { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string Token { get; set; } = "";
        public override string ToString() => nameof(GrantResponse);
    }

    private sealed class BlockedResponse : Stream
    {
        internal bool Cancelled { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
