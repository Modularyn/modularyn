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

The build matrix installs the pinned .NET SDK as [SDK install](#sdk-install) describes and the pinned Node.js as [Node.js install](#nodejs-install) describes, then restores with `dotnet restore --locked-mode`, builds with `dotnet build -c Release --no-restore`, and tests with `dotnet test -c Release --no-build --filter "Requires!=PostgreSQL"`. Windows and macOS are build and unit-test platforms, not qualified hosts. The `real-provider` job runs the tests that need PostgreSQL on Linux x64, and installs, type-checks and tests the TypeScript client there, on Linux only, as [Real-provider tests](#real-provider-tests) and [TypeScript client](#typescript-client) describe, and the `container-build` job builds the container fixture and the sample application's image, as [Container build](#container-build) describes, then runs the AC-001 container rows in the composition fixture image, as [Container acceptance](#container-acceptance) describes. Both of those jobs check the AC-001 evidence records their run wrote, as [Evidence records](#evidence-records) describes. Standard GitHub-hosted runners serve the public repository. Workflows have read-only contents permission, use only the allowed actions, and do not retain checkout credentials. No step interpolates a pull-request value into a script: the only expressions in steps are the checkout ref and the service container's id and port, which reach the scripts through the environment.

## SDK install

CI uses no setup action for .NET. Each `build-test` job, and the `real-provider` and `container-build` jobs with the same Linux steps, installs the SDK that `global.json` pins from the official archive recorded in [.github/dotnet-sdk.json](../.github/dotnet-sdk.json):

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

The `real-provider` job uses it, and the npm it bundles, for the TypeScript client of work order CODE-06, as [TypeScript client](#typescript-client) describes. The `build-test` jobs install and verify it on all three platforms but do not use it: the TypeScript client is type-checked and tested on Linux only, which [TypeScript client](#typescript-client) explains. The runner images' own Node.js and npm are never used.

### Updating the Node.js pin

The archive hashes are the values in the release's `SHASUMS256.txt`, whose detached signature must verify with a Node.js release key, and which the register's `binary:node` entry cites. To change the release, download each of the six archives and check it against that file, then change `NODE_VERSION`, `.github/node.json` and the register's `binary:node` version and digests in one reviewed commit, and require CI on all three platforms at its exact head.

The tests require the pin file's version to equal `NODE_VERSION` and the register, its source to be that release's `SHASUMS256.txt` and to be cited by the register, exactly the six platforms with their official URLs and distinct lowercase SHA-256 values, each equal to the register's digest for that platform, and the CI workflow to read the pin file.

## Real-provider tests

Tests that need PostgreSQL carry the xUnit trait `Requires=PostgreSQL`. The `real-provider` job runs exactly those, with `--filter "Requires=PostgreSQL"`, and every other job's `dotnet test` excludes them. The AC-001 integration methods carry the trait on their host rows only: their container rows carry `Deployment=single-host-container` instead, skip wherever `MODULARYN_DEPLOYMENT_MODE` is unset, as on every runner, and run only in the `container-build` job, inside the composition fixture image. A test with the trait fails, rather than skips, when no database is configured, and the job fails unless at least one such test ran and every one passed, so it cannot pass without PostgreSQL. Today the only such test is the environment probe in [PostgreSqlEnvironmentTests](../tests/Modularyn.Repository.Tests/PostgreSqlEnvironmentTests.cs); the real-provider cases of the [G-02 evidence plan](evidence/g02-plan.json) join it as their work orders add them.

### The connection-string convention

Tests read one environment variable, `MODULARYN_TEST_POSTGRESQL`, in both CI and local runs. It holds an Npgsql keyword connection string with exactly `Host`, an optional `Port` (5432 by default), `Database`, `Username` and `Password`. The host must be `localhost`, `127.0.0.1` or `::1` and must resolve only to loopback addresses, so real-provider tests never reach a remote server. The database belongs to a role created for the run: the role can sign in and owns its database, and it is not a superuser and cannot create roles or databases. Tests isolate their data within that database, for example by schema.

The probe test signs in with the variable's credentials over TCP and checks that the server is PostgreSQL 18, that the session's database and user are the ones named, that the user is not a superuser and that the encoding is UTF8. It sends a password only through SCRAM-SHA-256 and verifies the server's SCRAM signature; it refuses cleartext and MD5 requests without sending the password. No message, log line or `ToString` contains the password.

### In CI

The `real-provider` job runs on `ubuntu-latest`, a Linux x64 runner, with a PostgreSQL service from `docker.io/library/postgres:18.6-trixie` at its pinned index digest, published only on the runner's `127.0.0.1:5432`. The service starts with `POSTGRES_HOST_AUTH_METHOD=reject`, so no host connection can sign in until the job has created its credentials, and a `pg_isready` health check gates the job's steps. The image needs a superuser password to initialize; the job gives it the run's public id, which never authenticates anything.

After the SDK and Node.js steps, which are the same as `build-test`'s on Linux and which a test keeps identical, the job restores and builds, then installs and type-checks the TypeScript client as [TypeScript client](#typescript-client) describes. `Choose the host evidence directory` then sets `MODULARYN_EVIDENCE_DIR` beneath `RUNNER_TEMP` and `MODULARYN_SOURCE_COMMIT` to the exact head, so the AC-001 host rows write their records, and `Test against PostgreSQL` runs `bash .github/scripts/postgresql-test-database.sh ci bash .github/scripts/real-provider-tests.sh`. The database script:

1. fails unless the runner is Linux x64 and the service runs `POSTGRES_IMAGE`, the same reference as the service image, pulled by its index digest, on linux/amd64, and reports PostgreSQL `POSTGRESQL_VERSION`, 18.6;
2. generates a role and database name and a 64-character hexadecimal password from `/dev/urandom` and masks the password in the log;
3. creates the role and its database through the container's Unix socket, with statement logging off for that session, and removes the bootstrap superuser password;
4. replaces the service's `pg_hba.conf` so that the socket admits only the container's postgres user and host connections admit only the test role, to its database, with SCRAM-SHA-256, then checks that the file has no errors, prints its rules to the log and reloads it;
5. checks that the test role signs in over TCP and that the superuser cannot; and
6. runs its command with `MODULARYN_TEST_POSTGRESQL` set for `127.0.0.1` and the service's port, and returns the command's status.

The credentials therefore exist only in that step's process environment: no earlier step, file, `GITHUB_ENV` or later step receives them, and the restore, the build and the npm install run before they exist. The job's last step, `Check the host evidence records`, then checks the host records as [Evidence records](#evidence-records) describes.

The command is [real-provider-tests.sh](../.github/scripts/real-provider-tests.sh). `dotnet test` succeeds when its filter selects no test, so the script does not rely on its exit status alone. It runs `dotnet test --filter "Requires=PostgreSQL"` on every test project that `git ls-files` lists under `tests/`, one project after another and each with its own TRX results file, so no project's results can overwrite another's and the projects never share the database concurrently. It then reads each file's counters and fails unless at least one PostgreSQL test ran across all projects and every one that ran passed; a skipped test fails it too. Its `count` mode applies the same counting to existing results without running anything, and a repository test runs it on Linux and macOS against fixture results with no tests, a skipped test, a failed test, a missing results file and one passed test. Neither script writes to `GITHUB_ENV`, `GITHUB_OUTPUT`, `GITHUB_STATE` or `GITHUB_PATH`, which the tests also check. A local run removes its results directory when it ends. Because it runs every test project, PostgreSQL tests added in any project count. Its console output is detailed, so the log records each test's observations, such as the probe's sign-in method and server version. The repository tests check the image, the server version, the step order, the test filters and these rules against the [dependency register](dependencies/README.md).

### TypeScript client

The TypeScript client lives in `src/Framework/Modularyn.Framework.Sdk.Client/typescript`, with the exact devDependencies of its `package.json` and the committed `package-lock.json` that the [dependency register](dependencies/README.md#code-06-adoption) adopts. In the `real-provider` job, after the Build step and before the credentials exist, two steps run in that directory with the pinned Node.js and its bundled npm:

1. `Install the TypeScript client's locked packages` runs `npm ci --ignore-scripts --no-audit --no-fund`. `npm ci` installs exactly the lock, fails if the lock does not match `package.json`, and checks every package against its SHA-512 integrity; `--ignore-scripts` keeps every install script from running.
2. `Type-check the TypeScript client` runs `npm run check`, the manifest's `tsc --noEmit`.

The `Test against PostgreSQL` step sets `MODULARYN_CLIENT_TEST_VITEST` to `1`. The client test project then runs each AC-008 TypeScript scenario twice, on Node.js alone with its native type stripping and under Vitest, each against its own fresh PostgreSQL fixture, and the Node.js process it starts never receives `MODULARYN_TEST_POSTGRESQL`. Locally, run the same steps in that directory with the pinned Node.js first on `PATH`, then the database script with `MODULARYN_CLIENT_TEST_VITEST=1` in the environment.

The type check and both TypeScript runs happen in the `real-provider` job, on Linux x64 only. That is sufficient because the client is platform-independent. It is TypeScript compiled to standard JavaScript whose runtime uses only what every Node.js and browser provides, `fetch`, `Headers`, `URL`, `AbortSignal` and JSON, with no native code, file-system or process access and no branch on the operating system. The type check depends only on the sources and the pinned TypeScript release, whose per-platform packages implement the same compiler. The contract scenarios need PostgreSQL, which only that job provides, and the parts that do differ by platform, the sockets and TLS beneath `fetch`, are Node.js's own, whose pinned archive every `build-test` job installs and verifies on Linux, Windows and macOS. What Linux alone does not show is a platform-specific defect in Node.js or in the toolchain's native packages on Windows or macOS; those are development tools, and nothing of the client ships per platform.

The repository tests hold these steps to the contract: the `real-provider` job must install the client with `npm ci` and `--ignore-scripts`, in exactly that directory, before its `Test against PostgreSQL` step, and that step must set `MODULARYN_CLIENT_TEST_VITEST` to `'1'` in its own `env`. Every `npm` command in the workflows and CI scripts must be either `npm ci` with `--ignore-scripts`, which installs exactly the lock, or `npm run`, which runs the manifest's own scripts; `npm install`, `npm exec` and every other subcommand are refused. Like the docker rules, these read tokens: they are drift guards, and review is the control for a command assembled another way.

### Local runs

Local runs use a PostgreSQL 18 server listening on localhost, such as Homebrew's `postgresql@18` (decision D2); no Docker is needed. With the server running and a superuser reachable through libpq's defaults, or through `PGHOST`, `PGPORT` and `PGUSER`, run:

```sh
bash .github/scripts/postgresql-test-database.sh run dotnet test -c Release --no-build
```

To run exactly what the `real-provider` job runs, with its check that at least one PostgreSQL test passed, give the database script the tests script as its command:

```sh
bash .github/scripts/postgresql-test-database.sh run bash .github/scripts/real-provider-tests.sh
```

The database script checks that the server is PostgreSQL 18, creates a role and database with generated names and password exactly as CI does, runs the command with `MODULARYN_TEST_POSTGRESQL` set, and drops both when the command ends, whatever its result. It prints neither the password nor the connection string. If the local server trusts loopback connections, as Homebrew's default configuration does, the script says so: that server does not check the generated password, so only CI exercises SCRAM sign-in. Without a local PostgreSQL, exclude the PostgreSQL tests explicitly with `--filter "Requires!=PostgreSQL"`; nothing skips them silently.

## Container build

The `container-build` job checks every Dockerfile in the repository, then builds [the container fixture](../tests/Fixtures/container/Dockerfile), the smallest image on the ASP.NET Core runtime base, and then the sample application's image from [its Dockerfile](../src/Tooling/Modularyn.Tooling.Samples.TeamWorkspace.Host/Dockerfile). After those steps, in their order, it runs the AC-001 container rows, as [Container acceptance](#container-acceptance) describes.

The Dockerfile rules below, and the docker-command rules in [Docker commands](#docker-commands), are drift guards. They stop an accidental or careless change from adding an unpinned image, another Dockerfile base or a remote fetch. They are not a defense against a hostile pull request: CI runs the pull request's own code, and such a change could edit these checks themselves. Review is the control for that. Each rule is a simple, closed token rule, not an attempt to parse a Dockerfile or shell.


1. It lists every tracked file whose name, in any letter case, ends with `dockerfile` or `containerfile` or contains `dockerfile.` or `containerfile.`, and fails if there is none. In each, every `FROM` must name `ASPNET_IMAGE`, which is `mcr.microsoft.com/dotnet/aspnet:10.0.14-noble` at its pinned index digest, or an earlier stage, and its only allowed option is `--platform=linux/amd64`. Every `--from` and mount `from=` must name an earlier stage or stage index. No parser directive is allowed: `syntax`, `escape` and `check` are refused anywhere, and so is any `# name=` line before the first instruction, because a directive can select a frontend image or change the continuation character that the checks rely on. Like BuildKit, both checkers ignore a UTF-8 byte-order mark before the first line, so a directive behind one is still refused. An `ADD`, or `ONBUILD ADD`, of a git or ssh source, whether `git://`, `ssh://` or `<user>@<host>:<path>`, is refused, because `--checksum=sha256:` cannot pin it. An `ADD` of an `http://` or `https://` source, or of a variable, which could hide one, must name exactly one source and verify it with `--checksum=sha256:` and 64 lowercase hexadecimal characters. `FROM` and `ADD` instructions must each stay on one line.
2. It builds the fixture with `docker build --platform=linux/amd64 --pull --no-cache`, under the local tag `FIXTURE_IMAGE`, with the one command line that [Docker commands](#docker-commands) allows.
3. It fails unless the image is linux/amd64, runs as a numeric non-root user, has exactly the layers of the pinned base, which it pulls by the same reference, and reports exactly the ASP.NET Core and .NET runtimes of the tag's version, 10.0.14, when run without a network and with `--pull=never`.
4. It installs the pinned SDK with the same two Linux steps as `build-test`, which a repository test keeps identical, restores the sample host's project with `dotnet restore --locked-mode`, publishes it with `dotnet publish -c Release --no-restore --self-contained false` into `$RUNNER_TEMP/team-workspace`, and writes the deployment lock there with the host's `--describe-deployment`, as the host README's deployment sequence does.
5. It builds the sample image from that directory with `docker build --platform=linux/amd64 --pull --no-cache`, under the local tag `SAMPLE_IMAGE` and with `SAMPLE_DOCKERFILE`, the sample host's Dockerfile, whose only base is `ASPNET_IMAGE`.
6. It fails unless the image is linux/amd64, runs as a numeric non-root user and group, and starts with exactly the layers of the pinned base and adds at least one. It then runs the image with `--pull=never`, without a network and with no configuration or secret. The host cannot serve its health endpoints without PostgreSQL, its secret directory, its key ring and its deployment lock path, none of which the job has, so the job checks what the design supports honestly: run with no argument, the host refuses to start with status 1 and prints only its fixed startup diagnostic; run with `--describe-deployment`, it describes exactly the lock written from the published files, so the image holds the files that lock lists. The host's readiness endpoint is checked by the sample's smoke test in the `real-provider` job, in process, and in the container by the published-sample readiness test, which [Container acceptance](#container-acceptance) runs against this image once it is configured.

The repository tests apply the same Dockerfile rules on every platform, the composition fixture image's own rules below included, and require `ASPNET_IMAGE` to equal the register's reference. The sample application is therefore published with the pinned SDK and its output copied into an image on that base, rather than built in an SDK image that nothing pins. The tests also require `SAMPLE_DOCKERFILE` to be the sample host's Dockerfile, that file to exist, every `docker build` in the job to be for linux/amd64 and the job's SDK steps to equal `build-test`'s.

### Container acceptance

The AC-001 cases compose test modules A, B and C in version variants, invalid graphs and an injected fault. None of those modules, and no switch that selects them, may be in the published sample image: shipping test modules or a composition switch in the product image would be a security regression. The container rows therefore run in a test-only image that CI builds and runs and never publishes, the composition fixture image from [tests/Fixtures/composition/Dockerfile](../tests/Fixtures/composition/Dockerfile). Its only base is `ASPNET_IMAGE`, by the same tag and index digest as every Dockerfile. It holds the framework-dependent publish of the sample's test project, an xunit v3 executable that carries the sample host, the sample's modules and the A, B and C fixtures. It runs as UID/GID 1654, root owns its files, it runs no build step, and it sets `MODULARYN_DEPLOYMENT_MODE=single-host-container`, which selects the container rows and skips the host rows. The repository tests require each of those properties of the Dockerfile.

After the existing steps, in their order, the job:

1. checks that the checkout is clean and that HEAD is the exact head, restores the sample's test project with `dotnet restore --locked-mode`, publishes it with `dotnet publish -c Release --no-restore --self-contained false` into `$RUNNER_TEMP/composition`, and sets `MODULARYN_SOURCE_COMMIT` to that head;
2. builds the image from that directory with `docker build --platform=linux/amd64 --pull --no-cache`, under the local tag `COMPOSITION_IMAGE` and with `COMPOSITION_DOCKERFILE`;
3. fails unless the image is linux/amd64, runs as a numeric non-root user and group, starts with exactly the layers of the pinned base and adds at least one, and sets the deployment mode, and unless, run without a network and with `--pull=never`, it finds no file under `/app` that root does not own or that its user can write;
4. creates the Docker network `modularyn-acceptance` with `--internal`, so nothing on it reaches outside, pulls `POSTGRES_IMAGE`, the `real-provider` job's image by its index digest, and starts PostgreSQL on that network with `POSTGRES_HOST_AUTH_METHOD=reject`, the run's public id as its bootstrap password, and a `pg_isready` health check that the step waits for;
5. runs `bash .github/scripts/postgresql-test-database.sh ci bash .github/scripts/composition-tests.sh` with `POSTGRES_PORT` 5432. The database script checks the container's image, platform, index digest and reported server version `POSTGRESQL_VERSION`, creates the per-run role and database, removes the bootstrap password, so no password authenticates anything but that role, and runs [composition-tests.sh](../.github/scripts/composition-tests.sh) with `MODULARYN_TEST_POSTGRESQL` naming the container's loopback port;
6. checks the container records as [Evidence records](#evidence-records) describes, after building the repository tests.

[composition-tests.sh](../.github/scripts/composition-tests.sh) holds the credentials and writes no runner file. It:

1. writes the sample's connection string, the same role reached by the database container's name on the network, to a secret file that only the runner's group can read, and removes it when it ends;
2. applies the sample's migrations with the published sample image's `--migrate`, then starts that image on the network with the settings in [composition-sample.env](../.github/scripts/composition-sample.env), the secret directory mounted read-only, owner-only key and BLOB directories in memory, and the database container's address as its one declared proxy;
3. runs the published-sample readiness test in the composition fixture image until it passes, and fails with the sample's log tail if the sample stops or 30 checks pass without it;
4. runs the composition fixture image with the output directory mounted, `MODULARYN_TEST_POSTGRESQL`, `MODULARYN_SAMPLE_BASE_URL` naming the sample container, `MODULARYN_EVIDENCE_DIR=/output/evidence`, `MODULARYN_SOURCE_COMMIT` and `MODULARYN_CONTAINER_IMAGE_DIGEST`, the image digest it measures. Through the test executable's own filters, `-preEnumerateTheories`, `-trait "Deployment=single-host-container"` and one `-method` for each, it runs exactly the four AC-001 methods' container rows and the readiness test, and writes a CTRF report with `-result-ctrf`; and
5. fails unless the report lists exactly those five tests and every one passed: none skipped, none missing.

Every container the script runs, sample and fixture alike, uses `--pull=never`, `--read-only`, `--tmpfs=/tmp`, `--cap-drop=ALL` and `--security-opt=no-new-privileges`, and joins either the internal network or the database container's network namespace. The fixture image joins that namespace, rather than reaching the database by its network name, because the tests' database probe accepts only a loopback host, so that real-provider tests never reach a remote server. The sample image listens on private HTTP behind a declared TLS proxy, as the host README requires; inside the namespace the readiness test is that proxy, so it forwards the scheme, and the sample applies the header only from that declared address. The readiness test observes the published image; it is never AC-001 coverage.

The repository tests require the composition Dockerfile and its properties, the job's `COMPOSITION_DOCKERFILE`, its `POSTGRES_IMAGE` equal to the register's reference and its `POSTGRESQL_VERSION` equal to the register's release, the internal network, the order of the steps, that only the test step runs the database script, that the script runs exactly the evidence plan's CODE-10 methods and the readiness test, every one hardened as above, and checks its report, and that the evidence check follows. Docker is not needed to run them; the job itself is verified only when CI runs it.

### Evidence records

Both jobs write the AC-001 evidence records of their own deployment beneath `RUNNER_TEMP`, bound to the exact head: the `real-provider` job's host rows to `$RUNNER_TEMP/host-evidence`, and the container rows to the fixture image's mounted output, `$RUNNER_TEMP/composition-output/evidence`. The writer in the image cannot check a clean checkout, since the image has none: the job checked the checkout before it published the image, and the writer checks that the embedded source revision of every loaded Modularyn assembly equals `MODULARYN_SOURCE_COMMIT`. The container records' tuple carries the image digest that the job measures and passes in. [The evidence instructions](evidence/README.md#code-10-record-output) describe the records.

After each run, `Check the host evidence records` and `Check the container evidence records` run [evidence-records.sh](../.github/scripts/evidence-records.sh) with `MODULARYN_EVIDENCE_MODE` set to the job's mode. It runs the repository test `CiEvidenceTests.The_records_this_CI_run_wrote_validate_against_the_repository_contracts` through the test executable's own `-method` filter with `-failSkips`, and fails unless its CTRF report shows that it ran once and passed. The test applies the repository's own evaluator, `EvidenceRecordContract`, to every record beneath `MODULARYN_EVIDENCE_DIR` and requires one valid record for each AC-001 case, all from one run, at their case and mode locations, bound to `MODULARYN_SOURCE_COMMIT` and the job's mode, with no container image named by a host record, no pending record, and retained bytes matching every result and artifact hash. The script then prints each record's SHA-256. No step uploads an artifact: retaining the records is a later step of the milestone.

### Docker commands

The repository tests hold the docker commands of the workflows and CI scripts, and the names those commands use, to an exact allowlist: `DockerAllowlist` in [ToolPinContract.Docker.cs](../tests/Modularyn.Repository.Tests/ToolPinContract.Docker.cs). Like the Dockerfile rules, these are drift guards against careless change, not a defense against a hostile pull request, which runs its own code in CI and could edit them; review is the control for that. The rules are token rules over `.github/workflows/*.yml`, `.github/workflows/*.yaml` and `.github/scripts/*`:

- **Docker lines.** Every non-comment line that names `docker` or `docker-compose`, in any letter case and in any form, including `docker buildx`, `docker image`, `docker container`, `docker compose` and `docker service`, must equal, after trimming whitespace, one of the listed lines for its file. Each listed line must appear exactly once: a listed line that is unused, or used twice, fails, and so does a listed line that the line before it continues into.
- **Guarded names.** The image and build-file variables `FIXTURE_IMAGE`, `FIXTURE_DOCKERFILE`, `SAMPLE_IMAGE`, `SAMPLE_DOCKERFILE`, `COMPOSITION_IMAGE`, `COMPOSITION_DOCKERFILE` and `ASPNET_IMAGE` (in the `container-build` job) and `POSTGRES_IMAGE` (in both the `real-provider` and the `container-build` job), and the database script's arrays `admin` and `tcp`, which hold docker commands, are guarded. A guarded name may appear only in its `env` definitions, one in each job named for it, each a literal (and for `FIXTURE_IMAGE`, `SAMPLE_IMAGE` and `COMPOSITION_IMAGE` a local tag with no registry), and in listed lines: there as `${NAME}`, or for an array as `"${admin[@]}"` or `"${tcp[@]}"` or its one listed definition. Any other occurrence of the name fails, in any syntax and on any line, comments included, so `read`, `printf -v`, `for`, `${NAME:=…}`, `${NAME-…}`, `${!NAME}`, `$NAME`, `local -n`, `read -a`, an expression such as `${{ env.NAME }}` and a heredoc into `GITHUB_ENV` all fail. That is why the listed lines also include the lines that use these names without running docker, such as the Dockerfile base check.
- **Refused forms.** `--build-context`, `docker-image://`, a URL or git context and a standard-input context or Dockerfile (`-`) are refused outright on any docker line.
- **Wrappers.** `eval`, a shell given `-c` after any options, as in `bash -c` or `bash -o pipefail -c`, and a pipe into `bash` or `sh` fail outside the listed lines, because they can hide a docker command.
- **The list itself.** Each listed line must be one complete line, use no refused form, and use each guarded name only in its plain form.

A new docker command joins the list in the same reviewed change as the workflow line, the way an action joins `actions.allow`, as the sample application's build, inspection and runs did with work order CODE-09, and the AC-001 container rows' network, database, sample and composition fixture image commands did with CODE-10. Because a URL in a docker line is refused, the container rows' script passes the sample's address to the fixture image through the environment, and the sample's settings through its settings file.

These limits remain, as limits of a drift guard, and review is the control for them:

- **Scope.** The rules cover only `.github/workflows` and `.github/scripts`. A script elsewhere in the repository, a tool the job runs, and the tests themselves, which run code from the pull request with docker available on Linux runners, are outside them.
- **Comments.** Comment lines are exempt from the docker-line rule, since a comment does not run, though guarded names are refused in comments too.
- **Spelling.** The rules read tokens. They cannot see docker reached under another spelling, such as an escaped or split name, a command assembled from variables or a file, `source` of generated text, or a function or alias defined outside the listed lines; nor another tool that fetches images, such as Podman, Buildah, an action or Testcontainers; nor a download by other means, such as `curl` in a `RUN` instruction.
- **Wrappers.** Only the wrapper forms above are recognized; another interpreter given a script on its command line or standard input is not.
- **Call sites.** Arguments that a listed array receives at its listed call sites go to `psql`, not to docker, but only review keeps it that way.

## Browsers

No workflow installs browsers. Playwright 1.64.1 downloads its browser builds without verifying a hash, so CI never runs `playwright install`, and the tests reject a workflow or CI script that does. The first browser test is AC-003-N02, written by work order SEC-08; that order pins the browsers before its test runs in CI, as the digest-pinned Playwright image or as hash-verified browser archives, as [What CI-01 pins](dependencies/README.md#what-ci-01-pins) requires.

## Conventions

The test assembly locates the repository by walking upward to `Modularyn.slnx`. The tests enforce central build settings and package versions, the SDK and Node.js pins and their archive hashes, the gitleaks digest, the image references of the workflows and Dockerfiles, the structure of the `real-provider` and `container-build` jobs, complete license text, portable text files, and the exact action allow list. Local checks include tracked files and untracked files that Git does not ignore. Generated build output and local coordination data are excluded through `.gitignore`.

The convention validators also receive deliberately invalid content in memory. Those tests require each violation to be rejected, including project overrides, floating packages, incomplete license text, machine paths, workflow actions that are unpinned or absent from the allow list, tool hashes that are missing, malformed or out of step with `global.json` or the register, unpinned or unregistered images, Dockerfiles on another base, with a parser directive or with an unverified remote `ADD`, docker commands that are not on the allowlist, a PostgreSQL service that admits host connections before the job sets its credentials, credentials that outlive the test step, a real-provider run that could pass with no PostgreSQL test, diverged copies of the shared install steps, and browser installs. These cases do not write invalid files to the repository.

## Dependency and action updates

Change test-package versions only in `Directory.Packages.props`. Use stable exact versions. For an intentional dependency update, an authorized developer runs `dotnet restore --force-evaluate`, reviews the resulting `packages.lock.json` changes, then repeats locked restore, Release build, and tests. Commit the reviewed lock file with the package change. Normal CI never regenerates dependency locks.

`global.json` pins an exact stable .NET 10 SDK with `rollForward: disable` and prereleases disabled, so the .NET host and CI's install use exactly that SDK and every SDK change is a reviewed commit; the convention tests require that shape rather than one specific version, and the [dependency register](dependencies/README.md) records the pinned version. xUnit's VSTest adapter runs the tests through `Microsoft.NET.Test.Sdk`; the project explicitly disables the Microsoft Testing Platform application mode.

Every workflow action uses a full 40-character commit SHA. Keep each static `uses` property on its own line and use plain YAML keys without escape sequences or complex key syntax; flow mappings, folded values, aliases, and expressions for action selection are rejected. Literal quotes around `uses` and its pinned value are supported. `.github/workflows/actions.allow` contains exactly the used actions with release tags in comments. Verify the upstream commit and tag when updating either a workflow or the allow list, and change both together.

The secret-scan job downloads gitleaks 8.30.1 for Linux x64 and checks the archive with `sha256sum --check --strict` against `GITLEAKS_SHA256`, the digest pinned in the workflow. It also downloads the release's versioned checksums, requires exactly one checksum for the archive, fails if that checksum differs from the pinned digest, and checks the archive against it. Only then does it extract gitleaks and scan all fetched Git history with redaction. To update gitleaks, change `GITLEAKS_VERSION` and `GITLEAKS_SHA256` together, taking the digest from a fresh download that matches the release's checksum file, and update the register's gitleaks entry in the same commit. To check local history with the same installed version, run:

```sh
gitleaks git --log-opts="--all --full-history" --redact --no-banner .
```
