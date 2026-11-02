using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;
using Xunit;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

/// <summary>
/// The sample's checks that need no database: its inventory and media declarations, whole-microsecond time and the IANA
/// daylight-saving edges its digest schedule meets.
/// </summary>
public sealed class UnitTests
{
    [Fact]
    public void The_inventory_validates_and_its_OpenAPI_document_declares_the_pages_and_antiforgery()
    {
        WorkspaceApi.Inventory.Validate();
        var document = ApiGenerator.OpenApi(WorkspaceApi.Inventory);
        Check.That(document.Contains("text/html", StringComparison.Ordinal) && document.Contains("x-modularyn-antiforgery", StringComparison.Ordinal), "inventory-pages");
    }

    [Fact]
    public void The_note_form_declares_antiforgery() => Check.That(WorkspaceApi.Endpoint("NewNoteForm").Antiforgery, "form-antiforgery");

    [Fact]
    public void An_ordinary_member_may_grant_downloads_but_not_release_attachments() =>
        Check.That(!WorkspaceEndpoints.MemberPermissions.Contains(FilePermissions.Release) && WorkspaceEndpoints.MemberPermissions.Contains(FilePermissions.Grant),
            "member-role-least-privilege");

    [Fact]
    public void The_download_declares_exactly_the_released_media_types() =>
        Check.That(WorkspaceApi.Endpoint("DownloadAttachment").Download!.MediaTypes.SequenceEqual(TeamWorkspaceHost.ReleasedMediaTypes), "download-media");

    [Fact]
    public void Stored_times_normalize_to_whole_microseconds()
    {
        var instant = new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero).AddTicks(9);
        Check.That(UtcMicroseconds.Normalize(instant).Ticks % 10 == 0, "microsecond-normalization");
    }

    [Fact]
    public void A_daily_schedule_in_the_fall_back_fold_resolves_to_its_first_occurrence()
    {
        var fold = new DailySchedule("America/Chicago", new TimeOnly(1, 30));
        Check.That(fold.Resolve(new DateOnly(2026, 11, 1)) == new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), "iana-fold");
    }

    [Fact]
    public void A_daily_schedule_in_the_spring_forward_gap_does_not_occur()
    {
        var gap = new DailySchedule("America/Chicago", new TimeOnly(2, 30));
        Check.That(gap.Resolve(new DateOnly(2026, 3, 8)) is null, "iana-gap");
    }

    [Fact]
    public void A_daily_schedule_refuses_a_zone_that_is_a_path()
    {
        try { _ = new DailySchedule("../localtime", TimeOnly.MinValue); throw new SmokeFailure("iana-path-refusal"); }
        catch (ArgumentException) { }
    }

    [Theory]
    [InlineData("provision-host-operator")]
    [InlineData("provision-host-operator operator@example.test")]
    [InlineData("provision-host-operator 00000000-0000-0000-0000-000000000000")]
    [InlineData("provision-host-operator 6f9619ff-8b86-d011-b42d-00c04fc964ff --password=generated")]
    [InlineData("provision-host-operator --password generated")]
    public async Task The_provisioning_command_takes_exactly_one_account_identifier_and_nothing_else(string arguments) =>
        // Refused as a usage error before any configuration is read, so no secret can ride on the command line.
        Check.That(await Program.Main(arguments.Split(' ')) == 2, "provisioning-usage");

    [Fact]
    public void The_fixture_run_directory_is_owner_only_and_outside_the_repository()
    {
        // It holds the generated database password, so a run that dies before cleanup must leave nothing in the work tree.
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Modularyn.slnx"))) repository = repository.Parent;
        Check.That(repository is not null, "repository-root");
        var path = RunDirectory.NewPath();
        try
        {
            RunDirectory.Create(path);
            Check.That(!path.StartsWith(repository!.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "run-directory-outside-repository");
            if (!OperatingSystem.IsWindows())
                Check.That(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "run-directory-owner-only");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }
}

internal sealed class SmokeFailure(string check) : Exception("Smoke check failed: " + check);
internal static class Check
{
    internal static void That(bool condition, string check) { if (!condition) throw new SmokeFailure(check); }
}
