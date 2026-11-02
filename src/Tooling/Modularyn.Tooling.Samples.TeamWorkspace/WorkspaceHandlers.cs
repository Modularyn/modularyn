using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Events;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Settings;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;

namespace Modularyn.Tooling.Samples.TeamWorkspace;

/// <summary>Only EventPump and JobWorker invoke these handlers, inside AuthorizedWorkRunner.</summary>
public sealed class WorkspaceHandlers(WorkspaceService workspace, IFiles files, SettingsService settings)
{
    public EventConsumer Activity => new(WorkspaceDefinition.ActivityConsumer, WorkspaceDefinition.Write,
        [new(WorkspaceDefinition.NoteChanged, 1, ProjectActivityAsync)], EventOrdering.Contiguous);
    public JobDefinition[] Jobs =>
    [
        new(WorkspaceDefinition.DigestKind, WorkspaceDefinition.Digest, DigestAsync),
        new(WorkspaceDefinition.ReleaseKind, FilePermissions.Release, ReleaseAsync)
    ];

    private static async Task ProjectActivityAsync(EventExecution execution, CancellationToken token)
    {
        var reference = JsonSerializer.Deserialize<RevisionReference>(execution.Event.Payload)
            ?? throw new InvalidOperationException("Invalid revision reference.");
        var revision = await execution.Work.Context.Set<NoteRevision>().SingleAsync(
            row => row.NoteId == reference.NoteId && row.Number == reference.Number, token);
        execution.Work.Context.Add(new ActivityItem { EventId = execution.Event.Id, NoteId = revision.NoteId,
            Revision = revision.Number, RecordedAt = revision.CreatedAt });
    }

    private async Task DigestAsync(JobExecution execution, CancellationToken token)
    {
        await execution.CheckpointAsync(token);
        var tenant = execution.Work.Context.Tenant!;
        if (!(await settings.ResolveAsync(tenant, WorkspaceDefinition.Digests, token)).Get<bool>()) return;
        // The revalidated producing context identifies the recipient; payloads and rows grant no authority.
        var member = tenant.Actor.Subject;
        if (tenant.Actor.Kind != ActorKind.User) throw new InvalidOperationException("A digest requires its member's subscription.");
        var subscription = await execution.Work.Context.Set<DigestSubscription>().SingleAsync(row => row.Member == member, token);
        var schedule = new DailySchedule(subscription.TimeZone, TimeOnly.MinValue);
        var now = workspace.Now;
        var today = schedule.LocalDate(now);
        var start = schedule.Resolve(today) ?? schedule.Resolve(today.AddDays(-1))!.Value;
        var count = await execution.Work.Context.Set<ActivityItem>().CountAsync(row => row.RecordedAt >= start && row.RecordedAt <= now, token);
        execution.Work.Context.Add(new MemberDigest { JobId = execution.Claim.Id, Member = member, ActivityCount = count, CreatedAt = now });
        await execution.CheckpointAsync(token);
    }

    private async Task ReleaseAsync(JobExecution execution, CancellationToken token)
    {
        await execution.CheckpointAsync(token);
        var reference = JsonSerializer.Deserialize<AttachmentReference>(execution.Payload)
            ?? throw new InvalidOperationException("Invalid attachment reference.");
        // Files owns its transaction. Release is final/idempotent; recovery repeats the same version, never new bytes.
        await files.ReleaseAsync(execution.Work.Context.Tenant!, execution.Principal, reference.FileId, reference.VersionId, token);
        await execution.CheckpointAsync(token);
    }
}
