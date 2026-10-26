# PostgreSQL persistence

The abstraction project contains transaction outcomes, concurrency tokens, named
query-filter registration and migration progress contracts. The EF Core adapter
implements those contracts for one configured PostgreSQL database. These sources
and their tests require Builder execution before any provider qualification claim.

Create a `PostgreSqlModule` for each module identifier, schema and concrete
`ModuleDbContext` type, then put the complete inventory in one
`PostgreSqlModuleRegistry`. `AddModularynPostgreSqlPersistence` registers its
coordinator and activation validator in the same service collection as the
module host. A host with declared migrations refuses construction when no
migration validator is registered (`MDC200`).

## Ownership and migration

Each context implements `ConfigureModule`. The adapter supplies the default
schema, validates tables and mapping fragments, and rejects foreign schemas and
reserved framework tables. A registry rejects duplicate context, module and
schema owners. A durable `__ModularynModule` row binds the schema to its module
and context identity across host runs. An existing schema containing unclaimed
objects needs explicit reconciliation; it is never silently adopted.

EF migrations carry the owning context's `DbContext` attribute and a unique
`Migration` identity. A migration can derive from `ModuleMigration` and use its
`Schema` property to support a configured schema. Discovery is limited to the
context's assembly and exact context type. The manifest declares every migration
identity. The operation validator rejects raw SQL, foreign references, reserved
table changes, SQL expressions and operations outside its supported vocabulary.
Generated commands must participate in the migration transaction. Views,
function mappings and nontransactional migrations need separate support.

Compose a migration plan with `ModuleComposer`, without the activation-readiness
validator, and pass its `ActivationPlan` to `PostgreSqlMigrationCoordinator`.
This preserves all CODE-02 dependency edges and orders. Migration application is
a distinct operation from host activation. The coordinator takes one
database-scoped PostgreSQL advisory lock; a competing coordinator returns
`MGP002`. It creates no database or role.

The module's `__EFMigrationsHistory` records committed EF transactions.
`__ModularynMigrationProgress` records applying, complete, failed and interrupted
states with attempt counts. Each migration and its system seed changes share
one EF transaction. A checkpoint observer runs after that transaction commits
and before coordinator completion is recorded. After interruption, a new
coordinator reconciles both histories under the lock: committed migrations are
not replayed, and rolled-back migrations are retried. A seed uses a stable key
and belongs to its module migration. Repeated application performs no work.
The adapter does not run demo seeds or promise reverse migrations.

An unknown or contradictory history refuses application with `MGP003`.
A migration or checkpoint failure returns `MGP004` and keeps the module
unready. Cancellation propagates and records interrupted progress when the
connection remains available. Loss of that connection can leave applying
progress, which the next run reconciles. The activation validator reads the
database afresh: pending, failed or interrupted work yields `MDC201`, a held
migration lock yields `MDC202`, and invalid or unavailable readiness yields
`MDC203`. It never applies migrations during activation.

## Transactions and direct SQL

`EfCoreUnitOfWork.BeginAsync` creates a context and a PostgreSQL transaction
at read-committed isolation. `Join` takes an explicit
ambient unit and shares its context and transaction; completing the join
defers its callbacks to the outer outcome. An uncompleted join prevents commit.
`New` uses another context and transaction. `Suppress` uses another context and
connection with autocommitted statements; its completion is explicitly
`CompletedWithoutTransaction`, and previously written statements cannot be
rolled back. An external ambient transaction is refused; the supported ambient
scope is an explicitly supplied module unit. No mode promises atomicity across
modules or databases.

Dispose an incomplete transactional unit to roll it back. Database failures
prevent commit, and callbacks run once with the final outcome. A callback failure
after a successful commit leaves the outcome committed. A failed commit
acknowledgement is a failed outcome, not proof of rollback; database reconciliation
must precede a retry. Contexts and units are
scoped, sequential objects; they must not be shared between parallel workers.

Entities implementing `IConcurrencyTracked` use a required original `Guid`
version and a new version on writes. A stale EF save returns a structured failed
result containing the original token and proposed values. It does not load
database values. Callers can reload in a fresh unit and decide how to retry.

`ModuleSql` offers raw single-table reads, scalar inserts and single-key updates.
Its public methods accept typed member selectors and values, with no SQL-text
overload. The adapter builds and quotes identifiers from owned EF metadata and
binds values through provider parameters. Reads start with an adapter-built
SQL `SELECT`; EF composes the named filters over it. Updates use EF's direct
update translation with the same filters, a primary key and an expected version.
All commands use the unit's connection and transaction. Inserts require explicit
keys and scalar values; inheritance, generated values and split mappings are
refused. Direct writes do not refresh previously tracked objects.

## Filters and scope

`ConfigureFilters(IQueryFilterRegistry)` is the shared extension seam. The
adapter registers a named soft-delete predicate for `ISoftDeleted` entities and
converts tracked deletions to flag updates. `IncludingSoftDeleted` bypasses only
that named predicate for its returned query; other predicates remain active.
It requires a reason and operation identity, retained with the query for a future
audit integration. It grants no authority and writes no audit record.

SEC-04 can contribute its filter through the same registry. This adapter makes
no tenant, authorization, key-storage or secret-management decision. Connection
strings remain configured options. The bypass audit implementation belongs to
SEC-07.

## Verification handoff

The PostgreSQL test classes use `MODULARYN_TEST_POSTGRESQL` and the existing
repository probe. Every test instance creates fresh schemas inside that supplied
database and drops them afterwards. Missing configuration fails the fixture.
Real-provider classes share a serial test collection because the advisory lock
covers the database; the lock-contention case starts competing coordinators
explicitly. All real-provider cases carry `Requires=PostgreSQL`; the six AC-005 method names
match the evidence plan. Additional cases cover partial DDL rollback, forward
repair, lock contention, ownership, activation refusal, cancellation and the
direct-SQL filter and concurrency paths.

The Builder generates dependency locks, adopts the registered package versions,
then runs restore, Release build, the unit suite and PostgreSQL suite through
the existing CI scripts. All six required CI checks must pass at the same final
commit. No restore, compilation, test execution or CI result is claimed by this
source-only handoff.
