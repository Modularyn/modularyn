using System.Text.Json.Nodes;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class EditionMapTests
{
    [Fact]
    public void Catalog_register_homes_readme_and_every_manifest_example_agree()
    {
        Assert.Empty(EditionMapContract.Validate(RepositoryFiles.Current));
    }

    [Fact]
    public void Edition_documents_accept_platform_line_endings()
    {
        var files = RepositoryFiles.Current.ToDictionary();
        foreach (var path in files.Keys.Where(path => path.StartsWith("docs/", StringComparison.Ordinal)).ToArray())
            files[path] = files[path].Replace("\r\n", "\n").Replace("\n", "\r\n");
        Assert.Empty(EditionMapContract.Validate(files));
    }

    [Theory]
    [InlineData("community")]
    [InlineData("standard")]
    [InlineData("pro")]
    public void Every_register_capability_translates_to_its_distribution_edition(string distribution)
    {
        var files = RepositoryFiles.Current.ToDictionary();
        var path = ReleaseManifestContract.ExampleDirectory + distribution + ".json";
        var manifest = JsonNode.Parse(files[path])!;
        var source = manifest["capabilities"]!["CMS-011"]!;
        var capabilities = new JsonObject();
        var register = JsonNode.Parse(files[EditionMapContract.RegisterPath])!;
        foreach (var entry in register["entries"]!.AsArray())
        {
            var paid = entry!["edition"]!.GetValue<string>() == "paid";
            if (paid && distribution == "community") continue;
            var capability = source.DeepClone();
            capability["edition"] = paid ? distribution : "community";
            capabilities.Add(entry["id"]!.GetValue<string>(), capability);
        }
        manifest["capabilities"] = capabilities;
        files[path] = manifest.ToJsonString();
        Assert.Empty(EditionMapContract.Validate(files));
    }
}
