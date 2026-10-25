# Website profile — gate W

Profile key: `website`. Status: defined, awaiting qualification. Gate W sits between G-02 and G-03 under the approved gate plan; its REL-007 decision remains the authority for changes to the master sequence. W uses the same schema, revision, publication, Files, authorization and portability contracts as the complete CMS of G-04, never a fork. It does not claim complete CMS support.

This profile serves a product site with versioned documentation, a personal site with a blog, and a small business site.

| Tuple component | Selection |
|---|---|
| Runtime and architecture | .NET 10, C# 14, modular monolith |
| Database | PostgreSQL through EF Core |
| UI | Razor public site and a minimal React/TypeScript Admin subset |
| Tenancy and sites | One tenant with one site per deployment, one culture, alias domains and redirects |
| Topology | Single-host container deployment |
| Providers | Files backed by filesystem BLOB storage; deployment-configured email transport for contact requests |

The release pins exact runtime, database, module, renderer, container and provider versions. Alias domains resolve to the same site only after ownership validation; they do not create additional sites. Authorization and tenant/site boundaries still apply with only one configured tenant.

## Included capabilities

The [machine-readable map](website-capabilities.json) associates each capability with existing coverage-register identifiers and describes the subset drawn from each identifier. These associations do not change register states or qualify entire requirements.

| Capability key | W scope |
|---|---|
| `content` | Content types, schema versions and stable content records with validated fields and references |
| `composition` | Typed blocks, layouts and templates using installed renderers and declarative composition |
| `publication` | Revisions, an immutable publication manifest, scheduling, protected preview and rollback |
| `media` | Media owned and versioned through Files, with integrity, scanning/quarantine and access checks |
| `taxonomy-navigation` | Taxonomies and navigation for the single site and culture |
| `seo` | SEO metadata, sitemaps, alias handling and redirects; drafts stay out of public discovery |
| `blog` | Blog posts and their public listing and reading journeys |
| `documentation` | Versioned documentation supplied from a Git repository |
| `contact` | A contact form with durable submission and email delivery to an authorized configured destination |
| `content-packages` | Versioned Git-based content packages, validation, dry-run import and reviewed apply |
| `theme` | An original accessible theme meeting WCAG 2.2 AA, including keyboard and assistive-technology journeys |
| `admin-account` | Sign-in for the minimal Admin with two-factor authentication or passkeys, account recovery and session revocation |
| `security` | ASVS L2 controls for the included public, Admin, API, preview, import and contact surfaces |
| `backup-restore` | Coordinated backup and restore of schemas, revisions, publication manifests, database state and referenced Files |
| `upgrade` | Upgrade from the previous W release with retained content, identity mappings and a validated recovery path |

The minimal Admin supplies the actions needed to operate those capabilities: author, inspect validation and conflicts, preview, schedule/publish/rollback, manage media, taxonomy/navigation, routes and contact delivery, review import plans and operate recovery. Server authorization applies to direct calls as well as the UI. It does not imply the complete G-03 Admin ledger. Templates in content packages are identifiers for installed declarative renderers, never uploaded executable templates.

## Explicitly unsupported

| Capability key | Register identifiers | Reason |
|---|---|---|
| `multisite-governance` | CMS-007, CMS-022 | W has one site per deployment; cross-site sharing, approvals and governance require broader lifecycle evidence. Alias domains are aliases of that one site. |
| `localization` | CMS-008, UI-013, MOD-021 | W fixes one culture; translation, fallback and localized route coordination are not qualified. |
| `search` | CMS-011 | Indexing, query authorization, freshness and rebuild recovery are outside the early website scope. |
| `engagement` | CMS-014, CMS-015, CMS-018, CMS-019, MOD-010 | Comments, moderation, reactions, ratings, marks, polls, feedback and FAQ workflows require additional interaction and abuse controls. The included contact form does not qualify these workflows. |
| `newsletters` | CMS-016, MOD-010 | Subscription consent, preference changes, bulk delivery and unsubscribe handling need separate qualification. |
| `headless-graphql` | CMS-013 | GraphQL schema, query-cost and field-authorization behavior are a separate profile. W's application/API contracts do not qualify GraphQL. |
| `payments-commerce` | MOD-025, MOD-027, AC-011 | Checkout, provider reconciliation, refunds, commercial entitlements and commerce workflows are outside W. |

Selections outside the declared tuple or included list fail capability validation. No inference of search, localization, engagement or broader CMS support follows from an underlying shared contract.

## Acceptance subset

Every row requires positive, rejection, boundary and recovery cases on W's exact tuple. The scope column limits the family; a pass is not a claim for the family's other capabilities.

| Families | Concrete W scope |
|---|---|
| AC-001 | Compose the W module set; reject incompatible providers, missing renderers and unsupported capability selections before activation. |
| AC-002, AC-003 | Admin sign-in with two-factor authentication or passkeys, recovery and session revocation; deny direct calls after permission changes, including preview, import and recovery operations. |
| AC-004 | Reject forged tenant/site identifiers in content, Files, imports, routes, cache, jobs and events, despite the deployment containing only one tenant/site. |
| AC-005, AC-006 | PostgreSQL transactions, concurrency and interrupted migration; publication/contact outbox and inbox crash windows, duplicate and reordered delivery. |
| AC-007 | Publication schedules across restart, lease loss, retry, cancellation, timezone transitions and misfires without duplicate publication. |
| AC-008 | Only the API contracts used by W's Admin and Razor delivery: authorization, typed errors, version compatibility and cancellation; no GraphQL claim. |
| AC-009 | Files upload/import quarantine, metadata and bytes integrity, denied access, revoked media, interrupted storage and restore using the filesystem provider. |
| AC-012, AC-013 | Audit included privileged operations without sensitive content; contact email distinguishes accepted, queued, delivered and failed, with duplicate prevention and outage recovery. No chat, SMS or bulk newsletter claim. |
| AC-016 | Versioned types, fields, blocks/layouts/templates, author conflicts, stale writes and preview bound to an exact protected revision. |
| AC-017 | Immutable publication manifests, scheduled activation, withdrawal/rollback, route collisions and cache convergence; no search propagation claim. |
| AC-018 | Razor delivery for one site/culture, aliases, restricted media, absent renderer rejection, host manipulation and exclusion of drafts from delivery, SEO and sitemaps. |
| AC-019 | Blog, Git documentation versions, taxonomy, navigation, SEO/redirects and contact journeys only; reject missing references, menu cycles, redirect loops and unknown documentation sources. |
| AC-020 | Contact field validation, antispam, rate limits, configured recipient authorization, durable acceptance and duplicate suppression; no general form designer or workflow-engine claim. |
| AC-023 | The minimal Admin action ledger and original theme: keyboard, focus, screen reader, contrast, responsive/error states and WCAG 2.2 AA evidence. |
| AC-028 | Upgrade from the previous W release, content-package version compatibility, export/import round trip, missing dependencies and collision rejection, interrupted import resume and failed-activation recovery. The first W release records the absence of a predecessor; subsequent releases must exercise the actual previous W release. |
| AC-029 | Restore database, schemas, immutable manifests and Files into an isolated deployment; verify historical references, rebuild derived cache and reconcile scheduled publication/email without uncontrolled replay. |
| AC-030, AC-031 | Measure authoring, publish/delivery and restore under a declared small-site workload; verify ASVS L2 controls, dependency provenance, secret handling and operational security for included surfaces. |

G-02 foundation evidence is a prerequisite. W qualification must bind these results to the exact release commit and tuple. Repository schemas and example validation alone cannot establish a W release. Three-platform repository CI, locked restore, Release build, tests and secret scanning remain required at that same head before acceptance of this contract work.
