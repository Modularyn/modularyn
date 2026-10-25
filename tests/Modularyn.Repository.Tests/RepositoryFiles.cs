using System.Diagnostics;
using System.Text;

namespace Modularyn.Repository.Tests;

internal static class RepositoryFiles
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Snapshot = new(Read);

    public static IReadOnlyDictionary<string, string> Current => Snapshot.Value;

    private static Dictionary<string, string> Read()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(RepositoryFiles).Assembly.Location)!);
        while (!File.Exists(Path.Combine(directory.FullName, "Modularyn.slnx")))
        {
            directory = directory.Parent
                ?? throw new InvalidOperationException("Cannot find Modularyn.slnx above the test assembly.");
        }

        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory.FullName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Cannot start Git to enumerate repository files.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Git file enumeration timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git file enumeration failed: {error.GetAwaiter().GetResult()}");
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in output.GetAwaiter().GetResult().Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct())
        {
            var fullPath = Path.Combine(directory.FullName, path);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Tracked file is missing: {path}");
            }

            var bytes = File.ReadAllBytes(fullPath);
            // A Unicode byte-order mark identifies text even when its encoding contains zero bytes.
            var hasUnicodeMark = bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())
                || bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())
                || bytes.AsSpan().StartsWith(Encoding.UTF32.GetPreamble())
                || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 254, 255 });
            if (!hasUnicodeMark && bytes.Contains((byte)0))
            {
                continue;
            }

            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            files.Add(path, reader.ReadToEnd());
        }

        return files;
    }
}
