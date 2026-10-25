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

The SDK is pinned to 10.0.303 with `latestPatch` roll-forward and prereleases disabled. This permits servicing patches within its feature band. xUnit's VSTest adapter runs the tests through `Microsoft.NET.Test.Sdk`; the project explicitly disables the Microsoft Testing Platform application mode.

Every workflow action uses a full 40-character commit SHA. Keep each static `uses` property on its own line and use plain YAML keys without escape sequences or complex key syntax; flow mappings, folded values, aliases, and expressions for action selection are rejected. Literal quotes around `uses` and its pinned value are supported. `.github/workflows/actions.allow` contains exactly the used actions with release tags in comments. Verify the upstream commit and tag when updating either a workflow or the allow list, and change both together.

The secret-scan job downloads gitleaks 8.30.1 for Linux x64 and its versioned release checksums, requires exactly one checksum for the archive, verifies SHA-256 before extraction, then scans all fetched Git history with redaction. To check local history with the same installed version, run:

```sh
gitleaks git --log-opts="--all --full-history" --redact --no-banner .
```

## Foundation verification handoff

The initial package versions and lock data were selected from local NuGet metadata and cached packages without network access. Package archive hashes were checked, and the lock accounts for .NET 10 package pruning. The lock was assembled without running restore and needs the Builder's locked-restore validation. An offline inspection cannot establish that upstream metadata remains unchanged. The Builder must validate locked restore, Release build, and tests, including the negative convention cases. CI must then pass on all three platforms at the exact reviewed head, and gitleaks must report no findings. Record the commit and check results before claiming the foundation passes. Preparing these files does not authorize a push, merge, release, or publication.
