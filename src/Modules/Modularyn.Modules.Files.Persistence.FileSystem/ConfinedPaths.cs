using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.FileSystem;

/// <summary>Only server identities enter this path builder. Every existing component must be free of links.</summary>
internal sealed class ConfinedPaths
{
    private readonly string root;

    internal ConfinedPaths(string configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathFullyQualified(configuredRoot))
            throw new FileStorageException(FileStorageError.UnsafeLocation);
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
            CheckDirectory(root);
        }
        catch (Exception exception) when (IsIo(exception)) { throw new FileStorageException(FileStorageError.UnsafeLocation); }
    }

    internal string Tenant(Guid tenant)
    {
        CheckDirectory(root);
        var directory = UnderRoot(tenant.ToString("N"));
        CheckEntry(directory, directory: true, required: false);
        Directory.CreateDirectory(directory);
        CheckDirectory(directory);
        return directory;
    }

    internal string File(Guid tenant, Guid id, string extension)
    {
        if (id == Guid.Empty) throw new FileStorageException(FileStorageError.UnsafeLocation);
        return Entry(tenant, id.ToString("N") + extension);
    }

    internal string Lease(Guid tenant) => Entry(tenant, ".lease");

    private string Entry(Guid tenant, string name)
    {
        var directory = UnderRoot(tenant.ToString("N"));
        CheckDirectory(directory);
        var path = UnderRoot(tenant.ToString("N"), name);
        CheckEntry(path, directory: false, required: false);
        return path;
    }

    private string UnderRoot(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new FileStorageException(FileStorageError.UnsafeLocation);
        return path;
    }

    private static void CheckDirectory(string path)
    {
        // Check ancestors too: accepting a linked configured root would move every otherwise confined child.
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
            CheckEntry(directory.FullName, directory: true, required: true);
    }

    private static void CheckEntry(string path, bool directory, bool required)
    {
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        info.Refresh();
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new FileStorageException(FileStorageError.UnsafeLocation);
        if (!info.Exists)
        {
            if (required) throw new FileStorageException(FileStorageError.UnsafeLocation);
            return;
        }
        if (((info.Attributes & FileAttributes.Directory) != 0) != directory || (info.Attributes & FileAttributes.Device) != 0)
            throw new FileStorageException(FileStorageError.UnsafeLocation);
    }

    internal static bool IsIo(Exception exception) => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
