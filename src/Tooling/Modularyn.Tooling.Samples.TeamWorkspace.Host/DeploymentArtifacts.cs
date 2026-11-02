using System.Security.Cryptography;
using Modularyn.Framework.Modularity;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

/// <summary>Describes the built artifact for a trusted deployment step. Normal startup only verifies the supplied lock.</summary>
public static class DeploymentArtifacts
{
    public static DeploymentLock Describe()
    {
        var root = AppContext.BaseDirectory;
        var paths = Directory.EnumerateFiles(root, "*.dll").Concat(Directory.EnumerateFiles(root, "*.deps.json"))
            .Concat(Directory.EnumerateFiles(root, "*.runtimeconfig.json")).Append(Path.Combine(root, "wwwroot", "sign-in.js"))
            .Order(StringComparer.Ordinal).ToArray();
        if (paths.Length < 4) throw new InvalidOperationException("Describe the complete published host artifact.");
        var files = paths.Select(path =>
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null) throw new InvalidOperationException("Deployment files must be regular files.");
            using var stream = file.OpenRead();
            return new LockedFile(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Convert.ToHexStringLower(SHA256.HashData(stream)));
        }).ToArray();
        return new DeploymentLock(WorkspaceComposition.Declarations().Select(manifest => LockedModule.Describe(manifest, files)));
    }
}
