using System.Text.RegularExpressions;

namespace Modularyn.Repository.Tests;

// The docker commands of the workflows and CI scripts, as an exact allowlist. Every line in .github/workflows and
// .github/scripts that names docker must equal, after trimming whitespace, one of the lines below for its file. Any
// other docker command, continued line, or eval, bash -c or sh -c fails. A new command, such as the build of the
// sample application that work order CODE-09 adds, joins the list through review. docs/ci.md states the limits.
internal static partial class ToolPinContract
{
    private const string Ci = CiPath;
    private const string Database = DatabaseScriptPath;

    public static readonly (string Path, string Line)[] DockerAllowlist =
    [
        (Ci, "docker build --platform=linux/amd64 --pull --no-cache --tag=\"${FIXTURE_IMAGE}\" --file=\"${FIXTURE_DOCKERFILE}\" tests/Fixtures/container"),
        (Ci, "platform=\"$(docker image inspect --format '{{.Os}}/{{.Architecture}}' \"${FIXTURE_IMAGE}\")\""),
        (Ci, "user=\"$(docker image inspect --format '{{.Config.User}}' \"${FIXTURE_IMAGE}\")\""),
        (Ci, "docker pull --platform=linux/amd64 \"${ASPNET_IMAGE}\" > /dev/null || fail \"Pull of ${ASPNET_IMAGE} failed.\""),
        (Ci, "base_layers=\"$(docker image inspect --format '{{json .RootFS.Layers}}' \"${ASPNET_IMAGE}\")\""),
        (Ci, "fixture_layers=\"$(docker image inspect --format '{{json .RootFS.Layers}}' \"${FIXTURE_IMAGE}\")\""),
        (Ci, "reported=\"$(docker run --rm --pull=never --platform=linux/amd64 --network=none \"${FIXTURE_IMAGE}\")\" || fail \"The fixture image did not run.\""),
        (Ci, "echo \"Fixture image $(docker image inspect --format '{{.Id}}' \"${FIXTURE_IMAGE}\") on ${platform}, user ${user}, runtime ${runtime}, layers of ${ASPNET_IMAGE}\""),
        (Database, "configured=\"$(docker inspect --format '{{.Config.Image}}' \"${POSTGRES_CONTAINER}\")\" || fail \"Cannot inspect the service.\""),
        (Database, "image_id=\"$(docker inspect --format '{{.Image}}' \"${POSTGRES_CONTAINER}\")\" || fail \"Cannot inspect the service.\""),
        (Database, "platform=\"$(docker image inspect --format '{{.Os}}/{{.Architecture}}' \"${image_id}\")\" || fail \"Cannot inspect ${image_id}.\""),
        (Database, "repo_digests=\"$(docker image inspect --format '{{join .RepoDigests \" \"}}' \"${image_id}\")\" || fail \"Cannot inspect ${image_id}.\""),
        (Database, "local admin=(docker exec -i --user postgres \"${POSTGRES_CONTAINER}\" psql --no-psqlrc --quiet --tuples-only --no-align --set=ON_ERROR_STOP=1 --dbname=postgres)"),
        (Database, "local tcp=(docker exec -i -e PGPASSWORD --user postgres \"${POSTGRES_CONTAINER}\" psql --no-psqlrc --no-password --quiet --tuples-only --no-align --set=ON_ERROR_STOP=1)"),
        (Database, "printf '%s\\n' \"${hba_rules}\" | docker exec -i --user postgres \"${POSTGRES_CONTAINER}\" sh -c 'cat > \"$1\"' sh \"${hba_file}\" || fail \"Cannot write ${hba_file}.\"")
    ];

    // The variables that the allowed lines name for images and build files, each defined once, as a literal, in the
    // env of the job named, and assigned nowhere else.
    public static readonly (string Job, string Variable)[] DockerVariables =
    [
        (ContainerJob, "FIXTURE_IMAGE"), (ContainerJob, "FIXTURE_DOCKERFILE"), (ContainerJob, "ASPNET_IMAGE"), (RealProviderJob, "POSTGRES_IMAGE")
    ];

    private static readonly Regex DockerWord = new(@"(?<![A-Za-z0-9_.-])docker(?![A-Za-z0-9_.-])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ShellWrapper = new(@"(?<![A-Za-z0-9_.-])(?:eval|(?:ba|da|k|z)?sh\s+-[A-Za-z]*c)(?![A-Za-z0-9_.-])",
        RegexOptions.CultureInvariant);
    private static readonly Regex StandardInput = new(@"(?:^|\s)-(?:\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex RemoteContext = new("(?:://|git@)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ArrayDefinition = new(@"^local (?<name>[a-z_]+)=\(docker ", RegexOptions.CultureInvariant);
    private static readonly Regex LocalTag = new("^[a-z0-9][a-z0-9-]*:[a-z0-9][a-z0-9.-]*$", RegexOptions.CultureInvariant);

    public static bool InDockerScope(string path) => IsWorkflowPath(path) || path.StartsWith(".github/scripts/", StringComparison.Ordinal);

    public static List<string> DockerCommandErrors(IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        var used = new HashSet<(string, string)>();
        var scoped = files.Where(pair => InDockerScope(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        foreach (var (path, text) in scoped)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var location = $"{path}:{index + 1}";
                var allowed = DockerAllowlist.Contains((path, line));
                if (!DockerWord.IsMatch(line))
                {
                    if (!allowed && ShellWrapper.IsMatch(line))
                    {
                        errors.Add($"{location}: eval, bash -c and sh -c can hide a docker command, so CI files must not use them.");
                    }

                    continue;
                }

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

                if (!allowed)
                {
                    errors.Add($"{location}: this docker command is not on the allowlist in ToolPinContract.Docker.cs; add it there "
                        + "through review, as one complete line.");
                    continue;
                }

                used.Add((path, line));
                if (index > 0 && Continued.IsMatch(lines[index - 1]))
                {
                    errors.Add($"{location}: the line before an allowed docker command continues into it.");
                }
            }
        }

        foreach (var entry in DockerAllowlist.Where(entry => !used.Contains(entry)))
        {
            errors.Add($"{entry.Path}: the allowed docker command '{entry.Line}' is not used; remove it from the allowlist.");
        }

        // The arrays that the allowed lines define hold docker commands, so only those lines may assign them.
        foreach (var (path, line) in DockerAllowlist)
        {
            var array = ArrayDefinition.Match(line);
            if (!array.Success || !files.TryGetValue(path, out var text))
            {
                continue;
            }

            var name = Regex.Escape(array.Groups["name"].Value);
            var assignments = text.Replace("\r\n", "\n").Split('\n').Select(candidate => candidate.Trim())
                .Count(candidate => Regex.IsMatch(candidate, $@"(?<![A-Za-z0-9_]){name}(?:\[[^\]]*\])?\+?=", RegexOptions.CultureInvariant));
            if (assignments != 1)
            {
                errors.Add($"{path}: only its allowed docker line may assign {array.Groups["name"].Value}.");
            }
        }

        var jobs = Jobs(files.GetValueOrDefault(CiPath, ""));
        foreach (var (job, variable) in DockerVariables)
        {
            var definitions = scoped.SelectMany(pair => Values(pair.Value, variable)).ToList();
            var inJob = jobs.TryGetValue(job, out var jobText) ? Values(jobText, variable) : [];
            if (definitions.Count != 1 || inJob is not [var value] || value.Length == 0
                || value.Any(character => char.IsWhiteSpace(character) || character is '$' or '"' or '\'' or '`'))
            {
                errors.Add($"{CiPath}: {variable} must be defined exactly once, as a literal, in the env of the {job} job.");
            }
            else if (variable == "FIXTURE_IMAGE" && !LocalTag.IsMatch(value))
            {
                errors.Add($"{CiPath}: FIXTURE_IMAGE must be a local tag such as name:tag, with no registry, so it never names a pullable image.");
            }

            if (scoped.Any(pair => pair.Value.Split('\n').Any(candidate =>
                    Regex.IsMatch(candidate, $@"(?<![A-Za-z0-9_]){variable}\+?=", RegexOptions.CultureInvariant))))
            {
                errors.Add($"{CiPath}: {variable} may be set only by its env definition, not by a shell assignment or GITHUB_ENV.");
            }
        }

        return errors;
    }

    private static bool IsWorkflowPath(string path) => path.StartsWith(".github/workflows/", StringComparison.Ordinal)
        && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
}
