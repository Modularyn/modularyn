using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Jobs;

public sealed class ScheduleWorker(JobStore store, AuthorizedWorkRunner runner)
{
    public const string SchedulePermission = "jobs.schedule";
    public static readonly TimeSpan MisfireGrace = TimeSpan.FromMinutes(1);
    public const int MaximumDatesPerSchedule = 3660;

    public async Task<int> MaterializeAsync(TenantContext host, ExecutionPrincipal principal, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var scanAt = store.Now;
        await using var scan = await EfCoreUnitOfWork.BeginAsync(store.Module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var candidates = await (await scan.Context.AcrossTenantsAsync<JobSchedule>(cancellationToken).ConfigureAwait(false)).Query.AsNoTracking()
            .Where(row => row.Enabled && row.NextCheckAt <= scanAt)
            .OrderBy(row => row.NextCheckAt).ThenBy(row => row.Id).Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        var added = 0;
        foreach (var candidate in candidates)
        {
            TenantWorkOutcome? refused = null;
            TenantWorkOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(principal, candidate.Propagated, store.ScheduleBinding(candidate.Id), SchedulePermission, async (unit, _, token) =>
                {
                    if (unit.Context.TenantId != candidate.TenantId) throw new InvalidOperationException("The schedule is outside its producer's tenant.");
                    await using var work = await EfCoreUnitOfWork.BeginAsync(store.Module, unit.Context, cancellationToken: token).ConfigureAwait(false);
                    // The unit's tenant filter finds the schedule only in its own tenant, which the check above already holds.
                    var row = await work.Context.Set<JobSchedule>().SingleOrDefaultAsync(row => row.Id == candidate.Id, token).ConfigureAwait(false);
                    var now = store.Now;
                    if (row is null || !row.Enabled || row.NextCheckAt > now) return;
                    var schedule = new DailySchedule(row.TimeZone, row.LocalTime, row.Misfire);
                    var today = schedule.LocalDate(now);
                    var occurrences = new List<ScheduleOccurrence>();
                    if (today.DayNumber - row.NextDate.DayNumber >= MaximumDatesPerSchedule)
                    {
                        // Preserve the cursor and history for operator inspection instead of blocking other schedules.
                        row.Enabled = false;
                        row.Code = "BacklogLimit";
                        row.Payload = DurablePayloads.Redacted;
                    }
                    else
                    {
                        for (var date = row.NextDate; date <= today; date = date.AddDays(1))
                        {
                            var due = schedule.Resolve(date);
                            if (due > now) break;
                            occurrences.Add(new ScheduleOccurrence
                            {
                                ScheduleId = row.Id, LocalDate = date, DueAt = due,
                                State = due is null ? OccurrenceState.NonexistentLocalTime : due < now - MisfireGrace ? OccurrenceState.Missed : OccurrenceState.Queued
                            });
                            if (date == DateOnly.MaxValue)
                            {
                                row.Enabled = false;
                                break;
                            }
                            row.NextDate = date.AddDays(1);
                        }
                        if (row.Misfire == MisfirePolicy.CatchUpAll)
                            foreach (var missed in occurrences.Where(entry => entry.State == OccurrenceState.Missed)) missed.State = OccurrenceState.CatchUp;
                        else if (row.Misfire == MisfirePolicy.CatchUpOnce && !occurrences.Any(entry => entry.State == OccurrenceState.Queued)
                            && occurrences.LastOrDefault(entry => entry.State == OccurrenceState.Missed) is { } catchUp)
                            catchUp.State = OccurrenceState.CatchUp;
                        row.Code = row.Enabled ? "Evaluated" : "ScheduleEnded";
                        // The occurrences above copied the payload; an ended schedule never runs again and keeps none.
                        if (!row.Enabled) row.Payload = DurablePayloads.Redacted;
                        // A gap is revisited at most once a minute until its civil date arrives, then visibly skipped.
                        // Future schedules leave the due scan, so they cannot starve work in other time zones.
                        row.NextCheckAt = schedule.Resolve(row.NextDate) ?? now + MisfireGrace;
                    }
                    var count = 0;
                    foreach (var occurrence in occurrences)
                    {
                        if (occurrence.State is OccurrenceState.Queued or OccurrenceState.CatchUp)
                        {
                            occurrence.JobId = store.Enqueue(work, new(row.Kind, row.Payload, occurrence.DueAt!.Value), row.Id);
                            count++;
                        }
                        work.Context.Add(occurrence);
                    }
                    var decision = await unit.Services.GetRequiredService<IPermissionEvaluator>().AuthorizeAsync(
                        new(unit.Context, principal, SchedulePermission) { RequireCurrent = true }, token).ConfigureAwait(false);
                    if (!decision.Permitted)
                    {
                        refused = new(decision.IsTransient ? TenantWorkDisposition.Deferred : TenantWorkDisposition.Denied, decision.Code, null);
                        return;
                    }
                    row.Deferrals = 0;
                    if ((await work.CompleteAsync(token).ConfigureAwait(false)).State == UnitOfWorkState.Committed) added += count;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // A concurrent materializer may win the schedule version or the unique occurrence key.
                // The whole losing transaction rolls back, including every job and cursor change.
                continue;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A failure in one schedule's own evaluation, such as a stored zone that this host's time-zone database no
                // longer resolves, rolls back that schedule's transaction and takes the bounded deferral policy below, so it
                // cannot end the pass for every other schedule.
                outcome = new(TenantWorkDisposition.Deferred, "ScheduleFailed", null);
            }
            outcome = refused ?? outcome;
            if (outcome.Disposition == TenantWorkDisposition.Completed) continue;
            await using var disposition = await EfCoreUnitOfWork.BeginAsync(store.Module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
            var changed = await disposition.Sql.UpdateForTenantAsync<JobSchedule, Guid, string>(candidate.TenantId, row => row.Id, candidate.Id,
                candidate.Version, row => row.Code, outcome.Code ?? "Deferred", cancellationToken).ConfigureAwait(false);
            if (!changed.Succeeded) continue;
            var next = await disposition.Sql.UpdateForTenantAsync<JobSchedule, Guid, DateTimeOffset>(candidate.TenantId, row => row.Id, candidate.Id,
                changed.Version!.Value, row => row.NextCheckAt, store.Now + store.Policy.Backoff(candidate.Deferrals + 1), cancellationToken).ConfigureAwait(false);
            if (!next.Succeeded) continue;
            var deferrals = candidate.Deferrals + (outcome.Disposition == TenantWorkDisposition.Deferred ? 1 : 0);
            next = await disposition.Sql.UpdateForTenantAsync<JobSchedule, Guid, int>(candidate.TenantId, row => row.Id, candidate.Id,
                next.Version!.Value, row => row.Deferrals, deferrals, cancellationToken).ConfigureAwait(false);
            if (!next.Succeeded) continue;
            if (outcome.Disposition == TenantWorkDisposition.Denied || deferrals >= store.Policy.MaxAttempts)
            {
                var disabled = await disposition.Sql.UpdateForTenantAsync<JobSchedule, Guid, bool>(candidate.TenantId, row => row.Id, candidate.Id,
                    next.Version!.Value, row => row.Enabled, false, cancellationToken).ConfigureAwait(false);
                if (!disabled.Succeeded) continue;
                // A disabled schedule never runs again, so its payload is redacted with the disposition that disables it.
                disabled = await disposition.Sql.UpdateForTenantAsync<JobSchedule, Guid, string>(candidate.TenantId, row => row.Id, candidate.Id,
                    disabled.Version!.Value, row => row.Payload, DurablePayloads.Redacted, cancellationToken).ConfigureAwait(false);
                if (!disabled.Succeeded) continue;
            }
            await disposition.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        return added;
    }
}
