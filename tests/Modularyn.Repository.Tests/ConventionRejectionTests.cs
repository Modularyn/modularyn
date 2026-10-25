using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ConventionRejectionTests
{
    private const string TestProject = "tests/Modularyn.Repository.Tests/Modularyn.Repository.Tests.csproj";

    [Theory]
    [InlineData("sdk-version", "global.json")]
    [InlineData("sdk-roll-forward", "global.json")]
    [InlineData("sdk-prerelease", "global.json")]
    [InlineData("missing-file", "SECURITY.md")]
    [InlineData("central-nullable", "Directory.Build.props")]
    [InlineData("central-condition", "Directory.Build.props")]
    [InlineData("central-import", "declared central properties")]
    [InlineData("package-build-override", "without imports or build overrides")]
    [InlineData("ci-determinism", "ContinuousIntegrationBuild")]
    [InlineData("project-override", "inherit central settings")]
    [InlineData("project-version-attribute", "package versions belong")]
    [InlineData("project-version-element", "package versions belong")]
    [InlineData("project-version-override", "package versions belong")]
    [InlineData("disable-central-import", "inherit central settings")]
    [InlineData("nested-build", "nested central configuration")]
    [InlineData("nested-packages", "nested central configuration")]
    [InlineData("custom-import", "explicit imports")]
    [InlineData("missing-sdk", "Microsoft.NET.Sdk")]
    [InlineData("floating-package", "exact stable version")]
    [InlineData("unlisted-project", "Modularyn.slnx")]
    [InlineData("missing-lock", "invalid dependency lock")]
    [InlineData("changed-lock", "direct versions must match")]
    [InlineData("license-heading-only", "complete Apache License")]
    [InlineData("license-changed-term", "complete Apache License")]
    [InlineData("changed-notice", "NOTICE")]
    [InlineData("machine-path", "absolute machine path")]
    [InlineData("action-tag", "full SHA")]
    [InlineData("short-action-sha", "full SHA")]
    [InlineData("unknown-action", "absent from actions.allow")]
    [InlineData("quoted-action-tag", "full SHA")]
    [InlineData("flow-action-tag", "full SHA")]
    [InlineData("folded-action", "full SHA")]
    [InlineData("dynamic-action", "full SHA")]
    [InlineData("escaped-action-key", "plain YAML keys")]
    [InlineData("complex-action-key", "plain YAML keys")]
    [InlineData("new-workflow", "full SHA")]
    [InlineData("extra-allowed-action", "exactly the actions")]
    [InlineData("missing-allowed-action", "absent from actions.allow")]
    [InlineData("missing-release-tag", "release tag comments")]
    public void A_single_convention_violation_is_rejected(string mutation, string expectedDiagnostic)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var workflow = ".github/workflows/ci.yml";
        var allowList = ".github/workflows/actions.allow";
        var checkout = files[allowList].Split('\n')[0].Split(' ')[0];
        var lockPath = "tests/Modularyn.Repository.Tests/packages.lock.json";
        switch (mutation)
        {
            case "sdk-version":
                files["global.json"] = SetSdk(files["global.json"], "version", "10.0.*");
                break;
            case "sdk-roll-forward":
                files["global.json"] = SetSdk(files["global.json"], "rollForward", "latestPatch");
                break;
            case "sdk-prerelease":
                files["global.json"] = files["global.json"].Replace("false", "true");
                break;
            case "missing-file":
                files.Remove("SECURITY.md");
                break;
            case "central-nullable":
                files["Directory.Build.props"] = files["Directory.Build.props"].Replace("<Nullable>enable", "<Nullable>disable");
                break;
            case "central-condition":
                files["Directory.Build.props"] = files["Directory.Build.props"].Replace("<PropertyGroup>", "<PropertyGroup Condition=\"false\">");
                break;
            case "central-import":
                files["Directory.Build.props"] = files["Directory.Build.props"].Replace("</Project>", "<Import Project=\"extra.props\" /></Project>");
                break;
            case "package-build-override":
                files["Directory.Packages.props"] = files["Directory.Packages.props"].Replace("</Project>", "<PropertyGroup><Nullable>disable</Nullable></PropertyGroup></Project>");
                break;
            case "ci-determinism":
                files["Directory.Build.props"] = files["Directory.Build.props"].Replace("'$(CI)' == 'true'", "'$(CI)' == 'false'");
                break;
            case "project-override":
                files[TestProject] = files[TestProject].Replace("<PropertyGroup>", "<PropertyGroup><Nullable>disable</Nullable>");
                break;
            case "project-version-attribute":
                files[TestProject] = files[TestProject].Replace("Include=\"xunit.v3\"", "Include=\"xunit.v3\" Version=\"4.0.2\"");
                break;
            case "project-version-element":
                files[TestProject] = files[TestProject].Replace("</ItemGroup>", "<PackageReference Include=\"xunit.v3\"><Version>4.0.2</Version></PackageReference></ItemGroup>");
                break;
            case "project-version-override":
                files[TestProject] = files[TestProject].Replace("Include=\"xunit.v3\"", "Include=\"xunit.v3\" VersionOverride=\"4.0.2\"");
                break;
            case "disable-central-import":
                files[TestProject] = files[TestProject].Replace("<PropertyGroup>", "<PropertyGroup><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>");
                break;
            case "nested-build":
                files["tests/Directory.Build.props"] = "<Project />";
                break;
            case "nested-packages":
                files["tests/Directory.Packages.props"] = "<Project />";
                break;
            case "custom-import":
                files[TestProject] = files[TestProject].Replace("</Project>", "<Import Project=\"extra.props\" /></Project>");
                break;
            case "missing-sdk":
                files[TestProject] = files[TestProject].Replace(" Sdk=\"Microsoft.NET.Sdk\"", "");
                break;
            case "floating-package":
                files["Directory.Packages.props"] = files["Directory.Packages.props"].Replace("4.0.2", "4.*");
                break;
            case "unlisted-project":
                files["tests/Additional.Tests/Additional.Tests.csproj"] = files[TestProject];
                break;
            case "missing-lock":
                files.Remove(lockPath);
                break;
            case "changed-lock":
                // Change the resolved version of whichever direct package the lock lists first.
                var lockDocument = JsonNode.Parse(files[lockPath])!;
                var direct = lockDocument["dependencies"]!["net10.0"]!.AsObject()
                    .First(package => (string?)package.Value!["type"] == "Direct");
                direct.Value!["resolved"] = "0.0.1";
                files[lockPath] = lockDocument.ToJsonString();
                break;
            case "license-heading-only":
                files["LICENSE"] = "Apache License\nVersion 2.0, January 2004\n";
                break;
            case "license-changed-term":
                files["LICENSE"] = files["LICENSE"].Replace("royalty-free", "royalty-bearing");
                break;
            case "changed-notice":
                files["NOTICE"] = "Modularyn\n";
                break;
            case "machine-path":
                files["docs/portability.md"] = string.Join('/', "", "home", "person", "project");
                break;
            case "action-tag":
                files[workflow] = files[workflow].Replace(checkout, "actions/checkout@v4");
                break;
            case "short-action-sha":
                files[workflow] = files[workflow].Replace(checkout, checkout[..^1]);
                break;
            case "unknown-action":
                files[workflow] = files[workflow].Replace(checkout, "actions/cache@" + new string('a', 40));
                break;
            case "quoted-action-tag":
                files[workflow] += "\n      - 'uses': 'actions/checkout@v4'\n";
                break;
            case "flow-action-tag":
                files[workflow] += "\n      - { uses: actions/checkout@v4 }\n";
                break;
            case "folded-action":
                files[workflow] += "\n      - uses: >-\n          actions/checkout@v4\n";
                break;
            case "dynamic-action":
                files[workflow] += "\n      - uses: ${{ matrix.action }}\n";
                break;
            case "escaped-action-key":
                files[workflow] += """

                          - "\u0075ses": actions/checkout@v4
                    """;
                break;
            case "complex-action-key":
                files[workflow] += """

                          - ? uses
                            : actions/checkout@v4
                    """;
                break;
            case "new-workflow":
                files[".github/workflows/additional.yaml"] = """
                    jobs:
                      check:
                        uses: actions/checkout@v4
                    """;
                break;
            case "extra-allowed-action":
                files[allowList] += "actions/cache@" + new string('a', 40) + " # v4.0.0\n";
                break;
            case "missing-allowed-action":
                files[allowList] = string.Join('\n', files[allowList].Split('\n').Skip(1));
                break;
            case "missing-release-tag":
                // Remove the release tag comment from the first allowed action, whatever its tag is.
                files[allowList] = new Regex(@" # v\d+\.\d+\.\d+").Replace(files[allowList], "", 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown mutation case.");
        }

        var errors = Conventions.Validate(files);
        Assert.Contains(errors, error => error.Contains(expectedDiagnostic, StringComparison.Ordinal));
    }

    // Changes one sdk property of global.json, whatever the current pin is, so the mutation never silently does nothing.
    private static string SetSdk(string globalJson, string property, string value)
    {
        var root = JsonNode.Parse(globalJson)!;
        Assert.NotEqual(value, root["sdk"]![property]!.GetValue<string>());
        root["sdk"]![property] = value;
        return root.ToJsonString();
    }
}
