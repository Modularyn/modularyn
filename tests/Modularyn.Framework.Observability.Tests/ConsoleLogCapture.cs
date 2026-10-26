using Xunit;

namespace Modularyn.Framework.Observability.Tests;

[CollectionDefinition("Diagnostic console", DisableParallelization = true)]
public sealed class DiagnosticConsoleCollection { }

/// <summary>Reads the actual console logger's output from a temporary file.</summary>
internal sealed class ConsoleLogCapture : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "modularyn-diagnostics-" + Guid.NewGuid().ToString("N"));
    private readonly TextWriter original = Console.Out;
    private readonly TextWriter originalError = Console.Error;
    private readonly StreamWriter writer;

    internal ConsoleLogCapture()
    {
        writer = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var output = TextWriter.Synchronized(writer);
        Console.SetOut(output);
        Console.SetError(output);
    }

    // Dispose the logger factory first so its queue is flushed before reading.
    internal string Read()
    {
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        Console.SetOut(original);
        Console.SetError(originalError);
        writer.Dispose();
        File.Delete(file);
    }
}
