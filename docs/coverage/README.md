# Coverage register

[register.json](register.json) is the 2026-10-25 planning baseline for **Modularyn Product Design**, version 1.0, dated 2026-10-21. It contains 419 entries: 218 requirements and module contracts, 106 foundation rows, 63 tooling rows and 32 acceptance families. Every entry starts at **Intended**. An entry records planned responsibility and qualification work; it establishes no implementation, successful test, supported profile or release claim.

The public register carries identifiers, short titles and planning fields. The private design remains the authority for behavior. Its SHA-256 identifies the exact source used without reproducing its prose. Edition planning also follows decisions `01M5RN3J51DWQPW045D23MSMMC` and `01M5RN3JHEJZQZ9P5NZNQZMQX5`; delivery planning follows `01M5RN3JB8J87DGHHP4DSSWR3X`.

## Format

The root has exactly `version`, `design`, `date` and `entries`. `version` is the integer `1`. `design` contains `title`, document `version`, document `date` and the lowercase hexadecimal `sha256` of the pinned design. The register's `date` is an ISO calendar date on or after the design date. Dates use `YYYY-MM-DD`.

Entries follow the order of their definitions in the design: PRD through JRN, REL-001 through REL-004, the AC corpus, REL-005 through REL-008, then Appendices F and T. The acceptance corpus appears inside the release section, so REL entries are deliberately separated in the array.

| Field | Meaning and allowed values |
| --- | --- |
| `id` | Exactly one identifier from the ranges below; no additions or duplicates. |
| `kind` | `requirement`, `foundation-row`, `tooling-row` or `acceptance-family`, as determined by the identifier. |
| `family` | The identifier prefix: PRD, ARC, FND, MOD, CMS, UI, DEV, LOW, OPS, JRN, REL, F, T or AC. |
| `title` | A short identifying label, at most six words and 64 characters. |
| `owner` | One owning Modularyn subsystem from the vocabulary below. |
| `state` | `Intended` throughout this baseline. |
| `edition` | `community` or `paid`, following the edition decisions. Acceptance families use `community`. |
| `gate` | Planned qualification checkpoint: `G-01` through `G-09`, or `W` for an explicitly scoped website capability. |
| `acceptance` | Unique AC identifiers in numeric order; empty for acceptance families. |
| `requirements` | An inventory row's normative requirement identifiers in source order; empty for other kinds. |
| `evidence` | One original sentence describing planned qualification evidence, between 8 and 60 words and at most 400 characters. |

Unknown fields are rejected to keep the public format bounded. Values cannot be blank or padded with whitespace. Titles and evidence are single-line text; neither is a place for source paragraphs or machine-specific locations.

The owner vocabulary is: AI, API, Accounts, Admin, Audit, Authorization, Authorization Server, Automation, Caching, Capabilities, Communications, Configuration, Content, Content and Engagement, Diagnostics, Documentation, Events, Files, Forms, Framework composition, Generator, Identity, Identity Migration, Jobs, Limits, Localization, Low-code, Messaging, Operations, Payments, Persistence, Privacy, Publishing, Release governance, Runtime, SDK toolchain, Search, Security, Studio, Templates, Tenancy, Tenancy and Subscriptions, and Tooling. Owners identify the subsystem accountable for collecting evidence; collaborators remain part of the detailed qualification plan. Module-contract owners retain the design's ownership names.

## Mapping conventions

Requirement acceptance lists expand the design's requirement-to-evidence table. Inventory lists reproduce the individual normative mappings in the appendices, then take the union of those requirements' acceptance families and add `AC-032`, the design's final benchmark audit. They retain every mapped family even when a family qualifies a broader group than one row.

The design describes journey mappings by scenario rather than enumerating AC identifiers. This baseline makes that planning interpretation explicit:

| Journey | Acceptance families |
| --- | --- |
| JRN-001 | AC-001, AC-008, AC-024, AC-025, AC-028 |
| JRN-002 | AC-002, AC-003, AC-004, AC-006, AC-010, AC-011, AC-029 |
| JRN-003 | AC-009, AC-014, AC-016, AC-017, AC-018 |
| JRN-004 | AC-003, AC-004, AC-006, AC-007, AC-020, AC-021, AC-028 |
| JRN-005 | AC-003, AC-004, AC-009, AC-018, AC-022 |
| JRN-006 | AC-004, AC-010, AC-015, AC-029 |
| JRN-007 | AC-005, AC-006, AC-011, AC-028, AC-029 |

REL-001 through REL-008 map to all 32 families because release governance reviews all applicable cases. These expanded mappings are planning choices for Builder review; the tests preserve them alongside the explicit source mappings.

Gates assign the entry's capability to the relevant delivery checkpoint. Cross-cutting contracts are exercised again as more profiles reach later gates. A framework contract can first qualify at G-02 while a particular adapter or administrative workflow qualifies later. An inventory row therefore does not automatically inherit the latest gate of all its referenced requirements, and its gate does not certify all those requirements in full.

| Gate | Qualification focus |
| --- | --- |
| G-01 | Planning and contract baseline |
| G-02 | Secure foundation application |
| G-03 | Administration and commercial lifecycle |
| G-04 | Complete CMS |
| G-05 | Remaining modules and integrations |
| G-06 | Tooling and frontend coverage |
| G-07 | Governed low-code and AI authoring |
| G-08 | Production and maintenance readiness |
| G-09 | Comprehensive coverage audit |
| W | Website profile subset |

The setup decision places W between G-02 and G-03 and requires its exact terms to be recorded through REL-007. No exact website subset is defined by this brief, so this baseline keeps the full CMS contracts at G-04 and assigns no entries to W. An approved subset can later qualify website capabilities without implying completion of their full CMS contracts.

Community includes the framework, shared tooling, ordinary application development, payments and the complete CMS. Paid entries cover the SaaS commercial lifecycle, Studio, governed low-code and AI authoring, AI management and workflow. Shared runner, telemetry, SDK and MCP contracts remain Community; rows specifically describing Studio or paid authoring surfaces are paid. AI foundation adapters remain Community. A benchmark's original commercial label does not determine Modularyn's edition. Acceptance-family entries describe the common qualification corpus and use Community even when a scenario exercises paid modules.

## States and evidence

The seven canonical states are:

| State | Evidence needed for the claim |
| --- | --- |
| Intended | Named responsibility and a qualification plan. |
| Designed | Reviewed contracts, risks and failure behavior for a stated profile. |
| Implemented | Working artifacts with component-level results. |
| Preview | A published, bounded scenario with recorded limitations. |
| Qualified | Passing evidence for the complete advertised release and profile. |
| Deprecated | A supported transition plan with retained compatibility evidence. |
| Unsupported | A documented profile restriction and its reason. |

A state changes only after its owner records the implementation artifacts, exact versions and profiles, verification date, scenario results, documentation and limitations supporting that state, and the responsible review accepts the evidence. A plan sentence or the existence of a test cannot establish qualification. Other profiles retain their own evidence requirements.

This dated baseline deliberately rejects every state except Intended. Adopting another state requires a reviewed register and validation-policy revision with the evidence references; changing an enum alone is insufficient. Changes to the operator's design or baseline decisions require recorded decisions. This register does not amend the master design.

## Counts

| Family | Inclusive range | Kind | Count |
| --- | --- | --- | ---: |
| PRD | PRD-001–PRD-008 | requirement | 8 |
| ARC | ARC-001–ARC-018 | requirement | 18 |
| FND | FND-001–FND-040 | requirement | 40 |
| MOD | MOD-001–MOD-032 | requirement | 32 |
| CMS | CMS-001–CMS-024 | requirement | 24 |
| UI | UI-001–UI-014 | requirement | 14 |
| DEV | DEV-001–DEV-025 | requirement | 25 |
| LOW | LOW-001–LOW-021 | requirement | 21 |
| OPS | OPS-001–OPS-021 | requirement | 21 |
| JRN | JRN-001–JRN-007 | requirement | 7 |
| REL | REL-001–REL-008 | requirement | 8 |
| **Requirements** | | | **218** |
| F | F-001–F-106 | foundation-row | 106 |
| T | T-001–T-063 | tooling-row | 63 |
| AC | AC-001–AC-032 | acceptance-family | 32 |
| **Total** | | | **419** |

## Checks

[CoverageRegisterTests.cs](../../tests/Modularyn.Repository.Tests/CoverageRegisterTests.cs) reads the register through the existing repository-file discovery helper. Its expectations contain the design's exact family ranges, acceptance map and all 169 inventory mappings independently of the register data. The private design is not needed in CI.

The checks reject missing, duplicate and extra identifiers, substitutions that preserve the total count, incorrect order, missing or extra fields, invalid metadata, unsupported owners or gates, non-Intended states, edition-policy drift, empty evidence and invalid references. Requirement mappings must match the expanded source map; inventory mappings must match their source rows and inherit the complete acceptance union plus AC-032. In-memory mutations exercise these rejection paths without writing invalid fixtures. A line-ending case checks portability across platforms.

The existing [CI workflow](../../.github/workflows/ci.yml) discovers these tests in `Modularyn.Repository.Tests` on Linux, Windows and macOS. It checks out the exact head and performs locked restore, Release build and tests. The separate secret-scan workflow checks the same head's Git history. No new package or workflow is needed.

The Builder must run locked restore, Release build and tests, sample identifiers, owners, gates and mappings against the pinned design, and verify all required CI checks at the same exact commit before acceptance. Structural checks cannot establish that a proposed owner or gate is the right planning choice, or that any product capability has been implemented. This document records the check contract and makes no claim that those deferred checks have passed.
