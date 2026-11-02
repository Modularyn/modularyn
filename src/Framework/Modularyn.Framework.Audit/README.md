# Audit and diagnostics

`Modularyn.Framework.Audit` keeps the record of every privileged change and access
the G-02 threat model lists (TB-G02-12), separates reading that record and reading
detailed diagnostics from every other authority, and holds the rule for what
durable payloads may carry. It is a PostgreSQL module through the persistence
adapter, with its own schema, context and migration.

## Registration

`AuditModule.Create(options, schema)` returns the module. Add it to the host's
one `PostgreSqlModuleRegistry` and `AuditModule.Manifest(version)` to the
selection, so the coordinator applies its migration and the activation gate checks
it, and add `AuditModule.Permissions` and `AuditModule.HostOperations` to the
authorization registry. `services.AddModularynAudit(module)` registers the store
as the general `IAuditTrail`, as authorization's `IAuthorityAudit` and as
tenancy's `IHostScopeAudit` and `IFilterBypassAudit`, and registers the
`AuditReader`. While audit is absent, every change and access that needs a record
is refused, each with its owner's stable code.

## Records

A record holds its identity, time, action, outcome, tenant, actor, effective
subject, resource type and identity, detail, unit, host operation, reason and
correlation. Every value is a reference or a code: an identifier, a permission
name, a state or refusal code. The store refuses (`MAU001`) an entry whose values
break their forms or hold any secret form that
[`SecretForms`](../Modularyn.Framework.Observability/SecretForms.cs) recognizes,
so no record holds a secret, credential, token, session identifier or payload
value, and the change that entry describes is refused with it.

Times come from `AuditClock`, the one place audit reads time: the host's
`TimeProvider` floored to whole microseconds, the precision `timestamptz` keeps,
so a record reads back exactly as it was written. An authority change's record
carries the change's own identity and time, which must already be whole
microseconds. Host-scope grants take their time from tenancy at the same
precision.

## In the change's own transaction

`IAuditTrail.RecordAsync(work, entry)` appends the record through the unit of work
that makes the change, with the persistence adapter's
`EfCoreUnitOfWork.AppendAsync` (TM-G02-051). The record therefore commits exactly
when the change commits; a change that rolls back, is abandoned or fails at
commit takes its record with it. A record that cannot be formed rolls the unit
back before the refusal reaches the caller, and one that cannot be written leaves
the unit unable to commit, even when the caller catches the failure. A suppressed
unit has no transaction to append in (`MPS045`), and the journal must be a module
of the unit's own database (`MPS046`).

`RecordAccessAsync(entry)` records an access that precedes what it allows and
changes nothing, in its own committed transaction before the access exists:
acting under a delegation and reading audit. A host-scope grant and a filter
bypass are recorded the same way, before the scope or the query exists. A failure
refuses the access.

These owners record their changes through it:

| Change or access | Actions | Refused without its record |
|---|---|---|
| Permission grants, roles, assignments, resource shares, tenant owners, host authority, memberships | `authority.*` | `MAZ024` (authorization) |
| Host-scope grants | `tenancy.host-scope-granted` | `MTN011` (tenancy) |
| Cross-tenant and soft-deleted reads | `persistence.cross-tenant-read`, `persistence.soft-deleted-read` | `MPS047` (persistence) |
| Tenant creation, domains and state | `tenancy.tenant-created`, `tenancy.domain-added`, `tenancy.tenant-state-changed` | `MTN023` (tenancy) |
| Accounts, passwords, sessions, delegations and their use, credential forms | `identity.*` | `MID004` (identity); a delegation whose use cannot be recorded does not resolve |
| Setting overrides | `settings.value-set`, `settings.value-reset` | `MAZ024` through `ValueAccessDeniedException` |
| File grants | `files.grant-issued`, `files.grant-revoked` | `FileStorageError.AuditUnavailable` |
| Operator replays, dispatch replays, job cancellations, schedule stops | `events.replayed`, `events.dispatch-replayed`, `jobs.cancelled`, `jobs.schedule-stopped` | the store refuses before any change |
| Audit reads | `audit.read`, `audit.host-read` | `AuditAccessException` |

## Append-only

`AuditRecordRow` is an `IAppendOnly` entity. The persistence adapter inserts such
an entity only through `AppendAsync`, and refuses every other insert, every
update and every delete of one, through tracked changes, `ModuleSql` and bulk
writes alike (`MPS044`). No API changes or deletes a record, and G-02 has no
retention or purge for them: records are kept. This separates writing, which only
the framework does, from reading and from administration, which has nothing to
administer (TM-G02-052). Ordinary database rows carry no tamper evidence, and none
is claimed: a database administrator, or the schema's owner role, can still
change rows directly. Refusing that in the database itself needs a trigger or
separate roles, which the adapter's closed migration vocabulary does not express.

## Reading audit

`AuditReader.ReadAsync(context, principal, query)` reads at most 500 records, oldest
first, optionally by time window and action. A tenant's records need
`modularyn.audit.read` at the current profile through the evaluator, in that
tenant's context; no administration permission implies it, and the tenant's owner
holds it as the owner holds every permission. Every tenant's records, and the
host's own, need an active host scope granted for `modularyn.audit.host-read`,
which reaches no entity in host scope (`MAU002` otherwise). Each read is recorded
before anything is read, and a read that cannot be recorded returns nothing.

## Diagnostics

`DiagnosticsAccess` is the authority that reads detailed diagnostics, apart from
audit reading and from every administration permission (TM-G02-054): a tenant's
operational diagnostics need `modularyn.diagnostics.read` at the current profile
through the evaluator, and the host's own need an active host scope granted for
`modularyn.diagnostics.host-read`, whose grant is itself recorded. Its readers are
the durable stores' quarantine and job history views
([events](../Modularyn.Framework.Events/README.md#diagnostics) and
[jobs](../Modularyn.Framework.Jobs/README.md#diagnostics)) and the module host's
report ([hosting](../README.md#host-diagnostics)). Anonymous liveness and readiness
probes answer with their status text only.

## Durable payloads

`DurablePayloads` is the rule for what an outbox event, inbox receipt, job or
schedule payload may carry (TM-G02-039): references, such as identifiers, and the
operational values its handler needs; never a secret, credential, token, session
identifier or propagated context; and no personal data that the handler can read
again by reference. Payloads are classified as operational references, and the
framework has no classification for secrets or personal data, so the stores refuse
at append a payload that is empty or longer than 65,536 characters, holds a
control character other than a tab or line break, holds a recognized secret form,
or holds a propagated tenant context, and an ordering key that holds a secret
form. A recognized form is refused wherever it appears; one embedded in a longer
run of token characters, such as a token joined to an identifier by a hyphen, is
not recognized, so the rule backs up, and never replaces, the producer's own
choice of references. The stores redact a payload once its row can no longer run;
`PayloadSummary` is all that a diagnostics view shows of one.

## Codes

| Code | Meaning |
|---|---|
| `MAU001` | An entry breaks the record's forms, holds a secret form, or has no transactional unit to commit with |
| `MAU002` | A host-level audit or diagnostics read needs an active host scope granted for its own read operation |

## Threat-model controls

The tests are in `Audit.Tests` unless named otherwise, and run against PostgreSQL
with the real tenancy, identity, authorization and audit store, through the
repository's real-provider script, except where a test is named as a unit or
host test. Each control's removal check, breaking the control and confirming that
its test fails, is recorded with the test.

- **TM-G02-051, implemented.** Every privileged change in the table above commits
  with exactly one record in its own transaction, or is refused and changes
  nothing; a retried authority change leaves one record; each delegation use and
  audit read is recorded before it acts; record and host-grant times are whole
  microseconds from the host clock. Tests:
  `AuditStoreTests.TM_G02_051_A_change_and_its_audit_record_commit_together_or_not_at_all`,
  `TM_G02_051_Audit_and_host_grant_times_are_whole_microseconds_from_the_host_clock`,
  `AuthorityAuditTests.TM_G02_051_*`, `AdministrationAuditTests.TM_G02_051_*`,
  `OperatorAuditTests.TM_G02_037_*`, `Settings.Tests`
  `ManagementTests.TM_G02_051_Each_override_change_commits_with_its_record_or_not_at_all`
  and `Files.Application.Tests`
  `DownloadGrantTests.TM_G02_051_Each_grant_issue_and_revocation_commits_with_its_record_or_not_at_all`.
  Removal checks: skipping the append, or leaving a unit able to commit after a
  failed append, fails the first; dropping the authority, directory, identity,
  delegation-use, settings, file-grant, replay or cancellation record fails its
  test; reading the clock without the microsecond floor, in audit or in tenancy,
  fails the times test. Remaining: recovery evidence for AC-012 on host and
  container (CODE-10 on CI-01).
- **TM-G02-052, implemented.** Records are append-only on every adapter write path;
  audit is read only under its own permission or host operation, separate from
  administration, and every read is recorded; no tamper evidence is claimed.
  Tests: `AuditStoreTests.TM_G02_052_Audit_records_are_append_only_on_every_write_path`
  and `AuditReadTests.TM_G02_052_Audit_reads_need_their_own_permission_and_are_themselves_audited`.
  Removal checks: lifting the append-only refusal fails the first; skipping the
  permission or the read's own record fails the second. Remaining: refusal in the
  database itself, which needs a migration operation outside the adapter's closed
  vocabulary (CODE-03's ownership contract) or a separate database role
  (operational, G-08); AC-012's acceptance tests, which the G-02 evidence plan
  does not schedule (`RR-G02-029` and `RR-G02-038` in
  [the control map](../../../docs/security/control-map/README.md)).
- **TM-G02-053, implemented for audit, payloads and diagnostics.** No record holds a
  secret form, and the payload rule and diagnostics views keep payloads out of
  history and dead letters. Tests:
  `AuditStoreTests.TM_G02_053_An_audit_record_never_holds_a_secret_form` and
  `PayloadRuleTests` and `DurablePayloadTests` (`TM_G02_039_*`). Removal check:
  lifting the secret-form refusal fails the first. Log and trace redaction and
  server-issued correlation are recorded with
  [the framework's controls](../README.md#threat-model-controls).
- **TM-G02-054, implemented.** Durable diagnostics read only under
  `modularyn.diagnostics.read`, which neither audit reading nor the operator
  permissions imply, and host diagnostics only in a scope granted for
  `modularyn.diagnostics.host-read`; views show payload summaries only. Tests:
  `DurablePayloadTests.TM_G02_054_Durable_diagnostics_need_the_diagnostics_permission_alone`
  and `Hosting.Tests` `HostDiagnosticsTests.TM_G02_054_*` (host test). Removal
  checks: skipping either check fails its test. Remaining: an operator endpoint for
  either view, which no G-02 work order declares (CODE-06's inventory); host and
  container evidence for AC-025 and AC-031 (CODE-10 on CI-01).
