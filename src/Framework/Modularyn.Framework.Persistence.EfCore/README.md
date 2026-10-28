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

## Tenant scope

Every root entity of a module context declares `ITenantScoped` or `IHostScoped`
from `Modularyn.Framework.Tenancy.Abstractions`. An undeclared, doubly declared
or shared-type entity, or an owned type that declares a scope, is refused
(`MPS030`), and `ITenantScoped.TenantId` must be a mapped public `Guid`
(`MPS031`). Every key, unique constraint, index and reference of a tenant-scoped
type carries its tenant, in the model and in the module's migrations, and a
host-scoped type cannot reference tenant-scoped data (`MPS032`). A migration
cannot insert, update or delete rows of a tenant-scoped table (`MPS039`).

`EfCoreUnitOfWork.BeginAsync(module, tenant, ...)` binds a validated
`TenantContext` to the unit's context for its whole life, and
`BeginAsync(module, deployment, ...)` binds a named `DeploymentScope` instead, and
`BeginOwnedAsync(module, owner, ...)` a `ModuleOwnerScope`. A unit carries at most
one of the three. The overload with none binds nothing, a joined scope shares its
ambient unit's binding and must name the same one, and a new or suppressed scope
never inherits one. Each tenant-scoped root carries two adapter filters on the named seam:
`Modularyn.Tenant`, which requires `TenantId` to equal the unit's tenant, and
`Modularyn.Scope`, which requires the unit's context to be active and, outside an
active host scope, compares `TenantId` with the unit's tenant again. A query that
lifts only the tenant predicate therefore stays within a tenant unit's tenant.
Their values are read for each execution, so a cached query never carries
another unit's tenant. Without an active tenant-scope context,
tenant-scoped data is refused, never returned unfiltered or empty (`MPS035`). A
host-scope context reaches it only through `AcrossTenants`, on the EF and
direct-SQL paths. That lifts only the tenant predicate, keeps soft delete, module
filters and the scope check, and returns the audited grant (`MPS038` otherwise).

Writes take the tenant from the unit. A new row's empty tenant is set to it, and
a row of another tenant or a change of tenant is refused (`MPS036`), on tracked
saves, direct inserts and direct updates alike. A tenant unit can read
host-scoped data but cannot change it (`MPS037`), and a unit without a context
cannot change tenant-scoped data (`MPS035`). A host-scope unit cannot change
tenant-scoped data either (`MPS038`), except one named tenant's row through
`ModuleSql.UpdateForTenantAsync`, for records the host keeps about tenant work,
such as a denied disposition.

A host grant reaches only the entities its operation declares, each named by
its module and table, with an owned type following the entity that owns it.
`AcrossTenants`, `UpdateForTenantAsync` and every host-scoped write in a host
scope, on the EF and direct-SQL paths, refuse any other entity (`MPS043`). An
operation whose module writes its records in its own owner-bound unit declares
no reach, so its host scope reaches nothing here.

Host scope is never applied implicitly. Host-scoped data changes only in an
active host scope, which the tenancy runtime grants under host authority and
audit, in a `DeploymentScope`, which names its operation and reason and exists
for framework metadata, required seeds and tests under deployment authority, or
in the owning module's owner-bound unit. A unit with none of them can read
host-scoped data but change none of it (`MPS040`). Migrations run under the
coordinator's lock and do not use units of work.

Cross-tenant host authority and a module writing its own host-scoped data are
different things. The first reaches or changes tenant data from host level,
through `AcrossTenants` and `UpdateForTenantAsync`, and stays gated by the audited
host scope. The second is the owner-bound unit: a module changes the host-scoped
records it owns, such as identity's accounts and sessions, through its own
context, for its own use cases.

- **Explicit.** `EfCoreUnitOfWork.BeginOwnedAsync(module, owner, ...)` begins it,
  with `ModuleOwnerScope.For<TContext>(operationId)`, which names the module's
  context type and an operation. Nothing begins it by default.
- **Its own module only.** The unit is accepted only when the scope's context type
  is the module's (`MPS042`), and ownership confines that context to the module's
  own schema and tables, so it writes only that module's host-scoped entities.
  Joining a unit of another module is refused (`MPS013`), as is joining with a
  different binding (`MPS035`).
- **No tenant access and no host authority.** It refuses every tenant-scoped read
  and write (`MPS035`), and `AcrossTenants` and `UpdateForTenantAsync` still
  require an active host grant (`MPS038`). Bulk LINQ writes to host-scoped data
  stay refused (`MPS037`). A tenant unit still cannot change host-scoped data
  (`MPS037`), and an owner-bound unit carries no tenant: a module that writes
  its own host-scoped records during a tenant request begins an owner-bound unit
  for them, separately from its tenant units.
- **Not authorization, and not for authority.** Who may invoke the use case that
  opens it belongs to authorization, which gates identity's administrative use
  cases with a host scope for their own operation or the account owner's own
  session. Records that are themselves authority, such as permission grants,
  role memberships, host-authority grants and identity's tenant memberships,
  are never written in an owner-bound unit: they change only in a tenant unit
  of an authorized, audited change or in an audited host scope that reaches
  them (SEC-05).

The repository test `PersistenceBoundaryTests` keeps deployment scopes out of
product source outside this adapter, and allows `BeginOwnedAsync` and
`ModuleOwnerScope` only in this adapter and in a module's own `.Application` and
`.Persistence.*` projects, each scope naming a context type that the same module
declares. These rules rest on that source check, the architecture rules that keep
a module from referencing another module's persistence project, and the context
type check at run time; they do not resist reflection.

## Filters and query guard

`ConfigureFilters(IQueryFilterRegistry)` is the shared extension seam. Names
beginning with `Modularyn.` belong to the adapter (`MPS012`). The adapter
registers a named soft-delete predicate for `ISoftDeleted` entities and converts
tracked deletions to flag updates. `IncludingSoftDeleted` bypasses only that
named predicate for its returned query; other predicates remain active. It
requires a reason and operation identity, retained with the query for a future
audit integration. It grants no authority and writes no audit record.

Every module context registers a query-expression interceptor, which inspects
each LINQ query before EF translates it, on `Set<T>()` and `ModuleSql` alike, and
a command interceptor:

- EF Core's general `IgnoreQueryFilters()`, and every named form that the adapter
  did not issue, are refused (`MPS033`), in subqueries and compiled queries too.
  The adapter's two bypasses, `IncludingSoftDeleted` and `AcrossTenants`, carry a
  marker, a call to a private adapter method, that the guard checks and removes;
  each must ignore exactly its own filter.
- A `FromSql` root is accepted only when it carries `ModuleSql`'s marker and its
  SQL text equals the text `ModuleSql` builds for the root's entity, so module
  code calling `FromSqlRaw` itself is refused even with that text. `Database.SqlQuery`
  and every other non-entity query root are refused (`MPS034`), as is
  `Database.ExecuteSql` in each of its forms, through the command interceptor.
- A bulk LINQ write to host-scoped data is refused (`MPS037`), since
  `ModuleSql.UpdateAsync` checks the unit's scope instead. A bulk write over the
  cross-tenant bypass is refused (`MPS038`), and so is a bulk update that sets a
  tenant (`MPS036`).
- `ExecuteDelete` on an `ISoftDeleted` type would delete rows outright and is
  refused (`MPS041`): remove tracked entities, which the adapter marks deleted, or
  set `IsDeleted` with `ModuleSql.UpdateAsync`. A LINQ bulk write to an
  `IConcurrencyTracked` type would skip its version token and is refused
  (`MPS010`): use `ModuleSql.UpdateAsync` with the expected version, or tracked
  writes.

The markers are private methods, but code in the same process could still build
such a call by reflection. Tenant isolation therefore does not rest on a marker
alone: a raw SQL read must also carry the adapter's own text, and the scope
predicate holds a tenant unit to its tenant even where a marker lifts the tenant
predicate. The bulk-write rules do rest on the update marker: a marker built by
reflection could pass a LINQ bulk write through them, across tenants only in an
active host scope. Reflection over the adapter's private methods is outside what
an expression guard can see.

The guard is stateless and decides from the query's shape and the model, because
EF caches a compiled query by its shape. Decisions that depend on the unit are
made where the adapter issues a bypass and in the filter values that each
execution evaluates. Code in the same process can still call `GetDbConnection()`
and run any SQL; the repository test `PersistenceBoundaryTests` keeps the filter
bypass, raw SQL and Npgsql command APIs out of product source outside this
adapter, and cannot see code outside the repository.

This adapter enforces the tenant scope that the tenancy runtime validates; it
does not resolve tenants and makes no authorization, key-storage or
secret-management decision. Connection strings remain configured options. The
bypass audit implementation belongs to SEC-07.

## Raw SQL sites

Each raw or direct SQL site in this adapter, and how it keeps the tenant filter:

| Site | SQL | Tenant filter |
|---|---|---|
| `ModuleSql.Query` and its filtered overload, through `Read` | `SELECT *` from one owned table, optionally where one column `IS NOT DISTINCT FROM` a parameter, both from `ModuleSqlText` | EF composes `Modularyn.Tenant`, `Modularyn.Scope`, soft delete and module filters over the `SELECT`; the guard accepts this root only under the adapter's marker and only with the template's exact text for its entity |
| `ModuleSql.InsertAsync` | `INSERT` of one scalar row, every value a parameter | An insert has nothing to filter, so the row's tenant is set from, or must equal, the unit's tenant; a host-scoped row needs a host, deployment or owner scope |
| `ModuleSql.UpdateAsync` and `UpdateForTenantAsync` | EF's direct update of one value and the version, by key and expected version | Every filter applies, and the tenant column cannot be the value; the host form lifts only the tenant predicate, adds the named tenant as an explicit predicate and requires an active host grant |
| `ModuleSchemaOwnership` | Ownership-row and catalog reads on the unit's connection | Framework metadata of the module schema; no tenant data |
| `MigrationProgressStore`, `PostgreSqlMigrationLock` | Schema, ownership and progress tables, and the advisory lock | Framework metadata under the migration lock; no tenant data |
| Migrations and EF history, through the coordinator and the activation validator | Validated migration operations and EF's history queries | Migrations cannot change tenant rows, and tenant tables keep the tenant in keys, indexes and references |

Both read overloads share `Read`, so the one EF1003 suppression, with its
justification, covers the adapter's only `FromSqlRaw` call.

## Verification handoff

The PostgreSQL test classes use `MODULARYN_TEST_POSTGRESQL` and the existing
repository probe. Every test instance creates fresh schemas inside that supplied
database and drops them afterwards. Missing configuration fails the fixture.
Real-provider classes share a serial test collection because the advisory lock
covers the database; the lock-contention case starts competing coordinators
explicitly. All real-provider cases carry `Requires=PostgreSQL`; the six AC-005 method names
match the evidence plan. Additional cases cover partial DDL rollback, forward
repair, lock contention, ownership, activation refusal, cancellation and the
direct-SQL filter and concurrency paths. `QueryGuardTests` show the guard's
refusals against PostgreSQL on both paths, and `TenantModelTests` the
classification, key and migration rules; the tenant filter's behavior with
validated contexts is tested in `Modularyn.Framework.Tenancy.Tests`. Each
real-provider test also holds a session advisory lock under its own key, so the
PostgreSQL tests of different projects never contend for the migration lock when
they run at once.

The Builder generates dependency locks, adopts the registered package versions,
then runs restore, Release build, the unit suite and PostgreSQL suite through
the existing CI scripts. All six required CI checks must pass at the same final
commit. No restore, compilation, test execution or CI result is claimed by this
source-only handoff.
