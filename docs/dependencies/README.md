# Dependencies and licenses

[register.json](register.json) records every dependency the repository uses and every candidate planned for persistence, identity, the Admin and tests, as reviewed on 2026-10-25 for work order PLAN-06. Each entry has an exact version, an SPDX license expression, a dated maintenance review with its public sources, the reason it is listed, and a license clearance for both distributions: **Community** under Apache-2.0 and the **paid modules** under PolyForm Noncommercial 1.0.0.

Candidates are recorded, not adopted. Adopting one is a separate reviewed change that adds its pin and moves its entry to `in-use`.

**This document is a license compatibility analysis for engineering planning. It is not legal advice.** It does not grant, interpret or waive any license on anyone's behalf.

The policy carries out the design's dependency requirements: exact SDK and dependency versions for each release (D-02), a published runtime and package matrix with a deployment lock (FND-001), a release-pinned dependency map (DEV-025), provenance and license metadata in dependency reports (DEV-018), release artifacts with exact versions and a software bill of materials (OPS-009), and a reviewed shipping set (OPS-020, AC-031). The review belongs to G-01's exit condition and is repeated for G-08.

## Findings of the 2026-10-25 review

The register has 50 entries. **In use (26):** the SDK (`build`); two actions and gitleaks (`ci`); and the test project's 3 direct and 19 transitive NuGet packages (`test`). **Candidates (24):** 6 for `persistence`, 3 for `identity`, 10 for `admin` and 5 for `test`.

1. **The SDK was updated with this register.** SDK 10.0.303 (2026-08-11) bundled runtime 10.0.11, which predates three .NET security releases: 10.0.12 (2026-09-08, 6 CVEs), 10.0.13 (2026-10-13, 44 CVEs, including the critical ASP.NET Core advisories GHSA-rv87-ccxw-8ggf and GHSA-fpq8-4vhr-rcxx) and the out-of-band 10.0.14 (2026-10-25, 4 CVEs, including CVE-2026-111497 in the Brotli decoder). `global.json` now pins SDK 10.0.403, which carries runtime 10.0.14, under the security exception. See [SDK and runtime pin](#sdk-and-runtime-pin).
2. **The pin is now exact by construction.** `latestPatch` let the .NET host and setup-dotnet use the newest SDK in the pinned feature band, so the pin was exact only while no later SDK existed in that band. `global.json` now uses `rollForward: disable`.
3. **Microsoft.NET.Test.Sdk 18.11.0** and its vstest companions Microsoft.CodeCoverage, Microsoft.TestPlatform.ObjectModel and Microsoft.TestPlatform.TestHost were published 12 days before review. They were adopted before this policy, their release notes list no security fix, and they meet the 14-day age on 2026-10-27; their entries record an `ageException`, and no change is needed.
4. **Two licenses are flagged.** The Windows .NET distribution includes binaries under the .NET Library License and the Windows SDK License, and Lightning CSS, which Vite 8 requires at build time, is MPL-2.0. Neither blocks planned use; both limit redistribution. No entry is rejected.
5. **OpenIddict is recommended** for the authorization server, with its maintenance concentrated in one principal maintainer recorded as the main risk. See [Identity](#identity-trade-off-and-recommendation).
6. **Two smaller CI gaps.** The secret scan verifies gitleaks against a checksum file from the same release, which detects corruption but not a replaced release asset; recording the archive digest in the workflow would remove that reliance. The `ubuntu-latest`, `windows-latest` and `macos-latest` runner labels resolve to images that GitHub rebuilds regularly, most recently on 2026-10-23, and even versioned labels receive those rebuilds. No label pins the preinstalled tools, so each job log's image version is the available provenance for them.

Changing `global.json`, the workflows or the convention tests is outside this work order; findings 1, 2 and 6 need reviewed follow-up changes.

## Register format

The root has exactly `version` (the integer `1`) and `entries`. Entries are sorted by status (`in-use` first), then ecosystem in the order `dotnet-sdk`, `nuget`, `npm`, `github-action`, `binary`, then id.

| Field | Meaning |
| --- | --- |
| `id` | `<ecosystem>:<name>` in lowercase; unique. |
| `name` | The NuGet package ID, npm package name, `owner/repository` of an action, or the tool name. |
| `ecosystem` | `dotnet-sdk`, `nuget`, `npm`, `github-action` or `binary`. |
| `version` | The exact version: a full SDK version, an exact stable NuGet or npm version, a full 40-character commit SHA for an action, or a stable release number for a binary. Ranges, floating versions, wildcards and prerelease labels are rejected. |
| `license` | An SPDX license expression. `LicenseRef-` identifiers name licenses outside the SPDX list. |
| `status` | `in-use` when a repository pin references it; `candidate` when it is planned but not referenced. |
| `area` | `build`, `ci`, `persistence`, `identity`, `admin` or `test`. |
| `purpose` | What the dependency does here. |
| `reason` | Why it is listed and why this version, with design identifiers where they apply. |
| `maintenance.reviewedAt` | The review date. |
| `maintenance.published` | When the recorded version was published, from its registry or release. |
| `maintenance.latestEligible` | The version the publication-age rule selects at review. A candidate records exactly this version; an in-use entry that differs is a finding. |
| `maintenance.ageException` | `null`, or why a version younger than 14 days at review is recorded. |
| `maintenance.maintainer` | The maintaining organization or people. |
| `maintenance.activity` | Recent repository and release activity. |
| `maintenance.advisories` | The advisory review result for the recorded version. |
| `maintenance.sources` | The public https URLs consulted. |
| `clearance.community`, `clearance.paid` | `cleared` or `flagged` for each distribution. |
| `clearance.note` | The license obligations, or the reason for a flag. |

## Policy

### Exact pins

Every dependency is pinned to one exact version, never a range, floating version, wildcard or prerelease.

- **NuGet:** versions live only in [Directory.Packages.props](../../Directory.Packages.props) under central package management, with version overrides disabled; project files carry no versions. .NET tools, such as `dotnet-ef`, are pinned in a local tool manifest.
- **npm:** when the Admin is added, `package.json` uses exact versions without `^` or `~`, the committed `package-lock.json` is installed with `npm ci`, and install scripts run only for reviewed packages.
- **Actions:** each `uses` names a full 40-character commit SHA, and [actions.allow](../../.github/workflows/actions.allow) lists the same SHAs with their release tags, as [the CI guide](../ci.md) describes.
- **Binaries and images:** CI downloads tools by exact version and verifies their integrity before use; container images are referenced by digest.

### Locked restore and the advisory gate

`RestorePackagesWithLockFile` writes `packages.lock.json` with a SHA-512 content hash for every direct and transitive package. CI restores with `dotnet restore --locked-mode` and never regenerates locks. The register lists every locked package, transitive ones included, so any lock change appears as a register change.

NuGet audit runs during restore by default; for projects targeting .NET 10 it checks all packages, direct and transitive, at low severity and above. Because `TreatWarningsAsErrors` is on, a known advisory against a locked package fails restore; this was confirmed with SDK 10.0.303 during this review. A newly published advisory therefore fails CI until the package is updated or a reviewed suppression is recorded.

### Publication age and the security exception

Select the newest stable version published at least 14 days before the review date. The delay gives registries and maintainers time to withdraw a compromised or broken release before Modularyn depends on it.

**Security exception:** when a newer release fixes an advisory that affects the selected version, or the runtime it ships, select the newest release with the fix, however recent, and record the reason in `ageException`. A version younger than 14 days that a pin already references also records an `ageException`. Prereleases are never selected.

### Maintenance criteria

A dependency is acceptable when it:

- has a public source repository and an identifiable maintaining organization or people;
- has published a release or merged changes within the last 12 months, or is a stable, final package that a maintained dependency pins, recorded as such;
- is neither archived nor deprecated in its registry;
- supports the pinned runtime: .NET 10 for NuGet packages, and the Admin's Node.js LTS line for npm packages;
- announces its security fixes through advisories or release notes; and
- has any concentration risk, such as a single principal maintainer, recorded with a mitigation.

An entry that fails a criterion is flagged in its maintenance text and gets a replacement plan before adoption or the next release.

### Advisory monitoring

The review consults the GitHub Advisory Database for each ecosystem (`nuget`, `npm`, `actions` and `go`), the .NET release metadata and release notes for the SDK and runtimes, and the Node.js release notes. Release notes can precede the advisory record: CVE-2026-111497 was in the .NET release notes but not yet in the GitHub Advisory Database during this review. Before the PostgreSQL server is adopted, its security announcements must be added to the review's sources.

An advisory that affects an in-use version is fixed through the security exception without waiting 14 days. The fix is a reviewed change that updates the pin, the lock and the register together.

### Monthly dependency review

Once a month, and whenever a security release affects a recorded dependency, a Builder security session reviews the register:

1. Query each registry for the latest stable version, the latest eligible version, publication dates and deprecation.
2. Query the advisory sources for every recorded version and for the runtime.
3. Check repository activity and maintainers against the maintenance criteria.
4. Read the license of every newly selected version, because a version update can change the license.
5. Update `reviewedAt` and the maintenance fields, and record the findings in this document.
6. Propose reviewed updates for in-use entries whose `latestEligible` differs, immediately when an advisory applies.
7. Run locked restore, the Release build, the tests and gitleaks before the change is pushed.

Supply-chain review is security work, done by the Builder's own sessions.

### Adding or changing a dependency

1. Record the dependency as a `candidate` with its full review and clearance. A license outside the allowlist needs a recorded decision before adoption.
2. In the adoption change, add the exact pin, regenerate the lock as the [CI guide](../ci.md) describes, review the lock diff, add every new transitive package to the register as `in-use`, and move the entry to `in-use`.
3. Run locked restore, the Release build and the tests, require CI on all three platforms at the exact head, and run gitleaks.

The tests fail when a `PackageVersion`, a locked package, an action SHA or a workflow tool version has no matching `in-use` entry.

## SDK and runtime pin

[global.json](../../global.json) pins SDK **10.0.403** with `rollForward: disable` and `allowPrerelease: false`. SDK 10.0.403 was released on 2026-10-25 with the out-of-band security release 10.0.14 and bundles .NET Runtime 10.0.14, ASP.NET Core Runtime 10.0.14 and C# 14.0. .NET 10 is an LTS release in active support until 2028-11-14.

**Why it moved.** The previous pin, SDK 10.0.303 with `latestPatch`, bundled runtime 10.0.11 and predated the security releases 10.0.12, 10.0.13 and 10.0.14. Its feature band, 10.0.3xx, received none of their fixes, because those releases ship only 10.0.4xx and 10.0.1xx SDKs. SDK 10.0.403 is younger than the 14-day publication age and is adopted under the security exception.

**Exact by construction.** With `rollForward: disable`, the .NET host and the pinned setup-dotnet use exactly the named SDK, and a machine without it fails instead of substituting another. Every SDK patch is therefore a reviewed commit that changes `global.json` and this register together, and the monthly dependency review checks for new .NET security releases. The convention tests require an exact stable .NET 10 SDK version, `rollForward: disable` and prereleases disabled, and the register test requires the register's SDK entry to equal `global.json`.

**Provenance.** When the pin changed, the channel release metadata still listed 10.0.13 as the latest release, while the dotnet/core release record for 10.0.14 lists SDK 10.0.403 and its files' SHA-512 hashes. The Builder verified the downloaded archive against that record before using it. Each CI job's log shows the SDK it installed.

**Runtime.** Applications target `net10.0`. A framework-dependent application runs on the newest 10.0 patch runtime installed on its host, so each release states its runtime version in the FND-001 matrix, and container images and self-contained builds pin the runtime exactly (OPS-009). ASP.NET Core Identity, cookie authentication, data protection and antiforgery are part of the ASP.NET Core shared framework, so their security fixes arrive with the runtime.

## License allowlist

| SPDX identifier | Obligations when a distribution includes the component |
| --- | --- |
| `Apache-2.0` | Include the license text; keep copyright, patent, trademark and attribution notices; carry the component's NOTICE text, if it has one; and mark modified files. |
| `BSD-2-Clause` | Reproduce the copyright notice, the conditions and the disclaimer in the documentation of binary distributions. |
| `BSD-3-Clause` | As for BSD-2-Clause, and do not use the contributors' names to endorse Modularyn. |
| `ISC` | Include the copyright notice and the permission notice. |
| `MIT` | Include the copyright notice and the permission notice. |
| `PostgreSQL` | Include the copyright notice and the license's permission and disclaimer paragraphs. |

These permissive licenses impose notice obligations only, and they permit commercial use, modification, redistribution and combination with code under other terms. Each is cleared for both distributions.

**Review rules:**

- An expression is allowed when every license joined by `AND` is allowed, or when at least one alternative joined by `OR` is. A `WITH` exception always needs its own review.
- Any other license, including a `LicenseRef-` identifier, missing or unassertive metadata, or a license file that points to terms elsewhere, is flagged for both distributions with a note, and adopting it needs a recorded decision.
- The license comes from the package metadata, such as the NuGet license expression or the npm `license` field, checked against the repository's license file when the metadata is missing or unclear. A version update repeats the check.
- An allowed license may still be flagged when something else about the component needs attention; the note says what.
- Widening the allowlist is a policy change. It updates this table and the test's reviewed set in the same reviewed commit, with the reasoning recorded here.

## License review

### Community distribution (Apache-2.0)

Community is published under Apache-2.0 and is free for anyone to use, including commercially. Its dependencies must permit use, modification and redistribution, including commercial use, without fees, keys or accounts, and must impose no terms on Community's own code beyond notices. Permissive licenses on the allowlist meet these conditions.

When a Community artifact redistributes a dependency, such as the Admin bundle, a container image, a self-contained build or a sample, the artifact's THIRD-PARTY-NOTICES lists it. When Community only references a NuGet or npm package that users restore themselves, Modularyn does not redistribute it, and the register tracks its license so that dependency reports stay accurate (DEV-018).

### Paid-module distribution (PolyForm Noncommercial 1.0.0)

The Standard and Pro modules are published as source under PolyForm Noncommercial 1.0.0, and commercial use requires a separate license from Jason Furr. Their third-party dependencies must:

- allow combination with code under noncommercial terms, so they cannot require the combined work to adopt their own license or forbid added restrictions;
- allow commercial use, so that a commercial license for the modules needs no third party's consent; and
- work offline without a vendor account or activation, because paid rights are verified offline.

All licenses on the allowlist meet these conditions. Inside a paid distribution, each third-party component keeps its own license: the PolyForm terms cover Jason Furr's code, the modules' THIRD-PARTY-NOTICES identifies each component and its license, and a recipient's rights in an open-source component come from that component's license.

### How the two distributions combine

- **Direction.** Community never references the paid modules. The paid modules depend on Community only through published Community packages pinned to exact versions, so no PolyForm term reaches a Community user, and Community's Apache-2.0 grant is unchanged.
- **Use without modification.** A paid module that references an unmodified Community package uses it as Apache-2.0 permits for code under any license; Apache-2.0 does not extend to the paid module. When a paid distribution bundles Community packages, it includes the Apache-2.0 text and Community's NOTICE, and those packages remain under Apache-2.0 within it.
- **No copied source.** Copying Community source into a paid module would create a derivative of Community. Apache-2.0 would permit that with its notices, but the copied code would stay available under Apache-2.0 and blur the edition boundary; the package-only rule avoids it.
- **Reproducible combinations.** Each paid module records the exact Community versions it was built and tested with (FND-001, OPS-009).
- **Patents.** Apache-2.0 ends a party's patent license if it sues over the work, and PolyForm Noncommercial has a similar patent defense clause. Each applies to its own code and they do not conflict.

### Flagged and rejected license categories

| Category | Examples | Community | Paid modules | Reason |
| --- | --- | --- | --- | --- |
| Permissive with notices | The allowlist | Cleared | Cleared | Notice obligations only; THIRD-PARTY-NOTICES satisfies them. |
| Weak copyleft | `MPL-2.0`, `LGPL-2.1-only`, `LGPL-3.0-only`, `EPL-2.0` | Flagged | Flagged | Modified covered files, and for LGPL the means to replace or relink the library, must be offered under the original license. Acceptable only as an unmodified, separately replaceable component after a recorded review, and never copied into Modularyn source. |
| Strong copyleft | `GPL-2.0-only`, `GPL-3.0-only`, `AGPL-3.0-only` | Rejected | Rejected | A distributed combined work must be licensed as a whole under the GPL. GPL-2.0 is incompatible with Apache-2.0, GPL-3.0 would make Community combinations GPL-3.0 works, and the GPL family forbids the noncommercial restriction; the AGPL adds source obligations for network use. |
| Source-available | `BUSL-1.1`, `SSPL-1.0`, `Elastic-2.0`, vendor commercial terms | Rejected | Rejected | They restrict production, competing or commercial use, or require a fee or key. Community would lose free commercial use, and every paid-module licensee would inherit third-party terms. |
| Noncommercial third-party | `PolyForm-Noncommercial-1.0.0`, `CC-BY-NC-4.0` | Rejected | Rejected | Community permits commercial use, and a commercial license for the paid modules could not cover a component reserved for noncommercial use. |
| Platform licenses for tools | .NET Library License, Windows SDK License | Flagged | Flagged | Acceptable for building and testing. Redistributing the covered binaries needs a review against those terms. |
| Unknown or missing | No license metadata, or a license file that points elsewhere | Flagged | Flagged | Not cleared until the license text is identified and reviewed. |

### Flagged entries in this register

- **`dotnet-sdk:dotnet`** is `MIT AND LicenseRef-dotnet-library-license AND LicenseRef-windows-sdk-license`. Linux and macOS distributions are MIT. On Windows, `coreclr.dll`, the native symbol reader and WPF native binaries use the .NET Library License, and the WPF shader compiler uses the Windows SDK License. Modularyn builds with the SDK and does not redistribute it, and compiler output carries no SDK restriction. Review before a release redistributes Windows runtime binaries, such as a Windows self-contained or single-file build.
- **`npm:lightningcss`** is `MPL-2.0`. Vite 8 runs it at build time; the CSS it produces is not covered, and as an unmodified tool that Modularyn does not redistribute it imposes no obligation. Modifying its files, or shipping them in any distribution, needs a review and source availability for those files under MPL-2.0.

The other 48 entries are cleared for both distributions.

### THIRD-PARTY-NOTICES

Nothing is distributed yet, so the repository has no THIRD-PARTY-NOTICES file. Each distributed artifact will carry one, generated from the register and that artifact's lock files at release time: Community packages that bundle third-party files, the Admin bundle, container images, self-contained builds and each paid-module distribution. It lists every bundled component with its version, its license text and any NOTICE content, including notices the component itself carries, such as the .NET runtime's. Build, CI and test dependencies are not redistributed and are not listed, but their licenses are still reviewed here.

Among the current candidates, the Admin's runtime closure would enter the Admin bundle's notices: `react`, `react-dom`, `scheduler`, `react-router`, `cookie-es` and `@remix-run/route-pattern`, all MIT. A container image or self-contained build would also list the .NET runtime, EF Core, Npgsql, ASP.NET Core Identity's EF Core stores and OpenIddict.

## Candidates

| Area | Candidates | Design basis |
| --- | --- | --- |
| Persistence | EF Core 10.0.12, its design-time package 10.0.12 and `dotnet-ef` 10.0.12; Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 and Npgsql 10.0.3; PostgreSQL 18.6 | D-05, DEV-004, ARC-007, ARC-008; migration bundles for OPS-009 |
| Identity | ASP.NET Core Identity from the shared framework with its EF Core stores 10.0.12; OpenIddict.AspNetCore 7.7.2 and OpenIddict.EntityFrameworkCore 7.7.2 | D-08, MOD-001, MOD-002, MOD-023, OPS-003 |
| Admin | Node.js 24.22.1; React and react-dom 19.3.0; React Router 8.4.0; TypeScript 7.0.2; Vite 8.3.4 with @vitejs/plugin-react 6.1.2; @types/react and @types/react-dom 19.3.0; Lightning CSS 1.33.0 | D-06, T-015 |
| Tests | Testcontainers and Testcontainers.PostgreSql 4.16.0; Microsoft.Playwright 1.63.0; Vitest 5.0.3; @playwright/test 1.64.0 | DEV-023, T-058; real-provider tests for G-02 and UI tests for gate W |

Candidates are limited to what G-02 and gate W need, plus the Admin toolchain the work order names. Beyond the named packages, the list adds only what they cannot work without: EF Core's design-time package and tool for migration bundles, the PostgreSQL server the real-provider tests run against, the Node.js runtime, Vite's React plugin, React's type declarations, and Lightning CSS, which Vite 8 requires and which needs its own flag. The gate W Razor site uses the ASP.NET Core shared framework and adds no package.

**Admin closure.** Resolving the Admin candidates at their exact versions with lock-only npm resolution and no install scripts gave 94 packages: 54 MIT, 26 Apache-2.0, 12 MPL-2.0 (Lightning CSS and its 11 platform packages), 1 ISC and 1 BSD-3-Clause. No GitHub advisory affects any resolved version. The register records the direct candidates and Lightning CSS; the full closure is registered when the Admin's lock file is committed. The tests need an npm lock check by then, so they reject `in-use` npm entries today.

**NuGet candidate closures.** First-level dependencies were checked: EF Core's design-time package brings Roslyn workspaces, Mono.TextTemplating, Humanizer.Core and Newtonsoft.Json 13.0.4, and Testcontainers brings Docker.DotNet.Enhanced 4.3.3, SSH.NET 2026.0.0 and SharpZipLib 1.4.2. All are MIT, and no advisory affects those versions. The complete closures are recorded from the lock files at adoption.

**Integration notes.** Testcontainers.PostgreSql's parameterless builder and its default `postgres:15.1` image are obsolete; the suite must pass the reviewed PostgreSQL image explicitly, pinned by digest. Playwright installs browser builds that carry their own licenses; they are used for tests only and never redistributed. Node.js 26 becomes active LTS on 2026-10-28, so the next monthly review decides between lines 24 and 26.

## Identity: trade-off and recommendation

The authentication requirements (OPS-003, MOD-001, MOD-002, MOD-023) need local accounts with passkeys, MFA, lockout and recovery; authorization code with PKCE; a backend-for-frontend session cookie for the canonical Admin; scoped machine clients; and consent, revocation and signing-key rollover.

- **ASP.NET Core Identity alone** covers accounts, credentials, passkeys and cookie sessions from the shared framework, but it is not an OpenID Connect authorization server. It cannot serve external or machine clients with authorization code and PKCE as MOD-023 requires, so it is necessary but not sufficient.
- **ASP.NET Core Identity with OpenIddict 7.7.2**, the design's recommended foundation (D-08), adds an Apache-2.0 server, validation and client with EF Core stores on the persistence baseline. It has monthly releases and no advisories. Its costs: maintenance is concentrated in one principal maintainer, Kévin Chalet, with 1,965 commits against 9 for the next human contributors; it is a lower-level framework, so Modularyn owns the endpoints, sign-in and consent UI, key management and protocol configuration, and must prove PKCE, consent, audience and scope enforcement, redirect validation, revocation and key rollover itself (MOD-023); and version 8.0 is in preview, so a major upgrade lies ahead.
- **ASP.NET Core Identity with Duende IdentityServer** offers a commercially supported product. Its newest eligible version is 8.0.9 (2026-09-28). Its package license file points to Duende's terms on its website, which is outside this review's sources; the package depends on `Duende.Private.Licensing`, and GitHub does not identify its license. Requiring it would subject every Community user to third-party commercial terms, which Community's free commercial use excludes, and would pass those terms to every paid-module licensee. It is rejected as a dependency for both distributions. Its earlier advisories, GHSA-ff4q-64jc-gx98 and GHSA-v9xq-2mvm-x8xc, affected 6.x and 7.x releases.

**Recommendation, not a final selection:** use ASP.NET Core Identity from the shared framework with Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.12, and OpenIddict 7.7.2 through OpenIddict.AspNetCore and OpenIddict.EntityFrameworkCore, behind MOD-023's protocol adapter seam. For the Admin's backend-for-frontend session, OpenIddict's client, which OpenIddict.AspNetCore includes, avoids another dependency; Microsoft's OpenID Connect handler is the alternative and would be registered when chosen. To contain the maintainer concentration, keep protocol details behind the adapter, pin exact versions, review each release monthly, and keep the MOD-023 acceptance suite complete enough to qualify a replacement. G-02 confirms or replaces this choice with evidence.

## Checks

[DependencyRegisterTests.cs](../../tests/Modularyn.Repository.Tests/DependencyRegisterTests.cs) validates the register through the existing repository-file discovery helper, with no new packages. It requires:

- the `dotnet-sdk` entry to equal `global.json`;
- every `PackageVersion` in `Directory.Packages.props`, and every direct or transitive package in each `packages.lock.json`, to have an `in-use` NuGet entry at the same version, and every `in-use` NuGet entry to appear in a lock at its version;
- every action SHA in the workflows and `actions.allow` to have an `in-use` action entry, and every such entry to be used;
- every workflow tool version variable, such as `GITLEAKS_VERSION`, to match an `in-use` binary entry, and every such entry to match a variable;
- exact versions for each ecosystem, with no ranges, floating versions, wildcards or prerelease labels;
- each license to be an SPDX expression that the allowlist above permits, or else to be flagged for both distributions with a note, and the allowlist table to equal the test's reviewed set;
- candidates to record their `latestEligible` version, and an `ageException` exactly when the recorded version was published fewer than 14 days before review; and
- exact field sets, unique ids, sorted entries, ISO dates, unique https sources, and portable single-line review text.

In-memory mutations exercise each rule and assert its specific diagnostic without writing invalid files. A line-ending case checks portability across platforms. The existing CI workflow runs these tests on Linux, Windows and macOS.

The checks cannot establish that a version is still current, that an advisory search is complete or that a license analysis is correct; the monthly review provides that.

## What this review could not verify

- **PostgreSQL server advisories.** The GitHub Advisory Database does not cover the server, and the PostgreSQL security page is outside this review's permitted sources. The recorded publication date of 18.6 is its `REL_18_6` tag commit date on the GitHub mirror, not the release announcement.
- **Duende's license terms.** The package's license file refers to terms on Duende's website, which is outside the permitted sources.
- **CVE-2026-111497 in the advisory database.** It had no GitHub advisory record yet, so the SDK finding relies on the dotnet/core release notes, and the channel release metadata still listed 10.0.13.
- **Container images.** The PostgreSQL image and its digest are recorded when the integration suite is added, because container registries are outside the permitted sources.
- **Browser builds.** The licenses of the browsers that Playwright installs were not reviewed; they are used for tests only.
- **Complete candidate closures.** Only the first-level NuGet dependencies of candidates were reviewed; the npm closure was resolved but is not registered entry by entry.
- **Runner images.** The contents of GitHub's hosted runner images were not reviewed.
