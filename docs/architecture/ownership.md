# Module and source ownership

This guide applies ARC-001, ARC-003, ARC-012, DEV-005 and DEV-006 to the
[names and layout contract](../names-and-layout.md). The [dependency
guide](dependencies.md) defines the executable reference boundary. These are
repository contracts; changes to the master design require a recorded decision.

## One module, one owner

A module is the family plus the single module segment before its role suffix:
`Modularyn.Modules.Files` and `Modularyn.Cms.Content` are different owners.
Standard and Pro share the same paid source and owner; an edition does not
create a second implementation. Sharing a database, process or deployment does
not merge ownership.

| Artifact or responsibility | Authority and home |
|---|---|
| Write model, invariants and internal domain types | The owning module's `.Domain`; no other module reads or updates its tables or uses its internal types |
| Persistence mapping, migrations and migration history | The same module's `.Persistence.{Provider}`; each provider has an identifiable migration sequence and history owned by that module |
| Public requests, responses, service interfaces and integration event definitions | The owning module's `.Application.Contracts`; events have explicit schema versions and compatibility rules |
| Use cases and cross-module coordination | `.Application`, using another owner's public contracts or versioned events |
| Permission, feature and setting definitions | The module declares and versions its own definitions in its public contracts and module manifest |
| Export and deletion contributors | The owning module defines the contract, implements its contribution and reports its outcome; a coordinator does not take over the data |
| Transport, client and presentation code | `.HttpApi`, `.Client` and `.UI.{Adapter}` implement or consume public contracts without taking ownership of the domain |

The braces in adapter suffixes are the names contract's single-segment grammar
variables. They are replaced by the actual provider or adapter name. Integration
events live in `.Application.Contracts`; there is no additional event-package
suffix. Public contract types cannot expose domain entities or persistence types.
Event consumers depend on the published contract version, not the producer's
tables or implementation. Deletion and compatibility behavior are part of the
event contract.

A module's manifest declares required and optional dependencies with compatible
versions, backend contracts, providers, UI contributions, owned definitions,
migrations, background handlers and health checks. An optional dependency obeys
the same reference rules as a required one. Composition must resolve dependency
cycles, version conflicts, capability conflicts, schema effects and exposed
routes before adding, removing, updating, enabling or disabling a module.
Removing persisted data requires a retention and export plan (DEV-005).

A read model that copies another module's data belongs to the consumer. Its
explicit synchronization and retention contract identifies the source owner,
public contract or event and version, copied fields, refresh or replay procedure,
ordering and duplicate handling, tolerated lag, rebuild procedure, deletion
propagation, retention period and disposal trigger. Review that contract with
both owners before introducing the copy. It never grants direct table access or
ownership of the source write model.

## Product homes keep their existing boundaries

| Family or product home | Owned work |
|---|---|
| `Modularyn.Framework.*` and its `Sdk` subtree | Shared application infrastructure and public extension contracts; no business module implementation |
| `Modularyn.Modules.*` | Community business capabilities, each with its own role packages |
| `Modularyn.Cms.*` | Content and editorial capabilities, with the same module ownership rules |
| `Modularyn.UI.*`, including `Admin` | Presentation and the administrative shell; domain contracts stay with their modules |
| `Modularyn.Connectors.*` | Provider integration code; the provider can own external facts while the consuming module owns its local state |
| `Modularyn.Pro.*`, including `Studio` | Paid capabilities in `modularyn-pro`; Studio edits and composes the same artifacts used by ordinary tools |
| `Modularyn.Tooling.*`, including `Generator` | Command implementation and generation; the CLI package is `dotnet-modularyn` with assembly `Modularyn.Tooling.Cli` |
| `Modularyn` | The front-door composition package, without duplicating any module's contracts or data |

The names contract supplies the exact product homes and source directories.
Admin, SDK, Generator and Studio retain their more specific homes. Studio is a
product subtree, not a forced business-module role. Community lives in
`modularyn`; paid sources live in `modularyn-pro`. Paid consumers use published
Community packages with exact declared versions and a matching dependency lock.
Switching a package to source preserves its package version and source revision
provenance and records that future upgrades require source merging. Such a
switch cannot introduce a cross-repository project reference from paid code to
Community.

## One authority per artifact

Each artifact has one declared authority. The declaration identifies its owner,
artifact identity, authority kind and source identity, version or revision,
generation policy and customization seams. An artifact cannot simultaneously be
owned by a definition and by independently edited generated code.

| Authority kind | Change location | Derived output |
|---|---|---|
| Authored source | The owning source file and its reviewed revision | Compiled code or an initial generated scaffold explicitly handed over to its author |
| Versioned definition | The owning definition and its version | Generated source, schemas or other projections interpreted with declared tool and template versions |
| External service | The external contract and its versioned observations | Local adapter output or synchronized projections whose refresh and retention contract names the service authority |

Ordinary developer-managed application code is authored source. CMS content and
schemas and low-code definitions remain separately versioned artifacts. A
generated business-rule scaffold becomes author-owned only through an explicit
handover; subsequent regeneration treats that file as an owned customization
seam. A generated projection of a definition remains generator-owned.

Every generated file identifies the generator and its version, template and
template version, authoritative input and version, ownership policy and supported
customization seams. Where the format cannot carry this metadata, a versioned
companion maps each exact output path to it. Seams name separate author-owned
files, partial types, extension modules or stable hooks; an undocumented edit to
generated output is a conflict, not a new authority.

Regeneration compares the previous generated output, current files and proposed
output. It previews the diff before changing files, preserves declared seams and
reports conflicts without overwriting unknown edits. Identical declared inputs,
generator and template versions produce equivalent output. The generated output
must build and test with ordinary tools, carry initialization instructions,
sample configuration and supported run profiles, and keep production code
separate from sample data (DEV-006). An authority transfer records the old and
new authorities and the affected artifacts before further edits; it never
silently creates two editable sources.

## What the architecture checks establish

The repository tests enforce project identity, declared references and cycles,
including the boundary around `.Application.Contracts`. A project graph cannot
prove table ownership, inspect contract type members, establish event version
compatibility, or validate a generator's treatment of customization seams.
Those responsibilities remain review requirements and require implementation
tests when the corresponding modules and generators exist. This baseline adds
no product projects or generation implementation.
