# Repository checks

CI runs for every pull request and every push to `main`, without path filters. Both workflows check out the pull request's exact head commit or the pushed commit. Superseded runs for the same workflow and branch are canceled.

The required GitHub job check names are:

- `build-test (ubuntu-latest)`
- `build-test (windows-latest)`
- `build-test (macos-latest)`
- `secret-scan`

Use these names when configuring the repository's checked changes. This document records the intended checks; it does not configure branch protection or claim a successful run. Require all four checks at the same exact commit before accepting the foundation.

The build matrix installs the pinned .NET SDK as [SDK install](#sdk-install) describes, then restores with `dotnet restore --locked-mode`, builds with `dotnet build -c Release --no-restore`, and tests with `dotnet test -c Release --no-build`. Standard GitHub-hosted runners serve the public repository. Workflows have read-only contents permission and do not retain checkout credentials.

## SDK install

CI uses no setup action for .NET. Each `build-test` job installs the SDK that `global.json` pins from the official archive recorded in [.github/dotnet-sdk.json](../.github/dotnet-sdk.json):

1. It selects the archive from `runner.os` and `runner.arch`: Linux selects `linux-x64` or `linux-arm64`, macOS selects `osx-x64` or `osx-arm64`, and Windows selects `win-x64` or `win-arm64`, for X64 and ARM64 respectively. Any other runner fails the job. `ubuntu-latest` and `windows-latest` are x64 runners and `macos-latest` is an arm64 runner.
2. It reads the archive's URL and SHA-512 from the pin file, and fails unless the pin file's version equals `global.json` and the URL is the official archive for that version and runtime identifier.
3. It downloads the archive over HTTPS with TLS 1.2 or later: with `curl --proto '=https' --tlsv1.2` on Linux and macOS, and with `Invoke-WebRequest -SslProtocol Tls12, Tls13` on Windows.
4. It verifies the SHA-512 and stops on a mismatch before extracting anything.
5. It extracts the archive into an empty `$RUNNER_TEMP/dotnet`, sets `DOTNET_ROOT` to that directory and prepends it to `PATH`.
6. The next step fails unless the `dotnet` on `PATH` is that install and `dotnet --version` equals `global.json`, then lists the installed SDKs and runtimes.

Linux and macOS run these steps in bash, and Windows runs them in PowerShell. Every failure is reported as an error annotation that names its cause. No other step installs an SDK or a runtime, so the build and tests run on the runtime that the SDK bundles.

### Updating the SDK pin

The pin file's values come from the dotnet/core release record named by its `source`: the `release.json` of the .NET release that ships the SDK, under `release-notes/<channel>/<release>/` in [dotnet/core](https://github.com/dotnet/core). That record lists each SDK's files with their runtime identifier, URL and SHA-512 hash. To change the SDK:

1. Set `version` to the new SDK version and `source` to the new release's record. From that record's files for the new SDK, copy the URL and hash of the `.tar.gz` archive for each Linux and macOS runtime identifier, and of the `.zip` archive for each Windows one.
2. Download the archives and check each against its recorded hash.
3. Change `global.json`, `.github/dotnet-sdk.json` and the [dependency register](dependencies/README.md), whose SDK entry cites the new record, in one reviewed commit, and require CI on all three platforms at its exact head.

The tests require the pin file to name exactly the six runtime identifiers, its version to equal `global.json`, every URL to be the official archive of that version for its runtime identifier, and every hash to be 128 lowercase hexadecimal characters.

## Conventions

The test assembly locates the repository by walking upward to `Modularyn.slnx`. The tests enforce central build settings and package versions, the SDK pin and its archive hashes, the gitleaks digest, complete license text, portable text files, and the exact action allow list. Local checks include tracked files and untracked files that Git does not ignore. Generated build output and local coordination data are excluded through `.gitignore`.

The convention validators also receive deliberately invalid content in memory. Those tests require each violation to be rejected, including project overrides, floating packages, incomplete license text, machine paths, workflow actions that are unpinned or absent from the allow list, and tool hashes that are missing, malformed or out of step with `global.json` or the register. These cases do not write invalid files to the repository.

## Dependency and action updates

Change test-package versions only in `Directory.Packages.props`. Use stable exact versions. For an intentional dependency update, an authorized developer runs `dotnet restore --force-evaluate`, reviews the resulting `packages.lock.json` changes, then repeats locked restore, Release build, and tests. Commit the reviewed lock file with the package change. Normal CI never regenerates dependency locks.

`global.json` pins an exact stable .NET 10 SDK with `rollForward: disable` and prereleases disabled, so the .NET host and CI's install use exactly that SDK and every SDK change is a reviewed commit; the convention tests require that shape rather than one specific version, and the [dependency register](dependencies/README.md) records the pinned version. xUnit's VSTest adapter runs the tests through `Microsoft.NET.Test.Sdk`; the project explicitly disables the Microsoft Testing Platform application mode.

Every workflow action uses a full 40-character commit SHA. Keep each static `uses` property on its own line and use plain YAML keys without escape sequences or complex key syntax; flow mappings, folded values, aliases, and expressions for action selection are rejected. Literal quotes around `uses` and its pinned value are supported. `.github/workflows/actions.allow` contains exactly the used actions with release tags in comments. Verify the upstream commit and tag when updating either a workflow or the allow list, and change both together.

The secret-scan job downloads gitleaks 8.30.1 for Linux x64 and checks the archive with `sha256sum --check --strict` against `GITLEAKS_SHA256`, the digest pinned in the workflow. It also downloads the release's versioned checksums, requires exactly one checksum for the archive, fails if that checksum differs from the pinned digest, and checks the archive against it. Only then does it extract gitleaks and scan all fetched Git history with redaction. To update gitleaks, change `GITLEAKS_VERSION` and `GITLEAKS_SHA256` together, taking the digest from a fresh download that matches the release's checksum file, and update the register's gitleaks entry in the same commit. To check local history with the same installed version, run:

```sh
gitleaks git --log-opts="--all --full-history" --redact --no-banner .
```
