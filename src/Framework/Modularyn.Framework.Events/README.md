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
unit. Payloads and public envelopes contain no propagated context. Receipt never
copies the outbox's reference. The reference remains attached to its original
producer through replay. A stored reference that names another tenant than its
row never acts in either tenant: receipt refuses it, and consumption refuses it
before any read, so both count it against the failure budget and quarantine it.

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
are retained. A late redelivery after receipt pruning still cannot repeat a
projection effect because the watermark survives. Ordering keys must never be
reused for a different stream with reset positions. Outbox history and watermarks
remain until the owning module supplies its lifecycle policy.

The default failure budget is three attempts, with exponential delays starting
at one second. Unsupported kinds or versions and handler failures retain their
payload and cause code; exhaustion quarantines the inbox as `Poison`.
Authorization unavailability defers with the same bounded policy; permanent
refusal records `Denied`. Exhausted dispatch deferrals quarantine the outbox.
Receipt conflicts retry the same identity without classifying a competing
transaction as a handler failure.

`ReplayAsync` and `ReplayDispatchAsync` are application services for quarantined
inbox and outbox rows. Both require the current `events.replay` permission through
the evaluator, reset the bounded retry and gap-wait budgets and preserve the producer. Replay
always goes through delivery, inbox deduplication, ordering and current authority;
it never invokes a handler directly. The composing module declares this permission
and the consumer permission in its manifest and authorization registry. There is
no HTTP endpoint.

`IEventObserver` leaves a seam for audit and diagnostics without choosing their
policy. Tests inject interruption before producer commit, before dispatch, after
receipt before dispatch acknowledgement, after flushed consumer writes before
commit, and after consumer commit before acknowledgement. The PostgreSQL test
project also covers duplicates through retention, ordering, quarantine, replay,
concurrent consumers and revoked producer authority. Test source is a verification
handoff, not an execution result.
