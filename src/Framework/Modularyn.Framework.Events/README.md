# Durable events

`EventStore.Append` stages an outbox row in the producer's active tenant
transaction. Include `EventModel.Configure` in the owning module's context and
`EventModel.CreateTables` in its migration. Each consumer includes the same
model in its own schema. Suppressed transactions, ended contexts and units from
another module are refused. Business changes and intent commit together.

`EventPump` is a bounded PostgreSQL transport between those module stores.
Dispatch commits the receiver's inbox before acknowledging the outbox. A crash
between those commits repeats delivery under the same event identity. Consumption
commits the inbox disposition, ordering position and handler effects in one
receiver transaction. Handlers must write through `EventExecution.Work`; effects
in another transaction or an external system require their own idempotent
protocol. No distributed transaction or exactly-once external delivery is claimed.

Host scans and dispositions require an audited operation whose reach names the
producer's `OutboxEvents` and receiver's `InboxEvents`. The persistence adapter
checks that reach on every cross-tenant read and named-tenant disposition.
Every receiving and consuming step enters `AuthorizedWorkRunner`, which
revalidates the stored producer and decides the consumer permission now. The
handler receives the running principal explicitly, and the pump asks the
evaluator again before committing. Additional protected handler steps must ask
again with that principal and `RequireCurrent`.

Only `EventStore` captures durable propagated context: append captures from the
producing unit, and inbox receipt captures afresh from the revalidated receiving
unit. Each reference is bound to its row (`OutboxBinding`, `InboxBinding`) and
sealed with the declared key ring, and dispatch and consumption open it only under
its seal and that row's binding. Payloads and public envelopes contain no
propagated context. Receipt never copies the outbox's reference. The reference
remains attached to its original producer through replay. A stored reference that
does not open, such as one copied from another receipt of any tenant, is denied at
once with `MTN014` and its payload redacted, before any read or effect. Behind the
seal, a validly sealed reference that names another tenant than its row, which only
the key ring's holder could produce, still never acts in either tenant: receipt
refuses it and consumption refuses it before any read, so both count it against the
failure budget and quarantine it.

An event has a stable identity, kind, positive schema version, consumer, ordering
key and positive position. Identity and position uniqueness include the tenant.
The producer owns a key's monotonically increasing positions in its business
transaction. `MonotonicSnapshot` applies only positions higher than the durable
watermark; handlers implement replacement projections. `Contiguous` applies only
the next position, starting at one; a missing or poisoned position holds later
positions on that key while other keys remain eligible. Positions never expire.
A held receipt waits with the retry policy's exponential delays and counts each
finding in its own `GapWaits`, apart from handler attempts. The finding that
reaches the gap-wait budget, ten by default, so nine waits totalling 511 seconds
at the default one-second delay, quarantines it as `Poison` with the code
`OrderingGapExhausted`, visible for inspection. An operator replays it once the
predecessor is resolved. No action skips a position or replays a denied one.

Completed inbox receipts are retained for at least 24 hours. A tenant maintenance
unit may prune them in batches of at most 1,000. Active, denied and poison rows
are retained; a denied row keeps no payload, and a poison row keeps its payload
for replay for `QuarantineRetention`, 30 days from its last due time, after which
`RedactQuarantinedAsync` may remove it in batches of at most 1,000, in a tenant
maintenance unit that nothing runs on its own. A redacted poison row is never
replayed. A late redelivery after receipt pruning still cannot repeat a
projection effect because the watermark survives. Ordering keys must never be
reused for a different stream with reset positions. Outbox history and watermarks
remain until the owning module supplies its lifecycle policy.

Outbox rows have no retention yet: the outbox grows by one row per event and
never shrinks. The inbox keeps every pending, denied and poison receipt, and its
completed receipts until a tenant maintenance unit prunes them, which nothing
runs on its own. Positions grow by one row per tenant, consumer and ordering key.
The dispatcher's and the consumer's cross-tenant scans read every tenant's rows
for their consumer, and every index leads with the tenant, so the cost of each
pass grows with that history across all tenants. The scans are not fair between
tenants: each pass takes up to its limit in one order across all tenants, the
dispatcher's by creation time and the consumer's by due time, so a tenant with a
large backlog can fill a pass and delay other tenants' events until it drains.

The default failure budget is three attempts, with exponential delays starting
at one second. Unsupported kinds or versions and handler failures retain their
payload and cause code; exhaustion quarantines the inbox as `Poison`.
Authorization unavailability defers with the same bounded policy; permanent
refusal records `Denied`. Exhausted dispatch deferrals quarantine the outbox.
A lost concurrency check in receipt or consumption is not a handler failure and
retries the same identity. When a competing transaction decided the row, its
version changed and nothing is recorded. Any other conflict, such as a handler's
own stale write, counts in the row's `Conflicts`, apart from handler attempts,
and waits with the retry policy's delay for that count, so the row leaves the
head of the scan. The conflict that reaches the conflict budget, ten by default,
quarantines the row as `Poison` with the code `ConcurrencyConflictExhausted`.
Handler failures keep their own budget.

`ReplayAsync` and `ReplayDispatchAsync` are application services for quarantined
inbox and outbox rows. Both require the current `events.replay` permission through
the evaluator, reset the bounded retry, gap-wait and conflict budgets and preserve
the producer. Replay
always goes through delivery, inbox deduplication, ordering and current authority;
it never invokes a handler directly. The composing module declares this permission
and the consumer permission in its manifest and authorization registry. There is
no HTTP endpoint.

Each replay is recorded in audit in the replay's own transaction (TM-G02-037,
TM-G02-051): who replayed which row and when, the quarantine code and the retry,
gap-wait and conflict budgets it reset, which the next disposition overwrites,
and the original producer and producing unit its reference names, so a replay is
told apart from new work. A store built without an audit trail, or whose record
cannot be written, replays nothing.

## Payloads and retention

An event payload follows the durable-payload rule
([audit](../Modularyn.Framework.Audit/README.md#durable-payloads)): 1 to 65,536
characters of references, never a secret form or a propagated context, and an
ordering key with no secret form, refused at `Append`. A payload is redacted once
its row can no longer run: a delivered or denied outbox event and a consumed or
denied receipt keep none, in the transaction of that disposition; a receipt that
has run still names the same event, so a late duplicate delivery is still
recognized. Quarantined rows keep theirs for replay until their retention.

## Diagnostics

`QuarantineAsync(context, principal, evaluator)` lists the unit's tenant's
quarantined and denied events, outbox and inbox, newest first, under
`modularyn.diagnostics.read` at the current profile (TM-G02-054): each row's side,
identity, consumer, kind, schema version, ordering key, position, state, budgets,
code and due time, and of its payload only whether it is still held and its
length.

## Tests

`IEventObserver` is a test seam for interruption, not audit. Tests inject interruption before producer commit, before dispatch, after
receipt before dispatch acknowledgement, after flushed consumer writes before
commit, and after consumer commit before acknowledgement. The PostgreSQL test
project also covers duplicates through retention, ordering, quarantine, replay,
concurrent consumers and revoked producer authority. Test source is a verification
handoff, not an execution result.
