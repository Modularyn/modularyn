# Dependency rules

[The companion](dependency-rules.json) is the executable reference policy.
The tables below are checked against it by the repository architecture tests.
[Ownership](ownership.md) defines the data and source boundaries behind these
rules. They apply ARC-001 and ARC-003 without changing the
[names and layout contract](../names-and-layout.md).

## Identity and discovery

Every project in `Modularyn.slnx` must appear exactly once and match the complete
repository inventory of `.csproj` files. Package id, assembly, root namespace,
project filename and source directory follow the names companion. A missing or
unlisted project fails discovery; an empty product set is valid.

Family is the names companion's matching package prefix. `Modularyn` has the
special family `FrontDoor`; `dotnet-modularyn` has family `Tooling` and assembly
`Modularyn.Tooling.Cli`. Module identity includes the family and the single module
segment. Its role is the exact remaining suffix from the names companion.
Studio's product subtree has no module role, even if a name ends in `.Domain`.
Other non-module families have no module role. Standard and Pro are one `paid`
dependency edition, derived only from `Modularyn.Pro.*`; every other family and
both exact package identifiers are `community`.

Product paths are `src/` plus the names companion's family directory, then the
assembly name as directory and filename. The front-door path is
`src/Modularyn/Modularyn.csproj`. Repository identity must match the names
companion's source repository. Test projects use the existing test layout and
derive their edition from their repository. Tests may inspect product internals;
edition boundaries and cycle checks still apply. Products cannot reference tests.

## Combining the rules

Every edge must satisfy every applicable rule. An allowed family does not bypass
a role rule. Within a module, only the listed role edges are allowed. Across
modules, only the listed consumer roles can use `.Application.Contracts`, which
also contains versioned event contracts. There is no new event suffix. A
non-module consumer can reference a module only at `.Application.Contracts`.

The support-family table applies when a module references a non-module package.
Domain and application contracts additionally limit Framework targets to the
listed suffixes. These suffixes describe Framework packages only; they do not
introduce new module roles. Domain rejects the listed adapter roles, families,
identifier segments and platform dependencies regardless of other allowances.
Application contracts also reject those platform dependencies, so DTO and event
definitions cannot acquire transport, presentation or persistence machinery.
Adapters reference inward; HTTP and persistence never reference each other.

Community never references a paid package or project, including from the
front-door package, CLI, repository utilities or tests. Paid projects consume
Community only through published packages, never through project references.
A paid-to-Community package declaration is a literal stable `major.minor.patch`
version, with no range, floating component, expression or omitted value. The
dependency lock must resolve that declared version under locked restore; a
literal NuGet version alone is a minimum, so the lock is part of the pin.
The offline graph check validates the declaration; the existing lock checks and
locked restore validate resolution. Package publication is a release
fact and cannot be established by this offline source check.

<!-- dependency-policy:start -->

### Enforced policy tables

Authority: `dependency-rules.json`, format `1`. Generator: `ArchitecturePolicy`,
version `1`; template: `dependency-policy`, version `1`. Ownership policy:
regenerate only this marked section, preview its diff and preserve the authored
prose outside it. Edit the companion to change these tables.

An empty target set means no reference is allowed in that category.

#### Edition references

| Source edition | Target edition | Allowed reference kinds |
|---|---|---|
| `community` | `community` | `ProjectReference`, `PackageReference` |
| `community` | `paid` | None |
| `paid` | `community` | `PackageReference` |
| `paid` | `paid` | `ProjectReference`, `PackageReference` |

#### Family references

| Source family | Allowed target families |
|---|---|
| `Framework` | `Framework` |
| `Modules` | `Framework`, `Modules`, `Cms`, `UI`, `Connectors` |
| `Cms` | `Framework`, `Modules`, `Cms`, `UI`, `Connectors` |
| `UI` | `Framework`, `Modules`, `Cms`, `UI` |
| `Connectors` | `Framework`, `Modules`, `Cms`, `Connectors` |
| `Pro` | `Framework`, `Modules`, `Cms`, `UI`, `Connectors`, `Pro`, `Tooling` |
| `Tooling` | `Framework`, `Modules`, `Cms`, `UI`, `Connectors`, `Tooling` |
| `FrontDoor` | `Framework`, `Modules`, `Cms`, `UI`, `Connectors` |

#### References within one module

| Source role | Allowed target roles |
|---|---|
| `domain` | None |
| `application-contracts` | None |
| `application-services` | `domain`, `application-contracts` |
| `persistence-adapter` | `domain` |
| `http-endpoints` | `application-contracts` |
| `client` | `application-contracts` |
| `ui-adapter` | `application-contracts`, `client` |

#### References to another module

| Source role | Allowed target roles |
|---|---|
| `domain` | None |
| `application-contracts` | `application-contracts` |
| `application-services` | `application-contracts` |
| `persistence-adapter` | `application-contracts` |
| `http-endpoints` | `application-contracts` |
| `client` | `application-contracts` |
| `ui-adapter` | `application-contracts` |

#### References from a module to support packages

| Source role | Allowed non-module families |
|---|---|
| `domain` | `Framework` |
| `application-contracts` | `Framework` |
| `application-services` | `Framework` |
| `persistence-adapter` | `Framework`, `Connectors` |
| `http-endpoints` | `Framework` |
| `client` | `Framework` |
| `ui-adapter` | `Framework`, `UI` |

#### Classification and boundary values

| Setting | Value |
|---|---|
| `namesContract` | `docs/names-and-layout.json` |
| `moduleFamilies` | `Modules`, `Cms`, `Pro` |
| `nonModulePrefixes` | `Modularyn.Pro.Studio.` |
| `eventContractRole` | `application-contracts` |
| `domainFrameworkSuffixes` | `.Domain`, `.Contracts`, `.Abstractions` |
| `contractsFrameworkSuffixes` | `.Contracts`, `.Abstractions` |
| `domainForbiddenRoles` | `persistence-adapter`, `http-endpoints`, `client`, `ui-adapter` |
| `domainForbiddenFamilies` | `UI`, `Connectors`, `Tooling`, `FrontDoor` |
| `domainForbiddenSegments` | `UI`, `Persistence`, `HttpApi`, `Client` |
| `domainForbiddenPlatforms` | `Microsoft.AspNetCore`, `Microsoft.Maui`, `Microsoft.WindowsDesktop`, `Microsoft.JSInterop`, `Microsoft.EntityFrameworkCore`, `System.Net.Http`, `System.Windows.Forms` |
| `domainForbiddenProperties` | `UseWPF`, `UseWindowsForms`, `UseMaui` |
| `domainForbiddenSdks` | `Microsoft.NET.Sdk.Web`, `Microsoft.NET.Sdk.Razor`, `Microsoft.NET.Sdk.WindowsDesktop` |
| `paidCommunityVersionPattern` | `^(?:0\|[1-9][0-9]*)\.(?:0\|[1-9][0-9]*)\.(?:0\|[1-9][0-9]*)(?![\s\S])` |
| `cycleScopes` | `package`, `module` |

#### Diagnostics

| Code | Reason |
|---|---|
| `DEP001` | Project identity and solution membership must match the names contract. |
| `DEP002` | References must be literal and locally discoverable. |
| `DEP003` | Community must not reference paid modules. |
| `DEP004` | Paid projects must consume Community through packages, never project references. |
| `DEP005` | Paid references to Community require an exact stable version. |
| `DEP006` | The source family does not allow the target family. |
| `DEP007` | Domain must not reference UI, HTTP, client or persistence adapters. |
| `DEP008` | The source role does not allow this role within its module. |
| `DEP009` | Another module is accessible only through allowed application or event contracts. |
| `DEP010` | The source role does not allow this support package. |
| `DEP011` | Dependency cycles are forbidden. |
| `DEP012` | Product projects must not reference test projects. |
| `DEP013` | The dependency companion, guide and names contract must agree. |

<!-- dependency-policy:end -->

## Static reference surface

The checker reads every `ProjectReference` and `PackageReference`, including
conditional items on platforms other than the running platform. Conditions
cannot hide an edge. It also reads `FrameworkReference` and the listed UI build
properties and SDKs for domain and application-contract projects. Platform
prefixes match either the exact identifier or a following dot, not arbitrary
substrings. Other external packages are outside the Modularyn family graph.

Package versions come from the unique unconditional central `PackageVersion`.
Explicit `Version` attributes or elements are understood in isolated graph
fixtures; the repository's existing conventions require central versions and
prohibit project version overrides. Ambiguous declarations, dynamic item
expressions, wildcard or list includes, item updates/removals, explicit imports,
custom SDKs and custom targets fail discovery rather than being ignored.
References in auxiliary `.props` and `.targets` files are rejected: all direct
reference edges belong to their `.csproj`. Only the ordinary `Microsoft.NET.Sdk`
imports supported by the repository conventions are implicit. No MSBuild
evaluation or network access occurs in this check.

Project references resolve relative to the declaring project using either slash
style. They must target a discovered local project; a paid reference that points
outside its repository at a Community project still receives the paid boundary
diagnostic. Assembly `Reference` items and COM references are rejected because
they could conceal a Modularyn edge from the declared package/project graph.

Cycles are checked across all discovered package identities, including package
edges whose target has a local project. A second graph collapses each module's
role packages to its module identity, excluding internal module edges; this also
rejects mutually dependent modules whose individual contract assemblies form an
acyclic graph. Self references and indirect cycles fail. External package
transitives absent from the repository, reflection, runtime loading and type
usage are not inferred from source XML; this check does not claim to validate
them. ARC-001 also requires runtime composition to reject incompatible versions
and cycles in its complete manifest graph.

## Enforcement

The repository test checks the real inventory and policy. New rejection tests
construct in-memory projects and references through the same discovery and graph
checker, assert that their legal starting graph passes, then assert the specific
diagnostic after the forbidden edge is introduced. They cover both forms of
Community-to-paid references, every role matrix cell, family restrictions,
cross-module implementation access, paid project references and version failures,
platform adapters, hidden reference declarations, package and module cycles, and
policy/prose drift. Fixtures never create product projects on disk.

CI restores locked dependencies, builds Release and runs the repository test
project on Linux, Windows and macOS at the exact checked-out head. A
forbidden-reference test passes only when the validator reports its asserted
diagnostic. Execution evidence applies to the tested commit; source review
alone does not establish that the suite passes.
