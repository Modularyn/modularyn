using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modularyn.Repository.Tests;

// The pins and job structure that CI-01 added: the Node.js archives, the PostgreSQL service and ASP.NET Core
// runtime images, the real-provider and container-build jobs, and the rule that no workflow installs browsers.
internal static partial class ToolPinContract
{
    public const string NodePinPath = ".github/node.json";
    public const string DatabaseScriptPath = ".github/scripts/postgresql-test-database.sh";
    public const string TestsScriptPath = ".github/scripts/real-provider-tests.sh";
    public const string RealProviderTestStep = "Test against PostgreSQL";
    public static readonly string RealProviderTestRun = $"bash {DatabaseScriptPath} ci bash {TestsScriptPath}";
    public const string FixtureDockerfile = "tests/Fixtures/container/Dockerfile";
    public const string BuildTestJob = "build-test";
    public const string RealProviderJob = "real-provider";
    public const string ContainerJob = "container-build";
    public const string PostgresImageId = "container-image:docker.io/library/postgres";
    public const string AspNetImageId = "container-image:mcr.microsoft.com/dotnet/aspnet";
    public const string LinuxPlatform = "linux/amd64";
    public static readonly string RealProviderFilter = $"--filter \"{PostgreSqlEnvironmentTests.RequiresTrait}={PostgreSqlEnvironmentTests.PostgreSql}\"";
    public static readonly string UnitFilter = $"--filter \"{PostgreSqlEnvironmentTests.RequiresTrait}!={PostgreSqlEnvironmentTests.PostgreSql}\"";

    // The steps that the real-provider job copies from build-test; their run blocks must stay identical.
    public static readonly string[] SharedLinuxSteps =
    [
        "Install the pinned .NET SDK (Linux and macOS)", "Verify the .NET SDK (Linux and macOS)",
        "Install the pinned Node.js (Linux and macOS)", "Verify Node.js (Linux and macOS)"
    ];

    private static readonly string[] NodePinFields = ["version", "source", "archives"];
    private static readonly string[] NodeArchiveFields = ["url", "sha256"];
    private static readonly Regex PinnedImage = new(
        @"^(?<name>[a-z0-9-]+(?:\.[a-z0-9-]+)+/[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*):(?<tag>[A-Za-z0-9_][A-Za-z0-9_.-]*)@sha256:[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ImageKey = new(
        """^\s*(?:-\s+)?image\s*:\s*(?<quote>["']?)(?<value>[^"'\s#]*)\k<quote>\s*(?:#.*)?$""", RegexOptions.CultureInvariant);
    private static readonly Regex InlineContainer = new(@"^\s*container\s*:\s*[^\s#]", RegexOptions.CultureInvariant);
    private static readonly Regex JobKey = new(@"^  (?<id>[A-Za-z_][A-Za-z0-9_-]*):\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex BrowserInstall = new(@"\bplaywright\b.*\binstall\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ParserDirective = new(@"^\s*#\s*(?<name>[A-Za-z][A-Za-z0-9_-]*)\s*=", RegexOptions.CultureInvariant);
    private static readonly string[] NamedDirectives = ["syntax", "escape", "check"];
    private static readonly string[] RunnerFiles = ["GITHUB_ENV", "GITHUB_OUTPUT", "GITHUB_STATE", "GITHUB_PATH"];
    private static readonly Regex FromInstruction = new(@"^\s*from\s", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FromOption = new(@"(?:^|[\s,=-])from=(?<source>[^,\s]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AddInstruction = new(@"^\s*(?:onbuild\s+)?add\s", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HttpSource = new("https?://", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex GitSource = new("(?:git://|ssh://|[a-z0-9._-]+@[a-z0-9.-]+:)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AddChecksum = new(@"(?:^|\s)--checksum=(?<value>\S*)", RegexOptions.CultureInvariant);
    private static readonly Regex Continued = new(@"\\\s*$", RegexOptions.CultureInvariant);

    // Dockerfiles as the container-build job finds them: by file name, in any letter case.
    public static bool IsDockerfile(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return name.EndsWith("dockerfile", StringComparison.Ordinal) || name.Contains("dockerfile.", StringComparison.Ordinal)
            || name.EndsWith("containerfile", StringComparison.Ordinal) || name.Contains("containerfile.", StringComparison.Ordinal);
    }

    public static string NodeArchiveUrl(string version, string target) =>
        $"https://nodejs.org/dist/v{version}/node-v{version}-{target}.{(target.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}";

    // The reference CI pulls for an image entry: its name, the tag its index was fetched by, and the index digest.
    public static string? PinnedReference(string name, string indexUrl, string indexDigest)
    {
        const string marker = "/manifests/";
        var at = indexUrl.LastIndexOf(marker, StringComparison.Ordinal);
        var tag = at < 0 ? "" : indexUrl[(at + marker.Length)..];
        return tag.Length == 0 || tag.StartsWith("sha256:", StringComparison.Ordinal) ? null : $"{name}:{tag}@{indexDigest}";
    }

    public static bool IsPinnedImage(string reference) => PinnedImage.IsMatch(reference);

    // The jobs of a workflow by id, each as its indented text, read from the lines under the top-level jobs key.
    public static Dictionary<string, string> Jobs(string workflow)
    {
        var jobs = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = workflow.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, "jobs:");
        string? current = null;
        var block = new List<string>();
        for (var index = start < 0 ? lines.Length : start + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#')
            {
                break;
            }

            var key = JobKey.Match(line);
            if (key.Success)
            {
                if (current is not null)
                {
                    jobs[current] = string.Join('\n', block);
                }

                current = key.Groups["id"].Value;
                block = [];
                continue;
            }

            block.Add(line);
        }

        if (current is not null)
        {
            jobs[current] = string.Join('\n', block);
        }

        return jobs;
    }

    // The run script of the named step in a job: the block under "run: |", or the value of a one-line run.
    public static string? Run(string job, string stepName)
    {
        var lines = job.Split('\n');
        var start = Array.IndexOf(lines, $"      - name: {stepName}");
        if (start < 0)
        {
            return null;
        }

        for (var index = start + 1; index < lines.Length && !lines[index].StartsWith("      - ", StringComparison.Ordinal); index++)
        {
            if (lines[index] == "        run: |")
            {
                var script = lines.Skip(index + 1).TakeWhile(line => line.Length == 0 || line.StartsWith("          ", StringComparison.Ordinal));
                return string.Join('\n', script).TrimEnd('\n');
            }

            if (lines[index].StartsWith("        run: ", StringComparison.Ordinal))
            {
                return lines[index]["        run: ".Length..];
            }
        }

        return null;
    }

    // The values of every "KEY: value" line in a job or workflow. A value in matching quotes is unquoted; otherwise a
    // trailing comment is removed.
    public static List<string> Values(string text, string key)
    {
        var values = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var match = Regex.Match(line, $@"^\s*(?:-\s+)?{Regex.Escape(key)}\s*:\s*(?<value>.*?)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value;
            values.Add(value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0]
                ? value[1..^1]
                : Regex.Replace(value, @"\s+#.*$", "", RegexOptions.CultureInvariant));
        }

        return values;
    }

    // Every base and copy source of a Dockerfile must be the pinned image or an earlier stage, as the container-build
    // job also checks before it builds.
    public static List<string> DockerfileErrors(string path, string text, string pinned)
    {
        var errors = new List<string>();
        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var bases = 0;
        var instructions = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            // BuildKit ignores a UTF-8 byte-order mark before the first line, so the checks do too.
            var line = index == 0 && lines[index].StartsWith('\uFEFF') ? lines[index][1..] : lines[index];
            var location = $"{path}:{index + 1}";
            // Parser directives can select a frontend image or change the continuation character, so none is allowed:
            // syntax, escape and check anywhere, and any "# name=" line before the first instruction.
            var directive = ParserDirective.Match(line);
            if (directive.Success && (instructions == 0
                    || NamedDirectives.Contains(directive.Groups["name"].Value.ToLowerInvariant(), StringComparer.Ordinal)))
            {
                errors.Add($"{location}: the parser directive {directive.Groups["name"].Value.ToLowerInvariant()} is refused; "
                    + "directives can select a frontend image or change how lines continue.");
            }

            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (line.Trim().Length > 0)
            {
                instructions++;
            }

            foreach (Match option in FromOption.Matches(line))
            {
                var source = option.Groups["source"].Value;
                if (!stages.Contains(source) && !source.All(char.IsAsciiDigit))
                {
                    errors.Add($"{location}: {source} is not an earlier stage, so it would pull an unpinned image.");
                }
            }

            // ADD of a git or ssh source is refused, since --checksum=sha256: cannot pin it. ADD of an http(s) source must
            // name exactly one source and verify it with a pinned SHA-256; a variable source could hide a remote one, so it
            // needs the checksum too.
            if (AddInstruction.IsMatch(line))
            {
                var remote = HttpSource.Matches(line).Count;
                var checksums = AddChecksum.Matches(line);
                if (Continued.IsMatch(line))
                {
                    errors.Add($"{location}: keep each ADD instruction on one line.");
                }
                else if (GitSource.IsMatch(line))
                {
                    errors.Add($"{location}: ADD of a git or ssh source is refused, because --checksum=sha256: cannot pin it.");
                }
                else if ((remote > 0 || line.Contains('$', StringComparison.Ordinal)) && (remote > 1 || checksums.Count != 1
                    || !Regex.IsMatch(checksums[0].Groups["value"].Value, "^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)))
                {
                    errors.Add($"{location}: ADD of a remote or variable source needs exactly one source and --checksum=sha256:<digest>.");
                }
            }

            if (!FromInstruction.IsMatch(line))
            {
                continue;
            }

            bases++;
            if (Continued.IsMatch(line))
            {
                errors.Add($"{location}: keep each FROM instruction on one line.");
                continue;
            }

            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var position = 1;
            if (position < words.Length && words[position].StartsWith("--", StringComparison.Ordinal))
            {
                if (!words[position].Equals("--platform=" + LinuxPlatform, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{location}: the only FROM option allowed is --platform={LinuxPlatform}.");
                }

                position++;
            }

            var image = position < words.Length ? words[position] : "";
            position++;
            string? stage = null;
            if (words.Length > position)
            {
                if (words.Length == position + 2 && words[position].Equals("AS", StringComparison.OrdinalIgnoreCase))
                {
                    stage = words[position + 1];
                }
                else
                {
                    errors.Add($"{location}: FROM takes an image and an optional AS <stage>.");
                }
            }

            if (image != pinned && !stages.Contains(image))
            {
                errors.Add($"{location}: FROM {image} must be {pinned} or an earlier stage.");
            }

            if (stage is not null)
            {
                stages.Add(stage);
            }
        }

        if (bases == 0)
        {
            errors.Add($"{path}: a Dockerfile needs a FROM instruction.");
        }

        return errors;
    }

    private static void ValidateNode(IReadOnlyDictionary<string, string> files, List<RegisterEntry> register, List<string> errors)
    {
        var workflow = files.GetValueOrDefault(CiPath, "");
        if (!workflow.Contains(NodePinPath, StringComparison.Ordinal))
        {
            errors.Add($"{CiPath}: must install Node.js from the archives pinned in {NodePinPath}.");
        }

        var versions = Values(workflow, "NODE_VERSION");
        var registered = register.Where(entry => entry.Id == "binary:node" && entry.Status == "in-use").ToList();
        if (versions.Count != 1)
        {
            errors.Add($"{CiPath}: must pin NODE_VERSION exactly once.");
        }

        if (registered.Count != 1)
        {
            errors.Add($"{DependencyRegister.RegisterPath}: needs exactly one in-use binary:node entry for {NodePinPath}.");
        }

        if (!files.TryGetValue(NodePinPath, out var text))
        {
            errors.Add($"{NodePinPath}: the Node.js archive pin file is missing.");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{NodePinPath}: the pin file must be a JSON object.");
                return;
            }

            Fields(root, NodePinFields, NodePinPath, errors);
            var version = Text(root, "version");
            if (version is null || versions.Count != 1 || version != versions[0])
            {
                errors.Add($"{NodePinPath}: version {version ?? "(missing)"} must equal the workflow's NODE_VERSION.");
            }

            if (registered.Count == 1 && version != registered[0].Version)
            {
                errors.Add($"{NodePinPath}: version {version ?? "(missing)"} must equal the register's in-use binary:node version {registered[0].Version}.");
            }

            var expectedVersion = version ?? "<version>";
            var source = Text(root, "source");
            if (source != $"https://nodejs.org/dist/v{expectedVersion}/SHASUMS256.txt")
            {
                errors.Add($"{NodePinPath}: source must be the release's https://nodejs.org/dist/v{expectedVersion}/SHASUMS256.txt.");
            }
            else if (registered.Count == 1 && !registered[0].Sources.Contains(source))
            {
                errors.Add($"{NodePinPath}: the register's binary:node entry must cite the source {source}.");
            }

            if (!root.TryGetProperty("archives", out var archives) || archives.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{NodePinPath}: archives must be an object keyed by Node.js platform.");
                return;
            }

            var targets = archives.EnumerateObject().Select(archive => archive.Name).ToList();
            if (targets.Count != DependencyRegister.NodeTargets.Length || !targets.ToHashSet(StringComparer.Ordinal).SetEquals(DependencyRegister.NodeTargets))
            {
                errors.Add($"{NodePinPath}: archives must name exactly {string.Join(", ", DependencyRegister.NodeTargets)}.");
            }

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var archive in archives.EnumerateObject())
            {
                var location = $"{NodePinPath}: {archive.Name}";
                if (archive.Value.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{location} must be an object with url and sha256.");
                    continue;
                }

                Fields(archive.Value, NodeArchiveFields, location, errors);
                var url = Text(archive.Value, "url");
                var expected = NodeArchiveUrl(expectedVersion, archive.Name);
                if (url != expected)
                {
                    errors.Add($"{location} url must be the official Node.js archive {expected}.");
                }

                var sha256 = Text(archive.Value, "sha256");
                if (sha256 is null || !Sha256.IsMatch(sha256))
                {
                    errors.Add($"{location} sha256 must be 64 lowercase hexadecimal characters.");
                    continue;
                }

                if (!hashes.TryAdd(sha256, archive.Name))
                {
                    errors.Add($"{location} sha256 duplicates the hash of {hashes[sha256]}.");
                }

                // The register records the same digest, so a change to either the pin file or the register alone fails.
                if (registered.Count == 1 && !registered[0].Digests.Contains((archive.Name, url ?? "", "sha256:" + sha256)))
                {
                    errors.Add($"{location} must equal the register's binary:node {archive.Name} url and digest.");
                }
            }
        }
        catch (JsonException)
        {
            errors.Add($"{NodePinPath}: the pin file must contain valid JSON.");
        }
    }

    private static void ValidateImages(IReadOnlyDictionary<string, string> files, List<RegisterEntry> register, List<string> errors)
    {
        foreach (var (path, text) in files.Where(pair => pair.Key.StartsWith(".github/", StringComparison.Ordinal))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Any(line => BrowserInstall.IsMatch(line)))
            {
                errors.Add($"{path}: must not install Playwright browsers, whose downloads Playwright does not verify; "
                    + "pin and verify them first (docs/dependencies/README.md, exception 6).");
            }

            if (!path.StartsWith(".github/workflows/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var line in lines.Where(line => !line.TrimStart().StartsWith('#')))
            {
                var image = ImageKey.Match(line);
                if (image.Success && !IsPinnedImage(image.Groups["value"].Value))
                {
                    errors.Add($"{path}: image {image.Groups["value"].Value} must be <registry>/<repository>:<tag>@sha256:<index digest>.");
                }

                if (InlineContainer.IsMatch(line))
                {
                    errors.Add($"{path}: give a job container as a mapping with a pinned image key.");
                }
            }
        }

        var aspnet = ImageReference(register, AspNetImageId, errors);
        var dockerfiles = files.Where(pair => IsDockerfile(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        if (!files.ContainsKey(FixtureDockerfile))
        {
            errors.Add($"{FixtureDockerfile}: the container-build fixture is missing.");
        }

        if (aspnet is not null)
        {
            foreach (var (path, text) in dockerfiles)
            {
                errors.AddRange(DockerfileErrors(path, text, aspnet));
            }
        }
    }

    private static void ValidateJobs(IReadOnlyDictionary<string, string> files, List<RegisterEntry> register, List<string> errors)
    {
        if (!files.TryGetValue(CiPath, out var workflow))
        {
            return;
        }

        var jobs = Jobs(workflow);
        foreach (var id in new[] { BuildTestJob, RealProviderJob, ContainerJob }.Where(id => !jobs.ContainsKey(id)))
        {
            errors.Add($"{CiPath}: the {id} job is missing.");
        }

        // Tests that need PostgreSQL run in the real-provider job and nowhere else.
        foreach (var (id, job) in jobs)
        {
            var tests = job.Split('\n').Where(line => line.Contains("dotnet test", StringComparison.Ordinal)).ToList();
            var filter = id == RealProviderJob ? RealProviderFilter : UnitFilter;
            if (tests.Any(line => !line.TrimEnd().EndsWith(" " + filter, StringComparison.Ordinal)))
            {
                errors.Add($"{CiPath}: every dotnet test in the {id} job must select its tests with {filter}.");
            }

            if (id is BuildTestJob && tests.Count == 0)
            {
                errors.Add($"{CiPath}: the {id} job must run dotnet test with {filter}.");
            }
        }

        if (jobs.TryGetValue(BuildTestJob, out var buildTest) && jobs.TryGetValue(RealProviderJob, out var realProvider))
        {
            foreach (var step in SharedLinuxSteps)
            {
                var original = Run(buildTest, step);
                if (original is null || original != Run(realProvider, step))
                {
                    errors.Add($"{CiPath}: the {RealProviderJob} job must run the step '{step}' exactly as {BuildTestJob} does.");
                }
            }
        }

        if (jobs.TryGetValue(RealProviderJob, out var service))
        {
            ValidateRealProviderJob(service, register, files, errors);
        }

        if (jobs.TryGetValue(ContainerJob, out var container))
        {
            var aspnet = ImageReference(register, AspNetImageId, errors);
            if (Values(container, "runs-on") is not ["ubuntu-latest"])
            {
                errors.Add($"{CiPath}: the {ContainerJob} job must run on ubuntu-latest, a Linux x64 runner.");
            }

            if (aspnet is not null && (Values(container, "ASPNET_IMAGE") is not [var pinned] || pinned != aspnet))
            {
                errors.Add($"{CiPath}: the {ContainerJob} job's ASPNET_IMAGE must be the register's {AspNetImageId} reference {aspnet}.");
            }

            if (Values(container, "FIXTURE_DOCKERFILE") is not [FixtureDockerfile])
            {
                errors.Add($"{CiPath}: the {ContainerJob} job must build {FixtureDockerfile}.");
            }

            if (!container.Contains($"docker build --platform={LinuxPlatform} ", StringComparison.Ordinal))
            {
                errors.Add($"{CiPath}: the {ContainerJob} job must build for {LinuxPlatform} with docker build --platform={LinuxPlatform}.");
            }
        }

        errors.AddRange(DockerCommandErrors(files));
    }

    // The step names of a job, in order.
    public static List<string> StepNames(string job) =>
        job.Split('\n').Where(line => line.StartsWith("      - name: ", StringComparison.Ordinal))
            .Select(line => line["      - name: ".Length..]).ToList();

    // Logical lines of a script: continuation lines joined, comment lines dropped.
    public static List<string> LogicalLines(string text)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (current.Length == 0 && raw.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (Continued.IsMatch(raw))
            {
                current += Continued.Replace(raw, " ");
                continue;
            }

            lines.Add(current + raw);
            current = "";
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static void ValidateRealProviderJob(string job, List<RegisterEntry> register, IReadOnlyDictionary<string, string> files,
        List<string> errors)
    {
        var postgres = ImageReference(register, PostgresImageId, errors);
        if (Values(job, "runs-on") is not ["ubuntu-latest"])
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job must run on ubuntu-latest, a Linux x64 runner.");
        }

        var images = Values(job, "image");
        if (postgres is not null && (images is not [var image] || image != postgres))
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job's only service image must be the register's {PostgresImageId} reference {postgres}.");
        }

        if (postgres is not null && (Values(job, "POSTGRES_IMAGE") is not [var checkedImage] || checkedImage != postgres))
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job's POSTGRES_IMAGE must equal its service image {postgres}.");
        }

        if (Values(job, "POSTGRES_HOST_AUTH_METHOD") is not ["reject"])
        {
            errors.Add($"{CiPath}: the PostgreSQL service must start with POSTGRES_HOST_AUTH_METHOD reject, so no host connection "
                + "signs in before the database step sets the per-run credentials.");
        }

        if (!Values(job, "options").Any(options => options.Contains("--health-cmd", StringComparison.Ordinal)
                && options.Contains("pg_isready", StringComparison.Ordinal)))
        {
            errors.Add($"{CiPath}: the PostgreSQL service needs a pg_isready health check.");
        }

        var server = register.Where(entry => entry.Id == "binary:postgresql" && entry.Status == "in-use").ToList();
        var imageEntries = register.Where(entry => entry.Id == PostgresImageId).ToList();
        if (Values(job, "POSTGRESQL_VERSION") is not [var version] || server is not [var entry] || entry.Version != version
            || imageEntries is not [var imageEntry] || imageEntry.Version != version)
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job's POSTGRESQL_VERSION must equal the versions of the register's in-use "
                + $"binary:postgresql and {PostgresImageId} entries.");
        }

        // The credentials exist only in the test step: it comes after the build, and only it runs the database script.
        var steps = StepNames(job);
        if (Run(job, RealProviderTestStep) != RealProviderTestRun || steps.IndexOf(RealProviderTestStep) < steps.IndexOf("Build") + 1
            || steps.IndexOf("Build") < 0)
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job's '{RealProviderTestStep}' step must come after its Build step and run "
                + $"{RealProviderTestRun}.");
        }

        if (job.Split('\n').Count(line => line.Contains(DatabaseScriptPath, StringComparison.Ordinal)) != 1)
        {
            errors.Add($"{CiPath}: only the '{RealProviderTestStep}' step may run {DatabaseScriptPath}, so the credentials exist in no other step.");
        }

        var script = files.GetValueOrDefault(DatabaseScriptPath, "");
        if (!script.Contains("echo \"::add-mask::${password}\"", StringComparison.Ordinal)
            || !script.Contains("/dev/urandom", StringComparison.Ordinal) || !script.Contains(PostgreSqlTarget.Variable, StringComparison.Ordinal))
        {
            errors.Add($"{DatabaseScriptPath}: must generate the password from /dev/urandom, mask it, and set {PostgreSqlTarget.Variable}.");
        }

        // Both scripts hold the credentials in their environment, so neither may write to a runner file that a later step reads.
        var tests = files.GetValueOrDefault(TestsScriptPath, "");
        foreach (var (path, text) in new[] { (DatabaseScriptPath, script), (TestsScriptPath, tests) })
        {
            if (RunnerFiles.Any(file => text.Contains(file, StringComparison.Ordinal)))
            {
                errors.Add($"{path}: must pass {PostgreSqlTarget.Variable} only to its command, never to {string.Join(", ", RunnerFiles)}.");
            }
        }

        // dotnet test succeeds when its filter selects nothing, so the tests script counts the results itself.
        var runs = LogicalLines(tests).Where(line => line.Contains("dotnet test", StringComparison.Ordinal)).ToList();
        if (runs.Count == 0 || runs.Any(line => !line.TrimEnd().EndsWith(" " + RealProviderFilter, StringComparison.Ordinal)))
        {
            errors.Add($"{TestsScriptPath}: must run dotnet test with {RealProviderFilter}, and only with it.");
        }

        foreach (var required in new[]
                 {
                     "git ls-files -z -- 'tests/*/*.Tests.csproj'", "--logger \"trx;LogFileName=${name}.trx\"",
                     "[ \"${passed}\" -eq \"${total}\" ]", "[ \"${passed}\" -ge 1 ]", "count_results \"${results}\" \"${names[@]}\""
                 })
        {
            if (!tests.Contains(required, StringComparison.Ordinal))
            {
                errors.Add($"{TestsScriptPath}: must run every test project with its own results file and require at least one "
                    + $"PostgreSQL test to have run and all to have passed ({required}).");
            }
        }
    }

    private static string? ImageReference(List<RegisterEntry> register, string id, List<string> errors)
    {
        var entries = register.Where(entry => entry.Id == id && entry.Status == "in-use").ToList();
        var index = entries.Count == 1 ? entries[0].Digests.Where(digest => digest.Target == "index").ToList() : [];
        var reference = index.Count == 1 ? PinnedReference(entries[0].Name, index[0].Url, index[0].Digest) : null;
        if (reference is null || !IsPinnedImage(reference))
        {
            errors.Add($"{DependencyRegister.RegisterPath}: needs exactly one in-use {id} entry with its index digest.");
            return null;
        }

        return reference;
    }
}
