# Modularyn editions

Community is the free foundation under [Apache-2.0](../../LICENSE), including
commercial use. It is complete for building and running applications and sites
without paid modules. It includes payments, tenancy, the complete CMS, the Admin,
public rendering, shared tooling and AI foundation adapters.

Standard is for one person. Pro is for teams and organizations. They license the
same paid modules: the SaaS commercial lifecycle, Studio, governed low-code and
AI authoring, AI management, and workflow. Their source distribution is for review
under the PolyForm Noncommercial License 1.0.0. Commercial use requires a license
that is not offered yet. This map states the operator's edition decisions; it
does not offer a commercial license or add terms to the published licenses.

Editions package one technical catalog. Requirements and qualification gates
remain attached to each capability; an edition assignment does not establish
implementation, availability or support. Community features stay in Community.
Shared runner, telemetry, SDK and MCP contracts remain Community even when a
Studio or governed authoring surface that uses them is paid.

## Decision basis

The edition names and licensing basis come from decision
`01M5RN3D0WX8W5A3CX11G7KQXP`. Decision `01M5RN3J51DWQPW045D23MSMMC` places the
application foundation, payments and complete CMS in Community and names the
paid module set. Decision `01M5RN3JHEJZQZ9P5NZNQZMQX5` makes Standard and Pro
licenses of that same set and adopts the five rules below. The setup decision
`01M5RN3JB8J87DGHHP4DSSWR3X` preserves qualification through the delivery gates.
Changing the operator's master design or these boundaries requires a recorded
decision.

Commerce is the operator's Community catalog addition, gated after Payments at
G-03. It covers a catalog, cart, payment checkout, orders and order email for
physical and digital goods, with tax and shipping rates supplied by connectors.
Fulfillment, inventory and accounting remain outside that addition. Commerce has
no identifier in the current coverage register, so its empty coverage list is
explicit and unique; this map does not invent a register identifier.

## No-lock-in rules

These are the operator's design rules for every paid service:

1. Each paid service has a self-hosted path in the edition that provides it.
2. Access to your applications and data never requires a Modularyn account.
3. Moving between hosted and self-hosted operation preserves everything in either direction.
4. Paid rights are checked offline and remain valid beyond the life of Modularyn's servers.
5. Ending a subscription ends only the service; it preserves self-hosting of covered releases and every Community right.

## Map contract

[edition-map.json](edition-map.json), version `1`, assigns each catalog entry a
stable `key`, short `title`, `edition` (`community` or `paid`), `home`, `coverage`
list and one-sentence `reason`. The `register` and `names` fields identify its
repository-relative inputs. `entries` contains module families, UI contracts,
tooling entries and low-code entries. Related module variants share an entry and
home rather than becoming separate domain implementations. Each tooling
inventory row and each low-code contract has its own entry.

Every [coverage-register](../coverage/register.json) identifier occurs exactly
once, either in `entries` or in an explicitly named `foundation` group. Foundation
groups cover the common product, architecture, framework, operations, release and
acceptance contracts and foundation inventory. They remain Community even where
a qualification scenario exercises paid modules. Catalog ownership does not copy
the register's many-to-many requirement or acceptance references: those remain
traceability links, not additional edition assignments.

Every home is a family prefix or product home in
[names-and-layout.json](../names-and-layout.json). Paid entries use
`Modularyn.Pro.*` or its Studio product home in `modularyn-pro`. Community homes
belong to `modularyn` and never use that prefix. A family home identifies placement,
not a claim that a package has shipped.

[The dependency rules](../architecture/dependencies.md) continue to apply:
Community never references paid packages or projects. Paid projects consume
Community through published packages with exact stable version declarations and
locked resolution, never through project references. Family and module-role
restrictions still apply. The existing architecture tests check both reference
kinds and reject Community-to-paid edges with `DEP003`; the map does not replace
those graph checks.

## Release manifests and checks

Release manifests distinguish the distribution edition (`community`, `standard`
or `pro`) from the map's module classification (`community` or `paid`). A mapped
Community capability always has `edition: community`, including inside Standard
or Pro distributions. A mapped paid capability uses the containing distribution's
`standard` or `pro` value and cannot appear in a Community distribution.

The three [manifest examples](../release-manifest/examples/) already follow this
translation. New repository tests check every example against the map, including
cross-cutting capabilities. They also check exact coverage, edition agreement,
homes, the lists below and the five rules. In-memory rejection cases assert the
specific diagnostic for each violation. Existing manifest checks still enforce
the schema, package editions and qualification claims.

The Builder must run locked restore, Release build and tests, then verify Linux,
Windows and macOS CI at the same exact head before acceptance. This document
describes the checks and does not claim their execution or a release.

## Community catalog

| Key | Catalog entry | Home |
| --- | --- | --- |
| `accounts` | Accounts | `Modularyn.Modules.*` |
| `audit` | Audit | `Modularyn.Modules.*` |
| `background-jobs` | Background jobs | `Modularyn.Modules.*` |
| `blogging` | Blogging | `Modularyn.Modules.*` |
| `chat` | Chat | `Modularyn.Modules.*` |
| `cms` | Complete CMS | `Modularyn.Cms.*` |
| `documentation` | Documentation publishing | `Modularyn.Modules.*` |
| `feature-management` | Feature management | `Modularyn.Modules.*` |
| `files` | File management | `Modularyn.Modules.*` |
| `forms` | Forms | `Modularyn.Modules.*` |
| `privacy` | Privacy requests | `Modularyn.Modules.*` |
| `identity` | Identity | `Modularyn.Modules.*` |
| `identity-migration` | Identity migration | `Modularyn.Modules.*` |
| `localization` | Localization | `Modularyn.Modules.*` |
| `operation-limits` | Operation limits | `Modularyn.Modules.*` |
| `authorization-server` | Authorization server | `Modularyn.Modules.*` |
| `payments` | Payments | `Modularyn.Modules.*` |
| `permissions` | Permission management | `Modularyn.Modules.*` |
| `settings` | Setting management | `Modularyn.Modules.*` |
| `tenancy` | Tenant management | `Modularyn.Modules.*` |
| `text-templates` | Text templates | `Modularyn.Modules.*` |
| `sms` | SMS delivery | `Modularyn.Modules.*` |
| `virtual-files` | Virtual file diagnostics | `Modularyn.Modules.*` |
| `commerce` | Commerce | `Modularyn.Modules.*` |
| `ui-contracts` | UI and Admin contracts | `Modularyn.UI.*` |
| `tooling-contracts` | Tooling and IDE integrations | `Modularyn.Tooling.*` |
| `run-profiles` | Shared run profiles | `Modularyn.Tooling.*` |
| `local-observability` | Local observability | `Modularyn.Tooling.*` |
| `supply-chain` | Tooling supply chain | `Modularyn.Tooling.*` |
| `tool-architecture-families` | Architecture templates | `Modularyn.Tooling.*` |
| `tool-application-starters` | Application starters | `Modularyn.Tooling.*` |
| `tool-modern-creation` | Modern creation | `Modularyn.Tooling.*` |
| `tool-classic-creation` | Classic creation | `Modularyn.Tooling.*` |
| `tool-profile-choices` | Data and authentication profiles | `Modularyn.Tooling.*` |
| `tool-solution-composition` | Solution composition | `Modularyn.Tooling.*` |
| `tool-version-alignment` | Version alignment | `Modularyn.Tooling.*` |
| `tool-razor-pages` | MVC and Razor Pages | `Modularyn.UI.*` |
| `tool-angular` | Angular UI | `Modularyn.UI.*` |
| `tool-blazor-wasm` | Blazor WebAssembly | `Modularyn.UI.*` |
| `tool-blazor-server` | Blazor Server | `Modularyn.UI.*` |
| `tool-blazor-web-app` | Blazor Web App | `Modularyn.UI.*` |
| `tool-maui-blazor` | MAUI Blazor | `Modularyn.UI.*` |
| `tool-blazor-components` | Blazor components | `Modularyn.UI.*` |
| `tool-react` | React UI | `Modularyn.UI.*` |
| `tool-react-admin` | React Admin | `Modularyn.UI.Admin.*` |
| `tool-native-clients` | Native mobile clients | `Modularyn.UI.*` |
| `tool-theme-variants` | Theme variants | `Modularyn.UI.*` |
| `tool-minimal-theme` | Minimal theme | `Modularyn.UI.*` |
| `tool-legacy-theme-migration` | Legacy theme migration | `Modularyn.UI.*` |
| `tool-theme-appearance` | Theme appearance | `Modularyn.UI.*` |
| `tool-cluster-workflows` | Container and cluster workflows | `Modularyn.Tooling.*` |
| `tool-cli` | CLI versions | `dotnet-modularyn` |
| `tool-cli-composition` | CLI composition | `dotnet-modularyn` |
| `tool-cli-utilities` | CLI utilities | `dotnet-modularyn` |
| `tool-entity-generation` | Entity generation | `Modularyn.Tooling.Generator.*` |
| `tool-relationship-generation` | Relationship generation | `Modularyn.Tooling.Generator.*` |
| `tool-database-intake` | Existing database intake | `Modularyn.Tooling.Generator.*` |
| `tool-generator-customization` | Generator customization | `Modularyn.Tooling.Generator.*` |
| `tool-sdk` | Client SDKs | `Modularyn.Framework.Sdk.*` |
| `tool-react-api` | React API clients | `Modularyn.Framework.Sdk.*` |
| `tool-documentation-mcp` | Developer documentation and MCP | `Modularyn.Tooling.*` |
| `tool-contract-tests` | Contract tests | `Modularyn.Tooling.*` |
| `tool-frontend-tests` | Frontend tests | `Modularyn.Tooling.*` |
| `tool-samples` | Reference applications | `Modularyn.Tooling.*` |
| `tool-sample-migration` | Legacy sample migration | `Modularyn.Tooling.*` |
| `tool-upgrades` | Upgrade planning | `Modularyn.Tooling.*` |
| `tool-runtime-upgrades` | Runtime transitions | `Modularyn.Tooling.*` |
| `tool-deployment-guidance` | Deployment guidance | `Modularyn.Tooling.*` |

## Paid catalog

Standard and Pro contain the same entries below.

| Key | Catalog entry | Home |
| --- | --- | --- |
| `ai-management` | AI management | `Modularyn.Pro.*` |
| `workflow` | Workflow | `Modularyn.Pro.*` |
| `saas-lifecycle` | SaaS commercial lifecycle | `Modularyn.Pro.*` |
| `tool-studio-workspace` | Studio workspace | `Modularyn.Pro.Studio.*` |
| `tool-studio-runner` | Studio runner | `Modularyn.Pro.Studio.*` |
| `tool-studio-telemetry` | Studio telemetry | `Modularyn.Pro.Studio.*` |
| `tool-coding-assistance` | AI coding assistance | `Modularyn.Pro.*` |
| `tool-assistance-configuration` | AI assistance configuration | `Modularyn.Pro.*` |
| `tool-assistance-tools` | AI assistance tools | `Modularyn.Pro.*` |
| `tool-assistance-workflows` | AI assistance workflows | `Modularyn.Pro.*` |
| `tool-change-review` | AI change review | `Modularyn.Pro.*` |
| `tool-studio-mcp` | Studio MCP | `Modularyn.Pro.Studio.*` |
| `tool-low-code-mcp` | Low-code MCP | `Modularyn.Pro.*` |
| `tool-application-ai` | Application AI management | `Modularyn.Pro.*` |
| `tool-low-code-designer` | Low-code model designer | `Modularyn.Pro.*` |
| `tool-low-code-runtime` | Low-code runtime | `Modularyn.Pro.*` |
| `tool-low-code-views` | Low-code views | `Modularyn.Pro.*` |
| `tool-low-code-data` | Low-code data movement | `Modularyn.Pro.*` |
| `tool-low-code-page-groups` | Low-code page groups | `Modularyn.Pro.*` |
| `tool-low-code-health` | Low-code model health | `Modularyn.Pro.*` |
| `tool-low-code-references` | Low-code reference entities | `Modularyn.Pro.*` |
| `tool-low-code-scripting` | Low-code scripting | `Modularyn.Pro.*` |
| `tool-low-code-hooks` | Low-code CRUD hooks | `Modularyn.Pro.*` |
| `tool-low-code-endpoints` | Low-code custom endpoints | `Modularyn.Pro.*` |
| `tool-low-code-mutations` | Low-code MCP mutations | `Modularyn.Pro.*` |
| `tool-low-code-expressions` | Low-code expressions | `Modularyn.Pro.*` |
| `tool-low-code-profiles` | Low-code profile restrictions | `Modularyn.Pro.*` |
| `low-model-contract` | Stable model contract | `Modularyn.Pro.*` |
| `low-revisions` | Authoritative revisions | `Modularyn.Pro.*` |
| `low-drafts` | Draft lifecycle | `Modularyn.Pro.*` |
| `low-designer` | Designer experience | `Modularyn.Pro.*` |
| `low-model-health` | Effective-model health | `Modularyn.Pro.*` |
| `low-migration` | Schema migration | `Modularyn.Pro.*` |
| `low-crud-runtime` | CRUD and query runtime | `Modularyn.Pro.*` |
| `low-views` | View catalog | `Modularyn.Pro.*` |
| `low-renderers` | Renderer policy | `Modularyn.Pro.*` |
| `low-code-entities` | Registered code entities | `Modularyn.Pro.*` |
| `low-data-movement` | Data import and export | `Modularyn.Pro.*` |
| `low-attachments` | Files and attachments | `Modularyn.Pro.*` |
| `low-compiled-extensions` | Compiled extensions | `Modularyn.Pro.*` |
| `low-scripted-extensions` | Scripted extensions | `Modularyn.Pro.*` |
| `low-business-invariants` | Business invariants | `Modularyn.Pro.*` |
| `low-endpoints` | Custom endpoints | `Modularyn.Pro.*` |
| `low-runtime-mcp` | Runtime MCP | `Modularyn.Pro.*` |
| `low-provider-rollout` | Provider rollout | `Modularyn.Pro.*` |
| `low-recovery` | Promotion and recovery | `Modularyn.Pro.*` |
| `low-completion` | Completion evidence | `Modularyn.Pro.*` |
| `low-calculated-fields` | Calculated fields and rollups | `Modularyn.Pro.*` |

## Cross-cutting foundation

All groups below are Community. Their complete identifier lists are in the map.

| Key | Catalog entry | Home |
| --- | --- | --- |
| `product-contracts` | Product contracts | `Modularyn.Framework.*` |
| `architecture-contracts` | Architecture contracts | `Modularyn.Framework.*` |
| `foundation-contracts` | Foundation contracts | `Modularyn.Framework.*` |
| `operations-contracts` | Operations contracts | `Modularyn.Framework.*` |
| `release-contracts` | Release contracts | `Modularyn.Tooling.*` |
| `acceptance-corpus` | Acceptance corpus | `Modularyn.Tooling.*` |
| `foundation-inventory` | Foundation inventory | `Modularyn.Framework.*` |
