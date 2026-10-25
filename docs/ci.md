# Repository checks

CI runs for every pull request and every push to `main`, without path filters. Both workflows check out the pull request's exact head commit or the pushed commit. Superseded runs for the same workflow and branch are canceled.

The required GitHub job check names are:

- `build-test (ubuntu-latest)`
- `build-test (windows-latest)`
- `build-test (macos-latest)`
- `secret-scan`

Use these names when configuring the repository's checked changes. This document records the intended checks; it does not configure branch protection or claim a successful run. Require all four checks at the same exact commit before accepting the foundation.

The build matrix restores with `dotnet restore --locked-mode`, builds with `dotnet build -c Release --no-restore`, and tests with `dotnet test -c Release --no-build`. Standard GitHub-hosted runners serve the public repository. Workflows have read-only contents permission and do not retain checkout credentials.

## Conventions

The test assembly locates the repository by walking upward to `Modularyn.slnx`. The tests enforce central build settings and package versions, the SDK pin, complete license text, portable text files, and the exact action allow list. Local checks include tracked files and untracked files that Git does not ignore. Generated build output and local coordination data are excluded through `.gitignore`.

The convention validators also receive deliberately invalid content in memory. Those tests require each violation to be rejected, including project overrides, floating packages, incomplete license text, machine paths, and workflow actions that are unpinned or absent from the allow list. These cases do not write invalid files to the repository.

## Dependency and action updates

Change test-package versions only in `Directory.Packages.props`. Use stable exact versions. For an intentional dependency update, an authorized developer runs `dotnet restore --force-evaluate`, reviews the resulting `packages.lock.json` changes, then repeats locked restore, Release build, and tests. Commit the reviewed lock file with the package change. Normal CI never regenerates dependency locks.

`global.json` pins an exact stable .NET 10 SDK with `rollForward: disable` and prereleases disabled, so the .NET host and setup-dotnet use exactly that SDK and every SDK change is a reviewed commit; the convention tests require that shape rather than one specific version, and the [dependency register](dependencies/README.md) records the pinned version. xUnit's VSTest adapter runs the tests through `Microsoft.NET.Test.Sdk`; the project explicitly disables the Microsoft Testing Platform application mode.

Every workflow action uses a full 40-character commit SHA. Keep each static `uses` property on its own line and use plain YAML keys without escape sequences or complex key syntax; flow mappings, folded values, aliases, and expressions for action selection are rejected. Literal quotes around `uses` and its pinned value are supported. `.github/workflows/actions.allow` contains exactly the used actions with release tags in comments. Verify the upstream commit and tag when updating either a workflow or the allow list, and change both together.

The secret-scan job downloads gitleaks 8.30.1 for Linux x64 and its versioned release checksums, requires exactly one checksum for the archive, verifies SHA-256 before extraction, then scans all fetched Git history with redaction. To check local history with the same installed version, run:

```sh
gitleaks git --log-opts="--all --full-history" --redact --no-banner .
```
