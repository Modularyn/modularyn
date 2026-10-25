# Release profiles

A profile is the tuple a release qualifies: runtime, database, UI, tenancy mode, deployment topology and providers (PRD-008). Module versions and the release commit identify the implementation tested against that tuple. Passing one tuple does not qualify a different database, renderer, store or topology.

These definitions are planning contracts. They do not assert that a runtime or importer is implemented, that acceptance has passed, or that a supported release exists. A release may claim a profile only when its exact versions, capability subset, exclusions and acceptance evidence are recorded together. Unsupported selections must fail validation before activation.

| Profile | Purpose | Definition |
|---|---|---|
| `reference` | The initial development and qualification baseline of DEV-004 | [Reference profile](reference.md) |
| `website` | The early website subset at gate W | [Website profile](website.md) and [capability map](website-capabilities.json) |

An acceptance family is a set of scenarios, not a blanket certification. Each profile states the surfaces and failure cases it must exercise. Evidence must identify the exact tuple, release and module versions, test inputs, expected and observed outcomes, and remaining gaps. Positive, rejection, boundary and recovery cases all contribute. Repository contract tests check the definitions and examples; they do not replace real-provider, browser, security, upgrade or recovery qualification.

The [content-package contract](../content-packages/README.md) is versioned independently of application releases. A format version does not expand a profile's capabilities. Gate W shares the complete CMS contracts planned for G-04. Changes to the master design or delivery gates require recorded decisions; these profile documents do not amend them.
