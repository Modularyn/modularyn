# Module localization bootstrap

Modules publish each `ModuleLocalizationResource.Declaration` in their
manifest's `Definitions`. The resource supplies a version, a default culture
and dictionaries of translations. Registration copies the dictionaries into
immutable snapshots and checks owner, asset name and exact version against
the selected manifests. `AddModularynLocalization` registers both the bootstrap
service and its composition validator.

`Bootstrap` returns one module resource, its declared version, requested culture
and strings with their source cultures. Each key uses the requested culture,
its parents, the default culture and the invariant culture in that order.
Missing keys are absent. Identically named resources in different modules
remain separate. An invariant-culture request checks the invariant dictionary
first. The bootstrap is deterministic and does not read process culture.
Culture tags use a lowercase two- or three-letter language, an optional
title-case four-letter script, and an optional uppercase two-letter or numeric
three-digit region. The empty tag is invariant. Parents remove the last tag
segment. Matching needs no host culture database and also works with invariant
globalization.

Resources are deployment-owned and have no tenant overrides. No setting,
feature, permission, secret, file-content policy or application endpoint enters
the snapshot. A delivery adapter can use the module, resource, version and
requested culture as its variation dimensions. Tenant-specific resource
providers and approved client configuration can attach through separate
contracts when those capabilities are introduced.
