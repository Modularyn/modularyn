# Initial acceptance corpus

[corpus.json](corpus.json) contains planning contracts for the non-security acceptance work in G-02 and the proposed gate W. The family documents contain the same cases in a readable form. A case is an obligation to collect evidence; its presence, and a passing repository consistency check, do not show that product behavior exists or that a profile, requirement, gate or release has passed.

The [coverage register](../coverage/register.json) supplies requirement and family identities. The [reference profile](../profiles/reference.md) and [website profile](../profiles/website.md) bound each case. The [website capability map](../profiles/website-capabilities.json) and [content-package contract](../content-packages/README.md) further bound W. Requirement references identify the exercised part of a requirement, not its entire acceptance scope. This corpus changes no register state or master-design decision; W enters the master sequence only through a recorded decision.

## Coverage and ownership

Every family/profile pair below has positive, negative, boundary and recovery cases. Shared cases run separately on each named profile; evidence never transfers between tuples.

| Family | Profiles in this corpus | Non-security scope |
|---|---|---|
| [AC-001](AC-001.md) | reference, website | Composition, dependency diagnostics, renderer and capability selection |
| [AC-005](AC-005.md) | reference, website | PostgreSQL filtering, transactions, concurrency, migrations and interruption |
| [AC-006](AC-006.md) | reference, website | Durable intent, outbox/inbox crash windows, duplicates, ordering and poison recovery |
| [AC-007](AC-007.md) | reference, website | Schedules, retry, cancellation, restart, lease loss, timezone transitions and misfires |
| [AC-008](AC-008.md) | reference, website | Advertised clients, W endpoints, typed failures, versions and cancellation |
| [AC-009](AC-009.md) | reference, website | Files metadata, immutable versions, byte integrity, interrupted writes and restore |
| [AC-013](AC-013.md) | website | Contact email state, duplicate prevention and outage reconciliation |
| [AC-016](AC-016.md) | website | Versioned types, fields, references, blocks, layouts, templates, conflicts and exact revision preview |
| [AC-017](AC-017.md) | website | Immutable manifests, activation, schedules, withdrawal, rollback, routes and cache convergence |
| [AC-018](AC-018.md) | website | Razor delivery, one site/culture, configured aliases, renderer availability and published discovery |
| [AC-019](AC-019.md) | website | Blog, pinned Git documentation, taxonomy/navigation, SEO, redirects and contact journeys |
| [AC-020](AC-020.md) | website | Contact field validation, durable acceptance, duplicate suppression and recovery |
| [AC-023](AC-023.md) | website | Minimal Admin action ledger, original theme and accessible public journeys |
| [AC-028](AC-028.md) | website | Previous-release upgrade, package round trip, compatibility, conflicts and recovery |
| [AC-029](AC-029.md) | website | Coordinated database/Files restore, historical references and queued-effect reconciliation |
| [AC-030](AC-030.md) | website | Declared small-site authoring, publication/delivery and restore workload measurements |

AC-007 is included for reference because the profile's events/jobs contract requires scheduler qualification alongside the G-02 foundation. Its inclusion does not revise G-02's gate definition. Later reference-profile CMS, Admin and operational qualification is outside this initial slice.

Security families **AC-002, AC-003, AC-004, AC-012 and AC-031** follow this same format and are maintained as the Builder's security work. There are no cases for them in this corpus. `securityDependencies` records references, never substitute security scenarios. Authentication, authorization, isolation, audit and security-assurance results must be supplied by that work before dependent evidence can qualify a profile.

In particular, untrusted module decisions belong to AC-031; client authentication to AC-002; access, preview protection and revocation to AC-003; tenant/site and execution-context boundaries to AC-004; privileged-operation audit to AC-012; media scanning/quarantine policy, host manipulation, antispam and security rate-limit behavior to AC-031, with AC-003/AC-004 where applicable. A fixture may consume an already established security decision, but the cases here do not define or test those decisions. The non-security cases therefore cannot close G-02 or W on their own.

## Format 1

The root object has exactly `version`, `date` and `cases`. `version` is integer `1`; `date` is an ISO calendar date. `cases` is a nonempty array, ordered ordinally by id. JSON property names are unique; unknown fields, nulls, blank strings and repeated references are rejected. All prose values are single lines; ordered arrays supply paragraphs and steps. Arrays preserve order.

| Case field | Contract |
|---|---|
| `id` | Stable family id, type letter and two-digit nonzero sequence, such as `AC-005-B03`. Letters are P, N, B and R. Numbers are unique within family/type, never renumbered or reused. |
| `family` | An acceptance-family identifier from the coverage register. |
| `type` | Exactly `positive`, `negative`, `boundary` or `recovery`, agreeing with the id letter. |
| `title` | A short, single-line description, at most 100 characters. |
| `profiles` | Nonempty unique array of `reference`, `website` or both. |
| `scope` | Object with exactly the named profile keys. Each value describes the exercised slice on that profile, including relevant restrictions. |
| `requirements` | Nonempty unique array of register requirement ids. Every requirement must map to this case's family in the register. Inventory rows and family ids are not requirements. |
| `layers` | Nonempty unique array of REL-002 layers from the vocabulary below. |
| `securityDependencies` | Unique array of security-family references; may be empty. This field is available to security cases too. |
| `preconditions` | Nonempty ordered array of fixture data, initial states and declared configuration needed to run the case. |
| `steps` | Nonempty ordered array of concrete actions, including any fault injection and its exact point. |
| `expectedOutcome` | Nonempty ordered array of observable assertions, including retained state and failure disposition. |
| `evidence` | Nonempty ordered array describing the artifacts a future run must retain. These are evidence obligations, not result links or pass claims. |

The format accepts all register families, including the security families, without changing version or fields. The initial-scope repository check separately rejects cases outside this assignment. The Builder can extend that scope check when adding its security cases; this is an ownership change in the checked corpus, not a format change. Removing a case requires a reviewed retirement decision, preserving the retired id in that decision.

The four types mean: **positive**, the intended operation succeeds; **negative**, a concrete invalid operation produces a specific refusal or failure with an observable state; **boundary**, values or transitions on either side of a declared edge are exercised; **recovery**, an interruption or failed attempt is reconciled and reaches a declared usable state. One case may use a small explicitly enumerated input matrix, with each row reported separately.

## Profiles, fixtures and evidence

Every run records the exact release commit, case id, corpus revision, date, module/runtime/provider versions, profile tuple, fixture inputs, expected and observed outcomes, artifacts and remaining gaps. Reference cases run on the profile's host and container deployments; W cases run on its single-host container tuple. Use actual PostgreSQL through EF Core and actual filesystem storage where those layers are named. A simulated fault can control timing; a mock store cannot qualify a provider.

Names such as `article`, `contact-1`, `manifest-1`, `revision-1` and module A are synthetic fixture identities. Numbers in cases are explicit test inputs, not new product limits. A limit or policy selected for a fixture must be advertised and supported by the implementation; record its actual value before running. An unavailable required policy or dependency is an unresolved qualification gap, never a silent substitution. Recorded release budgets and supported-version ranges are inputs to qualification, not values inferred from a passing test.

For reference API cases, enumerate every advertised .NET and TypeScript client mode before running. For W, enumerate only the endpoints used by the minimal Admin and Razor delivery. W uses one site and one culture, installed declarative renderers, filesystem Files and its selected email transport. It claims no search, localization, engagement, newsletter, GraphQL, payment, commerce, distributed scale or complete CMS qualification. Negative capability-selection checks do not qualify the rejected capabilities.

A security dependency is satisfied only by the Builder's evidence for the same exercised scope and tuple. All actors, tenant/site contexts, named destinations and media eligibility used here are prequalified fixtures from that work. If such evidence is unavailable, record the dependency as unmet; do not invent a security implementation or report the dependent case as qualified.

The [REL-002 layer vocabulary](../security/threat-models/README.md) is shared unchanged:

| Layer | Required kind of evidence |
|---|---|
| `unit` | Domain, architecture and contract rules in process |
| `real-provider-integration` | Behavior on the selected actual database, filesystem or email transport |
| `api-contract` | API or event contracts over their declared transport |
| `isolation` | Builder-owned authorization and isolation exercises |
| `ui` | Browser workflows and accessibility inspection |
| `migration` | Schema/application compatibility and upgrade behavior |
| `operational-recovery` | Deployed failure, restart, repair and restore exercises |

Email fixtures use a bounded delivery environment and record the transport's actual confirmation capabilities. Provider acceptance alone never proves recipient delivery. Restore fixtures reconcile external delivery identities before resuming work. First-release upgrade evidence explicitly records that no predecessor exists; it cannot count as a previous-release upgrade pass.

## Readable documents and checks

Each populated family has exactly one `AC-NNN.md` file. Its heading names the family; subsequent sections render every case field in corpus order. Empty security dependencies render as `None.`. Profile scope, preconditions, expected outcomes and evidence use bullets; steps use a numbered list. `AcceptanceCorpusContract.RenderFamily` defines the deterministic rendering, with LF and CRLF treated equivalently. Edit JSON and its family document together.

[AcceptanceCorpusTests.cs](../../tests/Modularyn.Repository.Tests/AcceptanceCorpusTests.cs) checks shape, ids, initial scope, all four types per family/profile, requirement mappings, vocabulary membership, profile scope keys and complete Markdown agreement. In-memory rejection fixtures assert their particular diagnostics. A format-level fixture verifies that existing security-family identities use the same structural contract without authoring security cases. These checks establish consistency, not semantic completeness or execution evidence.

Runtime acceptance evidence remains a separate obligation tied to the exact release and tuple. Review the corpus when profiles, requirements, provider contracts or scope decisions change.
