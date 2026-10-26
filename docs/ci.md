# Repository checks

CI runs for every pull request and every push to `main`, without path filters. Both workflows check out the pull request's exact head commit or the pushed commit. Superseded runs for the same workflow and branch are canceled.

The required GitHub job check names are:

- `build-test (ubuntu-latest)`
- `build-test (windows-latest)`
- `build-test (macos-latest)`
- `real-provider`
- `container-build`
- `secret-scan`

Use these names when configuring the repository's checked changes. This document records the intended checks; it does not configure branch protection or claim a successful run. Require all six checks at the same exact commit before accepting a change.

The build matrix installs the pinned .NET SDK as [SDK install](#sdk-install) describes and the pinned Node.js as [Node.js install](#nodejs-install) describes, then restores with `dotnet restore --locked-mode`, builds with `dotnet build -c Release --no-restore`, and tests with `dotnet test -c Release --no-build --filter "Requires!=PostgreSQL"`. Windows and macOS are build and unit-test platforms, not qualified hosts. The `real-provider` job runs the tests that need PostgreSQL on Linux x64, as [Real-provider tests](#real-provider-tests) describes, and the `container-build` job builds the container fixture, as [Container build](#container-build) describes. Standard GitHub-hosted runners serve the public repository. Workflows have read-only contents permission, use only the allowed actions, and do not retain checkout credentials. No step interpolates a pull-request value into a script: the only expressions in steps are the checkout ref and the service container's id and port, which reach the scripts through the environment.

## SDK install

CI uses no setup action for .NET. Each `build-test` job, and the `real-provider` job with the same Linux steps, installs the SDK that `global.json` pins from the official archive recorded in [.github/dotnet-sdk.json](../.github/dotnet-sdk.json):

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

## Node.js install

CI uses no setup action for Node.js either. `NODE_VERSION` in the CI workflow pins the release, and [.github/node.json](../.github/node.json) records the official archive and its SHA-256 for linux-x64, linux-arm64, darwin-x64, darwin-arm64, win-x64 and win-arm64. Each `build-test` job and the `real-provider` job install it the way they install the SDK:

1. They select the archive from `runner.os` and `runner.arch`, and fail on any other runner.
2. They fail unless the pin file's version equals `NODE_VERSION`, the URL is `https://nodejs.org/dist/v<version>/node-v<version>-<platform>.tar.gz`, or `.zip` on Windows, and the SHA-256 is 64 lowercase hexadecimal characters.
3. They download the archive over HTTPS with TLS 1.2 or later, verify its SHA-256 and stop on a mismatch before extracting anything.
4. They extract it into an empty `$RUNNER_TEMP/node` and prepend its executables to `PATH`. The next step fails unless `node` and its bundled `npm` resolve to that install and `node --version` reports `NODE_VERSION`.

Nothing uses Node.js yet. The TypeScript client of work order CODE-06 will build and test with it on every platform, and its real-provider cases run in the `real-provider` job. The runner images' own Node.js is never used.

### Updating the Node.js pin

The archive hashes are the values in the release's `SHASUMS256.txt`, whose detached signature must verify with a Node.js release key, and which the register's `binary:node` entry cites. To change the release, download each of the six archives and check it against that file, then change `NODE_VERSION`, `.github/node.json` and the register's `binary:node` version and digests in one reviewed commit, and require CI on all three platforms at its exact head.

The tests require the pin file's version to equal `NODE_VERSION` and the register, its source to be that release's `SHASUMS256.txt` and to be cited by the register, exactly the six platforms with their official URLs and distinct lowercase SHA-256 values, each equal to the register's digest for that platform, and the CI workflow to read the pin file.

## Real-provider tests

Tests that need PostgreSQL carry the xUnit trait `Requires=PostgreSQL`. The `real-provider` job runs exactly those, with `--filter "Requires=PostgreSQL"`, and every other job excludes them. A test with the trait fails, rather than skips, when no database is configured, so the job cannot pass without PostgreSQL. Today the only such test is the environment probe in [PostgreSqlEnvironmentTests](../tests/Modularyn.Repository.Tests/PostgreSqlEnvironmentTests.cs); the real-provider cases of the [G-02 evidence plan](evidence/g02-plan.json) join it as their work orders add them.

### The connection-string convention

Tests read one environment variable, `MODULARYN_TEST_POSTGRESQL`, in both CI and local runs. It holds an Npgsql keyword connection string with exactly `Host`, an optional `Port` (5432 by default), `Database`, `Username` and `Password`. The host must be `localhost`, `127.0.0.1` or `::1` and must resolve only to loopback addresses, so real-provider tests never reach a remote server. The database belongs to a role created for the run: the role can sign in and owns its database, and it is not a superuser and cannot create roles or databases. Tests isolate their data within that database, for example by schema.

The probe test signs in with the variable's credentials over TCP and checks that the server is PostgreSQL 18, that the session's database and user are the ones named, that the user is not a superuser and that the encoding is UTF8. It sends a password only through SCRAM-SHA-256 and verifies the server's SCRAM signature; it refuses cleartext and MD5 requests without sending the password. No message, log line or `ToString` contains the password.

### In CI

The `real-provider` job runs on `ubuntu-latest`, a Linux x64 runner, with a PostgreSQL service from `docker.io/library/postgres:18.6-trixie` at its pinned index digest, published only on the runner's `127.0.0.1:5432`. The service starts with `POSTGRES_HOST_AUTH_METHOD=reject`, so no host connection can sign in until the job has created its credentials, and a `pg_isready` health check gates the job's steps. The image needs a superuser password to initialize; the job gives it the run's public id, which never authenticates anything.

After the SDK and Node.js steps, which are the same as `build-test`'s on Linux and which a test keeps identical, `bash .github/scripts/postgresql-test-database.sh ci`:

1. fails unless the runner is Linux x64 and the service runs `POSTGRES_IMAGE`, the same reference as the service image, pulled by its index digest, on linux/amd64, and reports PostgreSQL `POSTGRESQL_VERSION`, 18.6;
2. generates a role and database name and a 64-character hexadecimal password from `/dev/urandom` and masks the password in the log;
3. creates the role and its database through the container's Unix socket, with statement logging off for that session, and removes the bootstrap superuser password;
4. replaces the service's `pg_hba.conf` so that the socket admits only the container's postgres user and host connections admit only the test role, to its database, with SCRAM-SHA-256, then checks that the file has no errors, prints its rules to the log and reloads it;
5. checks that the test role signs in over TCP and that the superuser cannot; and
6. appends `MODULARYN_TEST_POSTGRESQL` for `127.0.0.1` and the service's port to `GITHUB_ENV`.

The job then restores, builds and runs the PostgreSQL tests with detailed console output, so the log records each test's observations, such as the probe's sign-in method and server version. The repository tests check the image, the server version and the test filters against the [dependency register](dependencies/README.md).

### Local runs

Local runs use a PostgreSQL 18 server listening on localhost, such as Homebrew's `postgresql@18` (decision D2); no Docker is needed. With the server running and a superuser reachable through libpq's defaults, or through `PGHOST`, `PGPORT` and `PGUSER`, run:

```sh
bash .github/scripts/postgresql-test-database.sh run dotnet test -c Release --no-build
```

The script checks that the server is PostgreSQL 18, creates a role and database with generated names and password exactly as CI does, runs the command with `MODULARYN_TEST_POSTGRESQL` set, and drops both when the command ends, whatever its result. It prints neither the password nor the connection string. If the local server trusts loopback connections, as Homebrew's default configuration does, the script says so: that server does not check the generated password, so only CI exercises SCRAM sign-in. Without a local PostgreSQL, exclude the PostgreSQL tests explicitly with `--filter "Requires!=PostgreSQL"`; nothing skips them silently.

## Container build

The `container-build` job checks every Dockerfile in the repository, then builds [the container fixture](../tests/Fixtures/container/Dockerfile), the smallest image on the ASP.NET Core runtime base:

1. It lists Dockerfiles with `git ls-files` (`*Dockerfile`, `*Dockerfile.*`, `*.dockerfile`, `*Containerfile` and `*Containerfile.*`) and fails if there is none. In each, every `FROM` must name `ASPNET_IMAGE`, which is `mcr.microsoft.com/dotnet/aspnet:10.0.14-noble` at its pinned index digest, or an earlier stage, and its only allowed option is `--platform=linux/amd64`. Every `--from` and mount `from=` must name an earlier stage or stage index, and no `syntax` directive may select a frontend image.
2. It builds the fixture with `docker build --platform linux/amd64 --pull --no-cache`.
3. It fails unless the image is linux/amd64, runs as a numeric non-root user, has exactly the layers of the pinned base, which it pulls by the same reference, and reports exactly the ASP.NET Core and .NET runtimes of the tag's version, 10.0.14, when run without a network.

The repository tests apply the same Dockerfile rules on every platform and require `ASPNET_IMAGE` to equal the register's reference. A build of the sample application therefore publishes it with the pinned SDK and copies the output into an image on that base, rather than building in an SDK image that nothing pins. Work order CODE-09 adds the sample application's Dockerfile, which the job's check already covers, and the step that builds it.

## Browsers

No workflow installs browsers. Playwright 1.64.1 downloads its browser builds without verifying a hash, so CI never runs `playwright install`, and the tests reject a workflow or CI script that does. The first browser test is AC-003-N02, written by work order SEC-08; that order pins the browsers before its test runs in CI, as the digest-pinned Playwright image or as hash-verified browser archives, as [What CI-01 pins](dependencies/README.md#what-ci-01-pins) requires.

## Conventions

The test assembly locates the repository by walking upward to `Modularyn.slnx`. The tests enforce central build settings and package versions, the SDK and Node.js pins and their archive hashes, the gitleaks digest, the image references of the workflows and Dockerfiles, the structure of the `real-provider` and `container-build` jobs, complete license text, portable text files, and the exact action allow list. Local checks include tracked files and untracked files that Git does not ignore. Generated build output and local coordination data are excluded through `.gitignore`.

The convention validators also receive deliberately invalid content in memory. Those tests require each violation to be rejected, including project overrides, floating packages, incomplete license text, machine paths, workflow actions that are unpinned or absent from the allow list, tool hashes that are missing, malformed or out of step with `global.json` or the register, unpinned or unregistered images, Dockerfiles on another base, a PostgreSQL service that admits host connections before the job sets its credentials, diverged copies of the shared install steps, and browser installs. These cases do not write invalid files to the repository.

## Dependency and action updates

Change test-package versions only in `Directory.Packages.props`. Use stable exact versions. For an intentional dependency update, an authorized developer runs `dotnet restore --force-evaluate`, reviews the resulting `packages.lock.json` changes, then repeats locked restore, Release build, and tests. Commit the reviewed lock file with the package change. Normal CI never regenerates dependency locks.

`global.json` pins an exact stable .NET 10 SDK with `rollForward: disable` and prereleases disabled, so the .NET host and CI's install use exactly that SDK and every SDK change is a reviewed commit; the convention tests require that shape rather than one specific version, and the [dependency register](dependencies/README.md) records the pinned version. xUnit's VSTest adapter runs the tests through `Microsoft.NET.Test.Sdk`; the project explicitly disables the Microsoft Testing Platform application mode.

Every workflow action uses a full 40-character commit SHA. Keep each static `uses` property on its own line and use plain YAML keys without escape sequences or complex key syntax; flow mappings, folded values, aliases, and expressions for action selection are rejected. Literal quotes around `uses` and its pinned value are supported. `.github/workflows/actions.allow` contains exactly the used actions with release tags in comments. Verify the upstream commit and tag when updating either a workflow or the allow list, and change both together.

The secret-scan job downloads gitleaks 8.30.1 for Linux x64 and checks the archive with `sha256sum --check --strict` against `GITLEAKS_SHA256`, the digest pinned in the workflow. It also downloads the release's versioned checksums, requires exactly one checksum for the archive, fails if that checksum differs from the pinned digest, and checks the archive against it. Only then does it extract gitleaks and scan all fetched Git history with redaction. To update gitleaks, change `GITLEAKS_VERSION` and `GITLEAKS_SHA256` together, taking the digest from a fresh download that matches the release's checksum file, and update the register's gitleaks entry in the same commit. To check local history with the same installed version, run:

```sh
gitleaks git --log-opts="--all --full-history" --redact --no-banner .
```
