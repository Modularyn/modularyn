# Reference profile

Profile key: `reference`. Status: defined, awaiting qualification. This is the initial DEV-004 baseline.

| Tuple component | Selection |
|---|---|
| Runtime and language | .NET 10 LTS, C# 14; exact SDK/runtime servicing versions recorded per release |
| Architecture | Modular monolith with explicit module ownership and shared application contracts |
| Database | PostgreSQL through EF Core; exact database and provider versions recorded per release |
| UI | React and TypeScript Admin; Razor public-site renderer |
| Tenancy | Shared database with tenant-scoped data, permissions, files, caches and background execution |
| Topology | Single host, qualified both as a host deployment and as a container deployment; no distributed scale claim |
| Providers | PostgreSQL/EF Core persistence and filesystem BLOB storage; release-selected email transport for the exercised communication surface |

The provider entries bound the initial qualification. Email transport configuration and credentials belong to the deployment, never to content packages. Additional providers need their own evidence.

## Included capabilities and acceptance subset

This profile supplies the baseline on which gates add capabilities. Its first qualification is the secure application slice of G-02, followed by separately evidenced Admin and CMS work. Listing a later gate here does not mark that work complete.

| Capability | AC families and concrete scope |
|---|---|
| Module composition | AC-001: valid module sets and rejection of absent, cyclic, incompatible or untrusted dependencies before activation |
| Identity, permissions, settings and features | AC-002, AC-003: sign-in and session boundaries for exercised account actions; server-authoritative access, precedence and revocation through Admin and direct APIs |
| Shared-database tenancy | AC-004: adversarial cross-tenant identifiers through queries, references, imports, files, cache, jobs and events; context cannot leak between executions |
| Persistence | AC-005: actual PostgreSQL filtering, transactions, optimistic concurrency, migrations and recovery from interruption |
| Events and jobs | AC-006, AC-007: outbox/inbox crash windows, duplicate delivery, retries, lease loss, restarts and schedule boundaries |
| APIs and clients | AC-008: advertised .NET and TypeScript client contracts, versioning, authorization, cancellation and structured failures |
| Files | AC-009: filesystem upload, quarantine, versioning, authorization, revocation, integrity, interrupted writes and restore |
| Operable application | AC-012, AC-025, AC-030, AC-031: scoped audit and redaction, local diagnostics, declared workload measurements, security and dependency provenance |
| React Admin and Razor CMS | AC-016–AC-020, AC-023: only action ledgers explicitly qualified by the release; exact revision preview, publication and delivery, editorial conflicts, forms and accessible interaction |
| Upgrade and recovery | AC-028, AC-029: prior supported application upgrade, custom content preservation, coordinated database/media restore and safe reconciliation of queued effects |

G-01 defines the baseline and initial corpus. G-02 must pass its foundation families AC-001, AC-003, AC-004, AC-005, AC-006, AC-008 and AC-009 with real providers and failure cases. G-03 qualifies the canonical Admin and its selected operational modules. G-04 qualifies the complete CMS journeys; [gate W](website.md) qualifies an earlier subset on these contracts. G-08 supplies production security, accessibility, maintenance and recovery evidence. G-09's AC-032 audit is required for comprehensive coverage claims. Individual qualified subsets may precede those broad claims.

## Unsupported selections

| Selection | Reason |
|---|---|
| Other runtimes, languages, relational stores or MongoDB | This tuple exercises .NET 10, C# 14 and PostgreSQL/EF Core only; provider semantics need independent qualification. |
| Angular, Blazor, mobile or another public renderer | Their native components and action ledgers are separate deliverables; React Admin and Razor evidence does not transfer. |
| Database-per-tenant or hybrid tenant placement | Isolation, placement and migration behavior differ from the shared-database mode. |
| Microservices, multi-host scale, Kubernetes or a separate authorization server | Distributed failure, networking and operational behavior are outside the initial single-host topology. |
| Cloud/object BLOB stores and unspecified mail transports | Provider integrity, revocation, delivery and recovery behavior require their own tests. |
| Unqualified catalog modules, low-code, AI tooling, payments or commerce | A baseline tuple does not certify every module; each release must name and qualify its included capability set. |
| Universal compatibility or full CMS completion at G-02 | G-02 establishes the foundation slice; later gates retain their own acceptance obligations. |
