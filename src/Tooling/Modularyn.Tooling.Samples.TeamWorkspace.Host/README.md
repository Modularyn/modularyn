# Team workspace host

This is the sample's composition root. The sibling application's README describes
behavior and security boundaries. The target is Linux x64 with .NET/ASP.NET Core
10.0.14, PostgreSQL 18.6, a shared database and local filesystem BLOB storage.
Host and container execution are pending Builder verification.

## Deployment configuration

Configuration is read from environment variables only. .NET's `__` separator
maps the names below to configuration sections. No secret or connection string
belongs in the repository, a command line, image or diagnostic.

| Variable | Value supplied by the deployment |
|---|---|
| `Workspace__Database` | SEC-02 secret reference naming the PostgreSQL connection string file |
| `Modularyn__Secrets__Directory` | Existing absolute directory containing mounted secret files |
| `Workspace__BlobDirectory` | Existing absolute filesystem storage directory, without links in its path |
| `Workspace__SchemaPrefix` | Optional lowercase schema prefix; `team_` by default |
| `Workspace__DeploymentLock` | Absolute path of the reviewed deployment lock matching the published artifact |
| `Modularyn__DataProtection__ApplicationName` | Stable application boundary across restarts |
| `Modularyn__DataProtection__Directory` | Existing owner-only persistent key directory, separate from application files |
| `Modularyn__Ingress__PublicOrigins__0` | Exact public HTTPS origin |
| `Modularyn__Ingress__HttpsPort` | Public HTTPS port |
| `Modularyn__Ingress__Proxies__0` | Exact directly connected TLS proxy address, when using a proxy |
| `Modularyn__Ingress__Limits__MaxRequestBodyBytes` | `10485760` to admit the full 10 MiB attachment limit |
| `ASPNETCORE_URLS` | Private HTTP listening address behind the declared TLS proxy; defaults to loopback port 8080 |
| `Workspace__DevelopmentSeed` | Optional `true`, accepted only with `DOTNET_ENVIRONMENT=Development` or `Testing` |

Use the foundation's identity/authorization deployment options unchanged. No
operator is configured by the sample. The Builder's separately reviewed
provisioning establishes accounts, tenant owners and host authority. The
declared service identity `workspace-worker` has a permission ceiling but needs
separately provisioned `workspace.dispatch` host authority before it can dispatch.
Missing authority leaves work queued. The service does not become a tenant member.

The proxy must terminate HTTPS and forward from its explicitly configured peer
address. Restrict the private HTTP listener to that proxy. Keep cross-origin
origins empty: the pages, script and Identity requests use the same origin.
Session and antiforgery cookies stay secure. Proxy access logs and trace
instrumentation must exclude all credential headers and request/response bodies.

Create mounted key/storage directories with ownership for the process account;
the key ring must have owner-only access. Keep secrets, BLOBs and keys outside
the published artifact, and persist them across process/container replacement.
The PostgreSQL role needs ownership of the sample's schemas in an existing
database, not superuser, database creation or role creation.

## Builder sequence

The Builder first performs its locked restore workflow, generating the new
projects' package lock files, builds the solution and runs the tests. Nothing
in this sample replaces restore. The following deployment steps are for the
Builder after successful validation; they were not executed during authoring.

From the repository root, choose output directories inside the workspace:

```sh
workspace_project=src/Tooling/Modularyn.Tooling.Samples.TeamWorkspace.Host
workspace_publish="$PWD/$workspace_project/artifacts/publish"
dotnet publish "$workspace_project/Modularyn.Tooling.Samples.TeamWorkspace.Host.csproj" -c Release --no-restore --self-contained false -o "$workspace_publish"
dotnet "$workspace_publish/Modularyn.Tooling.Samples.TeamWorkspace.Host.dll" --describe-deployment > "$workspace_publish/deployment-lock.json"
```

Review and retain that deployment lock with the artifact. The describe operation
uses the foundation's `LockedModule.Describe`, hashes the published assemblies,
runtime descriptors and sign-in script, and writes JSON to stdout. It does not
activate modules, migrate a database, provision authority or create a package
lock. Normal startup requires this supplied lock and never creates or refreshes
it automatically. Protect the artifact and lock with deployment access controls.

Set the environment configuration, including the lock path, then apply migrations
as a distinct operation and run the host:

```sh
dotnet "$workspace_publish/Modularyn.Tooling.Samples.TeamWorkspace.Host.dll" --migrate
dotnet "$workspace_publish/Modularyn.Tooling.Samples.TeamWorkspace.Host.dll"
```

Migration uses the foundation coordinator and its advisory lock. Ordinary startup
only checks migration readiness. A missing/altered deployment lock, undeclared
key ring, pending migration or invalid policy refuses startup. There is no
unverified-composition switch. Health endpoints are `/health/live` and
`/health/ready`, reached through the configured HTTPS origin.

After provisioning and assigning the member role, `/sign-in` accepts the account
credentials and tenant identifier and opens `/t/{tenant}/notes/new`. Create a
project through the declared API first, or use the explicitly enabled development
seed as an already authorized member. API mutations use the antiforgery token
from `/identity/antiforgery` in `RequestVerificationToken`; idempotent mutations
also require a nonempty UUID in `Idempotency-Key`.

## Container

The Dockerfile's single base is exactly the CI register's ASP.NET Core runtime
image and digest. It runs as UID/GID 1654 and copies only runtime assemblies,
descriptors, the deployment lock and the sign-in script. Its build context must
be the validated publish directory:

```sh
docker build --platform=linux/amd64 --file "$workspace_project/Dockerfile" --tag modularyn-team-workspace:local "$workspace_publish"
```

Run the image on Linux x64 with the same environment and secret references as
the host. Mount the secret directory read-only and persist the separately mounted
key and BLOB directories, writable only by UID 1654. Map the internal port 8080
only to the TLS proxy's private network. Set the container's deployment-lock
path to `/app/deployment-lock.json`. Apply migrations with the image's
`--migrate` argument before running its default entry point. No container startup
path provisions identities or authority. Host filesystem paths and container
mount paths are deployment inputs, not committed configuration.

The existing CI job scans the Dockerfile's image pin but builds only its existing
fixture. The Builder must add this image's build/run and Linux x64 verification
to the workflow and its reviewed command allowlist; neither was modified here.
No image has been built or published in this run.
