using System.Text.RegularExpressions;

namespace Modularyn.Repository.Tests;

// The docker commands of the workflows and CI scripts, and the names they use, as an exact allowlist. These checks are
// drift guards: they stop an accidental or careless change from adding an unpinned image or remote fetch. They do not
// defend against a hostile pull request, which runs its own code in CI and could edit these checks; review is the control
// for that. Each rule is a closed token rule, not a shell parser:
//
// - Every line in .github/workflows and .github/scripts that names docker, or uses one of the guarded names below, must
//   equal, after trimming whitespace, one of the listed lines for its file, exactly once. The env definitions of the
//   guarded variables are the only exception.
// - A guarded name appears nowhere else, in any syntax, and in a listed line only as ${NAME}, or for an array as
//   "${NAME[@]}" or its one definition.
// - eval, a shell with -c, and a pipe into a shell fail outside the listed lines, and so does a continuation into one.
//
// A new docker command joins the list through review, as the sample application's build and run did with work order
// CODE-09, and the AC-001 container rows' network, database, sample and composition fixture image did with CODE-10.
// docs/ci.md states the limits.
internal static partial class ToolPinContract
{
    private const string Ci = CiPath;
    private const string Database = DatabaseScriptPath;
    private const string Composition = CompositionScriptPath;
    private const string Bundle = ".github/scripts/g02-container-results.sh";

    public static readonly (string Path, string Line)[] DockerAllowlist =
    [
        (Ci, "writable=\"$(docker run --rm --pull=never --platform=linux/amd64 --network=none --read-only --cap-drop=ALL --security-opt=no-new-privileges --entrypoint=find \"${SAMPLE_IMAGE}\" /app \\( ! -user 0 -o -perm /022 \\) -print)\" || fail \"The sample image ownership check did not run.\""),
        (Composition, "timeout --kill-after=30s \"${bound}\" docker run --rm --pull=never --platform=linux/amd64 --name=g02-composition --network=\"container:${POSTGRES_CONTAINER}\" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --group-add=\"${group}\" --volume=\"${output}:/output\" --env=MODULARYN_TEST_POSTGRESQL --env=MODULARYN_SAMPLE_BASE_URL --env=MODULARYN_SOURCE_COMMIT --env=MODULARYN_EVIDENCE_RUN_ID --env=MODULARYN_CONTAINER_IMAGE_DIGEST=\"${digest}\" --env=MODULARYN_EVIDENCE_DIR=/output/evidence \"${COMPOSITION_IMAGE}\" -noLogo -noColor -longRunning 60 -preEnumerateTheories -trait \"Deployment=single-host-container\" -method \"Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_B01_CompatibilityEdgesHonorTheDeclaredRange_Integration\" -method \"Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_N01_InvalidGraphsRefuseActivationWithDiagnostics_Integration\" -method \"Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_P01_DependenciesInitializeOnceAndStopInReverseOrder_Integration\" -method \"Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_R01_FailedInitializationCleansUpAndRecovers_Integration\" -method \"Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_is_ready_over_HTTP\" -method \"Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_serves_its_sign_in_page_over_HTTP\" -result-ctrf /output/composition-results.json 2>&1 | long_running || status=$?"),
        (Composition, "docker kill g02-composition > /dev/null 2>&1 || true"),
        (Bundle, "timeout --kill-after=30s \"${bound}\" docker run --rm --pull=never --platform=linux/amd64 --name=g02-measurement --network=\"container:${POSTGRES_CONTAINER}\" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --group-add=\"${group}\" --volume=\"${output}:/output\" --env=MODULARYN_TEST_POSTGRESQL --env=MODULARYN_SOURCE_COMMIT --env=MODULARYN_EVIDENCE_RUN_ID --env=MODULARYN_CONTAINER_IMAGE_DIGEST --env=MODULARYN_EVIDENCE_DIR=/output/evidence --env=MODULARYN_EVIDENCE_MEASURE=1 --env=MODULARYN_EVIDENCE_BINARIES=/app \"${COMPOSITION_IMAGE}\" -noLogo -noColor -failSkips -longRunning 60 -method Modularyn.Tooling.Samples.TeamWorkspace.Tests.EvidenceMeasurementTests.Measure_the_evidence_run_before_collecting_results 2>&1 | long_running || run=$?"),
        (Bundle, "docker kill \"$1\" > /dev/null 2>&1 || true"),
        (Bundle, "timeout --kill-after=30s \"${bound}\" docker run --rm --pull=never --platform=linux/amd64 --name=\"g02-${name}\" --network=\"container:${POSTGRES_CONTAINER}\" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --group-add=\"${group}\" --volume=\"${output}:/output\" --env=MODULARYN_TEST_POSTGRESQL --entrypoint=dotnet \"${COMPOSITION_IMAGE}\" \"/app/projects/${name}/${name}.dll\" -noLogo -noColor -preEnumerateTheories -failSkips -longRunning 60 \"${selected[@]}\" -result-ctrf \"/output/${name}.raw.json\" 2>&1 | long_running || run=$?"),
        (Ci, "[[ \"${ASPNET_IMAGE}\" =~ ^mcr\\.microsoft\\.com/dotnet/aspnet:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$ ]] || fail \"The ASP.NET Core base image must be named by tag and index digest.\""),
        (Ci, "git ls-files --error-unmatch -- \"${FIXTURE_DOCKERFILE}\" > /dev/null || fail \"${FIXTURE_DOCKERFILE} is not tracked.\""),
        (Ci, "[ \"${image}\" = \"${ASPNET_IMAGE}\" ] || [[ \"${stages}\" == *\" $(lower \"${image}\") \"* ]] || fail \"${location}: FROM ${image} must be ${ASPNET_IMAGE} or an earlier stage.\""),
        (Ci, "echo \"${file}: every base is ${ASPNET_IMAGE} or an earlier stage\""),
        (Ci, "[ \"${checked}\" -gt 0 ] || fail \"No Dockerfile found; ${FIXTURE_DOCKERFILE} must exist.\""),
        (Ci, "docker build --platform=linux/amd64 --pull --no-cache --tag=\"${FIXTURE_IMAGE}\" --file=\"${FIXTURE_DOCKERFILE}\" tests/Fixtures/container"),
        (Ci, "platform=\"$(docker image inspect --format '{{.Os}}/{{.Architecture}}' \"${FIXTURE_IMAGE}\")\""),
        (Ci, "user=\"$(docker image inspect --format '{{.Config.User}}' \"${FIXTURE_IMAGE}\")\""),
        (Ci, "docker pull --platform=linux/amd64 \"${ASPNET_IMAGE}\" > /dev/null || fail \"Pull of ${ASPNET_IMAGE} failed.\""),
        (Ci, "base_layers=\"$(docker image inspect --format '{{json .RootFS.Layers}}' \"${ASPNET_IMAGE}\")\""),
        (Ci, "fixture_layers=\"$(docker image inspect --format '{{json .RootFS.Layers}}' \"${FIXTURE_IMAGE}\")\""),
        (Ci, "[ \"${fixture_layers}\" = \"${base_layers}\" ] || fail \"The fixture image's layers are not those of ${ASPNET_IMAGE}.\""),
        (Ci, "tag=\"${ASPNET_IMAGE}\""),
        (Ci, "[[ \"${runtime}\" =~ ^[0-9]+\\.[0-9]+\\.[0-9]+$ ]] || fail \"Cannot read the runtime version from ${ASPNET_IMAGE}.\""),
        (Ci, "reported=\"$(docker run --rm --pull=never --platform=linux/amd64 --network=none \"${FIXTURE_IMAGE}\")\" || fail \"The fixture image did not run.\""),
        (Ci, "echo \"Fixture image $(docker image inspect --format '{{.Id}}' \"${FIXTURE_IMAGE}\") on ${platform}, user ${user}, runtime ${runtime}, layers of ${ASPNET_IMAGE}\""),
        (Ci, "docker build --platform=linux/amd64 --pull --no-cache --tag=\"${SAMPLE_IMAGE}\" --file=\"${SAMPLE_DOCKERFILE}\" \"${RUNNER_TEMP}/team-workspace\""),
        (Ci, "platform=\"$(docker image inspect --format '{{.Os}}/{{.Architecture}}' \"${SAMPLE_IMAGE}\")\""),
        (Ci, "user=\"$(docker image inspect --format '{{.Config.User}}' \"${SAMPLE_IMAGE}\")\""),
        (Ci, "pinned_layers=\"$(docker image inspect --format '{{json .RootFS.Layers}}' \"${ASPNET_IMAGE}\")\""),
        (Ci, "sample_layers=\"$(docker image inspect --format '{{json .RootFS.Layers}}' \"${SAMPLE_IMAGE}\")\""),
        (Ci, "refused=\"$(docker run --rm --pull=never --platform=linux/amd64 --network=none \"${SAMPLE_IMAGE}\" 2>&1)\" || status=$?"),
        (Ci, "described=\"$(docker run --rm --pull=never --platform=linux/amd64 --network=none \"${SAMPLE_IMAGE}\" --describe-deployment)\" || fail \"The sample image did not describe its deployment.\""),
        (Ci, "echo \"Sample image $(docker image inspect --format '{{.Id}}' \"${SAMPLE_IMAGE}\") on ${platform}, user ${user}, refuses to start without configuration\""),
        (Ci, "git ls-files --error-unmatch -- \"${COMPOSITION_DOCKERFILE}\" > /dev/null || fail \"${COMPOSITION_DOCKERFILE} is not tracked.\""),
        (Ci, "docker build --platform=linux/amd64 --pull --no-cache --tag=\"${COMPOSITION_IMAGE}\" --file=\"${COMPOSITION_DOCKERFILE}\" \"${RUNNER_TEMP}/composition\""),
        (Ci, "platform=\"$(docker image inspect --format '{{.Os}}/{{.Architecture}}' \"${COMPOSITION_IMAGE}\")\""),
        (Ci, "user=\"$(docker image inspect --format '{{.Config.User}}' \"${COMPOSITION_IMAGE}\")\""),
        (Ci, "aspnet_layers=\"$(docker image inspect --format '{{json .RootFS.Layers}}' \"${ASPNET_IMAGE}\")\""),
        (Ci, "composition_layers=\"$(docker image inspect --format '{{json .RootFS.Layers}}' \"${COMPOSITION_IMAGE}\")\""),
        (Ci, "configured=\"$(docker image inspect --format '{{json .Config.Env}}' \"${COMPOSITION_IMAGE}\")\""),
        (Ci, "writable=\"$(docker run --rm --pull=never --platform=linux/amd64 --network=none --entrypoint=find \"${COMPOSITION_IMAGE}\" /app \\( ! -user 0 -o -writable \\) -print)\" || fail \"The composition fixture image did not run.\""),
        (Ci, "echo \"Composition fixture image $(docker image inspect --format '{{.Id}}' \"${COMPOSITION_IMAGE}\") on ${platform}, user ${user}, on the layers of ${ASPNET_IMAGE}\""),
        (Ci, "docker network create --internal modularyn-acceptance > /dev/null || fail \"Cannot create the internal network.\""),
        (Ci, "docker pull --platform=linux/amd64 \"${POSTGRES_IMAGE}\" > /dev/null || fail \"Pull of ${POSTGRES_IMAGE} failed.\""),
        (Ci, "database=\"$(docker run --detach --pull=never --platform=linux/amd64 --name=modularyn-postgres --network=modularyn-acceptance --env=POSTGRES_PASSWORD=\"${GITHUB_RUN_ID}\" --env=POSTGRES_HOST_AUTH_METHOD=reject --health-cmd=\"pg_isready --host=127.0.0.1 --port=5432 --username=postgres --dbname=postgres\" --health-interval=2s --health-timeout=5s --health-retries=30 \"${POSTGRES_IMAGE}\")\" || fail \"PostgreSQL did not start.\""),
        (Ci, "health=\"$(docker inspect --format '{{.State.Health.Status}}' \"${database}\")\""),
        (Composition, "proxy=\"$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' \"${POSTGRES_CONTAINER}\")\""),
        (Composition, "docker run --rm --pull=never --platform=linux/amd64 --network=\"${network}\" --read-only --tmpfs=/tmp --tmpfs=/var/lib/modularyn/keys:uid=1654,gid=1654,mode=0700 --tmpfs=/var/lib/modularyn/blobs:uid=1654,gid=1654,mode=0700 --cap-drop=ALL --security-opt=no-new-privileges --group-add=\"${group}\" --volume=\"${secrets}:/run/modularyn/secrets:ro\" --env-file=.github/scripts/composition-sample.env --env=Modularyn__Ingress__Proxies__0=\"${proxy}\" \"${SAMPLE_IMAGE}\" --migrate || fail \"The sample image did not apply its migrations.\""),
        (Composition, "docker run --detach --pull=never --platform=linux/amd64 --name=\"${sample}\" --network=\"${network}\" --read-only --tmpfs=/tmp --tmpfs=/var/lib/modularyn/keys:uid=1654,gid=1654,mode=0700 --tmpfs=/var/lib/modularyn/blobs:uid=1654,gid=1654,mode=0700 --cap-drop=ALL --security-opt=no-new-privileges --group-add=\"${group}\" --volume=\"${secrets}:/run/modularyn/secrets:ro\" --env-file=.github/scripts/composition-sample.env --env=Modularyn__Ingress__Proxies__0=\"${proxy}\" \"${SAMPLE_IMAGE}\" > /dev/null || fail \"The sample image did not start.\""),
        (Composition, "if docker run --rm --pull=never --platform=linux/amd64 --network=\"container:${POSTGRES_CONTAINER}\" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --env=MODULARYN_SAMPLE_BASE_URL \"${COMPOSITION_IMAGE}\" -noLogo -noColor -failSkips -method \"Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_is_ready_over_HTTP\" > /dev/null 2>&1; then"),
        (Composition, "[ \"$(docker inspect --format '{{.State.Running}}' \"${sample}\")\" = \"true\" ] || break"),
        (Composition, "digest=\"$(docker image inspect --format '{{.Id}}' \"${COMPOSITION_IMAGE}\")\""),
        (Database, "[[ \"${POSTGRES_IMAGE}\" == docker.io/library/postgres:* && \"${POSTGRES_IMAGE}\" =~ ^[^@]+:[A-Za-z0-9._-]+@(sha256:[0-9a-f]{64})$ ]] || fail \"The PostgreSQL service image must be named by tag and index digest.\""),
        (Database, "configured=\"$(docker inspect --format '{{.Config.Image}}' \"${POSTGRES_CONTAINER}\")\" || fail \"Cannot inspect the service.\""),
        (Database, "[ \"${configured}\" = \"${POSTGRES_IMAGE}\" ] || fail \"The service runs ${configured}, not ${POSTGRES_IMAGE}.\""),
        (Database, "image_id=\"$(docker inspect --format '{{.Image}}' \"${POSTGRES_CONTAINER}\")\" || fail \"Cannot inspect the service.\""),
        (Database, "platform=\"$(docker image inspect --format '{{.Os}}/{{.Architecture}}' \"${image_id}\")\" || fail \"Cannot inspect ${image_id}.\""),
        (Database, "repo_digests=\"$(docker image inspect --format '{{join .RepoDigests \" \"}}' \"${image_id}\")\" || fail \"Cannot inspect ${image_id}.\""),
        (Database, "echo \"Service ${POSTGRES_CONTAINER} runs ${POSTGRES_IMAGE}, image ${image_id}, ${platform}\""),
        (Database, "local admin=(docker exec -i --user postgres \"${POSTGRES_CONTAINER}\" psql --no-psqlrc --quiet --tuples-only --no-align --set=ON_ERROR_STOP=1 --dbname=postgres)"),
        (Database, "local tcp=(docker exec -i -e PGPASSWORD --user postgres \"${POSTGRES_CONTAINER}\" psql --no-psqlrc --no-password --quiet --tuples-only --no-align --set=ON_ERROR_STOP=1)"),
        (Database, "version=\"$(\"${admin[@]}\" --command='SHOW server_version' < /dev/null)\" || fail \"Cannot query the service's server version.\""),
        (Database, "create_sql \"${name}\" \"${password}\" | \"${admin[@]}\" || fail \"Cannot create the test role and database.\""),
        (Database, "\"${admin[@]}\" --command='ALTER ROLE postgres PASSWORD NULL' < /dev/null > /dev/null || fail \"Cannot remove the bootstrap superuser password.\""),
        (Database, "hba_file=\"$(\"${admin[@]}\" --command='SHOW hba_file' < /dev/null)\" || fail \"Cannot read the hba_file setting.\""),
        (Database, "printf '%s\\n' \"${hba_rules}\" | docker exec -i --user postgres \"${POSTGRES_CONTAINER}\" sh -c 'cat > \"$1\"' sh \"${hba_file}\" || fail \"Cannot write ${hba_file}.\""),
        (Database, "rules=\"$(\"${admin[@]}\" --command=\"SELECT count(*) FILTER (WHERE error IS NOT NULL) || ' ' || count(*) FROM pg_hba_file_rules\" < /dev/null)\" || fail \"Cannot read pg_hba_file_rules.\""),
        (Database, "\"${admin[@]}\" --command=\"${listing}\" < /dev/null || fail \"Cannot read pg_hba_file_rules.\""),
        (Database, "\"${admin[@]}\" --command='SELECT pg_reload_conf()' < /dev/null > /dev/null || fail \"Cannot reload the server configuration.\""),
        (Database, "if PGPASSWORD=\"${password}\" \"${tcp[@]}\" \"host=127.0.0.1 port=5432 dbname=${name} user=${name}\" --command='SELECT current_user' < /dev/null > /dev/null 2>&1; then"),
        (Database, "if \"${tcp[@]}\" \"host=127.0.0.1 port=5432 dbname=postgres user=postgres\" --command='SELECT 1' < /dev/null > /dev/null 2>&1; then")
    ];

    // The variables that the listed lines name for images and build files, each defined once, as a literal, in the env of
    // each job named for it; and the arrays of the database script that hold docker commands. POSTGRES_IMAGE is defined in
    // both jobs that run PostgreSQL.
    public static readonly (string Job, string Variable)[] DockerVariables =
    [
        (ContainerJob, "FIXTURE_IMAGE"), (ContainerJob, "FIXTURE_DOCKERFILE"), (ContainerJob, "ASPNET_IMAGE"), (ContainerJob, "SAMPLE_IMAGE"),
        (ContainerJob, "SAMPLE_DOCKERFILE"), (ContainerJob, "COMPOSITION_IMAGE"), (ContainerJob, "COMPOSITION_DOCKERFILE"),
        (RealProviderJob, "POSTGRES_IMAGE"), (ContainerJob, "POSTGRES_IMAGE")
    ];

    // The guarded variables that name a locally built image, which must be a local tag.
    private static readonly string[] LocalImages = ["FIXTURE_IMAGE", "SAMPLE_IMAGE", "COMPOSITION_IMAGE"];

    public static readonly string[] DockerArrays = ["admin", "tcp"];

    private static readonly Regex DockerWord = new(@"(?<![A-Za-z0-9_.-])docker(?:-compose)?(?![A-Za-z0-9_.-])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ShellWrapper = new(
        @"(?<![A-Za-z0-9_.-])(?:eval|(?:ba|da|k|z)?sh(?![A-Za-z0-9_.-])[^|;&]*?\s-[A-Za-z]*c(?![A-Za-z0-9_-]))|\|\s*(?:sudo\s+)?(?:ba|da|k|z)?sh(?![A-Za-z0-9_.-])",
        RegexOptions.CultureInvariant);
    private static readonly Regex StandardInput = new(@"(?:^|\s)-(?:\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex RemoteContext = new("(?:://|[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:)", RegexOptions.CultureInvariant);
    private static readonly Regex LocalTag = new("^[a-z0-9][a-z0-9-]*:[a-z0-9][a-z0-9.-]*$", RegexOptions.CultureInvariant);

    public static bool InDockerScope(string path) => IsWorkflowPath(path) || path.StartsWith(".github/scripts/", StringComparison.Ordinal);

    public static IEnumerable<string> GuardedNames => DockerVariables.Select(variable => variable.Variable).Distinct(StringComparer.Ordinal).Concat(DockerArrays);

    private static Regex Guarded(string name) => new($"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);

    private static bool IsDefinition(string line) =>
        DockerVariables.Any(variable => line.StartsWith(variable.Variable + ":", StringComparison.Ordinal)
            && (line.Length == variable.Variable.Length + 1 || char.IsWhiteSpace(line[variable.Variable.Length + 1])));

    public static List<string> DockerCommandErrors(IReadOnlyDictionary<string, string> files) => DockerCommandErrors(files, DockerAllowlist);

    public static List<string> DockerCommandErrors(IReadOnlyDictionary<string, string> files, IReadOnlyList<(string Path, string Line)> allowlist)
    {
        var errors = new List<string>();
        errors.AddRange(AllowlistErrors(allowlist));
        var counts = allowlist.Distinct().ToDictionary(entry => entry, _ => 0);
        var scoped = files.Where(pair => InDockerScope(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        foreach (var (path, text) in scoped)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var location = $"{path}:{index + 1}";
                if (counts.ContainsKey((path, line)))
                {
                    counts[(path, line)]++;
                    if (index > 0 && Continued.IsMatch(lines[index - 1]))
                    {
                        errors.Add($"{location}: the line before an allowed line continues into it.");
                    }

                    continue;
                }

                // A guarded name may appear outside the listed lines only as its own env definition, which the variable rules check.
                foreach (var name in GuardedNames.Where(name => Guarded(name).IsMatch(line)))
                {
                    if (!(IsDefinition(line) && line.StartsWith(name + ":", StringComparison.Ordinal)))
                    {
                        errors.Add($"{location}: the guarded name {name} appears outside its definition and the allowlisted lines.");
                    }
                }

                if (line.StartsWith('#'))
                {
                    continue;
                }

                if (DockerWord.IsMatch(line))
                {
                    if (line.Contains("--build-context", StringComparison.Ordinal))
                    {
                        errors.Add($"{location}: docker build --build-context is refused; a build reads only its tracked Dockerfile and context.");
                    }

                    if (line.Contains("docker-image://", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"{location}: docker-image:// is refused; an image enters a build only through a checked FROM.");
                    }
                    else if (RemoteContext.IsMatch(line))
                    {
                        errors.Add($"{location}: a URL or git context is refused in a docker command.");
                    }

                    if (StandardInput.IsMatch(line))
                    {
                        errors.Add($"{location}: standard input is refused as a docker context or Dockerfile.");
                    }

                    errors.Add($"{location}: this docker command is not on the allowlist in ToolPinContract.Docker.cs; add it there "
                        + "through review, as one complete line.");
                }
                else if (ShellWrapper.IsMatch(line))
                {
                    errors.Add($"{location}: eval, a shell with -c and a pipe into a shell can hide a docker command, so CI files must not use them.");
                }
            }
        }

        foreach (var (entry, count) in counts.Where(pair => pair.Value != 1))
        {
            errors.Add(count == 0
                ? $"{entry.Path}: the allowed line '{entry.Line}' is not used; remove it from the allowlist."
                : $"{entry.Path}: the allowed line '{entry.Line}' appears {count} times; each allowed line appears once.");
        }

        var jobs = Jobs(files.GetValueOrDefault(CiPath, ""));
        foreach (var (job, variable) in DockerVariables)
        {
            var definitions = scoped.SelectMany(pair => Values(pair.Value, variable)).ToList();
            var inJob = jobs.TryGetValue(job, out var jobText) ? Values(jobText, variable) : [];
            if (definitions.Count != DockerVariables.Count(entry => entry.Variable == variable) || inJob is not [var value] || value.Length == 0
                || value.Any(character => char.IsWhiteSpace(character) || character is '$' or '"' or '\'' or '`'))
            {
                errors.Add($"{CiPath}: {variable} must be defined exactly once, as a literal, in the env of the {job} job.");
            }
            else if (LocalImages.Contains(variable, StringComparer.Ordinal) && !LocalTag.IsMatch(value))
            {
                errors.Add($"{CiPath}: {variable} must be a local tag such as name:tag, with no registry, so it never names a pullable image.");
            }
        }

        return errors;
    }

    // The listed lines themselves: one complete line each, no refused form, and each guarded name only in its plain form.
    private static IEnumerable<string> AllowlistErrors(IReadOnlyList<(string Path, string Line)> allowlist)
    {
        if (allowlist.Distinct().Count() != allowlist.Count)
        {
            yield return "ToolPinContract.Docker.cs: the allowlist lists a line twice.";
        }

        foreach (var (path, line) in allowlist)
        {
            var location = $"ToolPinContract.Docker.cs: the allowed line '{line}' for {path}";
            if (line != line.Trim() || line.Length == 0 || line.Contains('\n', StringComparison.Ordinal) || Continued.IsMatch(line))
            {
                yield return $"{location} must be one complete, trimmed line.";
            }

            if (line.Contains("--build-context", StringComparison.Ordinal) || line.Contains("docker-image://", StringComparison.OrdinalIgnoreCase)
                || (DockerWord.IsMatch(line) && (RemoteContext.IsMatch(line) || StandardInput.IsMatch(line))))
            {
                yield return $"{location} uses a refused build context.";
            }

            foreach (var variable in DockerVariables.Select(entry => entry.Variable).Distinct(StringComparer.Ordinal))
            {
                var plain = Regex.Matches(line, Regex.Escape("${" + variable + "}")).Count;
                if (Guarded(variable).Matches(line).Count != plain)
                {
                    yield return $"{location} uses {variable} other than as ${{{variable}}}.";
                }
            }

            foreach (var array in DockerArrays)
            {
                var plain = Regex.Matches(line, Regex.Escape("\"${" + array + "[@]}\"")).Count
                    + (line.StartsWith($"local {array}=(docker ", StringComparison.Ordinal) ? 1 : 0);
                if (Guarded(array).Matches(line).Count != plain)
                {
                    yield return $"{location} uses {array} other than as \"${{{array}[@]}}\" or its definition.";
                }
            }
        }

        foreach (var array in DockerArrays)
        {
            if (allowlist.Count(entry => entry.Line.StartsWith($"local {array}=(docker ", StringComparison.Ordinal)) != 1)
            {
                yield return $"ToolPinContract.Docker.cs: the array {array} must have exactly one listed definition.";
            }
        }
    }

    private static bool IsWorkflowPath(string path) => path.StartsWith(".github/workflows/", StringComparison.Ordinal)
        && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
}
