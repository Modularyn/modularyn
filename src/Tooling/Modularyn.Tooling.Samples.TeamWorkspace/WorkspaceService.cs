using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Api;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Events;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;

namespace Modularyn.Tooling.Samples.TeamWorkspace;

public sealed record CreateProject(string Name);
public sealed record ProjectView(Guid Id, string Name, Guid Version);
public sealed record CreateNote(Guid ProjectId, string Text);
public sealed record ReviseNote(Guid NoteId, Guid Version, string Text);
public sealed record NoteView(Guid Id, Guid ProjectId, string Text, int Revision, Guid Version);
public sealed record SubscribeDigest(string TimeZone, string LocalTime);
public sealed record IdentityView(Guid Id);
public sealed record RevisionReference(Guid NoteId, int Number);
public sealed record AttachmentReference(Guid FileId, Guid VersionId);

/// <summary>Tenant-wide collaboration: all members with read/write permission see/change this tenant's projects.</summary>
public sealed class WorkspaceService(PostgreSqlModule module, EventStore events, JobStore jobs,
    IPermissionEvaluator permissions, IAuditTrail audit, TimeProvider clock)
{
    public PostgreSqlModule Module { get; } = module;
    public EventStore Events { get; } = events;
    public JobStore Jobs { get; } = jobs;
    public DateTimeOffset Now => UtcMicroseconds.Now(clock);

    public async Task RequireAsync(TenantContext tenant, ExecutionPrincipal principal, string permission, CancellationToken token)
    {
        var decision = await permissions.AuthorizeAsync(new(tenant, principal, permission) { RequireCurrent = true }, token);
        if (!decision.Permitted) throw new ApiException(decision.Code!, decision.IsTransient ? 503 : 403);
    }

    public async Task<ProjectView> CreateProjectAsync(TenantContext tenant, ExecutionPrincipal principal, Guid operation,
        int apiVersion, CreateProject request, CancellationToken token)
    {
        await RequireAsync(tenant, principal, WorkspaceDefinition.Write, token);
        Text(request.Name, 120);
        return await ApiOperations.ExecuteAsync(Module, tenant, principal, operation, "workspace.create-project", apiVersion, request, async (work, ct) =>
        {
            var project = new WorkspaceProject { Id = Guid.NewGuid(), Name = request.Name };
            work.Context.Add(project);
            await RecordAsync(work, tenant, principal, "workspace.project-created", "project", project.Id, ct);
            await work.FlushAsync(ct);
            return new ProjectView(project.Id, project.Name, project.Version);
        }, token);
    }

    public async Task<NoteView> CreateNoteAsync(TenantContext tenant, ExecutionPrincipal principal, Guid operation,
        int apiVersion, CreateNote request, CancellationToken token)
    {
        await RequireAsync(tenant, principal, WorkspaceDefinition.Write, token);
        Text(request.Text, 16000);
        return await ApiOperations.ExecuteAsync(Module, tenant, principal, operation, "workspace.create-note", apiVersion, request, async (work, ct) =>
        {
            if (!await work.Context.Set<WorkspaceProject>().AnyAsync(row => row.Id == request.ProjectId, ct)) Missing();
            var note = new WorkspaceNote { Id = Guid.NewGuid(), ProjectId = request.ProjectId, Text = request.Text, Revision = 1, UpdatedAt = Now };
            work.Context.Add(note);
            AppendRevision(work, note, principal);
            await RecordAsync(work, tenant, principal, "workspace.note-created", "note", note.Id, ct);
            await work.FlushAsync(ct);
            return View(note);
        }, token);
    }

    public async Task<NoteView> ReviseAsync(TenantContext tenant, ExecutionPrincipal principal, Guid operation,
        int apiVersion, ReviseNote request, CancellationToken token)
    {
        await RequireAsync(tenant, principal, WorkspaceDefinition.Write, token);
        Text(request.Text, 16000);
        return await ApiOperations.ExecuteAsync(Module, tenant, principal, operation, "workspace.revise-note", apiVersion, request, async (work, ct) =>
        {
            var note = await work.Context.Set<WorkspaceNote>().SingleOrDefaultAsync(row => row.Id == request.NoteId, ct)
                ?? throw new ApiException(ApiCodes.NotFound, 404);
            if (request.Version == Guid.Empty || request.Version != note.Version) throw new ApiException(ApiCodes.Conflict, 409);
            note.Text = request.Text;
            note.Revision = checked(note.Revision + 1);
            note.UpdatedAt = Now;
            AppendRevision(work, note, principal);
            await RecordAsync(work, tenant, principal, "workspace.note-revised", "note", note.Id, ct);
            await work.FlushAsync(ct);
            return View(note);
        }, token);
    }

    private void AppendRevision(EfCoreUnitOfWork work, WorkspaceNote note, ExecutionPrincipal principal)
    {
        work.Context.Add(new NoteRevision { NoteId = note.Id, Number = note.Revision, Text = note.Text,
            Author = principal.EffectiveSubject.Id, CreatedAt = note.UpdatedAt });
        Events.Append(work, new(WorkspaceDefinition.ActivityConsumer, WorkspaceDefinition.NoteChanged, 1,
            note.Id.ToString("D"), note.Revision, JsonSerializer.Serialize(new RevisionReference(note.Id, note.Revision))));
    }

    public async Task<IReadOnlyList<ProjectView>> ProjectsAsync(TenantContext tenant, ExecutionPrincipal principal, CancellationToken token)
    {
        await RequireAsync(tenant, principal, WorkspaceDefinition.Read, token);
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, tenant, cancellationToken: token);
        return await work.Context.Set<WorkspaceProject>().AsNoTracking().OrderBy(row => row.Id).Take(100)
            .Select(row => new ProjectView(row.Id, row.Name, row.Version)).ToListAsync(token);
    }

    public async Task<IReadOnlyList<NoteRevision>> RevisionsAsync(TenantContext tenant, ExecutionPrincipal principal, Guid noteId, CancellationToken token)
    {
        await RequireAsync(tenant, principal, WorkspaceDefinition.Read, token);
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, tenant, cancellationToken: token);
        return await work.Context.Set<NoteRevision>().AsNoTracking().Where(row => row.NoteId == noteId)
            .OrderByDescending(row => row.Number).Take(100).ToListAsync(token);
    }

    public async Task<IReadOnlyList<ActivityItem>> ActivityAsync(TenantContext tenant, ExecutionPrincipal principal, CancellationToken token)
    {
        await RequireAsync(tenant, principal, WorkspaceDefinition.Read, token);
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, tenant, cancellationToken: token);
        return await work.Context.Set<ActivityItem>().AsNoTracking().OrderByDescending(row => row.RecordedAt).ThenBy(row => row.EventId).Take(100).ToListAsync(token);
    }

    public async Task<IdentityView> SubscribeAsync(TenantContext tenant, ExecutionPrincipal principal, Guid operation,
        int apiVersion, SubscribeDigest request, CancellationToken token)
    {
        await RequireAsync(tenant, principal, WorkspaceDefinition.Digest, token);
        await RequireAsync(tenant, principal, ScheduleWorker.SchedulePermission, token);
        if (principal.Actor.Kind != IdentityKind.User || principal.Delegation is not null) throw new ApiException(ApiCodes.Refused, 403);
        if (!TimeOnly.TryParseExact(request.LocalTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var localTime)) throw new ApiException(ApiCodes.Validation, 400);
        DailySchedule schedule;
        try { schedule = new DailySchedule(request.TimeZone, localTime, MisfirePolicy.CatchUpOnce); }
        catch (ArgumentException) { throw new ApiException(ApiCodes.Validation, 400); }
        return await ApiOperations.ExecuteAsync(Module, tenant, principal, operation, "workspace.subscribe-digest", apiVersion, request, async (work, ct) =>
        {
            var member = principal.Actor.Id;
            var existing = await work.Context.Set<DigestSubscription>().SingleOrDefaultAsync(row => row.Member == member, ct);
            if (existing is not null) throw new ApiException(ApiCodes.Conflict, 409);
            var id = Jobs.Schedule(work, WorkspaceDefinition.DigestKind, "{}", schedule, schedule.LocalDate(Now));
            work.Context.Add(new DigestSubscription { Member = member, ScheduleId = id, TimeZone = schedule.TimeZone });
            await RecordAsync(work, tenant, principal, "workspace.digest-subscribed", "schedule", id, ct);
            await work.FlushAsync(ct);
            return new IdentityView(id);
        }, token);
    }

    public async Task<IReadOnlyList<MemberDigest>> DigestsAsync(TenantContext tenant, ExecutionPrincipal principal, CancellationToken token)
    {
        await RequireAsync(tenant, principal, WorkspaceDefinition.Digest, token);
        var member = principal.EffectiveSubject.Id;
        await using var work = await EfCoreUnitOfWork.BeginAsync(Module, tenant, cancellationToken: token);
        return await work.Context.Set<MemberDigest>().AsNoTracking().Where(row => row.Member == member)
            .OrderByDescending(row => row.CreatedAt).Take(30).ToListAsync(token);
    }

    public async Task<IdentityView> QueueReleaseAsync(TenantContext tenant, ExecutionPrincipal principal, Guid operation,
        int apiVersion, AttachmentReference request, CancellationToken token)
    {
        await RequireAsync(tenant, principal, FilePermissions.Release, token);
        if (request.FileId == Guid.Empty || request.VersionId == Guid.Empty) throw new ApiException(ApiCodes.Validation, 400);
        return await ApiOperations.ExecuteAsync(Module, tenant, principal, operation, "workspace.release-attachment", apiVersion, request, async (work, ct) =>
        {
            var id = Jobs.Enqueue(work, new(WorkspaceDefinition.ReleaseKind, JsonSerializer.Serialize(request), Now));
            await RecordAsync(work, tenant, principal, "workspace.release-requested", "file", request.FileId, ct);
            return new IdentityView(id);
        }, token);
    }

    private ValueTask RecordAsync(EfCoreUnitOfWork work, TenantContext tenant, ExecutionPrincipal principal,
        string action, string resource, Guid id, CancellationToken token) => audit.RecordAsync(work,
            AuditEntry.InContext(action, tenant, principal) with { ResourceType = resource, ResourceId = id.ToString("D") }, token);

    private static NoteView View(WorkspaceNote note) => new(note.Id, note.ProjectId, note.Text, note.Revision, note.Version);
    private static void Missing() => throw new ApiException(ApiCodes.NotFound, 404);
    private static void Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ApiException(ApiCodes.Validation, 400);
    }
}
