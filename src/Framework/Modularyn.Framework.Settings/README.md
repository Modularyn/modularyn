# Scoped settings and features

`Modularyn.Framework.Settings.Abstractions` supplies immutable module contracts
for text, Boolean, integer and secret-reference settings. Modules publish each
contract's `Declaration` in their manifest's `Definitions`. Feature contracts
come from `Modularyn.Framework.Features.Abstractions`. `ValueDefinitionRegistry`
requires an exact match of owner, asset name and version, and also participates
in composition validation. Change the definition version when changing its
type, default or validation. The deployment lock binds the module's contract
assembly as well as its manifest.

Register the module returned by `SettingsModule.Create` with the persistence
coordinator, and include `SettingsModule.Manifest` in the composition selection.
After registering tenancy, call `AddModularynSettings` with that module and the
definition registry. `AddModularynFeatures` adds the flag facade. These projects
use the existing persistence adapter and shared frameworks; they introduce no
external dependency identities.

Effective values resolve in this order:

| Source | Ownership and identity |
| --- | --- |
| User | Current tenant and current user actor |
| Tenant | Current tenant |
| Global | Host-owned row, shared as an inherited value |
| Default | Immutable module definition |

The first present value wins. Empty text and `false` are present values.
Services do not receive user or tenant identifiers from a value request: those
identities come from the active tenancy context. Service actors have no user
override. An effective read requires tenant scope; management reads of global
overrides require host scope. `ResolvedValue` reports the type, source, row
version and normalized update time, and exposes a typed `Get<T>()` method.

Boolean text is exactly `true` or `false`. Integers are canonical invariant
signed 64-bit decimal values, within any declared inclusive bounds. Text has a
declared maximum length and can have an ordinal allowlist. Defaults and writes
must validate; stored overrides validate again when resolved. An unavailable
store or an invalid effective override fails the read and caches no default.
Resetting an override exposes the next source without changing its definition.

`SetAsync` with no expected version creates an override. Replacement and reset
require its current version, obtained from `ReadOverrideAsync` or the effective
result when that scope is selected. Each write owns a tracked unit and commits
before invalidating caches. Conflicts and rollbacks leave the committed value
in place. Global writes require an explicit host context granted for
`SettingsService.GlobalOperationId`; a grant for another operation is refused. Tenant and user rows
carry `TenantId` in their composite primary key. The module owns its schema,
migration and history; it uses the persistence adapter's query and write guards.
`SettingsTimestamp.Normalize` is the one boundary for stored update times:
UTC whole microseconds, read from the registered `TimeProvider`.

Secret definitions have an unset default and accept only an explicit
`SecretReference`, and only as a global override under the settings host
grant. A reference names a deployment-wide secret, so a tenant or a user may
not choose one, and a stored tenant or user reference fails the read. Secrets
owned by one tenant would need a tenant-bound secret namespace, which this
project does not provide. Literal secret defaults, literal writes
and arbitrary validation callbacks are not supported. `SetSecretReferenceAsync`
uses the configuration contract; `SetAsync` applies the same reference check
before opening a unit. Storage and effective caches contain references only.
Inspection, formatting and serialization mask a secret setting. Each
`ResolveSecretAsync` call invokes the existing `ISecretStore` afresh and returns
a caller-owned `SecretValue`, which the caller disposes. Rotation of mounted
content is therefore independent of reference-cache expiry. This project adds
no secret store or authorization policy.

`FeatureService.ResolveAsync` reports the configured flag and its source.
`IsEnabledAsync` also calls the separately supplied `IFeatureGate` on each
operation; no gate means no enabled operation. Permission and entitlement
implementations attach there. Settings and flags never confer authority, alter
installed composition, or expose client configuration endpoints. Management
authorization and audit integrations belong at the application boundary.

## Cache and work lifetime

The cache is `ScopedValueCache` over SEC-04's `TenantCache`. Every key includes
the tenant, actor kind and subject, owning module, definition version, asset
name and invalidation generations. Global changes invalidate every tenant's
entries for that module in the process; tenant changes invalidate that tenant;
user changes invalidate that user in that tenant. A fill from an older
generation cannot repopulate the current generation. Concurrent misses share
a fill; a cancelled waiter does not cancel other waiters.

Each definition declares a positive maximum staleness of at most one day.
The injected clock measures it from the start of the fill, including database
read time. An overlong fill fails and is not cached. Each fill captures a
`PropagatedTenantContext` and executes through `TenantWorkRunner`, in its own
revalidated scope with cancellation at the staleness deadline. No request's database context is
shared with a detached fill. A context that ends while awaiting a fill cannot
receive the result.

This is a process-local provider. Changes through another process can remain
visible at their old values until the declared bound, then the next read loads
PostgreSQL. A restart starts with an empty cache. A dependency outage may use
an unexpired cached value; after expiry, reads fail until the dependency
recovers. There is no distributed cache or cluster coordination claim.

## Verification handoff

`Modularyn.Framework.Settings.Tests.G02AC003Tests` contains the exact unit and
integration method names assigned to AC-003-P02. The integration cases cover
settings and feature precedence, resets, invalidation, tenant/user separation,
validation, conflicts, rollback, clock precision, independent caches, recovery
and context boundaries. Secret tests use the real mounted-file store and
inspect actual PostgreSQL rows, logs and EF diagnostics for generated canaries.
Unit checks cover definitions, cache races and module localization fallback.

Every real-provider test has `Requires=PostgreSQL`. Fixtures read
`MODULARYN_TEST_POSTGRESQL`, acquire the shared `PostgreSqlTestLock`, migrate
fresh schemas in the supplied database, and drop only their own schemas. They
need no elevated database role. Identity, membership and host-authority test
seams supply fixture contexts only and establish no security qualification.

The Builder must restore and validate the dependency locks, build Release,
run the unit suite and the repository's PostgreSQL script, and collect all six
CI checks at the final commit on the required platforms. This source handoff
does not claim those checks have run or passed.
