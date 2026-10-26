# Dependencies and licenses

[register.json](register.json) records every dependency the repository uses and every candidate planned for persistence, identity, observability, the Admin, tests and the container deployment, as reviewed on 2026-10-25 for work order PLAN-06, updated for SEC-01, which made CI verify every tool it downloads against a pinned hash, reviewed again on 2026-10-26 for PLAN-10, which selected the exact versions G-02 uses (see [G-02 dependency selection](#g-02-dependency-selection)), and updated the same day for CI-01, which put the CI pins in use (see [CI-01 adoption](#ci-01-adoption)), and for CODE-03, which put the persistence packages in use (see [CODE-03 adoption](#code-03-adoption)). Each entry has an exact version, the SHA-256 digests of any archive or image that no lock file covers, an SPDX license expression, a dated maintenance review with its public sources, the reason it is listed, and a license clearance for both distributions: **Community** under Apache-2.0 and the **paid modules** under PolyForm Noncommercial 1.0.0.

Candidates are recorded, not adopted. Adopting one is a separate reviewed change that adds its pin and moves its entry to `in-use`.

**This document is a license compatibility analysis for engineering planning. It is not legal advice.** It does not grant, interpret or waive any license on anyone's behalf.

The policy carries out the design's dependency requirements: exact SDK and dependency versions for each release (D-02), a published runtime and package matrix with a deployment lock (FND-001), a release-pinned dependency map (DEV-025), provenance and license metadata in dependency reports (DEV-018), release artifacts with exact versions and a software bill of materials (OPS-009), and a reviewed shipping set (OPS-020, AC-031). The review belongs to G-01's exit condition and is repeated for G-08.

## Findings of the 2026-10-25 review

After SEC-01 the register had 49 entries. **In use (25):** the SDK (`build`); actions/checkout and gitleaks (`ci`); and the test project's 3 direct and 19 transitive NuGet packages (`test`). **Candidates (24):** 6 for `persistence`, 3 for `identity`, 10 for `admin` and 5 for `test`. The review recorded 51 entries; SEC-01 removed `actions/setup-dotnet` and the runtime it installed, `binary:dotnet-runtime`, when CI stopped using them (findings 7 and 8). The [G-02 selection](#g-02-dependency-selection) brings the register to 53 entries, and the [CODE-03 adoption](#code-03-adoption) to 65.

1. **The SDK was updated with this register.** SDK 10.0.303 (2026-08-11) bundled runtime 10.0.11, which predates three .NET security releases: 10.0.12 (2026-09-08, 6 CVEs), 10.0.13 (2026-10-13, 44 CVEs, including the critical ASP.NET Core advisories GHSA-rv87-ccxw-8ggf and GHSA-fpq8-4vhr-rcxx) and the out-of-band 10.0.14 (2026-10-25, 4 CVEs, including CVE-2026-111497 in the Brotli decoder). `global.json` now pins SDK 10.0.403, which carries runtime 10.0.14, under the security exception. See [SDK and runtime pin](#sdk-and-runtime-pin).
2. **The pin is now exact by construction.** `latestPatch` let the .NET host and setup-dotnet use the newest SDK in the pinned feature band, so the pin was exact only while no later SDK existed in that band. `global.json` now uses `rollForward: disable`.
3. **Microsoft.NET.Test.Sdk 18.11.0** and its vstest companions Microsoft.CodeCoverage, Microsoft.TestPlatform.ObjectModel and Microsoft.TestPlatform.TestHost were published 12 days before review. They were adopted before this policy, their release notes list no security fix, and they meet the 14-day age on 2026-10-27; their entries record an `ageException`, and no change is needed.
4. **Two entries are flagged, under two license categories.** Under *platform licenses for tools*, the .NET SDK (`dotnet-sdk:dotnet`) includes Windows binaries under the .NET Library License, and its Windows distribution also includes one under the Windows SDK License. Under *weak copyleft*, Lightning CSS (`npm:lightningcss`), which Vite 8 requires at build time, is MPL-2.0. None blocks planned use; each limits redistribution. No entry is rejected. The review also flagged the runtime that CI installed beside the SDK (`binary:dotnet-runtime`) under the .NET Library License, until SEC-01 removed it.
5. **OpenIddict is recommended** for the authorization server, with its maintenance concentrated in one principal maintainer recorded as the main risk. See [Identity](#identity-trade-off-and-recommendation).
6. **Two smaller CI gaps.** The secret scan verified gitleaks only against a checksum file from the same release, which detects corruption but not a replaced release asset; recording the archive digest in the workflow would remove that reliance. The `ubuntu-latest`, `windows-latest` and `macos-latest` runner labels resolve to images that GitHub rebuilds regularly, most recently on 2026-10-23, and even versioned labels receive those rebuilds. No label pins the preinstalled tools, so each job log's image version is the available provenance for them. **Resolved by SEC-01 for gitleaks:** the secret-scan workflow pins the archive's SHA-256 as `GITLEAKS_SHA256` and checks it with `sha256sum --check --strict` before extraction, keeps the release checksum-file check, and fails if the two disagree. The runner images still cannot be pinned.
7. **CI did not verify the SDK it installed.** setup-dotnet's install script downloaded SDK 10.0.403 by exact version, but it compared the download only with the size the server reported, continued even when the sizes differed, and computed no hash. In CI on 2026-10-25 it reported equal sizes on Linux and macOS; on Windows it could not measure the size and continued. The Builder's SHA-512 check covered only its own local download. **Resolved by SEC-01:** CI installs the SDK from the official archive for each runner and verifies its SHA-512 against [.github/dotnet-sdk.json](../../.github/dotnet-sdk.json) before extraction; see [SDK and runtime pin](#sdk-and-runtime-pin).
8. **CI installed an unpinned runtime.** Before the SDK, setup-dotnet installed the newest .NET runtime in the LTS channel, and it had no input to skip or pin that step. The channel resolved to runtime 10.0.13 on 2026-10-25, after the 10.0.14 security release had shipped; the Windows image already had 10.0.13. No repository file selected it, so the register recorded it as `binary:dotnet-runtime` with an `unpinned` note, and the logs did not show which runtime ran the build and tests. **Resolved by SEC-01:** with setup-dotnet gone, CI installs no runtime beyond those the SDK bundles, and each job lists its SDKs and runtimes in its log. The register no longer lists `actions/setup-dotnet` or `binary:dotnet-runtime`.

Findings 1 and 2 were carried out in PLAN-06, together with the convention tests and the CI guide. Findings 6, 7 and 8 needed reviewed workflow changes outside PLAN-06's permitted paths, and SEC-01 carried them out:

- the secret-scan workflow records the gitleaks archive's SHA-256, so verification no longer depends on a checksum file from the same release;
- CI downloads each platform's SDK archive and verifies its SHA-512, copied from the dotnet/core release record into `.github/dotnet-sdk.json`, before extraction; and
- that hash-verified install replaces setup-dotnet, which removes the extra runtime.

The runner images cannot be pinned; their job-log versions remain the provenance for the tools CI takes from them, such as Git, curl, jq, tar, PowerShell and the hash utilities.

## G-02 dependency selection

Work order PLAN-10 reviewed, on 2026-10-26, every dependency that G-02 will use: persistence, identity, observability, the generated TypeScript client's toolchain, the browser test, the PostgreSQL test image and the container deployment's runtime image. After PLAN-10 the register had 53 entries. **In use (25)**, unchanged. **Candidates (28):** 6 for `persistence`, 3 for `identity`, 2 for `observability`, 2 for `build`, 8 for `test`, 6 for `admin` and 1 for `deployment`. Nothing moved to `in-use`, because no code used these yet; CI-01 and the code work orders adopt the pins: CI-01 has since moved four entries to `in-use` (see [CI-01 adoption](#ci-01-adoption)), and CODE-03 three more, with the twelve packages their locks add (see [CODE-03 adoption](#code-03-adoption)).

### Method

- **Registries, read anonymously.** nuget.org's registration, catalog and package endpoints; the npm registry; nodejs.org's distribution index and release files; Docker Hub's registry API, with an anonymous pull token from auth.docker.io; and the Microsoft Artifact Registry's API, which needs no token. GitHub was read anonymously or through its API, for reads only.
- **Versions.** For each package: its stable versions, publication dates, listing and deprecation, license metadata and dependency groups; then the selection rules under [Publication age and the security exception](#publication-age-and-the-security-exception), including the runtime-alignment and image-build rules this review added.
- **Advisories.** The GitHub Advisory Database for each package and for its exact selected version (`affects=<package>@<version>`, checked against a version known to be affected), nuget.org's vulnerability data in each registration, the release notes of the selected and newer versions for fixes without an advisory, the .NET release notes and metadata, the Node.js release notes, and the [PostgreSQL security page](https://www.postgresql.org/support/security/).
- **Closures.** Scratch projects outside the repository restored the NuGet candidates at their exact versions with NuGet audit in `all` mode, using nuget.org's vulnerability data of 2026-10-25. As an ASP.NET Core host the closure has 47 packages, because the SDK prunes packages that the shared framework supplies; as a class library it has 69. All are MIT, Apache-2.0 or PostgreSQL, and audit reported nothing. A lock-only npm resolution of TypeScript 7.0.2, Vitest 5.0.4 and Vite 8.3.4, with install scripts disabled and an empty npm configuration, gave 81 packages: 44 MIT, 23 Apache-2.0, 12 MPL-2.0 (Lightning CSS and its 11 platform packages), 1 ISC and 1 BSD-3-Clause. npm audit reported no advisory, and only the optional macOS package fsevents 2.3.3 declares an install script.

### Selected versions

| Dependency | Version | Selected by | License | Advisories affecting the version |
| --- | --- | --- | --- | --- |
| Microsoft.EntityFrameworkCore, its Design package and `dotnet-ef` | 10.0.14 | Runtime alignment | MIT | None |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.14 | Runtime alignment | MIT | None |
| Npgsql and Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | Newest stable releases for EF Core 10 | PostgreSQL | None |
| OpenTelemetry.Exporter.OpenTelemetryProtocol and OpenTelemetry.Extensions.Hosting | 1.19.1 | Age rule | Apache-2.0 | None |
| Microsoft.Playwright | 1.64.1 | Security exception, for its bundled Node.js | MIT | None |
| Testcontainers and Testcontainers.PostgreSql | 4.16.0 | Reviewed; not used by G-02 (decision D2) | MIT | None |
| TypeScript | 7.0.2 | Age rule, unchanged | Apache-2.0 | None |
| Vitest | 5.0.4 | Age rule | MIT | None |
| Vite, which Vitest requires | 8.3.4 | Age rule, unchanged | MIT | None |
| Lightning CSS, which Vite requires | 1.33.0 | Age rule, unchanged | MPL-2.0, flagged | None |
| Node.js | 24.22.1 | Security exception, unchanged | MIT | None known; it is the October 2026 security release |
| PostgreSQL server | 18.6 | Current minor release | PostgreSQL | None on the PostgreSQL security page |
| `docker.io/library/postgres` | 18.6, tag `18.6-trixie`, built 2026-10-21 | Newest build of the reviewed tag | PostgreSQL with Debian packages, flagged | None for the server; the Debian packages were not scanned |
| `mcr.microsoft.com/dotnet/aspnet` | 10.0.14, tag `10.0.14-noble`, built 2026-10-26 | Runtime alignment and the security exception | MIT with Ubuntu packages, flagged | None known for runtime 10.0.14; the Ubuntu packages were not scanned |

Compared with the 2026-10-25 review: the four Microsoft packages move from 10.0.12 to 10.0.14, Microsoft.Playwright from 1.63.0 to 1.64.1 and Vitest from 5.0.3 to 5.0.4; the two OpenTelemetry packages and the two images are new; TypeScript and Node.js move to the `build` area, and Vite and Lightning CSS to `test`, because G-02 uses them before the Admin does; the PostgreSQL server's publication date is now its release announcement, 2026-08-13, rather than its tag commit; and the gitleaks entry records the digest that the secret-scan workflow pins. The Npgsql packages, Testcontainers, TypeScript, Vite, Lightning CSS and Node.js keep their versions, which are still the newest eligible ones.

### Runtime alignment

EF Core, its Design package, `dotnet-ef` and the Identity EF Core stores ship in every .NET servicing release; the [10.0.14 release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md) list all four at 10.0.14 among the packages updated in that release. Their nuspecs name dotnet/dotnet commit `0427b9abbead3fa64c6655eb4290c537397131fa`, and the shared frameworks that SDK 10.0.403 installs record the same commit in their `.version` files. Selecting them at the runtime version keeps EF Core, its tool and the ASP.NET Core shared framework on one tested release, and their transitive Microsoft.Extensions packages on its servicing fixes. All four were published on 2026-10-25, so each records an `ageException`. No advisory names them, so the security exception alone would not have selected them; the age rule alone would have kept 10.0.12, which belongs to an older servicing release than the runtime the SDK pin bundles.

The test applies the rule to `dotnet-ef` and to NuGet packages named `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.*`, `Microsoft.AspNetCore.*` or `Microsoft.Extensions.*`, and to container images under `mcr.microsoft.com/dotnet/`; the 10.0.14 release notes list 157 packages with those prefixes, all at 10.0.14. A package under those prefixes from another release train, such as dotnet/extensions, needs a reviewed exception in the test's `RuntimeAlignmentExceptions`, with its reason. CODE-03 put thirteen of these packages in use, each corroborated the same way; see [CODE-03 adoption](#code-03-adoption).

### Entries left as Admin candidates

G-02 has no React Admin. React, react-dom, React Router, @types/react, @types/react-dom, @vitejs/plugin-react and @playwright/test stay candidates exactly as the 2026-10-25 review recorded them, and this review did not advance them. Vite and Lightning CSS are not Admin-only: Vitest 5 requires Vite as a peer dependency, and Vite 8 depends on Lightning CSS, so the TypeScript client toolchain needs both. They were reviewed again and keep their versions. G-02's browser case uses Microsoft.Playwright, so @playwright/test, the Admin's TypeScript test runner, is not needed yet.

### How each hash and digest was obtained and verified

- **Node.js 24.22.1.** The six archive hashes are the values in [SHASUMS256.txt](https://nodejs.org/dist/v24.22.1/SHASUMS256.txt) for the `.tar.gz` archives of linux-x64, linux-arm64, darwin-x64 and darwin-arm64 and the `.zip` archives of win-x64 and win-arm64, the same six platforms that `.github/dotnet-sdk.json` covers for the SDK. The detached signature `SHASUMS256.txt.sig` verified with GnuPG in an empty keyring as a good EdDSA signature, made 2026-10-21, by key `5BE8A3F6C8A5C01D106C0AD820B1A390B168D356`. The [nodejs/node README at v24.22.1](https://github.com/nodejs/node/blob/v24.22.1/README.md#release-keys) lists that key as Antoine du Hamel's primary release key, and the key came from [nodejs/release-keys](https://github.com/nodejs/release-keys/blob/main/keys/5BE8A3F6C8A5C01D106C0AD820B1A390B168D356.asc). The clear-signed `SHASUMS256.txt.asc` verified with the same key and carries the same lines. All six archives were downloaded and each matched its hash. GnuPG reports the key as not certified by a trusted signature; its authenticity rests on those two GitHub sources, not on a web of trust.
- **Container images.** For each tag, the image index came from the registry's manifest endpoint; the SHA-256 of the body it returned equaled its `Docker-Content-Digest` header. The linux/amd64 manifest listed in the index was then fetched by its digest, and its body hashed to that digest; its configuration blob hashed to the digest the manifest names. The configuration sets `PG_VERSION=18.6-1.pgdg13+2` for PostgreSQL, and `DOTNET_VERSION=10.0.14` and `ASPNET_VERSION=10.0.14` on Ubuntu 24.04 for ASP.NET Core. Tags `18.6` and `10.0.14` resolved to the same indexes as `18.6-trixie` and `10.0.14-noble`. Each pair was cross-checked against the publisher's own record: [docker-library/repo-info](https://github.com/docker-library/repo-info/blob/bcf8a9234d4abbdc9929a0a5a478e0584a09b95d/repos/postgres/remote/18.6-trixie.md) for PostgreSQL, and [dotnet/versions](https://github.com/dotnet/versions/blob/18f2104ed862edcda93b75415f2c5c027bcefd2e/build-info/docker/image-info.dotnet-dotnet-docker-main.json) for ASP.NET Core, which also names the dotnet-docker commit the image was built from.
- **gitleaks 8.30.1.** The register now records the SHA-256 that the secret-scan workflow pins. A fresh download of the Linux x64 archive matched it, and so did the release's checksum file.
- **`dotnet-ef` 10.0.14.** A local tool manifest has no lock file, so no content hash pins the tool. Its Microsoft author signature and nuget.org repository countersignature verified with `dotnet nuget verify`, and a fresh download's SHA-512 matched nuget.org's catalog: `Z0peTXsBCnJe2r1JOQW39qIZYs9whCazG/tRNuuIcRYr2tCh9HczKDuBVryeX3+X9HrbBfz2+iqlorHXAeEQYg==`, the value a restore writes to `dotnet-ef.10.0.14.nupkg.sha512`. The change that adopts the tool checks it.

### Which image digest CI pins

CI pins the **multi-platform index digest** and records the linux/amd64 manifest digest beside it. The index is content-addressed and lists each platform's manifest by digest, so pinning it fixes the linux/amd64 image as firmly as the manifest digest would; it also fixes the build attestations that the PostgreSQL index lists, which a platform manifest reference would drop. The same reference serves developers on arm64 machines without emulation, while CI and the qualified deployment run linux/amd64. The recorded linux/amd64 digest lets CI and each release record show exactly which image the qualified platform ran. A build of the deployment image should name the platform `linux/amd64` explicitly. A pull by digest that fails, for example because a registry removed an old manifest, is a reason to review again, never to fall back to the tag.

### Exceptions and open decisions

1. **Runtime alignment over the age rule.** The four Microsoft packages and the ASP.NET Core image are one day old or younger; see [Runtime alignment](#runtime-alignment).
2. **Microsoft.Playwright 1.64.1** is 4 days old. Versions 1.63.0 and 1.64.0 bundle Node.js 24.21.0, and the Node.js project's October 2026 security release post says its fixes, the most severe rated high, cover all of line 24; 1.64.1 bundles 24.22.1, as its `Version.props` and the release URL in its linux-x64 `node` binary show. That runtime is what the security exception covers. The post also lists CVE-2026-106072 for 24.x, which the 24.22.1 release notes do not name.
3. **Image builds younger than 14 days.** Docker Official Images and the .NET images rebuild a tag whenever its base image changes, so the newest build at review is pinned and its creation date recorded as `published`: 5 days for PostgreSQL, rebuilt after the Debian base update of 2026-10-20, and 0 days for ASP.NET Core. An older build would lack those updates. The 14-day rule still applies to the version each image packages.
4. **OpenTelemetry 1.19.1, not 1.20.1.** 1.20.1 (2026-10-20) hardens the exporter: it no longer follows redirects, keeps inline certificate material and invalid endpoint values out of its diagnostics, and bounds how much of a failed response it reads. None of this has an advisory, so the policy selects 1.19.1. 1.20.1 meets the 14-day age on 2026-11-03; the adopting change re-selects, and G-02 should not enable the exporter's experimental disk retry.
5. **Vitest brings Vite and Lightning CSS.** The toolchain closure has 81 npm packages, including Lightning CSS under MPL-2.0, flagged and used unmodified at build and test time. Node.js's built-in test runner with TypeScript alone would avoid about 60 of them; choosing it is a G-02 design decision this review does not make.
6. **Playwright does not verify its browser downloads.** `playwright install` downloads the browser builds that 1.64.1 names (Chromium 156.0.8078.4, Firefox 157.0 and WebKit 27.2) from Playwright's own servers. The Reviewer confirmed in Playwright 1.64.1's source that its fetcher downloads and extracts each archive with no hash check, and that its browser list carries only build revisions. The repository pins none. CI never runs `playwright install`, and a test rejects any workflow or CI script that does; see [What CI-01 pins](#what-ci-01-pins).
7. **`dotnet-ef` has no lock-file hash.** See its SHA-512 above.
8. **Transitive Microsoft.Extensions packages.** In an ASP.NET Core host the shared framework supplies them at 10.0.14. In a class library, OpenTelemetry's dependencies resolve Microsoft.Extensions.Configuration.EnvironmentVariables, Microsoft.Extensions.FileProviders.Abstractions, Microsoft.Extensions.Hosting.Abstractions and Microsoft.Extensions.Logging.Configuration at 10.0.0. No advisory affects 10.0.0, but once those packages are registered `in-use` the runtime-alignment test requires them at 10.0.14, for example through central transitive pinning, or a reviewed exception. The nine Microsoft.Extensions packages that CODE-03's persistence library resolves need neither: EF Core 10.0.14 and its dependencies require each at 10.0.14 or later, so the lock resolves 10.0.14, including for Microsoft.Extensions.Logging.Abstractions, which Npgsql asks for only at 10.0.0 or later.
9. **Testcontainers is reviewed but not used by G-02.** The register already planned it, so this review checked it again at 4.16.0. The operator's G-02 decision (D2) has tests read a connection string and CI run PostgreSQL as a digest-pinned service, with no Docker needed locally, so G-02 does not adopt it. If a later change adopts it, Testcontainers 4.16.0 starts its Ryuk resource-reaper sidecar by default, privileged and with the Docker socket mounted, from `docker.io/testcontainers/ryuk:0.14.0` at the index digest `sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0` hard-coded in the library. That change must either register the sidecar image like the other images and pin it with `TESTCONTAINERS_RYUK_CONTAINER_IMAGE`, or disable it with `TESTCONTAINERS_RYUK_DISABLED=true`, which suits ephemeral runners. A register test enforces this: an in-use Testcontainers package fails unless an in-use `container-image:docker.io/testcontainers/ryuk` entry is pinned by a workflow's `TESTCONTAINERS_RYUK_CONTAINER_IMAGE`, or a workflow sets `TESTCONTAINERS_RYUK_DISABLED` to `true`.
10. **The TypeScript client generator is not selected.** The work order names the client toolchain; the tool that generates the clients is a separate choice for the code work order.
11. **Node.js 26** becomes active LTS on 2026-10-28; the next monthly review decides between lines 24 and 26.

### What CI-01 pins

PLAN-10 listed what CI-01 must pin. On 2026-10-26 CI-01 put the two images and the Node.js archives in use, started no Ryuk sidecar, and deferred the browsers to SEC-08; see [CI-01 adoption](#ci-01-adoption).

- **PostgreSQL test image:** `docker.io/library/postgres:18.6-trixie@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6`. Its linux/amd64 image is `sha256:9770878138a2e83abcb10856d30cc3f24dae0d317bb956db6ab9159de6c3c80d`. The `real-provider` job runs it as its service and names it again as `POSTGRES_IMAGE`, which the job checks against the running service.
- **Node.js 24.22.1 archives,** each downloaded from `https://nodejs.org/dist/v24.22.1/` and verified by SHA-256 before extraction. `NODE_VERSION` in the CI workflow and [.github/node.json](../../.github/node.json) pin them:

  | Archive | SHA-256 |
  | --- | --- |
  | `node-v24.22.1-linux-x64.tar.gz` | `eb6728e77c576a35162c122c45ccadf224d89ff00abc8dae46cc2897c6002d7c` |
  | `node-v24.22.1-linux-arm64.tar.gz` | `33c37be234ba8bee041961047b19160f4d7d0f164e8e246f78bdd10feff148b3` |
  | `node-v24.22.1-darwin-x64.tar.gz` | `d132557e7ee2b85af08b0c8c34338e65c6430f2238d01f727728ef42dc83b035` |
  | `node-v24.22.1-darwin-arm64.tar.gz` | `89c682593d76cd44233fa53b1563264e7de9cbb189cad169135c5b4fadfbc0a7` |
  | `node-v24.22.1-win-x64.zip` | `d2046d28c2c3dcd211ac5145c0bf30e2047ef9dfb306693c8c6e2eccd8b95fa5` |
  | `node-v24.22.1-win-arm64.zip` | `a0c807319fede24c4a0d8f84564c82a619bd313b16b10c007c383c2609e99171` |

- **ASP.NET Core runtime image:** `mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee`. Its linux/amd64 image is `sha256:43cbcf11fd6f88c0375c9c0f7fe59af619abeb323befe5f57dc1502b5a05d8c3`. The `container-build` job's `ASPNET_IMAGE` and the `FROM` of [the container fixture](../../tests/Fixtures/container/Dockerfile) name it, and every Dockerfile in the repository must use it.

- **Playwright browsers:** never from an unverified `playwright install`. Either run the browser tests in the digest-pinned `mcr.microsoft.com/playwright/dotnet:v1.64.1-noble` image, whose index the Reviewer observed at `sha256:6955861f6e17b2580c00f0970198e446f006cdae6c34fb8f9ad5318aaab50da0`, after registering it with the same review as the other images; or download the archives for the browser revisions 1.64.1 names, record their SHA-256 values in the register, and verify each before extracting it into `PLAYWRIGHT_BROWSERS_PATH`. **Deferred to work order SEC-08,** which writes AC-003-N02, the only G-02 case that needs a browser. No browser test exists yet, and which of the two ways fits depends on how that test runs, so CI-01 pins neither; until then the tests reject any `playwright install` in a workflow or CI script.
- **No Testcontainers sidecar:** G-02 starts PostgreSQL as a CI service, so nothing starts Ryuk. A change that adopts Testcontainers must register or disable it first; see exception 9.

The register's `digests` hold the same values for the PostgreSQL and ASP.NET Core images and the six Node.js archives, and the tests require each workflow, pin-file and Dockerfile value to equal them. The Playwright image is not registered: it has not had the review the other images had, so the register holds no digest for it, and SEC-08 registers it, or the browser archives, before any workflow uses them.

## CI-01 adoption

Work order CI-01 put the CI pins in use on 2026-10-26. Four entries moved from `candidate` to `in-use`, and no version or digest changed:

| Entry | Where CI pins it | How CI verifies it |
| --- | --- | --- |
| `binary:node` 24.22.1 | `NODE_VERSION` in the CI workflow and the six archives in `.github/node.json` | Every `build-test` job and the `real-provider` job check the runner's archive against its SHA-256 before extraction and require `node --version` to report `NODE_VERSION` |
| `binary:postgresql` 18.6 | `POSTGRESQL_VERSION` in the `real-provider` job | The job requires the service to report that server release |
| `container-image:docker.io/library/postgres` 18.6 | The `real-provider` job's service image and `POSTGRES_IMAGE` | The service is pulled by the index digest; the job checks that the running service was pulled by it, on linux/amd64 |
| `container-image:mcr.microsoft.com/dotnet/aspnet` 10.0.14 | The `container-build` job's `ASPNET_IMAGE` and the container fixture's `FROM` | The build pulls the base by the index digest for linux/amd64; the job checks that the fixture has exactly the base's layers and reports runtime 10.0.14 |

After CI-01 the register had 53 entries. **In use (29):** the SDK and Node.js (`build`); actions/checkout and gitleaks (`ci`); the PostgreSQL server (`persistence`); the PostgreSQL image and the test project's 3 direct and 19 transitive NuGet packages (`test`); and the ASP.NET Core image (`deployment`). **Candidates (24):** 5 for `persistence`, 3 for `identity`, 2 for `observability`, 1 for `build`, 7 for `test` and 6 for `admin`. [CODE-03 adoption](#code-03-adoption) gives the current counts.

Before adopting them, the Builder fetched both image indexes again by tag, anonymously: each body hashed to its pinned digest, the linux/amd64 manifest it lists hashed to the recorded digest, and each configuration blob hashed to the digest its manifest names, with `PG_VERSION=18.6-1.pgdg13+2`, and `DOTNET_VERSION` and `ASPNET_VERSION` 10.0.14. All six Node.js archives were downloaded again and each matched both its recorded SHA-256 and its line in `SHASUMS256.txt`, whose detached signature again verified, in an empty keyring, as a good signature by key `5BE8A3F6C8A5C01D106C0AD820B1A390B168D356`.

The [CI guide](../ci.md) describes the jobs. The tests require the workflow's image references, `NODE_VERSION`, `POSTGRESQL_VERSION`, the Node.js pin file and every Dockerfile to agree with these entries; see [Checks](#checks).

## CODE-03 adoption

Work order CODE-03 put the persistence packages in use on 2026-10-26. `Modularyn.Framework.Persistence.EfCore` references EF Core, EF Core Relational, Npgsql and Npgsql.EntityFrameworkCore.PostgreSQL through `Directory.Packages.props`, and its test project reaches them through that project. Three candidates moved to `in-use` with their versions unchanged, and the twelve other packages that the locks resolve were added as `in-use`, all in the `persistence` area. No other version changed.

| Entries | Version | Change | Selected by |
| --- | --- | --- | --- |
| `nuget:microsoft.entityframeworkcore` | 10.0.14 | Candidate to `in-use`; direct | Runtime alignment |
| `nuget:npgsql` and `nuget:npgsql.entityframeworkcore.postgresql` | 10.0.3 | Candidates to `in-use`; direct | Age rule, unchanged |
| `nuget:microsoft.entityframeworkcore.relational` | 10.0.14 | Added; direct, and required by the PostgreSQL provider | Runtime alignment |
| `nuget:microsoft.entityframeworkcore.abstractions` and `nuget:microsoft.entityframeworkcore.analyzers` | 10.0.14 | Added; transitive | Runtime alignment |
| Microsoft.Extensions Caching.Abstractions, Caching.Memory, Configuration.Abstractions, DependencyInjection, DependencyInjection.Abstractions, Logging, Logging.Abstractions, Options and Primitives | 10.0.14 | Added; transitive | Runtime alignment |

The persistence library's lock resolves all fifteen. EF Core Relational is a direct reference because the library uses its migration services, migration history, isolation-level transactions and connection itself. The persistence test project's lock holds the library's four direct packages as central transitive dependencies, and EF Core Abstractions and Analyzers; it omits the nine Microsoft.Extensions packages, because the test project references the ASP.NET Core shared framework, which supplies them at 10.0.14.

- **Runtime alignment.** The thirteen Microsoft packages were published on 2026-10-25, one day before review, so each records an `ageException` with both corroborations. First, the 10.0.14 release notes, at dotnet/core commit `60871330fce2b3da383461be027ebdf3ce30f8fb`, list each of them at 10.0.14; they list 157 packages under the rule's prefixes, all at 10.0.14. Second, each nuspec names repository `https://github.com/dotnet/dotnet` at commit `0427b9abbead3fa64c6655eb4290c537397131fa`. The osx-arm64 SDK 10.0.403 archive, downloaded again and matched against its SHA-512 in `.github/dotnet-sdk.json`, records the same commit in the `.version` files of Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0.14, and dotnet/dotnet's `v10.0.14` tag names it. The age rule alone would select 10.0.12, which no advisory affects, and no newer stable version exists, so the security exception does not apply.
- **Npgsql.** Npgsql 10.0.3, published 2026-05-27, and Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, published 2026-07-10, are still each package's newest stable release and meet the 14-day age, so the age rule selects them as PLAN-10 recorded, without an `ageException`.
- **Integrity.** For each of the fifteen, the package that the locked restore used matched nuget.org's catalog SHA-512, and its lock `contentHash` equals the content hash that restore recorded, which for a signed package excludes the signature file. `dotnet nuget verify --all` verified all fifteen: the Microsoft packages carry a Microsoft author signature and the nuget.org repository signature, and the two Npgsql packages carry only the repository signature.
- **Advisories.** The GitHub Advisory Database, queried for each package and for its exact version, lists no advisory affecting any of the fifteen; queries for Npgsql 8.0.2 and Microsoft.Extensions.Caching.Memory 8.0.0 returned their known advisories. Only older versions are affected: GHSA-x9vc-6hfv-hg8c covers Npgsql before 8.0.3 and older lines, and GHSA-qj66-m88j-hmgj covers Microsoft.Extensions.Caching.Memory through 6.0.1 and 8.0.0 and the 9.0 prereleases through 9.0.0-rc.1. nuget.org's vulnerability data, with its base of 2026-10-19 and its update of 2026-10-26, lists those two for the same ranges and nothing else for these packages, and `dotnet list package --vulnerable --include-transitive` reported no vulnerable package in any project.
- **Licenses.** Each nuspec carries an SPDX license expression, which nuget.org's catalog repeats: MIT for the Microsoft packages and PostgreSQL for the Npgsql packages. Both are on the allowlist, so all fifteen are cleared for Community and the paid modules. A redistributed copy lists each in THIRD-PARTY-NOTICES, except EF Core's analyzer package, which holds only a compiler analyzer that applications do not ship.
- **Still candidates.** `Microsoft.EntityFrameworkCore.Design` and `dotnet-ef` remain candidates, because no G-02 code uses design-time services. The change that adopts them checks `dotnet-ef` against the SHA-512 recorded above.
- **One test changed.** The test of the runtime-alignment exception mutates the first runtime-aligned NuGet entry. That entry is now in use, so changing its version also failed the pin and lock checks; the test now picks the first such candidate, `nuget:dotnet-ef`, the entry it used before. `RuntimeAlignmentExceptions` stays empty.

The register has 65 entries. **In use (44):** the SDK and Node.js (`build`); actions/checkout and gitleaks (`ci`); the PostgreSQL server and the 15 persistence NuGet packages (`persistence`); the PostgreSQL image and the test projects' 3 direct and 19 transitive NuGet packages (`test`); and the ASP.NET Core image (`deployment`). **Candidates (21):** 2 for `persistence`, 3 for `identity`, 2 for `observability`, 1 for `build`, 7 for `test` and 6 for `admin`.

## Register format

The root has exactly `version` (the integer `1`) and `entries`. Entries are sorted by status (`in-use` first), then ecosystem in the order `dotnet-sdk`, `nuget`, `npm`, `github-action`, `binary`, `container-image`, then id.

| Field | Meaning |
| --- | --- |
| `id` | `<ecosystem>:<name>` in lowercase; unique. |
| `name` | The NuGet package ID, npm package name, `owner/repository` of an action, the tool name, or an image's `<registry>/<repository>` in lowercase, such as `docker.io/library/postgres`. |
| `ecosystem` | `dotnet-sdk`, `nuget`, `npm`, `github-action`, `binary` or `container-image`. |
| `version` | The exact version: a full SDK version, an exact stable NuGet or npm version, a full 40-character commit SHA for an action, or a stable release number for a binary or for the software an image packages. Ranges, floating versions, wildcards and prerelease labels are rejected. |
| `unpinned` | `null` when a repository file fixes the version, and for every candidate. Otherwise, a note on what selects the version at run time; only an in-use binary without a workflow version pin may have one, and its `version` records the version observed at review. |
| `digests` | `null`, or the SHA-256 digests of artifacts that no lock file, commit SHA or SDK pin file covers, each an object with exactly `target`, an https `url` and `digest` in the form `sha256:<64 lowercase hexadecimal characters>`. Only `binary` and `container-image` entries may have them. A container image records exactly `index`, its multi-platform index fetched by tag, and `linux/amd64`, that platform's manifest fetched by digest, both at the registry's `/v2/<repository>/manifests/` URL; the tag names the version, optionally followed by a variant such as `-trixie`. `binary:node` records the six official archives. Targets and digests are unique within an entry. |
| `license` | An SPDX license expression. `LicenseRef-` identifiers name licenses outside the SPDX list. |
| `status` | `in-use` when the repository uses it, through a pin or, for an `unpinned` entry, through a CI step; `candidate` when it is planned but not used. |
| `area` | `build`, `ci`, `persistence`, `identity`, `observability`, `admin`, `test` or `deployment`. |
| `purpose` | What the dependency does here. |
| `reason` | Why it is listed and why this version, with design identifiers where they apply. |
| `maintenance.reviewedAt` | The review date. |
| `maintenance.published` | When the recorded version was published, from its registry or release; for a container image, when the pinned build was created. |
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

- **NuGet:** versions live only in [Directory.Packages.props](../../Directory.Packages.props) under central package management, with version overrides disabled; project files carry no versions. .NET tools, such as `dotnet-ef`, are pinned in a local tool manifest, which has no lock file, so the change that adopts a tool also checks the restored package's SHA-512 against the value this document records.
- **npm:** when the Admin is added, `package.json` uses exact versions without `^` or `~`, the committed `package-lock.json` is installed with `npm ci`, and install scripts run only for reviewed packages.
- **Actions:** each `uses` names a full 40-character commit SHA, and [actions.allow](../../.github/workflows/actions.allow) lists the same SHAs with their release tags, as [the CI guide](../ci.md) describes.
- **Binaries and images:** CI must download each tool by exact version and verify it against a cryptographic hash pinned in the repository before extraction or use, and container images are referenced by digest. A checksum published beside the download may add a check but never replaces the pinned hash, and no step installs a tool or runtime from a channel or an unpinned version. The .NET SDK archive for each runner is verified by SHA-512 against [.github/dotnet-sdk.json](../../.github/dotnet-sdk.json), the Node.js archive by SHA-256 against [.github/node.json](../../.github/node.json), and gitleaks by SHA-256 against `GITLEAKS_SHA256` in the secret-scan workflow and against its release's checksum file, which must agree. Every other archive and image records its reviewed digests in the register's `digests`; the pin that adopts one must equal the register, as `GITLEAKS_SHA256` must equal the gitleaks entry and `.github/node.json` the Node.js entry, and a workflow or Dockerfile references an image by the tag its index was fetched by and that index digest. An image reference names the multi-platform index digest; see [Which image digest CI pins](#which-image-digest-ci-pins).

### Locked restore and the advisory gate

`RestorePackagesWithLockFile` writes `packages.lock.json` with a SHA-512 content hash for every direct and transitive package. CI restores with `dotnet restore --locked-mode` and never regenerates locks. The register lists every locked package, transitive ones included, so any lock change appears as a register change.

NuGet audit runs during restore by default; for projects targeting .NET 10 it checks all packages, direct and transitive, at low severity and above. Because `TreatWarningsAsErrors` is on, a known advisory against a locked package fails restore; this was confirmed with SDK 10.0.303 during this review. A newly published advisory therefore fails CI until the package is updated or a reviewed suppression is recorded.

### Publication age and the security exception

Select the newest stable version published at least 14 days before the review date. The delay gives registries and maintainers time to withdraw a compromised or broken release before Modularyn depends on it.

**Security exception:** when a newer release fixes an advisory that affects the selected version, or the runtime it ships, select the newest release with the fix, however recent, and record the reason in `ageException`. A version younger than 14 days that a pin already references also records an `ageException`. Prereleases are never selected.

**Runtime alignment:** a Microsoft package or image released with each .NET servicing release, such as EF Core, `dotnet-ef`, the ASP.NET Core and Microsoft.Extensions packages and the `mcr.microsoft.com/dotnet` images, is selected at the servicing version of the runtime that the SDK pin bundles, which `.github/dotnet-sdk.json` names through its release record, when nuget.org or the registry has that version. A version younger than 14 days may be selected this way only when both of these hold, and the review records both:

1. The dotnet/core release record for that servicing release lists the package at that version.
2. The repository commit in the package's nuspec equals the commit in the `.version` file of the shared framework that the pinned SDK installs. For an image, the publisher's image record names the same runtime version.

The pairing of the SDK pin with its runtime rests on the release record. It is established again from that record whenever the SDK pin moves. A younger version records the alignment in `ageException`. A package under those names that follows another release train needs a reviewed, tested exception. See [Runtime alignment](#runtime-alignment).

**Image builds:** official images are rebuilt under the same tag when their base image changes. The 14-day age applies to the version an image packages; the digest pins the newest build of the reviewed tag at review, because an older build lacks the base image's updates, and the entry records that build's creation date and an `ageException` while it is younger than 14 days. A build younger than 14 days may be pinned only when the publisher's own out-of-band record shows the same index and linux/amd64 digests: docker-library/repo-info for Docker Official Images, and dotnet/versions image-info for the .NET images. The review cites that record at its commit. Neither this rule nor runtime alignment widens what may be selected: each only lets a reviewed version's newest corroborated build or aligned servicing release be pinned before the 14 days pass.

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

The review consults the GitHub Advisory Database for each ecosystem (`nuget`, `npm`, `actions` and `go`), the .NET release metadata and release notes for the SDK and runtimes, and the Node.js release notes. Release notes can precede the advisory record: CVE-2026-111497 was in the .NET release notes but not yet in the GitHub Advisory Database during this review. Since PLAN-10 the review also consults nuget.org's vulnerability data, npm audit for resolved npm closures, the [PostgreSQL security page](https://www.postgresql.org/support/security/) for the server, and the release notes of each selected and newer version for security fixes that have no advisory.

An advisory that affects an in-use version is fixed through the security exception without waiting 14 days. The fix is a reviewed change that updates the pin, the lock and the register together.

### Monthly dependency review

Once a month, and whenever a security release affects a recorded dependency, a Builder security session reviews the register:

1. Query each registry for the latest stable version, the latest eligible version, publication dates and deprecation.
2. Query the advisory sources for every recorded version and for the runtime.
3. Compare the versions that the latest CI logs show installed, including the SDK and runtimes each build-test job lists and any `unpinned` entries, with the register.
4. Check repository activity and maintainers against the maintenance criteria.
5. Read the license of every newly selected version, because a version update can change the license.
6. Update `reviewedAt` and the maintenance fields, and record the findings in this document.
7. Propose reviewed updates for in-use entries whose `latestEligible` differs, immediately when an advisory applies.
8. Run locked restore, the Release build, the tests and gitleaks before the change is pushed.

Supply-chain review is security work, done by the Builder's own sessions.

### Adding or changing a dependency

1. Record the dependency as a `candidate` with its full review and clearance. A license outside the allowlist needs a recorded decision before adoption.
2. In the adoption change, add the exact pin and, for a tool that CI downloads or an image it pulls, its pinned hash or digest from the register's `digests`; regenerate the lock as the [CI guide](../ci.md) describes, review the lock diff, add every new transitive package to the register as `in-use`, and move the entry to `in-use`.
3. Run locked restore, the Release build and the tests, require CI on all three platforms at the exact head, and run gitleaks.

The tests fail when a `PackageVersion`, a locked package, an action SHA or a workflow tool version has no matching `in-use` entry, when a tool's pinned hash is missing, malformed or disagrees with `global.json` or the register, when an image reference in a workflow or Dockerfile has no `in-use` entry with that tag and index digest or an `in-use` image is pulled nowhere, and when an `in-use` Testcontainers package has neither a pinned Ryuk image nor a workflow that disables Ryuk.

## SDK and runtime pin

[global.json](../../global.json) pins SDK **10.0.403** with `rollForward: disable` and `allowPrerelease: false`. SDK 10.0.403 was released on 2026-10-25 with the out-of-band security release 10.0.14 and bundles .NET Runtime 10.0.14, ASP.NET Core Runtime 10.0.14 and C# 14.0. .NET 10 is an LTS release in active support until 2028-11-14.

**Why it moved.** The previous pin, SDK 10.0.303 with `latestPatch`, bundled runtime 10.0.11 and predated the security releases 10.0.12, 10.0.13 and 10.0.14. Its feature band, 10.0.3xx, received none of their fixes, because those releases ship only 10.0.4xx and 10.0.1xx SDKs. SDK 10.0.403 is younger than the 14-day publication age and is adopted under the security exception.

**Exact by construction.** With `rollForward: disable`, the .NET host uses exactly the named SDK, and a machine without it fails instead of substituting another. Every SDK patch is therefore a reviewed commit that changes `global.json`, [.github/dotnet-sdk.json](../../.github/dotnet-sdk.json) and this register together, and the monthly dependency review checks for new .NET security releases. The convention tests require an exact stable .NET 10 SDK version, `rollForward: disable` and prereleases disabled; the register test requires the register's SDK entry to equal `global.json`; and the tool-pin tests require the pin file's version and archive URLs to name that version too.

**Provenance.** When the pin changed, the channel release metadata still listed 10.0.13 as the latest release, while the dotnet/core release record for 10.0.14 lists SDK 10.0.403 and its files' SHA-512 hashes. `.github/dotnet-sdk.json` records that record's URL and, for linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64 and win-arm64, the archive URL and SHA-512 it lists. The Builder verified its own osx-arm64 download against them, and SEC-01 found that all six official downloads matched on 2026-10-25. CI selects the archive for the runner's operating system and architecture, downloads it over HTTPS with TLS 1.2 or later, and verifies its SHA-512 before extraction, stopping on a mismatch. It then requires the `dotnet` on `PATH` to be that install and `dotnet --version` to equal `global.json`. Each job's log shows the archive, its verified hash, and the SDK and runtimes installed. The [CI guide](../ci.md#sdk-install) describes the steps and how to update the pin.

**Runtime.** Applications target `net10.0`. A framework-dependent application runs on the newest 10.0 patch runtime installed on its host, so each release states its runtime version in the FND-001 matrix, and container images and self-contained builds pin the runtime exactly (OPS-009). ASP.NET Core Identity, cookie authentication, data protection and antiforgery are part of the ASP.NET Core shared framework, so their security fixes arrive with the runtime. The `global.json` pin covers the SDK and its bundled runtime only, and CI installs nothing else: it extracts the SDK archive into an empty directory that `DOTNET_ROOT` names and that comes first on `PATH`, so the build and tests run on the runtime the SDK bundles, 10.0.14.

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
| Operating-system packages in images | The Debian and Ubuntu packages in a container image, among them GPL and LGPL programs | Flagged | Flagged | The packages are separate programs aggregated in the image, not combined with Modularyn's code, so they place no terms on it. Running an image imposes no obligation. Distributing an image, or one built on it, redistributes the packages: its notices must cover them and source must be available for the copyleft ones, so a release that publishes an image needs this review first. |
| Unknown or missing | No license metadata, or a license file that points elsewhere | Flagged | Flagged | Not cleared until the license text is identified and reviewed. |

### Flagged entries in this register

- **`dotnet-sdk:dotnet`** is `MIT AND LicenseRef-dotnet-library-license AND LicenseRef-windows-sdk-license`. Linux and macOS distributions are MIT. On Windows, `coreclr.dll`, the native symbol reader and WPF native binaries use the .NET Library License, and the WPF shader compiler uses the Windows SDK License. Modularyn builds with the SDK and does not redistribute it, and compiler output carries no SDK restriction. Review before a release redistributes Windows runtime binaries, such as a Windows self-contained or single-file build.
- **`npm:lightningcss`** is `MPL-2.0`. Vite 8 runs it at build time, and Vitest runs on Vite; the CSS it produces is not covered, and as an unmodified tool that Modularyn does not redistribute it imposes no obligation. Modifying its files, or shipping them in any distribution, needs a review and source availability for those files under MPL-2.0.
- **`container-image:docker.io/library/postgres`** is `PostgreSQL AND LicenseRef-debian-packages`: PostgreSQL with the Debian 13 packages of the image. It runs only in tests and is never redistributed.
- **`container-image:mcr.microsoft.com/dotnet/aspnet`** is `MIT AND LicenseRef-ubuntu-packages`: the .NET runtime, MIT on Linux, with the Ubuntu 24.04 packages of the image. A release that publishes a Modularyn image built on it redistributes those packages, so it needs the operating-system package review first. The container-build job's fixture image is built and run on the runner only and never pushed.

The other 61 entries are cleared for both distributions.

### THIRD-PARTY-NOTICES

Nothing is distributed yet, so the repository has no THIRD-PARTY-NOTICES file. Each distributed artifact will carry one, generated from the register and that artifact's lock files at release time: Community packages that bundle third-party files, the Admin bundle, container images, self-contained builds and each paid-module distribution. It lists every bundled component with its version, its license text and any NOTICE content, including notices the component itself carries, such as the .NET runtime's. Build, CI and test dependencies are not redistributed and are not listed, but their licenses are still reviewed here.

Among the current candidates, the Admin's runtime closure would enter the Admin bundle's notices: `react`, `react-dom`, `scheduler`, `react-router`, `cookie-es` and `@remix-run/route-pattern`, all MIT. A container image or self-contained build would also list the .NET runtime, the in-use EF Core and Npgsql packages, and the candidates ASP.NET Core Identity's EF Core stores, OpenTelemetry with the third-party notices its packages carry for code from .NET and gRPC for .NET, and OpenIddict; an image would also list the operating-system packages of its base image.

## Candidates

| Area | Candidates | Design basis |
| --- | --- | --- |
| Persistence | EF Core's design-time package 10.0.14 and `dotnet-ef` 10.0.14, which remain candidates because no G-02 code uses design-time services; EF Core, Npgsql.EntityFrameworkCore.PostgreSQL and Npgsql are in use since [CODE-03](#code-03-adoption) | D-05, DEV-004, ARC-007, ARC-008; migration bundles for OPS-009 |
| Identity | ASP.NET Core Identity from the shared framework with its EF Core stores 10.0.14; OpenIddict.AspNetCore 7.7.2 and OpenIddict.EntityFrameworkCore 7.7.2 | D-08, MOD-001, MOD-002, MOD-023, OPS-003 |
| Observability | OpenTelemetry.Exporter.OpenTelemetryProtocol and OpenTelemetry.Extensions.Hosting 1.19.1, beside the built-in .NET diagnostics | Decision D4; AC-025's local diagnostics |
| Build | TypeScript 7.0.2 | AC-008 for G-02's TypeScript client; D-06 |
| Tests | Microsoft.Playwright 1.64.1; Vitest 5.0.4 with Vite 8.3.4 and Lightning CSS 1.33.0; @playwright/test 1.64.0; Testcontainers and Testcontainers.PostgreSql 4.16.0, reviewed but not used by G-02 (decision D2) | DEV-023, T-058; real-provider tests for G-02 and UI tests for gate W |
| Admin | React and react-dom 19.3.0; React Router 8.4.0; @vitejs/plugin-react 6.1.2; @types/react and @types/react-dom 19.3.0 | D-06, T-015 |

Candidates are limited to what G-02 and gate W need, plus the Admin toolchain the work order names. Beyond the named packages, the list adds only what they cannot work without: EF Core's design-time package and tool for migration bundles, the PostgreSQL server and image the real-provider tests run against, the Node.js runtime and the ASP.NET Core runtime image, which were candidates until CI-01 put them in use, the OpenTelemetry hosting package the exporter needs, Vite, which Vitest requires, Vite's React plugin, React's type declarations, and Lightning CSS, which Vite 8 requires and which needs its own flag. The gate W Razor site uses the ASP.NET Core shared framework and adds no package.

**Admin closure.** Resolving the Admin candidates at their exact versions with lock-only npm resolution and no install scripts gave 94 packages on 2026-10-25: 54 MIT, 26 Apache-2.0, 12 MPL-2.0 (Lightning CSS and its 11 platform packages), 1 ISC and 1 BSD-3-Clause. No GitHub advisory affects any resolved version. The register records the direct candidates and Lightning CSS; the full closure is registered when the Admin's lock file is committed. The tests need an npm lock check by then, so they reject `in-use` npm entries today. G-02's TypeScript client toolchain is a separate closure of 81 packages; see [Method](#method).

**NuGet candidate closures.** The G-02 review restored every NuGet candidate except OpenIddict at its exact version in a scratch project; see [Method](#method). EF Core's design-time package brings Roslyn workspaces 5.0.0, Microsoft.Build.Framework 18.0.2, Mono.TextTemplating 3.0.0, Humanizer.Core 2.14.1 and Newtonsoft.Json 13.0.4, and Testcontainers brings Docker.DotNet.Enhanced 4.3.3, SSH.NET 2026.0.0 with BouncyCastle.Cryptography 2.7.0, and SharpZipLib 1.4.2. All are MIT, and no advisory affects those versions. The complete closures are registered from the lock files at adoption, as [CODE-03](#code-03-adoption) did for EF Core and the Npgsql packages.

**Integration notes.** G-02's real-provider tests do not use Testcontainers: under decision D2 they read the connection string in `MODULARYN_TEST_POSTGRESQL`, and CI runs PostgreSQL as a workflow service pinned by digest (see the [CI guide](../ci.md#real-provider-tests)). A later change that adopts Testcontainers must pass the reviewed PostgreSQL image explicitly, pinned by digest, because Testcontainers.PostgreSql's parameterless builder and its default `postgres:15.1` image are obsolete, and must register or disable Ryuk first (exception 9). Playwright installs browser builds that carry their own licenses; they are for tests only and never redistributed, and because Playwright does not verify their downloads (exception 6), CI does not install them until SEC-08 pins them. Node.js 26 becomes active LTS on 2026-10-28, so the next monthly review decides between lines 24 and 26.

## Identity: trade-off and recommendation

The authentication requirements (OPS-003, MOD-001, MOD-002, MOD-023) need local accounts with passkeys, MFA, lockout and recovery; authorization code with PKCE; a backend-for-frontend session cookie for the canonical Admin; scoped machine clients; and consent, revocation and signing-key rollover.

- **ASP.NET Core Identity alone** covers accounts, credentials, passkeys and cookie sessions from the shared framework, but it is not an OpenID Connect authorization server. It cannot serve external or machine clients with authorization code and PKCE as MOD-023 requires, so it is necessary but not sufficient.
- **ASP.NET Core Identity with OpenIddict 7.7.2**, the design's recommended foundation (D-08), adds an Apache-2.0 server, validation and client with EF Core stores on the persistence baseline. It has monthly releases and no advisories. Its costs: maintenance is concentrated in one principal maintainer, Kévin Chalet, with 1,965 commits against 9 for the next human contributors; it is a lower-level framework, so Modularyn owns the endpoints, sign-in and consent UI, key management and protocol configuration, and must prove PKCE, consent, audience and scope enforcement, redirect validation, revocation and key rollover itself (MOD-023); and version 8.0 is in preview, so a major upgrade lies ahead.
- **ASP.NET Core Identity with Duende IdentityServer** offers a commercially supported product. Its newest eligible version is 8.0.9 (2026-09-28). Its package license file points to Duende's terms on its website, which is outside this review's sources; the package depends on `Duende.Private.Licensing`, and GitHub does not identify its license. Requiring it would subject every Community user to third-party commercial terms, which Community's free commercial use excludes, and would pass those terms to every paid-module licensee. It is rejected as a dependency for both distributions. Its earlier advisories, GHSA-ff4q-64jc-gx98 and GHSA-v9xq-2mvm-x8xc, affected 6.x and 7.x releases.

**Recommendation, not a final selection:** use ASP.NET Core Identity from the shared framework with Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.14, and OpenIddict 7.7.2 through OpenIddict.AspNetCore and OpenIddict.EntityFrameworkCore, behind MOD-023's protocol adapter seam. For the Admin's backend-for-frontend session, OpenIddict's client, which OpenIddict.AspNetCore includes, avoids another dependency; Microsoft's OpenID Connect handler is the alternative and would be registered when chosen. To contain the maintainer concentration, keep protocol details behind the adapter, pin exact versions, review each release monthly, and keep the MOD-023 acceptance suite complete enough to qualify a replacement. G-02 confirms or replaces this choice with evidence.

## Checks

[DependencyRegisterTests.cs](../../tests/Modularyn.Repository.Tests/DependencyRegisterTests.cs) validates the register through the existing repository-file discovery helper, with no new packages. It requires:

- the `dotnet-sdk` entry to equal `global.json`;
- every `PackageVersion` in `Directory.Packages.props`, and every direct or transitive package in each `packages.lock.json`, to have an `in-use` NuGet entry at the same version, and every `in-use` NuGet entry to appear in a lock at its version;
- every action SHA in the workflows and `actions.allow` to have an `in-use` action entry, and every such entry to be used;
- every workflow tool version variable, such as `GITLEAKS_VERSION`, `NODE_VERSION` or `POSTGRESQL_VERSION`, to match an `in-use` binary entry, and every `in-use` binary entry either to match a variable or to explain in `unpinned` what selects its version, with `unpinned` set to `null` everywhere else;
- exact versions for each ecosystem, with no ranges, floating versions, wildcards or prerelease labels;
- each license to be an SPDX expression that the allowlist above permits, or else to be flagged for both distributions with a note, and the allowlist table to equal the test's reviewed set;
- candidates to record their `latestEligible` version, and an `ageException` exactly when the recorded version was published fewer than 14 days before review;
- `digests` to be `null` or unique `sha256:<64 lowercase hexadecimal characters>` values with https URLs, only on `binary` and `container-image` entries; each container image to record exactly its index, at a registry manifest URL whose tag names the version, and its linux/amd64 manifest, at the URL of that digest; and `binary:node` to record the six official archives of its version;
- every image reference in a workflow or Dockerfile to name an `in-use` container-image entry by the tag its index was fetched by and its index digest, and every `in-use` image to be pulled somewhere at that reference;
- an `in-use` Testcontainers package to have an `in-use` `container-image:docker.io/testcontainers/ryuk` entry that a workflow pins with `TESTCONTAINERS_RYUK_CONTAINER_IMAGE`, or a workflow that sets `TESTCONTAINERS_RYUK_DISABLED` to `true`;
- every runtime-aligned entry to equal the runtime release that `.github/dotnet-sdk.json` names, unless the test's `RuntimeAlignmentExceptions` lists it with a reason, and every listed exception to name an entry that differs;
- every entry of the G-02 selection to be registered and reviewed on or after 2026-10-26; and
- exact field sets, unique ids, sorted entries, ISO dates, unique https sources, and portable single-line review text.

In-memory mutations exercise each rule and assert its specific diagnostic without writing invalid files. Direct checks read the register again for the digests, the G-02 selection and runtime alignment, and the exception mechanism is tested with and without a reviewed exception. A line-ending case checks portability across platforms. The existing CI workflow runs these tests on Linux, Windows and macOS.

[ToolPinTests.cs](../../tests/Modularyn.Repository.Tests/ToolPinTests.cs) checks the hashes and digests pinned for what CI downloads and pulls, and the jobs that use them, with its own in-memory mutations and line-ending case. It requires:

- `.github/dotnet-sdk.json` to name exactly linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64 and win-arm64, its version to equal `global.json`, and its source to be the dotnet/core `release.json` record for that version's channel, which the register's SDK entry cites;
- each archive URL to be the official `https://builds.dotnet.microsoft.com/dotnet/Sdk/<version>/` archive for its RID, `.zip` for Windows and `.tar.gz` otherwise, and each SHA-512 to be 128 lowercase hexadecimal characters, distinct from the others;
- the CI workflow to read that file, and no workflow or allow-list line to name actions/setup-dotnet or the dotnet-install script;
- the secret-scan workflow to pin `GITLEAKS_SHA256` once, as 64 lowercase hexadecimal characters, to check the archive against it with `sha256sum --check --strict`, and to pin a `GITLEAKS_VERSION` equal to the register's in-use `binary:gitleaks` version; and
- the register's `binary:gitleaks` entry to record that digest for the release's Linux x64 archive URL;
- `.github/node.json` to have the workflow's single `NODE_VERSION` and the in-use `binary:node` version, the release's `SHASUMS256.txt` as its source, cited by the register, and exactly the six platforms, each with its official archive URL and a distinct lowercase SHA-256 equal to the register's digest for that platform, and the CI workflow to read it;
- every workflow `image` to be `<registry>/<repository>:<tag>@sha256:<index digest>`, no job container to be given inline, and the `real-provider` job's only service image and its `POSTGRES_IMAGE` to equal the register's PostgreSQL reference;
- the `real-provider` job to run on `ubuntu-latest`, start its service with `POSTGRES_HOST_AUTH_METHOD` `reject` and a `pg_isready` health check, set `POSTGRESQL_VERSION` to the version of both PostgreSQL entries, and run the SDK and Node.js steps exactly as `build-test` does;
- only its `Test against PostgreSQL` step, after its Build step, to run `.github/scripts/postgresql-test-database.sh`, which must generate and mask the password and must not write to `GITHUB_ENV` or `GITHUB_OUTPUT`, and that step to run `.github/scripts/real-provider-tests.sh` as its command;
- that tests script to run `dotnet test` only with `Requires=PostgreSQL`, on every test project with its own results file, and to require at least one PostgreSQL test to have run and every one to have passed, which a behavioral test of its `count` mode also checks on Linux and macOS; neither script may write to `GITHUB_ENV`, `GITHUB_OUTPUT`, `GITHUB_STATE` or `GITHUB_PATH`;
- every `dotnet test` in the `build-test` job to select `Requires!=PostgreSQL`, and any in the `real-provider` job to select `Requires=PostgreSQL`;
- the `container-build` job to run on `ubuntu-latest`, set `ASPNET_IMAGE` to the register's ASP.NET Core reference and build the fixture with `docker build --platform=linux/amd64`, and every Dockerfile, found by name in any letter case, to have a `FROM` and use only that reference or earlier stages, with no other platform, no parser directive, even behind a byte-order mark, no copy from another image, no `ADD` of a git or ssh source, and no `ADD` of an http(s) or variable source without one source and a pinned `--checksum=sha256:`;
- every line in the workflows and CI scripts that names docker, or a guarded image variable or docker array, to be one of the exact lines in the contract's allowlist, once, with no `--build-context`, `docker-image://`, URL, git or standard-input context and no `eval`, shell `-c` or pipe into a shell elsewhere, and each guarded variable defined once as a literal in its job's `env` and named nowhere else, as the [CI guide](../ci.md#docker-commands) describes with its limits; and
- no workflow or CI script to run `playwright install`.

The checks cannot establish that a version is still current, that an advisory search is complete, that a license analysis is correct or that a pinned hash is the publisher's; the monthly review and the release records provide that, and only a CI run shows that each runner's download matches its pin.

## What this review could not verify

- **Duende's license terms.** The package's license file refers to terms on Duende's website, which is outside the permitted sources.
- **CVE-2026-111497 in the advisory database.** It still had no GitHub advisory record on 2026-10-26, so the SDK finding relies on the dotnet/core release notes; by then the channel release metadata listed 10.0.14 as the latest release.
- **Operating-system packages in the images.** The Debian packages of the PostgreSQL image and the Ubuntu packages of the ASP.NET Core image were not scanned for vulnerabilities, and their licenses were not reviewed package by package. A release that ships the deployment image scans and reviews them.
- **The Node.js release key.** The signature verifies with the key that the nodejs/node README and nodejs/release-keys publish, but GnuPG found no certification chain to it; its authenticity rests on those GitHub sources.
- **Registry retention.** The pins assume the registries keep serving these manifests by digest; this review did not establish how long Docker Hub and the Microsoft Artifact Registry retain a manifest after its tag moves.
- **Browser builds.** The licenses of the browser builds that Playwright 1.64.1 installs were not reviewed; they are for tests only. Playwright does not verify their downloads (exception 6), so CI does not install them, and SEC-08 pins them before the first browser test runs in CI.
- **Complete candidate closures.** The NuGet and npm closures of the G-02 candidates were resolved in scratch projects but are registered entry by entry only at adoption, from the committed lock files; OpenIddict's closure was not resolved, because G-02 does not use it.
- **The runtime CI executes.** Before SEC-01, the CI logs showed runtime 10.0.13 from the LTS channel installed beside the SDK's 10.0.14, but not which of them ran the build and tests. CI now installs only the SDK archive and lists its SDKs and runtimes in each job's log.
- **Archives that CI does not download.** The hosted runners are Linux x64, Windows x64 and macOS arm64, so no CI run checks the linux-arm64, osx-x64 and win-arm64 SDK archives or the linux-arm64, darwin-x64 and win-arm64 Node.js archives. SEC-01 downloaded all six SDK archives on 2026-10-25 and CI-01 all six Node.js archives on 2026-10-26, and each matched its pinned hash; a runner of another platform still checks its archive on every install.
- **Runner images.** The contents of GitHub's hosted runner images were not reviewed, including the Docker engine that pulls and runs the pinned images; its job-log version is the available provenance.
- **Provenance of the CODE-03 packages.** The repository commits in the nuspecs are metadata that the publishers supply; no build from those commits was compared with the packages. The two Npgsql packages carry no author signature, so their link to the Npgsql maintainers rests on the nuget.org account that published them. Only the osx-arm64 SDK archive was downloaded again for the `.version` check; SEC-01 matched all six archives against their pins on 2026-10-25.
