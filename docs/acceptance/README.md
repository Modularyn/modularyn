# Initial acceptance corpus

[corpus.json](corpus.json) contains planning contracts for the acceptance work in G-02 and gate W: the non-security families and the security families that the Builder maintains. The family documents contain the same cases in a readable form. A case is an obligation to collect evidence; its presence, and a passing repository consistency check, do not show that product behavior exists or that a profile, requirement, gate or release has passed.

The [coverage register](../coverage/register.json) supplies requirement and family identities. The [reference profile](../profiles/reference.md) and [website profile](../profiles/website.md) bound each case. The [website capability map](../profiles/website-capabilities.json) and [content-package contract](../content-packages/README.md) further bound W. Requirement references identify the exercised part of a requirement, not its entire acceptance scope. This corpus changes no register state or master-design decision; W enters the master sequence only through a recorded decision.

## Coverage and ownership

Every family/profile pair below has positive, negative, boundary and recovery cases. Shared cases run separately on each named profile; evidence never transfers between tuples.

| Family | Profiles in this corpus | Scope |
|---|---|---|
| [AC-001](AC-001.md) | reference, website | Composition, dependency diagnostics, renderer and capability selection |
| [AC-002](AC-002.md) | website | Admin sign-in with a second factor or passkey, recovery, session revocation, recent authentication for high-impact operations, stored credential forms, guessing, lockout and bootstrap |
| [AC-003](AC-003.md) | reference, website | Server-side permissions, grants, settings, features, delegation and freshness; W direct calls after permission changes, including preview, import and recovery, and cross-origin policy |
| [AC-004](AC-004.md) | reference, website | Cross-tenant and forged tenant/site identifiers through data and execution surfaces, host scope and execution-context reuse |
| [AC-005](AC-005.md) | reference, website | PostgreSQL filtering, transactions, concurrency, migrations and interruption |
| [AC-006](AC-006.md) | reference, website | Durable intent, outbox/inbox crash windows, duplicates, ordering and poison recovery |
| [AC-007](AC-007.md) | reference, website | Schedules, retry, cancellation, restart, lease loss, timezone transitions and misfires |
| [AC-008](AC-008.md) | reference, website | Advertised clients, W endpoints, typed failures, versions and cancellation |
| [AC-009](AC-009.md) | reference, website | Files metadata, immutable versions, byte integrity, interrupted writes and restore |
| [AC-012](AC-012.md) | website | Privileged-operation audit without sensitive content, separated audit roles, collection failure, retention and restore gaps |
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
| [AC-031](AC-031.md) | website | ASVS L2 applicability, shared-origin content policy, HTTPS, provenance, secrets and keys, hostile package input, media quarantine, host validation, antispam, public rate limits, backups, container and network posture |

AC-007 is included for reference because the profile's events/jobs contract requires scheduler qualification alongside the G-02 foundation. Its inclusion does not revise G-02's gate definition. Later reference-profile CMS, Admin and operational qualification is outside this initial slice.

Security families **AC-002, AC-003, AC-004, AC-012 and AC-031** follow this same format and are maintained as the Builder's security work. This corpus holds the Builder's cases for AC-003 and AC-004 on the reference profile, within the G-02 foundation slice, and for all five families on the website profile, within W's concrete scope. AC-002, AC-012 and AC-031 have no reference cases here; their reference evidence belongs to the later gates that the register assigns. `securityDependencies` records references, never substitute security scenarios; a security case may name another security family whose established decision it consumes, but never its own family. Authentication, authorization, isolation, audit and security-assurance results must be supplied by evidence for these cases before dependent evidence can qualify a profile.

Security families depend on one another: AC-003 cases, for example, consume AC-004's tenant context, and AC-004 cases consume AC-003's access decisions. These dependencies are not circular. Each family's own evidence satisfies its own cases, and a dependency is satisfied only by the other family's evidence for the same exercised scope on the same tuple, never by the dependent case's own evidence. On the reference profile, AC-003 and AC-004 cases name AC-002 where they consume sign-in fixtures, and AC-004-N03 names AC-012 for the audit record of a host operation. AC-002 and AC-012 have no reference cases until G-03, so G-02's qualification plan must qualify those sign-in fixtures and that audit record on the same tuple; until it does, each of those dependencies is unmet, as is any other reference dependency on AC-002, AC-012 or AC-031.

Each security case cites, in its steps or evidence, the identifiers of the [threat-model controls](../security/threat-models/controls.json) that its evidence must show working. Every control that a case cites, in any field, must be planned under that case's family in the control map and must name every profile of the case. Behavior that the map plans under another family is named as a dependency on that family, by its family identifier, never as the citing case's evidence. On the website profile, a family's cases together cite every control that names that family and the website profile. On the reference profile, they cite every foundation-model (`TM-G02-`) control that names the family and the reference profile; website-model controls that also name `reference` implement CMS contracts that the reference profile qualifies at G-04, outside this slice. Citations trace obligations; they do not show that a control exists or works.

The control map allocates that evidence as follows. Module composition, including rejection of untrusted selections and lock-manifest verification, belongs to AC-001 (TM-G02-001), and release-artifact provenance to AC-028 and AC-031 (TM-W-050). Sign-in, factors, recovery, session revocation and credential throttling belong to AC-002, as do the refusal of Admin and preview namespaces on alias hosts, which AC-018 shares (TM-W-009), and recent authentication before high-impact operations, which AC-012 shares (TM-W-013). Permissions, resource authorization, preview permission, freshness and revocation belong to AC-003, including per-item preview authorization, which AC-003 shares with AC-009 and AC-016 (TM-W-021), and the dedicated contact-destination permission, which it shares with AC-012 and AC-020 (TM-W-033). Binding preview to an exact revision belongs to AC-009 and AC-016 (TM-W-021), and keeping it out of caches and discovery to AC-016 and AC-018 (TM-W-020). Tenant and site resolution, host and forwarded-header trust and execution-context boundaries belong to AC-004 (TM-G02-006, TM-G02-025, TM-G02-026). Privileged-operation audit belongs to AC-012. Secrets and keys, release provenance, hostile package and source input, the shared origin's content policy, backups and container posture belong to AC-031, many of them jointly with another family, such as AC-002 for stored credentials and AC-028 for package input. Minimal anonymous health and readiness responses are planned under AC-025 and AC-031 (TM-G02-054); AC-025 is outside W's subset, so AC-031 supplies that evidence on the website profile. Media scanning and quarantine are planned under AC-009 and AC-031 (TM-G02-042, TM-W-026), host validation with absolute and canonical links built from the configured origin under AC-018 and AC-031 (TM-W-008), contact antispam under AC-020, AC-030 and AC-031 (TM-W-030), and public rate limits under AC-030 and AC-031 (TM-W-006).

The AC-031 cases qualify those four behaviors as security evidence on the website profile, adversarially and with their own control citations. The W scopes of AC-009, AC-018, AC-020 and AC-030 describe these behaviors as security-family prerequisites, and that AC-031 evidence on the same tuple supplies them, so no coverage gap remains between the families. AC-009, AC-018, AC-020 and AC-030 still own their functional cases, and the AC-031 cases do not replace them. Reference-profile AC-031 evidence, including the trust qualification that AC-001's reference scope names and these controls' reference evidence, belongs to G-08 and is not part of this corpus. A non-security fixture may consume an already established security decision, but the non-security cases do not define or test those decisions and cannot close G-02 or W on their own. Neither can the security cases: each is an evidence obligation for its exact release and tuple.

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

The format accepts all register families, including the security families, without changing version or fields. The scope repository check separately rejects family/profile pairs outside this assignment: non-security pairs outside the initial corpus scope and security pairs outside the Builder's security scope. The Builder extended that check with its security pairs; this is an ownership change in the checked corpus, not a format change. Removing a case requires a reviewed retirement decision, preserving the retired id in that decision.

The four types mean: **positive**, the intended operation succeeds; **negative**, a concrete invalid operation produces a specific refusal or failure with an observable state; **boundary**, values or transitions on either side of a declared edge are exercised; **recovery**, an interruption or failed attempt is reconciled and reaches a declared usable state. One case may use a small explicitly enumerated input matrix, with each row reported separately.

## Profiles, fixtures and evidence

Every run records the exact release commit, case id, corpus revision, date, module/runtime/provider versions, profile tuple, fixture inputs, expected and observed outcomes, artifacts and remaining gaps. Reference cases run on the profile's host and container deployments; W cases run on its single-host container tuple. Use actual PostgreSQL through EF Core and actual filesystem storage where those layers are named. A simulated fault can control timing; a mock store cannot qualify a provider.

Names such as `article`, `contact-1`, `manifest-1`, `revision-1` and module A are synthetic fixture identities. Numbers in cases are explicit test inputs, not new product limits. A limit or policy selected for a fixture must be advertised and supported by the implementation; record its actual value before running. An unavailable required policy or dependency is an unresolved qualification gap, never a silent substitution. Recorded release budgets and supported-version ranges are inputs to qualification, not values inferred from a passing test.

For reference API cases, enumerate every advertised .NET and TypeScript client mode before running. For W, enumerate only the endpoints used by the minimal Admin and Razor delivery. W uses one site and one culture, installed declarative renderers, filesystem Files and its selected email transport. It claims no search, localization, engagement, newsletter, GraphQL, payment, commerce, distributed scale or complete CMS qualification. Negative capability-selection checks do not qualify the rejected capabilities.

A security dependency is satisfied only by evidence for the Builder's security cases on the same exercised scope and tuple. All actors, tenant/site contexts, named destinations and media eligibility that the non-security cases use are prequalified fixtures from that work. If such evidence is unavailable, record the dependency as unmet; do not invent a security implementation or report the dependent case as qualified.

Security cases follow the same fixture rules. The security thresholds, windows and lifetimes they name, such as a freshness bound, a throttling threshold or a session lifetime, are the release's declared values, recorded before running, rather than values a case selects. Fixture accounts, tenants, decoy rows, sentinel values and forged identifiers are synthetic test inputs. Evidence records secrets, cookies and credential values only by form, length or digest, never by value.

Some security cases assume fixtures that the release must provide; an absent one is an unresolved qualification gap for the case that needs it, never a substitution. On the reference profile, the G-02 fixture module is assumed to declare an integration endpoint, a bulk update, a batch import and a direct-SQL report query (AC-003-P01, AC-003-R01, AC-004-N02). On the website profile, the minimal Admin's recovery actions are assumed to include interrupted-import reconciliation and held-effect release (AC-003-N04). Because the website profile has one tenant and one site, its AC-004 cases write decoy rows, files and cache entries that carry absent tenant and site identifiers directly to PostgreSQL, the Files root and the cache. Decoys show that no W path reads foreign-scope data, but they cannot distinguish a predicate on the configured tenant from one on the resolved tenant, because both exclude the decoys; the reference cases, with two real tenants, cover that distinction.

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

[AcceptanceCorpusTests.cs](../../tests/Modularyn.Repository.Tests/AcceptanceCorpusTests.cs) checks shape, ids, the assigned family/profile scope, all four types per family/profile, requirement mappings, vocabulary membership, profile scope keys, control citations against the control map and complete Markdown agreement. In-memory rejection fixtures assert their particular diagnostics. A format-level fixture verifies that every security-family identity uses the same structural contract. These checks establish consistency, not semantic completeness or execution evidence.

Runtime acceptance evidence remains a separate obligation tied to the exact release and tuple. Review the corpus when profiles, requirements, provider contracts, threat models or scope decisions change. A control-map revision that adds a control to a security family on an assigned profile, or changes a cited control's profiles, needs the matching case change in the same reviewed revision.
