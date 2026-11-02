using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Jobs;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Identity.Application.Contracts;
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
    public void An_ordinary_member_may_release_and_grant_downloads_of_attachments() =>
        // A release only asks the deployment's allowlist policy to judge a file, and Files lets a member ask only for its own.
        Check.That(WorkspaceEndpoints.MemberPermissions.Contains(FilePermissions.Release) && WorkspaceEndpoints.MemberPermissions.Contains(FilePermissions.Grant),
            "member-role-files");

    [Fact]
    public void The_sign_in_form_submits_no_credentials_without_its_script()
    {
        var context = new DefaultHttpContext();
        var html = ((ContentHttpResult)WorkspacePages.SignIn(context)).ResponseContent!;
        // Unnamed inputs are never part of a form submission, and the form names no target and no method.
        var inputs = Regex.Matches(html, "<input[^>]*>").Select(match => match.Value).ToArray();
        Check.That(inputs.Length == 3 && inputs.All(input => !input.Contains("name=", StringComparison.Ordinal)), "unnamed-credential-inputs");
        Check.That(Regex.Match(html, "<form[^>]*>").Value == "<form id=\"sign-in\">", "form-without-action-or-method");
        Check.That(!html.Contains("<script>", StringComparison.Ordinal)
            && context.Response.Headers.ContentSecurityPolicy.ToString().Contains("script-src 'self';", StringComparison.Ordinal), "same-origin-script-only");
        // The script reads each input by the identifier the page gives it.
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "wwwroot", "sign-in.js"));
        Check.That(inputs.All(input => Regex.Match(input, "id=\"([^\"]+)\"") is { Success: true } id
            && script.Contains($"document.getElementById(\"{id.Groups[1].Value}\")", StringComparison.Ordinal)), "script-reads-inputs-by-id");
    }

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
    public void Host_endpoints_answer_directory_and_identity_refusals_with_their_own_problems()
    {
        static (string Code, int Status)? Problem(Exception exception) =>
            WorkspaceEndpoints.HostRefusal(exception) is { } refusal ? (refusal.Code, refusal.Status) : null;
        Check.That(Problem(new ArgumentException("name")) == (ApiCodes.Validation, 400), "host-validation");
        Check.That(Problem(new TenancyException("MTN009", "authority")) == ("MTN009", 403), "host-tenancy-refusal");
        Check.That(Problem(new TenancyException("MTN023", "audit")) == ("MTN023", 503), "host-tenancy-audit");
        Check.That(Problem(new TenancyException("MTN022", "commit")) == ("MTN022", 503), "host-tenancy-commit");
        Check.That(Problem(new IdentityAuthorityException(IdentityAuthorityCodes.HostOperationRequired, "operation"))
            == (IdentityAuthorityCodes.HostOperationRequired, 403), "host-identity-refusal");
        Check.That(Problem(new IdentityAuthorityException(IdentityAuthorityCodes.AuditUnavailable, "audit"))
            == (IdentityAuthorityCodes.AuditUnavailable, 503), "host-identity-audit");
        Check.That(Problem(new InvalidOperationException("other")) is null, "host-other-unchanged");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Production", null)]
    [InlineData("Staging", null)]
    [InlineData(null, "Development")]
    public async Task A_production_host_refuses_the_development_seed(string? environment, string? aspNetCoreEnvironment)
    {
        // Only DOTNET_ENVIRONMENT of Development or Testing admits the seed; an absent one is production, and ASP.NET Core's
        // own variable does not count. The refusal comes before any other configuration is read.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workspace:DevelopmentSeed"] = "true", ["DOTNET_ENVIRONMENT"] = environment, ["ASPNETCORE_ENVIRONMENT"] = aspNetCoreEnvironment
        }).Build();
        try
        {
            await using var app = await TeamWorkspaceHost.BuildAsync(configuration, cancellationToken: TestContext.Current.CancellationToken);
            throw new SmokeFailure("seed-refused-in-production");
        }
        catch (InvalidOperationException exception)
        {
            Check.That(exception.Message == "Development seed is refused outside Development and Testing.", "seed-refused-in-production");
        }
    }

    [Fact]
    public void The_container_runs_as_a_non_root_user_that_owns_none_of_its_files_and_holds_no_secret()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Modularyn.slnx"))) repository = repository.Parent;
        Check.That(repository is not null, "repository-root");
        var instructions = File.ReadAllLines(Path.Combine(repository!.FullName, "src", "Tooling", "Modularyn.Tooling.Samples.TeamWorkspace.Host", "Dockerfile"))
            .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        // The process runs as a numeric user and group, not root, after every file is in place.
        var user = Array.FindLastIndex(instructions, line => line.StartsWith("USER ", StringComparison.Ordinal));
        Check.That(user >= 0 && Regex.IsMatch(instructions[user], @"^USER [1-9][0-9]*:[1-9][0-9]*$")
            && Array.FindLastIndex(instructions, line => Regex.IsMatch(line, "^(COPY|ADD|RUN) ")) < user, "non-root-user");
        // The files stay root's, so the process can read its assemblies and deployment lock but never rewrite them.
        Check.That(instructions.All(line => !line.Contains("--chown", StringComparison.Ordinal) && !line.Contains("--chmod", StringComparison.Ordinal)),
            "root-owned-files");
        // No build argument, and no variable that names a secret, a password, a key or a connection.
        Check.That(instructions.All(line => !line.StartsWith("ARG ", StringComparison.Ordinal)
            && !(line.StartsWith("ENV ", StringComparison.Ordinal) && Regex.IsMatch(line, "(?i)secret|password|token|key|connection"))), "no-secret");
    }

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
