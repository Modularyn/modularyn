# Background Jobs application

Register `BackgroundJobsDefinition.Manifest`, its permissions and host operation
with composition and authorization. Register the persistence module, tenancy,
authorization, a `TimeProvider`, service identities and the host's `ModuleWorkGate`,
then call `AddModularynBackgroundJobs` with trusted `JobDefinition` registrations.
The host invokes `BackgroundJobRuntime.RunAsync` for a bounded scheduling and
execution pass while the lifecycle gate admits work. Host authority is separate
from the service's job permissions, and shutdown cancellation reaches active work.

The module owns its job tables. Other modules can include the framework job model
in their own stores when business effects and job completion must be atomic.
Cross-module requests should use a transactional event into the owning module,
whose authorized handler enqueues in its receiving unit. No application service
accepts a propagated reference from a caller.

Identity exposes `IdentityRetentionJob.Create` for registration on a durable
worker. Its permission and `IdentityHostOperations.Retention` must be composed.
Schedule it as a declared maintenance service in an active maintenance tenant;
the handler passes its running principal to each checkpoint and obtains a fresh
audited host scope before pruning. It removes at most 1,000 eligible throttle
rows and 1,000 sessions per invocation, with 100 of each by default. A host can
schedule repeated passes to drain larger backlogs.

Throttle rows remain for 60 days after activity, never while blocked. The window
covers the largest supported throttle reset interval, including the source/account
pair's doubled reset. Retention uses an `UpdatedAt` concurrency check so it cannot
erase a counter refreshed after selection. Ended sessions remain for 30 days;
sessions whose absolute lifetime expired also become eligible after 30 days.
Live sessions and recent endings remain. Pruning is an idempotent identity-owned
transaction, separate from the job store; a lost acknowledgement may repeat the
selection but cannot delete a retained row twice. It is not an atomic transaction
between the two modules.

Growth before pruning is one throttle row per source, one per account and
verification kind, and one per source/account pair and kind encountered in the
retention window; distinct pair counts can approach the product of source and
account counts. Sessions grow with sign-ins over the live lifetime plus the
30-day ended-session window. Backlog can grow when maintenance stops or ingress
exceeds the configured batch throughput, so the host chooses a cadence with
capacity for its observed input rate. Authority records are never retention targets.

Audit, redaction, diagnostics and file policy attach through their existing seams.
No HTTP endpoint or new package dependency is introduced. Restore, compilation,
all test execution and all six CI checks at the final commit belong to the Builder.
