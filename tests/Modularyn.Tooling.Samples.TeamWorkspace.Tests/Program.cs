using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

// A package-free executable, without xUnit traits or dotnet-test discovery.
// Builder integration into the repository's existing test runner remains required.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--unit"] and not ["--postgresql"]) return 2;
        try
        {
            UnitChecks();
            if (args is ["--postgresql"])
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await SmokeScenario.RunAsync(timeout.Token);
            }
            Console.WriteLine("Team workspace smoke checks passed.");
            return 0;
        }
        catch (SmokeFailure failure)
        {
            Console.Error.WriteLine(failure.Message);
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Team workspace smoke setup or execution failed.");
            return 1;
        }
    }

    public static void UnitChecks()
    {
        WorkspaceApi.Inventory.Validate();
        var document = ApiGenerator.OpenApi(WorkspaceApi.Inventory);
        Check.That(document.Contains("text/html", StringComparison.Ordinal) && document.Contains("x-modularyn-antiforgery", StringComparison.Ordinal), "inventory-pages");
        Check.That(WorkspaceApi.Endpoint("NewNoteForm").Antiforgery, "form-antiforgery");
        Check.That(WorkspaceApi.Endpoint("DownloadAttachment").Download!.MediaTypes.SequenceEqual(TeamWorkspaceHost.ReleasedMediaTypes), "download-media");
        var instant = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero).AddTicks(9);
        Check.That(UtcMicroseconds.Normalize(instant).Ticks % 10 == 0, "microsecond-normalization");
        var fold = new DailySchedule("America/Chicago", new TimeOnly(1, 30));
        Check.That(fold.Resolve(new DateOnly(2026, 11, 1)) == new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), "iana-fold");
        var gap = new DailySchedule("America/Chicago", new TimeOnly(2, 30));
        Check.That(gap.Resolve(new DateOnly(2026, 3, 8)) is null, "iana-gap");
        try { _ = new DailySchedule("../localtime", TimeOnly.MinValue); throw new SmokeFailure("iana-path-refusal"); }
        catch (ArgumentException) { }
    }
}

internal sealed class SmokeFailure(string check) : Exception("Smoke check failed: " + check);
internal static class Check
{
    internal static void That(bool condition, string check) { if (!condition) throw new SmokeFailure(check); }
}
