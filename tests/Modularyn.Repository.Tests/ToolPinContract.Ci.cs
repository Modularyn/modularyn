using System.Text.Json;
using System.Text.RegularExpressions;

namespace Modularyn.Repository.Tests;

// The pins and job structure that CI-01 added: the Node.js archives, the PostgreSQL service and ASP.NET Core
// runtime images, the real-provider and container-build jobs, and the rule that no workflow installs browsers. CODE-10
// added the AC-001 container rows in the container-build job, the composition fixture image they run in, and the checks
// of both jobs' evidence records.
internal static partial class ToolPinContract
{
    public const string NodePinPath = ".github/node.json";
    public const string BrowserPinPath = ".github/browsers.json";
    public const string BrowserStep = "Download and verify the pinned browser archive";
    public const string BrowserArchiveVariable = "MODULARYN_TEST_BROWSER_ARCHIVE";
    public const string BrowserSource = "https://googlechromelabs.github.io/chrome-for-testing/known-good-versions-with-downloads.json";
    public const string PlaywrightId = "nuget:microsoft.playwright";
    public const string DatabaseScriptPath = ".github/scripts/postgresql-test-database.sh";
    public const string TestsScriptPath = ".github/scripts/real-provider-tests.sh";
    public const string RealProviderTestStep = "Test against PostgreSQL";
    public static readonly string RealProviderTestRun = $"bash {DatabaseScriptPath} ci bash {TestsScriptPath}";
    public const string FixtureDockerfile = "tests/Fixtures/container/Dockerfile";
    public const string SampleDockerfile = "src/Tooling/Modularyn.Tooling.Samples.TeamWorkspace.Host/Dockerfile";
    public const string CompositionDockerfile = "tests/Fixtures/composition/Dockerfile";
    public const string CompositionScriptPath = ".github/scripts/composition-tests.sh";
    public const string EvidenceScriptPath = ".github/scripts/evidence-records.sh";
    public const string DatabaseStartStep = "Start PostgreSQL on an internal network";
    public const string CompositionCheckStep = "Check the composition fixture image";
    public const string CompositionTestStep = "Run the AC-001 container rows against PostgreSQL";
    public static readonly string CompositionTestRun = $"bash {DatabaseScriptPath} ci bash {CompositionScriptPath}";
    public const string HostEvidenceStep = "Choose the host evidence directory";
    public const string HostEvidenceCheckStep = "Check the host evidence records";
    public const string ContainerEvidenceCheckStep = "Check the container evidence records";
    public static readonly string EvidenceCheckRun = $"bash {EvidenceScriptPath}";
    public const string EvidenceCheckTest = "Modularyn.Repository.Tests.CiEvidenceTests.The_records_this_CI_run_wrote_validate_against_the_repository_contracts";
    public const string ReadinessTest = "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_is_ready_over_HTTP";
    public const string SignInPageTest = "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_serves_its_sign_in_page_over_HTTP";
    public const string AcceptanceNetwork = "modularyn-acceptance";
    public const string BuildTestJob = "build-test";
    public const string RealProviderJob = "real-provider";
    public const string ContainerJob = "container-build";
    public const string PostgresImageId = "container-image:docker.io/library/postgres";
    public const string AspNetImageId = "container-image:mcr.microsoft.com/dotnet/aspnet";
    public const string LinuxPlatform = "linux/amd64";
    public static readonly string RealProviderFilter = $"--filter \"{PostgreSqlEnvironmentTests.RequiresTrait}={PostgreSqlEnvironmentTests.PostgreSql}\"";
    public static readonly string UnitFilter = $"--filter \"{PostgreSqlEnvironmentTests.RequiresTrait}!={PostgreSqlEnvironmentTests.PostgreSql}\"";
    public const string ClientDirectory = "src/Framework/Modularyn.Framework.Sdk.Client/typescript";
    public const string VitestSwitch = "MODULARYN_CLIENT_TEST_VITEST";

    // The steps that the real-provider job copies from build-test; their run blocks must stay identical.
    public static readonly string[] SharedLinuxSteps =
    [
        "Install the pinned .NET SDK (Linux and macOS)", "Verify the .NET SDK (Linux and macOS)",
        "Install the pinned Node.js (Linux and macOS)", "Verify Node.js (Linux and macOS)"
    ];

    // The SDK steps that the container-build job copies from build-test to publish the sample application's host.
    public static readonly string[] SharedSdkSteps = SharedLinuxSteps[..2];

    private static readonly string[] NodePinFields = ["version", "source", "archives"];
    private static readonly string[] BrowserPinFields = ["browser", "version", "playwright", "source", "archives"];
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
    // npm in a command position: at the start of a line, after a run key, a separator or an opening substitution, or after a
    // shell keyword. npm in a message, such as "npm is not on PATH", is not a command.
    private static readonly Regex NpmCommand = new(
        @"(?:^|[;&|(`]\s*|\brun:[ \t]+|\b(?:then|do|else)\s+)npm(?:\.cmd)?\s+(?<arguments>[^;&|)`#]*)", RegexOptions.CultureInvariant);
    private static readonly Regex NpmClean = new(@"^npm ci(?:\s|$)", RegexOptions.CultureInvariant);
    public const string PythonRule = "no CI step runs python or python3: every tool CI runs is pinned, registered and drift-guarded, and no Python runtime is.";
    // python, python3, a versioned, quoted or absolute python, or the py launcher, in a command position: at the start of a line,
    // after a separator, an opening substitution or a run or shell key, after a shell keyword or launcher, or after env with its
    // options and variable assignments, each optionally followed by variable assignments.
    private static readonly Regex PythonCommand = new(
        @"(?:^|[;&|(`{]\s*|\brun:[ \t]+|\bshell:[ \t]+|\b(?:then|do|else|exec|command|time|nohup|sudo|xargs)\s+(?:-\S+\s+)*"
        + @"|\benv(?:\s+(?:-u\s*\S+|--unset=\S+|-C\s*\S+|-\S+|[A-Za-z_][A-Za-z0-9_]*=\S*))*\s+)"
        + @"(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*[""']?(?:\S*/)?(?:python[0-9.]*|py)(?:\.exe)?[""']?(?=$|[\s;&|)`])", RegexOptions.CultureInvariant);
    private static readonly Regex PythonShebang = new(@"\bpython|\bpy(?:\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex EnvEntry = new(@"^          (?<name>[A-Za-z_][A-Za-z0-9_]*):\s*(?<value>.*?)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex NumericUser = new("^[1-9][0-9]*(?::[1-9][0-9]*)?$", RegexOptions.CultureInvariant);
    // The hardening that every docker run of the composition fixture image or the sample image in the container rows' script
    // must carry, as the container-build job runs them (docs/ci.md, "Container acceptance").
    private static readonly string[] Hardening = ["--pull=never", "--read-only", "--tmpfs=/tmp", "--cap-drop=ALL", "--security-opt=no-new-privileges"];

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

    // The browser that AC-003-N02 runs: the pinned Chrome for Testing archives, the register entry that records the same digests
    // and the Playwright release that names them, and the real-provider job's step that downloads the linux64 archive and checks
    // its SHA-256 before the test step receives it. The test checks the archive again before it extracts anything.
    private static void ValidateBrowser(IReadOnlyDictionary<string, string> files, List<RegisterEntry> register, List<string> errors)
    {
        var registered = register.Where(entry => entry.Id == DependencyRegister.BrowserId && entry.Status == "in-use").ToList();
        var driver = register.Where(entry => entry.Id == PlaywrightId && entry.Status == "in-use").ToList();
        if (registered.Count != 1)
        {
            errors.Add($"{DependencyRegister.RegisterPath}: needs exactly one in-use {DependencyRegister.BrowserId} entry for {BrowserPinPath}.");
        }

        if (driver.Count != 1)
        {
            errors.Add($"{DependencyRegister.RegisterPath}: needs exactly one in-use {PlaywrightId} entry, the release that names the pinned browser.");
        }

        var central = Regex.Match(files.GetValueOrDefault("Directory.Packages.props", ""),
            """<PackageVersion\s+Include="Microsoft\.Playwright"\s+Version="(?<version>[^"]*)"\s*/>""", RegexOptions.CultureInvariant);
        if (!files.TryGetValue(BrowserPinPath, out var text))
        {
            errors.Add($"{BrowserPinPath}: the browser archive pin file is missing.");
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{BrowserPinPath}: the pin file must be a JSON object.");
                    return;
                }

                Fields(root, BrowserPinFields, BrowserPinPath, errors);
                if (Text(root, "browser") != "chrome-headless-shell")
                {
                    errors.Add($"{BrowserPinPath}: browser must be chrome-headless-shell, the build Playwright runs headless.");
                }

                var version = Text(root, "version");
                var jobVersion = Jobs(files.GetValueOrDefault(CiPath, "")).TryGetValue(RealProviderJob, out var realProvider)
                    ? Values(realProvider, "CHROME_HEADLESS_SHELL_VERSION") : [];
                if (version is null || jobVersion is not [var pinned] || pinned != version)
                {
                    errors.Add($"{BrowserPinPath}: version {version ?? "(missing)"} must equal the {RealProviderJob} job's CHROME_HEADLESS_SHELL_VERSION.");
                }

                if (registered.Count == 1 && version != registered[0].Version)
                {
                    errors.Add($"{BrowserPinPath}: version {version ?? "(missing)"} must equal the register's in-use {DependencyRegister.BrowserId} version {registered[0].Version}.");
                }

                var playwright = Text(root, "playwright");
                if (driver.Count == 1 && playwright != driver[0].Version || !central.Success || central.Groups["version"].Value != playwright)
                {
                    errors.Add($"{BrowserPinPath}: playwright {playwright ?? "(missing)"} must equal the register's in-use {PlaywrightId} version and "
                        + "Directory.Packages.props's Microsoft.Playwright version.");
                }

                var source = Text(root, "source");
                if (source != BrowserSource)
                {
                    errors.Add($"{BrowserPinPath}: source must be Chrome for Testing's index {BrowserSource}.");
                }
                else if (registered.Count == 1 && !registered[0].Sources.Contains(source))
                {
                    errors.Add($"{BrowserPinPath}: the register's {DependencyRegister.BrowserId} entry must cite the source {source}.");
                }

                if (!root.TryGetProperty("archives", out var archives) || archives.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{BrowserPinPath}: archives must be an object keyed by Chrome for Testing platform.");
                }
                else
                {
                    var targets = archives.EnumerateObject().Select(archive => archive.Name).ToList();
                    if (targets.Count != DependencyRegister.BrowserTargets.Length
                        || !targets.ToHashSet(StringComparer.Ordinal).SetEquals(DependencyRegister.BrowserTargets))
                    {
                        errors.Add($"{BrowserPinPath}: archives must name exactly {string.Join(", ", DependencyRegister.BrowserTargets)}.");
                    }

                    foreach (var archive in archives.EnumerateObject())
                    {
                        var location = $"{BrowserPinPath}: {archive.Name}";
                        if (archive.Value.ValueKind != JsonValueKind.Object)
                        {
                            errors.Add($"{location} must be an object with url and sha256.");
                            continue;
                        }

                        Fields(archive.Value, NodeArchiveFields, location, errors);
                        var url = Text(archive.Value, "url");
                        var expected = DependencyRegister.BrowserArchiveUrl(version ?? "<version>", archive.Name);
                        if (url != expected)
                        {
                            errors.Add($"{location} url must be the Chrome for Testing archive {expected}.");
                        }

                        var sha256 = Text(archive.Value, "sha256");
                        if (sha256 is null || !Sha256.IsMatch(sha256))
                        {
                            errors.Add($"{location} sha256 must be 64 lowercase hexadecimal characters.");
                            continue;
                        }

                        // The register records the same digest, so a change to either the pin file or the register alone fails.
                        if (registered.Count == 1 && !registered[0].Digests.Contains((archive.Name, url ?? "", "sha256:" + sha256)))
                        {
                            errors.Add($"{location} must equal the register's {DependencyRegister.BrowserId} {archive.Name} url and digest.");
                        }
                    }
                }
            }
            catch (JsonException)
            {
                errors.Add($"{BrowserPinPath}: the pin file must contain valid JSON.");
            }
        }

        // The real-provider job downloads the archive, checks its SHA-256 and only then names it to the test step; the check comes
        // before the export, and the step before the tests.
        var jobs = Jobs(files.GetValueOrDefault(CiPath, ""));
        if (!jobs.TryGetValue(RealProviderJob, out var job))
        {
            return;
        }

        var steps = StepNames(job);
        var run = Run(job, BrowserStep) ?? "";
        var check = run.IndexOf("[ \"${actual}\" = \"${sha256}\" ]", StringComparison.Ordinal);
        var export = run.IndexOf($"echo \"{BrowserArchiveVariable}=${{archive}}\" >> \"${{GITHUB_ENV}}\"", StringComparison.Ordinal);
        if (steps.IndexOf(BrowserStep) < 0 || steps.IndexOf(BrowserStep) > steps.IndexOf(RealProviderTestStep)
            || !run.Contains(BrowserPinPath, StringComparison.Ordinal) || !run.Contains("sha256sum \"${archive}\"", StringComparison.Ordinal)
            || check < 0 || export < 0 || check > export || run.Split('\n').Count(line => line.Contains(BrowserArchiveVariable, StringComparison.Ordinal)) != 1)
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job's '{BrowserStep}' step must come before '{RealProviderTestStep}', read {BrowserPinPath}, "
                + $"check the archive's SHA-256 and only then name it once in {BrowserArchiveVariable}.");
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

        if (!files.ContainsKey(SampleDockerfile))
        {
            errors.Add($"{SampleDockerfile}: the sample application's Dockerfile, which the container-build job builds, is missing.");
        }

        if (!files.TryGetValue(CompositionDockerfile, out var composition))
        {
            errors.Add($"{CompositionDockerfile}: the composition fixture image's Dockerfile, which the container-build job builds, is missing.");
        }
        else
        {
            errors.AddRange(CompositionDockerfileErrors(composition));
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

        // dotnet test runs the tests that need PostgreSQL in the real-provider job and nowhere else. The container-build job runs
        // only the AC-001 container rows, in the composition fixture image and through its own filters, which ValidateContainerRows checks.
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

            // Every image the job builds, the fixture and the sample application, is for the qualified platform.
            var builds = container.Split('\n').Where(line => line.Contains("docker build", StringComparison.Ordinal)).ToList();
            if (builds.Count == 0 || builds.Any(line => !line.Contains($"docker build --platform={LinuxPlatform} ", StringComparison.Ordinal)))
            {
                errors.Add($"{CiPath}: the {ContainerJob} job must build for {LinuxPlatform} with docker build --platform={LinuxPlatform}.");
            }

            // The sample application's image: its Dockerfile, and its host published with the SDK that build-test installs.
            if (Values(container, "SAMPLE_DOCKERFILE") is not [SampleDockerfile])
            {
                errors.Add($"{CiPath}: the {ContainerJob} job must build {SampleDockerfile}.");
            }

            if (Values(container, "COMPOSITION_DOCKERFILE") is not [CompositionDockerfile])
            {
                errors.Add($"{CiPath}: the {ContainerJob} job must build {CompositionDockerfile}.");
            }

            ValidateContainerRows(container, register, files, errors);

            if (jobs.TryGetValue(BuildTestJob, out var sdkJob))
            {
                foreach (var step in SharedSdkSteps)
                {
                    var original = Run(sdkJob, step);
                    if (original is null || original != Run(container, step))
                    {
                        errors.Add($"{CiPath}: the {ContainerJob} job must run the step '{step}' exactly as {BuildTestJob} does.");
                    }
                }
            }
        }

        errors.AddRange(DockerCommandErrors(files));
        errors.AddRange(NpmCommandErrors(files));
        errors.AddRange(PythonCommandErrors(files));
    }

    // Every tool a CI step runs is pinned, registered and drift-guarded, and no Python runtime is: the evidence assembler runs on
    // the pinned SDK (docs/ci.md, "Evidence records"). So no workflow step or CI script runs python, python3, a versioned or
    // absolute python, or the py launcher, whether directly, after variable assignments, through env or another launcher, or
    // as a step's shell; and no CI script is a Python file or names Python in its shebang. Like the docker and npm rules, this
    // reads tokens: it is a drift guard, and review is the control for a command assembled another way.
    public static IEnumerable<string> PythonCommandErrors(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, text) in files.Where(pair => InDockerScope(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (path.EndsWith(".py", StringComparison.Ordinal))
            {
                yield return $"{path}: {PythonRule}";
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (index == 0 && line.StartsWith("#!", StringComparison.Ordinal) && PythonShebang.IsMatch(line))
                {
                    yield return $"{path}:1: {PythonRule}";
                }
                else if (!line.StartsWith('#') && PythonCommand.IsMatch(line))
                {
                    yield return $"{path}:{index + 1}: {PythonRule}";
                }
            }
        }
    }

    // npm runs a package's install scripts unless told not to, and npm install may rewrite the lock, so the workflows and CI
    // scripts install packages only with npm ci --ignore-scripts and otherwise only run the manifest's own scripts. Like the
    // docker rules, this reads tokens: it is a drift guard, and review is the control for a command assembled another way.
    public static IEnumerable<string> NpmCommandErrors(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, text) in files.Where(pair => InDockerScope(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.StartsWith('#'))
                {
                    continue;
                }

                foreach (Match command in NpmCommand.Matches(line))
                {
                    var words = command.Groups["arguments"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var subcommand = words.FirstOrDefault(word => !word.StartsWith('-'));
                    if (subcommand is null or "run" or "run-script" || subcommand == "ci" && words.Contains("--ignore-scripts"))
                    {
                        continue;
                    }

                    yield return $"{path}:{index + 1}: npm may only install packages with npm ci --ignore-scripts, or run the manifest's own "
                        + "scripts with npm run.";
                }
            }
        }
    }

    // The lines of the named step after its name line, up to the next step.
    private static IEnumerable<string> StepLines(string job, string stepName)
    {
        var lines = job.Split('\n');
        var start = Array.IndexOf(lines, $"      - name: {stepName}");
        return start < 0 ? [] : lines.Skip(start + 1).TakeWhile(line => !line.StartsWith("      - ", StringComparison.Ordinal));
    }

    // The value of a step's own "key: value" property, unquoted, or null.
    public static string? StepValue(string job, string stepName, string key)
    {
        var prefix = $"        {key}:";
        var line = StepLines(job, stepName).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        return line is null ? null : Unquote(line[prefix.Length..].Trim());
    }

    // The entries of a step's env mapping, unquoted.
    public static Dictionary<string, string> StepEnv(string job, string stepName)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var inEnv = false;
        foreach (var line in StepLines(job, stepName))
        {
            if (line == "        env:")
            {
                inEnv = true;
                continue;
            }

            var entry = EnvEntry.Match(line);
            if (inEnv && entry.Success)
            {
                entries[entry.Groups["name"].Value] = Unquote(entry.Groups["value"].Value);
            }
            else if (!line.StartsWith("          ", StringComparison.Ordinal))
            {
                inEnv = false;
            }
        }

        return entries;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : Regex.Replace(value, @"\s+#.*$", "", RegexOptions.CultureInvariant);

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

        // The TypeScript client installs exactly its committed lock, with no install script, in its own directory and before the
        // credentials exist, and the client tests also run its scenarios under Vitest.
        var testIndex = steps.IndexOf(RealProviderTestStep);
        var installed = steps.Take(Math.Max(testIndex, 0)).Any(step => StepValue(job, step, "working-directory") == ClientDirectory
            && (Run(job, step) ?? "").Split('\n').Select(line => line.Trim()).Any(line => NpmClean.IsMatch(line)
                && line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("--ignore-scripts")));
        if (!installed)
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job must install the TypeScript client with npm ci --ignore-scripts in {ClientDirectory} "
                + $"before its '{RealProviderTestStep}' step.");
        }

        if (!StepEnv(job, RealProviderTestStep).TryGetValue(VitestSwitch, out var vitest) || vitest != "1")
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job's '{RealProviderTestStep}' step must set {VitestSwitch} to '1', so the client tests "
                + "also run the TypeScript scenarios under Vitest.");
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

        // The host rows write their evidence beneath the runner's temporary directory, bound to the exact head, and a later step
        // checks those records with the repository's own contracts.
        var prepare = Run(job, HostEvidenceStep) ?? "";
        var prepared = steps.IndexOf(HostEvidenceStep);
        if (prepared < 0 || prepared > testIndex
            || !prepare.Contains("echo \"MODULARYN_EVIDENCE_DIR=${RUNNER_TEMP}/", StringComparison.Ordinal)
            || !prepare.Contains("head=\"$(git rev-parse HEAD)\"", StringComparison.Ordinal)
            || !prepare.Contains("echo \"MODULARYN_SOURCE_COMMIT=${head}\" >> \"${GITHUB_ENV}\"", StringComparison.Ordinal))
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job's '{HostEvidenceStep}' step must, before its '{RealProviderTestStep}' step, set "
                + "MODULARYN_EVIDENCE_DIR beneath RUNNER_TEMP and MODULARYN_SOURCE_COMMIT to the exact head.");
        }

        if (steps.IndexOf(HostEvidenceCheckStep) <= testIndex || Run(job, HostEvidenceCheckStep) != EvidenceCheckRun
            || StepEnv(job, HostEvidenceCheckStep).GetValueOrDefault("MODULARYN_EVIDENCE_MODE") != "single-host")
        {
            errors.Add($"{CiPath}: the {RealProviderJob} job must check the host evidence records with {EvidenceCheckRun} in a "
                + $"'{HostEvidenceCheckStep}' step after its '{RealProviderTestStep}' step, with MODULARYN_EVIDENCE_MODE single-host.");
        }

        errors.AddRange(EvidenceScriptErrors(files.GetValueOrDefault(EvidenceScriptPath, "")));

        // These scripts, and the evidence scripts the tests scripts call, hold the credentials in their environment, so none may
        // write to a runner file that a later step reads.
        var tests = files.GetValueOrDefault(TestsScriptPath, "");
        foreach (var (path, text) in new[] { (DatabaseScriptPath, script), (TestsScriptPath, tests),
                     (CompositionScriptPath, files.GetValueOrDefault(CompositionScriptPath, "")) }
                     .Concat(new[] { EvidencePipelineContract.HostResultsScriptPath, EvidencePipelineContract.ContainerResultsScriptPath,
                         EvidencePipelineContract.BundleScriptPath }.Select(path => (path, files.GetValueOrDefault(path, "")))))
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

    // The composition fixture image holds test modules and stays test-only: it runs as a numeric non-root user, root owns the
    // files it copies, it runs no build step of its own, and it names the deployment its tests run in.
    public static List<string> CompositionDockerfileErrors(string text)
    {
        var errors = new List<string>();
        var instructions = LogicalLines(text).Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var users = instructions.Where(line => Regex.IsMatch(line, @"^user\s", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            .Select(line => line[4..].Trim()).ToList();
        if (users.Count == 0 || !NumericUser.IsMatch(users[^1]))
        {
            errors.Add($"{CompositionDockerfile}: the composition fixture image must run as a numeric non-root user and group, such as USER 1654:1654.");
        }

        if (instructions.Any(line => Regex.IsMatch(line, @"^(?:onbuild\s+)?(?:copy|add)\s.*--(?:chown|chmod)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)))
        {
            errors.Add($"{CompositionDockerfile}: root must own the files the composition fixture image copies, so COPY and ADD take no --chown or --chmod.");
        }

        if (instructions.Any(line => Regex.IsMatch(line, @"^(?:onbuild\s+)?run\s", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)))
        {
            errors.Add($"{CompositionDockerfile}: the composition fixture image runs no build step; it holds only the published test project.");
        }

        if (!instructions.Contains("ENV MODULARYN_DEPLOYMENT_MODE=single-host-container", StringComparer.Ordinal))
        {
            errors.Add($"{CompositionDockerfile}: the composition fixture image must set ENV MODULARYN_DEPLOYMENT_MODE=single-host-container.");
        }

        return errors;
    }

    // The AC-001 container rows: their database pins, the step that creates the credentials and runs the rows, the script that
    // runs them, and the check of their evidence records.
    private static void ValidateContainerRows(string job, List<RegisterEntry> register, IReadOnlyDictionary<string, string> files, List<string> errors)
    {
        var postgres = ImageReference(register, PostgresImageId, errors);
        if (postgres is not null && (Values(job, "POSTGRES_IMAGE") is not [var image] || image != postgres))
        {
            errors.Add($"{CiPath}: the {ContainerJob} job's POSTGRES_IMAGE must be the register's {PostgresImageId} reference {postgres}.");
        }

        var server = register.Where(entry => entry.Id == "binary:postgresql" && entry.Status == "in-use").ToList();
        if (Values(job, "POSTGRESQL_VERSION") is not [var version] || server is not [var entry] || entry.Version != version)
        {
            errors.Add($"{CiPath}: the {ContainerJob} job's POSTGRESQL_VERSION must equal the version of the register's in-use binary:postgresql entry.");
        }

        var lines = job.Split('\n').Select(line => line.Trim()).ToList();
        if (!lines.Any(line => line.StartsWith($"docker network create --internal {AcceptanceNetwork} ", StringComparison.Ordinal))
            || !lines.Any(line => line.Contains($"--network={AcceptanceNetwork} ", StringComparison.Ordinal) && line.Contains("\"${POSTGRES_IMAGE}\"", StringComparison.Ordinal)))
        {
            errors.Add($"{CiPath}: the {ContainerJob} job must create {AcceptanceNetwork} with docker network create --internal and start PostgreSQL on it.");
        }

        var steps = StepNames(job);
        var run = steps.IndexOf(CompositionTestStep);
        var start = steps.IndexOf(DatabaseStartStep);
        var check = steps.IndexOf(CompositionCheckStep);
        if (Run(job, CompositionTestStep) != CompositionTestRun || start < 0 || start > run || check < 0 || check > run)
        {
            errors.Add($"{CiPath}: the {ContainerJob} job's '{CompositionTestStep}' step must come after its '{DatabaseStartStep}' and "
                + $"'{CompositionCheckStep}' steps and run {CompositionTestRun}.");
        }

        if (job.Split('\n').Count(line => line.Contains(DatabaseScriptPath, StringComparison.Ordinal)) != 1)
        {
            errors.Add($"{CiPath}: only the {ContainerJob} job's '{CompositionTestStep}' step may run {DatabaseScriptPath}, so the credentials exist in no other step.");
        }

        if (steps.IndexOf(ContainerEvidenceCheckStep) <= run || !(Run(job, ContainerEvidenceCheckStep) ?? "").Split('\n')
                .Any(line => line.Trim().EndsWith(" " + EvidenceCheckRun, StringComparison.Ordinal))
            || StepEnv(job, ContainerEvidenceCheckStep).GetValueOrDefault("MODULARYN_EVIDENCE_MODE") != "single-host-container")
        {
            errors.Add($"{CiPath}: the {ContainerJob} job must check the container evidence records with {EvidenceCheckRun} in a "
                + $"'{ContainerEvidenceCheckStep}' step after its '{CompositionTestStep}' step, with MODULARYN_EVIDENCE_MODE single-host-container.");
        }

        var script = files.GetValueOrDefault(CompositionScriptPath, "");
        var scriptLines = LogicalLines(script).Select(line => line.Trim()).ToList();
        // Exactly the planned CODE-10 methods and the published sample image's readiness and sign-in page tests, through the test
        // executable's own filters.
        var planned = PlannedMethods(files, "CODE-10").Append(ReadinessTest).Append(SignInPageTest).ToList();
        var fixtureRun = scriptLines.Where(line => line.StartsWith("docker run ", StringComparison.Ordinal)
            && line.Contains("\"${COMPOSITION_IMAGE}\"", StringComparison.Ordinal) && line.Contains("-result-ctrf ", StringComparison.Ordinal)).ToList();
        if (planned.Count < 2 || fixtureRun is not [var rows]
            || Regex.Matches(rows, "-method \"(?<name>[^\"]+)\"").Select(match => match.Groups["name"].Value).Order(StringComparer.Ordinal)
                .SequenceEqual(planned.Order(StringComparer.Ordinal), StringComparer.Ordinal) is false
            || !rows.Contains("-preEnumerateTheories -trait \"Deployment=single-host-container\"", StringComparison.Ordinal))
        {
            errors.Add($"{CompositionScriptPath}: must run the composition fixture image once with a report, selecting through its own filters "
                + "exactly the container rows of every CODE-10 method in the evidence plan and the published sample image's readiness and sign-in page tests.");
        }

        foreach (var line in scriptLines.Where(line => line.Contains("docker run ", StringComparison.Ordinal)
                     && (line.Contains("\"${COMPOSITION_IMAGE}\"", StringComparison.Ordinal) || line.Contains("\"${SAMPLE_IMAGE}\"", StringComparison.Ordinal))))
        {
            var words = line.Split(' ');
            if (!Hardening.All(words.Contains)
                || !(words.Contains("--network=\"${network}\"") || words.Contains("--network=\"container:${POSTGRES_CONTAINER}\"")))
            {
                errors.Add($"{CompositionScriptPath}: every docker run of the sample or composition fixture image must use {string.Join(", ", Hardening)} "
                    + $"and run on the internal network or in the database container's network namespace: {line}");
            }
        }

        if (!scriptLines.Contains($"network={AcceptanceNetwork}", StringComparer.Ordinal)
            || !script.Contains("[ \"$(jq -r '.results.summary.tests' \"${report}\")\" = \"6\" ] && [ \"${passed}\" = \"${expected}\" ]", StringComparison.Ordinal)
            || !new[] { ReadinessTest, SignInPageTest }.All(test => script.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Contains($"\n  '{test}' \\\n", StringComparison.Ordinal))
            || !script.Contains("select(.status == \"passed\")", StringComparison.Ordinal))
        {
            errors.Add($"{CompositionScriptPath}: must use the {AcceptanceNetwork} network and fail unless its report shows the six expected tests, all passed.");
        }
    }

    // The evidence check runs the one repository test through the test executable's filter, fails a skip, and requires its
    // report to show that the test ran and passed.
    private static IEnumerable<string> EvidenceScriptErrors(string script)
    {
        if (!script.Contains($"-failSkips -method \"{EvidenceCheckTest}\" -result-ctrf \"${{report}}\"", StringComparison.Ordinal)
            || !script.Contains("[ \"$(jq -r '.results.summary.tests' \"${report}\")\" = \"1\" ] && [ \"$(jq -r '.results.summary.passed' \"${report}\")\" = \"1\" ]", StringComparison.Ordinal))
        {
            yield return $"{EvidenceScriptPath}: must run {EvidenceCheckTest} with -failSkips and fail unless its report shows that it ran once and passed.";
        }

        if (!script.Contains("xargs -0 sha256sum", StringComparison.Ordinal))
        {
            yield return $"{EvidenceScriptPath}: must print each evidence record's SHA-256.";
        }
    }

    // The fully qualified names of the plan's planned tests that a work order writes.
    private static IEnumerable<string> PlannedMethods(IReadOnlyDictionary<string, string> files, string workOrder)
    {
        try
        {
            using var plan = JsonDocument.Parse(files.GetValueOrDefault(G02EvidencePlanContract.PlanPath, ""));
            return plan.RootElement.GetProperty("cases").EnumerateArray().SelectMany(row => row.GetProperty("plannedTests").EnumerateArray())
                .Where(test => Text(test, "workOrder") == workOrder).Select(test => Text(test, "name") ?? "").ToList();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [];
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
