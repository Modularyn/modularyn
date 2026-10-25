# Threat models

These documents are the OPS-001 threat models for the first two qualification targets: the G-02 foundation slice of the [reference profile](../../profiles/reference.md) and the [website profile](../../profiles/website.md) at gate W. Each model documents its trust boundaries, assets and their classification, privileged operations, external inputs, threats and abuse cases, and the controls that answer them. Every control has a named owning subsystem, the coverage-register requirements it implements and its planned acceptance evidence.

**These models are planning contracts.** They state which controls later implementation must provide and which evidence must later show them working. They are not evidence that any control exists, that any test has run or passed, or that a gate, profile or release is qualified. No control in this directory has been implemented. Requirement state remains in the [coverage register](../../coverage/register.json), where every entry is Intended; nothing here changes a state, gate or owner there.

| Model | Scope | Document |
|---|---|---|
| `g02-foundation` | The secure foundation application that G-02 qualifies on the reference profile | [G-02 foundation slice](g02-foundation.md) |
| `website-profile` | The public Razor site, minimal Admin and supporting operations that gate W qualifies | [Website profile](website-profile.md) |

[controls.json](controls.json) is the machine-readable control map shared by both models. The design remains the authority for behavior; these documents cite its requirements by identifier only.

## Method

Each model starts from its profile's tuple and capability scope, then records what it assumes about the environment. It decomposes the system at **trust boundaries**: places where data or control passes between parties that warrant different trust, such as an anonymous visitor and the public site, or a queued job and the worker that runs it. The boundary inventory names the less trusted and more trusted side of each crossing.

For every boundary, the model lists threats by category:

| Category | Question asked at the boundary |
|---|---|
| Spoofing | Can a party claim an identity, tenant, host or origin it does not hold? |
| Tampering | Can data, code, configuration or ordering be changed without authority, including through duplication or replay? |
| Repudiation | Can a party act without a durable, attributable record? |
| Information disclosure | Can data reach someone outside its classification's audience? |
| Denial of service | Can a party exhaust capacity or leave the system unready? |
| Elevation of privilege | Can a party obtain authority it was not granted? |

**Abuse cases** complement the per-boundary threats. Each describes a person pursuing a goal across several boundaries: a malicious outsider, a legitimate user acting beyond their purpose, or an insider with real but limited authority. Abuse cases use the threat identifier space with the category `Abuse case`.

A threat describes an attacker's action and its consequence. It never contains an exploit procedure. Each threat names at least one control; each control states required behavior, belongs to one boundary and one owner, and points to requirements and evidence. The models finish with residual risks, which remain after the controls work as specified, and with what is out of scope.

The design's verification target is OWASP ASVS Level 2 where it applies, supported by a documented applicability assessment. These models identify where controls are needed for the included surfaces; the applicability assessment and its results are evidence planned under AC-031, not part of this directory.

Every model document has the same sections: Scope and assumptions, Trust boundaries, Assets and classification, Privileged operations, External inputs, Threats, Abuse cases, Controls, and Residual risks and out of scope.

## Identifiers

| Kind | Form | Defined in |
|---|---|---|
| Boundary | `TB-G02-` or `TB-W-` followed by two digits | The model's boundary inventory |
| Threat | `TH-G02-` or `TH-W-` followed by three digits | A row under a boundary heading, or in the abuse-case table |
| Control | `TM-G02-` or `TM-W-` followed by three digits | The model's control table and the control map |

The `G02` identifiers belong to the foundation model and the `W` identifiers to the website model. Identifiers are stable. They are never renumbered or reused; retiring a threat or control requires a reviewed revision that records why, and its number stays unused.

## Data classification

Each asset receives one confidentiality class and one integrity rating.

| Class | Meaning | Required handling |
|---|---|---|
| Public | Approved for anonymous readers, such as published revisions, public media and sitemaps | Protect integrity and availability; data becomes Public only through an authorized publication or policy decision |
| Internal | Operational information not meant for public surfaces, such as module inventories, versions, schema state and telemetry | Keep off anonymous responses; available to operators with a purpose |
| Confidential | Tenant and site business data that is not public, such as drafts, previews, unpublished media, import plans and job payloads | Authorize every read; exclude from public caches, sitemaps and discovery |
| Restricted | Personal data and security state, such as account records, contact submissions, permission grants and audit records | Confidential handling plus minimization, retention limits, redaction from telemetry and audited access (OPS-006, OPS-007, OPS-008) |
| Secret | Values that confer authority when disclosed, such as credentials, credential verifiers, keys, session cookies, recovery codes and signed grants | Never place in source, packages, logs, telemetry, exports or UI; resolve from authorized stores; support rotation and revocation (OPS-004) |

| Integrity | Meaning |
|---|---|
| Critical | An unauthorized change alters authorization, publication, routing, composition or recovery outcomes. Changes need authorization, concurrency checks and audit. |
| Standard | An unauthorized change harms the affected record without changing those outcomes. |

Derived data, including caches, renditions, indexes, exports and backups, inherits the highest class of its sources unless a named control reduces it. Personal data is at least Restricted. The class and rating names above are exact; the repository tests reject others.

## Control conventions

**Owner.** Each control has one owner from the coverage register's owner vocabulary, and that owner must be the register owner of at least one requirement listed for the control. The owner is accountable for collecting the control's evidence. Collaborating subsystems belong to the detailed qualification plan, not the map. Shared middleware never excuses a module owner from resource authorization or business-rule validation (OPS-001).

**Requirements.** Each control lists the register requirements it helps implement. Listing a requirement records traceability only; it does not change that requirement's gate, owner or state, and a control never satisfies a requirement by itself.

**Evidence.** Planned evidence has two parts. `acceptance` names acceptance families from the register, each of which the register must map to at least one of the control's requirements. `layers` names the independent acceptance layers of REL-002 that the evidence must include:

| Layer | Evidence it produces |
|---|---|
| `unit` | Domain rules, policies and architecture rules checked in process, with no claim about provider behavior |
| `real-provider-integration` | Behavior against the profile's actual providers: PostgreSQL through EF Core, the filesystem BLOB store and, where exercised, the release-selected email transport |
| `api-contract` | Endpoint and event contracts exercised over their real transport, including status codes, error contracts, headers, versioning and authorization metadata |
| `isolation` | Adversarial authorization and tenant or site isolation attempts across data and execution surfaces |
| `ui` | Browser workflows, including accessibility and security-relevant browser behavior |
| `migration` | Schema and release upgrade paths, including interruption and recovery |
| `operational-recovery` | Failure, restart, backup, restore and repair exercises on a deployed tuple |

REL-002's architecture checks are recorded under `unit` and its event contract tests under `api-contract`. In-memory providers and mocked gateways cannot produce `real-provider-integration` evidence. A family whose gate is later than the model's gate means the control's complete evidence arrives at that later gate; the model records the obligation now so earlier work does not contradict it.

**Profiles.** `profiles` names the release profiles whose qualification must produce the control's evidence: `reference`, `website` or both. A control that addresses a foundation threat names `reference`; a control that addresses a website threat names `website`. The website model reuses foundation controls on W's tuple, so most foundation controls name both profiles. A website control also names `reference` when it implements a CMS contract that the reference profile qualifies at G-04 on the same terms; a control for a W-specific restriction or procedure names only `website`. Naming a profile does not claim that the profile has reached the gate where the evidence is collected.

## Control map format

The root of [controls.json](controls.json) has exactly `version`, `date` and `controls`. `version` is the integer `1`. `date` is the ISO calendar date of the latest model revision. `controls` lists foundation controls and then website controls, each in numeric order.

| Field | Meaning |
|---|---|
| `id` | The stable control identifier |
| `title` | A short statement of the control, one line of at most 100 characters |
| `threats` | The threats the control addresses; each is defined in a model, and a website control addresses only website threats |
| `boundary` | The boundary where the control operates, from the same model as the control |
| `owner` | The owning subsystem, as described above |
| `requirements` | Register requirement identifiers |
| `evidence` | An object with exactly `acceptance` and `layers` |
| `profiles` | `reference`, `website` or both |

Unknown fields, duplicate properties, blank values and duplicate references are rejected. Version 1 has no implementation state: every control is planned. Recording implementation or qualification requires a reviewed format revision tied to the register's evidence process.

## Keeping the models current

OPS-001 makes these models living documents. Revise a model and its control map together, in one reviewed change, when:

- a module, provider, surface, deployment topology or profile enters or leaves the model's scope, or an included capability changes;
- a trust boundary, external input or privileged operation is added or altered;
- the coverage register changes an owner, gate, requirement or acceptance mapping that the map cites;
- the content-package format revises its rules, including the format's forbidden-content rules on which the website model relies;
- a security finding, failed test, incident or vulnerability report shows that a threat or control is missing, wrong or ineffective;
- a gate's qualification review or a release's security review begins. The model is reread against the exact tuple under review, and each residual risk is reduced or explicitly accepted again.

Revisions are security work, drafted and reviewed as such; the release security review examines the models again before any supported release. Removing a threat or control keeps its identifier unused, as described above. The map's `date` records the latest revision. Evidence that a control works belongs to the register's evidence process; it is never recorded by editing these documents.

## Checks

[ThreatModelTests.cs](../../../tests/Modularyn.Repository.Tests/ThreatModelTests.cs) reads these documents, the control map and the coverage register through the repository-file discovery helper. It checks that identifiers are unique, well formed and defined in the model that owns them; that every owner, requirement and acceptance family exists in the register, that each owner owns a listed requirement and that each family is mapped to one; that profiles and test layers come from their vocabularies; that every threat cited in the prose is defined and has a control in the map; that each threat row's controls equal the map's controls for that threat; that every map control is described and cited in the prose; that every boundary has threats and controls; and that each model has its required sections, valid categories and valid asset classifications. In-memory mutations exercise each rejection with its specific diagnostic.

Passing these checks shows only that the planning contract is internally consistent and traceable. It cannot show that a threat list is complete, that a control is the right one, or that any product behavior exists.
