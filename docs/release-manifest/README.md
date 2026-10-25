# Release manifest, format version 1

A release manifest records the capability claims for one release and edition.
Documentation, selectors and diagnostics derive their wording from this
record (PRD-004). A capability is always bounded by its stated scope and exact
profile; a passing tuple supplies no evidence for another tuple (PRD-008).

The [draft 2020-12 schema](../../schemas/release-manifest/release-manifest.schema.json)
has identity `urn:modularyn:release-manifest:1`. Validation has two parts:
the schema checks shapes and local state requirements; semantic validation
checks repository references, tuple consistency, edition placement, dates and
the exact qualified subset. Passing the schema alone is insufficient.

## Record identity and editions

| Field | Contract |
|---|---|
| `format` | Exactly `modularyn.release-manifest` |
| `formatVersion` | Integer `1`, independent of the product version |
| `kind` | `release` or `illustration` |
| `version` | Exact SemVer 2.0.0, including any prerelease and build metadata |
| `sourceCommit` | Exact lowercase 40-digit source commit |
| `channel` | `stable`, `preview` or `nightly`; stable has no prerelease, and the other channels require one |
| `edition` | `community`, `standard` or `pro` |
| `license` | Source license for this edition's own packages, as below |
| `qualifiedSubset` | A public subset label and its exact capability/profile members |
| `packages` | Exact NuGet ids, SemVer versions and owning editions; an id occurs once |
| `profiles` | Nonempty object keyed by a profile declared in `docs/profiles/` |
| `capabilities` | Nonempty object keyed by identifiers in `docs/coverage/register.json` |

Community uses `Apache-2.0`. Standard and Pro use
`PolyForm-Noncommercial-1.0.0` for the same paid sources; commercial use
requires a separate license. Standard covers one person, Pro covers teams.
Community packages retain Apache-2.0 when included in a paid edition.
The edition field does not change a package's source license.

A Community manifest contains only Community packages and capabilities.
A Standard manifest may include Community and Standard entries; a Pro
manifest may include Community and Pro entries. A `Modularyn.Pro.*` package
must carry the manifest's paid edition. Other packages carry `community`.
Capability editions agree with the coverage register's Community/paid
placement; the register remains a planning inventory, not release evidence.
Package ids obey the [names and layout contract](../names-and-layout.md).

## Profile tuples

Every profile has all seven components, with no implicit defaults:

| Component | Representation |
|---|---|
| `moduleVersions` | Nonempty array of package `id` and exact `version` pairs from `packages`; ids are unique within the profile |
| `runtime` | Runtime `name` and exact numeric `version` |
| `database` | Database `name` and exact numeric `version` |
| `uiProfile` | A profile `name` and nonempty `packages` list drawn from this tuple's module versions |
| `tenancyMode` | Explicit lowercase profile term |
| `deploymentTopology` | Explicit lowercase profile term |
| `providers` | Nonempty list of unique `role` entries, each with a `package` and exact `version` from this tuple |

Runtime and database versions have two to four numeric components, without
ranges or aliases. Module and provider package versions use SemVer. The
tuple's selections must fit the referenced profile definition. The examples
choose the single-host container variant of `reference` and the container
deployment of `website`; they make no host-deployment or scale claim.

A capability has one `state`, its owning `edition`, a nonempty `profiles`
list, a bounded `scope` and a `publicClaim`. All profile references resolve
inside this manifest. Different states or scopes for different tuples use
separate manifests; one entry never silently merges different states.

## State and public wording rules

| State | Permitted `publicClaim` | Additional requirement |
|---|---|---|
| `Intended` | `none` or `planned` | Describes intent |
| `Designed` | `none` | Contract exists; no implementation claim follows |
| `Implemented` | `none` | Implementation alone does not qualify a profile |
| `Preview` | `none` | Preview wording carries no supported claim |
| `Qualified` | `none` or `supported` | Nonempty `evidence`, covering every applicable profile exactly once |
| `Deprecated` | `none` or `supported` | Valid calendar date `supportEndsOn`; supported only through that date |
| `Unsupported` | `none` | Nonblank `reason` |

`publicClaim` is a request for public wording, checked against the state;
it is not another state. `planned` means `Intended`. `supported` is available
only for a `Qualified` profile or a `Deprecated` profile within its support
period. `none` adds no planned or supported wording; the canonical state and
scope can still be displayed. Missing capabilities convey no support claim.

Each evidence entry has a `profile` and nonempty `references` list. Evidence
is bound to that exact tuple, the enclosing release version and source
commit. References must identify the corresponding retained results; a
nonempty reference is not proof that those results passed. Repository tests
check the contract, while qualification inspects the referenced results.
Evidence entries cannot name an unrelated profile or duplicate a profile.

`supportEndsOn` is an inclusive UTC calendar date. A consumer supplies the
current UTC date when checking a supported claim, including when rereading
an older manifest. It must not reuse the manifest's original validation date.
Expired Deprecated entries remain valid with `publicClaim: "none"` and must
not be described as supported. Tests use a supplied date to check the day
before, the final day and the first day after support ends deterministically.

## Qualified subset and compatibility

REL-005 requires a release to name the qualified subset it ships. The
`qualifiedSubset.label` accompanies its public release name and comparison
wording. `members` contains each `{capability, profile}` pair in a `Qualified`
entry exactly once, including entries whose `publicClaim` is `none`.
Deprecated entries are not new qualifications. No missing, extra or repeated
pair is allowed. An empty qualified subset uses the exact label
`No qualified capabilities`. A subset label never implies complete design,
implementation or support coverage.

Version 1 rejects unknown fields, unknown states and unsupported format
versions. Consumers must refuse an unknown version before making claims.
Adding a field, state, tuple component or edition changes this closed
contract and requires a new integer format version and schema identity.
Changing meaning, removing a field or weakening a validation rule also
requires a new format version. Editorial clarifications that preserve every
accepted record and its meaning may keep version 1.

Under REL-006, an incompatible published format change must document its
migration strategy and retain fixtures for supported prior versions. A
migration preserves release identity and evidence scope; it cannot promote
a state, invent evidence or extend a support period. Product SemVer and
format versions evolve independently. Version 1 is the initial format and
has no predecessor to migrate.

## Illustrations and validation

[Community](examples/community.json), [Standard](examples/standard.json)
and [Pro](examples/pro.json) are illustrative test data, not real releases.
All use `kind: "illustration"` and `0.1.0-example.1`. The `example` prerelease
identifier is mandatory for illustrations and forbidden for real releases.
Their source commit identifies the repository baseline used to construct
the examples; it does not assert a product build at that commit.

The examples exercise all seven states, both existing profiles and every
edition. Their Qualified states and symbolic
`urn:modularyn:example:evidence:content-types:reference` and
`urn:modularyn:example:evidence:content-types:website` references exercise
profile binding only. They record no qualification runs. A release cannot
use these illustration references. Renderers identify illustration records
as examples and must not use them as public product claims.

Repository tests validate every example against the schema and semantic
contract, reject individual invalid mutations with their specific reasons,
and check the names companion against the guide and existing projects.
The schema evaluator uses only the local draft 2020-12 keywords in this
schema; unknown keywords and nonlocal references fail validation. It requires
no new package dependency or network retrieval. The Builder runs locked
restore, Release build and tests; CI repeats those checks on Linux, Windows
and macOS at the exact head. Writing these tests does not establish those
outcomes or qualify any application capability.
