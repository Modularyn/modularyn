using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class RepositoryConventionTests
{
    [Fact]
    public void Repository_satisfies_its_conventions()
    {
        var errors = Conventions.Validate(RepositoryFiles.Current);
        Assert.Empty(errors);
    }

    [Fact]
    public void License_check_accepts_different_line_endings()
    {
        var files = RepositoryFiles.Current.ToDictionary();
        files["LICENSE"] = files["LICENSE"].Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.Empty(Conventions.Validate(files));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("'")]
    public void Quoted_static_action_references_are_accepted(string quote)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var action = files[".github/workflows/actions.allow"].Split('\n')[0].Split(' ')[0];
        files[".github/workflows/ci.yml"] = files[".github/workflows/ci.yml"]
            .Replace("uses: " + action, quote + "uses" + quote + ": " + quote + action + quote);
        Assert.Empty(Conventions.Validate(files));
    }

    [Theory]
    [InlineData("docs/ci.md")]
    [InlineData("https://github.com/Modularyn/modularyn")]
    [InlineData("${RUNNER_TEMP}/modularyn-gitleaks")]
    [InlineData("../tests/Modularyn.Repository.Tests")]
    public void Portable_references_are_accepted(string text)
    {
        Assert.False(Conventions.HasMachinePath(text));
    }

    [Theory]
    [InlineData("Users")]
    [InlineData("home")]
    [InlineData("Volumes")]
    [InlineData("mnt")]
    [InlineData("media")]
    [InlineData("run/media")]
    [InlineData("root")]
    public void Unix_machine_roots_are_rejected(string root)
    {
        var text = string.Join('/', "", root, "person", "project");
        Assert.True(Conventions.HasMachinePath(text));
    }

    [Theory]
    [InlineData('C')]
    [InlineData('d')]
    [InlineData('Z')]
    public void Windows_drive_paths_are_rejected(char drive)
    {
        var text = drive + ":" + '\\' + "project";
        Assert.True(Conventions.HasMachinePath(text));
    }
}
