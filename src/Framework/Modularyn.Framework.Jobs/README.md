# Durable jobs

Include `JobModel.Configure` and `JobModel.CreateTables` in an owning module's
context and migration. `JobStore.Enqueue` writes in that module's active tenant
transaction. The store captures `PropagatedTenantContext` from the validated
producing context, binds it to the job (`JobBinding`) or schedule
(`ScheduleBinding`) and seals it with the declared key ring; callers supply only
kind, payload and due time. It also owns capture for schedules and materialized
jobs, capturing anew in the revalidated schedule unit instead of copying the
schedule's stored reference. A run or materialization opens the reference only
under its seal and that row's binding.

`JobWorker.RunDueAsync` scans under an audited host operation reaching `Jobs`
and `JobAttempts`. `ScheduleWorker.MaterializeAsync` additionally needs reach to
`JobSchedules`. Every item enters `AuthorizedWorkRunner` before a claim or handler.
The producer's membership and current authority are checked afresh, and every
checkpoint passes the running principal explicitly to the evaluator. A stored
payload never supplies a principal, permission or tenant. Definitions in trusted
server code bind job kinds to composed permissions.

Service-originated tenant work uses `IServiceIdentityScopes.Begin` and
`TenantExecution.EstablishServiceAsync`. The latter requires a current declared
service identity and active tenant; it grants no use-case permission. The tenant
comes from server state the caller already holds, such as its own configuration
or records, never from a request, a payload or a stored row. Server code
still authorizes the producing operation before opening its unit. Service
identities are never made tenant members. Propagated service work rechecks its
identity registration and must run as that same service identity, within its
current declared scope. User-produced work runs against the user's current
authority within the running service's ceiling. Service host dispatch uses
`EstablishServiceHostAsync`, which checks freshness, separate host authority and
the host audit seam. No request adapter exposes these production routes.

Claims, lease renewal, attempts and effects use tracked, versioned writes. A
claim commits a fresh lease token and a numbered attempt before the handler starts.
Expired ownership can be replaced after restart. `JobExecution.CheckpointAsync`
checks the lease, durable cancellation and current permission; renewal cannot
revive an expired lease. The final tracked job version fences completion against
replacement and cancellation. Completion, attempt history and all effects in
`JobExecution.Work` commit together. A stale worker cannot commit over a replacement
lease. Unknown completion acknowledgement is reconciled against the original
job identity; committed completion is terminal.

Handlers must keep transactional effects in `Work`, must not complete that unit
themselves, and must call checkpoints between long steps. Independent transactions
and external actions need an idempotent protocol of their own. There is no atomic
commit across module stores. Lease renewal is explicit so the host can choose its
cadence and stop it with cooperative shutdown.

The default policy allows three claimed attempts, a one-minute lease, and
exponential retry delays of one, two and four seconds. Failed attempts retain a
cause code and timestamps. Cancellation before claim is `CancelledBeforeRun`
and creates no attempt; cancellation during ownership is `CancelledDuringRun`
with a cancelled attempt and no committed transactional effect. Shutdown leaves
the lease recoverable. Permanent authorization refusal is `Denied`; transient
refusals have a separate bounded deferral count, leaving handler attempts distinct.
A stored reference that does not open under its seal and its row's binding, such
as one copied from another row, is denied at once with `MTN014`, before any claim;
the worker's own tenant check stays behind the seal: a validly sealed reference
that names another tenant than its row fails before any claim, and the
materializer's before any read. Any such failure outside a handler's attempt takes
the same bounded deferrals, with the code `DispatchFailed` for a job and
`ScheduleFailed` for a schedule, and never ends the pass for other jobs. A refusal on expired ownership closes the previous attempt in the same host
disposition transaction. Exhaustion is visible as `Failed`. Schedules also bound
authorization deferrals; exhaustion disables the schedule while retaining its
cursor, deferral count and last refusal code for inspection, and redacts its
payload. A failure in one
schedule's own evaluation, such as a stored zone that the host's time-zone database
no longer resolves, rolls back that schedule's transaction and takes the same
bounded deferrals with the code `ScheduleFailed`, so it never ends the pass for
other schedules.

Schedules are daily civil times in IANA zones evaluated through `TimeZoneInfo`.
A zone is accepted only in the form of an IANA zone or link name, such as
`America/Chicago`, `Etc/UTC` or `US/Eastern`, and only when the host's time-zone
database matches it; a path that the database merely resolves, such as a doubled
separator, `posixrules`, `localtime` or the `posix` and `right` copies of the
database, is refused. Zone names are matched by the host's time-zone database, so
on a case-insensitive file system, such as a default macOS volume, a letter-case
variant such as `america/chicago` resolves to the same zone and is accepted.
Linux, the qualified host, is case-sensitive and refuses that variant, so a
schedule declared with one stops resolving there and defers as described above;
declare names in the database's own letter case. On a host running in
globalization's invariant mode, IANA lookups need the operating system's
time-zone database, as on Linux, and a Windows host in invariant mode cannot
declare a schedule. A nonexistent local time is visibly skipped; an ambiguous
time selects the first UTC occurrence. No machine-local time participates. Every instant is normalized
through `UtcMicroseconds` before storage or comparison; clocks are `TimeProvider`.
The due occurrence, job and cursor share a transaction, with tenant-qualified
unique keys for schedule/date and schedule/due time. Restart or competing
materialization cannot create a second job for an occurrence.

An occurrence more than one minute late is a misfire. `Skip` records missed
occurrences only; `CatchUpAll` queues every missed occurrence; `CatchUpOnce`
queues the latest missed occurrence unless a timely occurrence is already queued
in that pass. Each skipped, missed or queued date remains visible. The scan stores
a next-check instant so future schedules leave room for due work. A backlog of
more than 3,660 inclusive dates disables that schedule with `BacklogLimit`, keeping
its cursor for inspection, but not its payload, instead of silently dropping history. An operator can
stop it and create a replacement schedule with an explicit first date. Stopping a
schedule prevents future materialization; existing jobs have separate cancellation.

## Operator actions

`CancelAsync` and `StopScheduleAsync` need the current `jobs.cancel` and
`jobs.schedule` permissions through the evaluator. Each is recorded in audit, with
who acted, when, in which unit, on which job or schedule and the state, attempts
or next date it ended, in the transaction of the change itself (TM-G02-037,
TM-G02-051); a store built without an audit trail, or whose record cannot be
written, changes nothing.

## Payloads and retention

A job or schedule payload follows the durable-payload rule
([audit](../Modularyn.Framework.Audit/README.md#durable-payloads)): 1 to 65,536
characters of references, never a secret form or a propagated context, refused at
`Enqueue` and `Schedule`. A job without arguments still carries a payload, such as
`{}`, so a redacted one is told apart. A job that can no longer run keeps no
payload: completion, failure, cancellation and denial redact it in the transaction
that ends the job, and stopping, ending or disabling a schedule redacts the
schedule's. Queued and retrying jobs keep theirs until they end.

## Diagnostics

`HistoryAsync(context, principal, evaluator)` lists the unit's tenant's jobs, newest
first, under `modularyn.diagnostics.read` at the current profile (TM-G02-054): each
job's identity, kind, state, attempts, deferrals, code, times and schedule, and of
its payload only whether it is still held and its length.

## Growth

Job history has no retention yet, and nothing removes it. Jobs grow by one row
per enqueue and per queued or caught-up occurrence, job attempts by one row per
claim, and schedule occurrences by one row per schedule for every civil date it
evaluates, skipped and missed dates included, so a daily schedule adds about 365
rows a year. The job worker's and the materializer's cross-tenant scans read every
tenant's rows, and every index leads with the tenant, so the cost of each pass
grows with that history across all tenants. The scans are not fair between
tenants: each pass takes up to its limit in one order across all tenants, jobs by
the time they become available and schedules by their next check, so a tenant
with many due jobs or schedules can fill a pass and delay other tenants' work
until it drains.

The PostgreSQL tests cover once-per-due-time execution, leases and restart,
renewal, failure, both cancellation outcomes, schedule transitions, misfires and
current authorization. They require Builder execution before provider qualification.
