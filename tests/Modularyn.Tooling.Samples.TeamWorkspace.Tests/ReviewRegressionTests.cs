using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Files.Application;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Persistence.EfCore;
using Modularyn.Modules.Files.Persistence.FileSystem;
using Modularyn.Tooling.Samples.TeamWorkspace.Host;
using Xunit;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

// These are the sample review's carried regressions. They use its existing authority fixtures and do not qualify
// any security acceptance family or supply prerequisite evidence for AC-001.
[Collection(TeamWorkspacePostgreSqlCollection.Name)]
public sealed class ReviewRegressionTests
{
    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task A_download_grant_in_the_query_is_refused_without_reaching_logs_or_traces()
    {
        using var timeout = Timeout();
        var token = timeout.Token;
        var traces = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => traces.Enqueue(TraceText(activity))
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = await SmokeHost.StartAsync(token);
        var tenant = await host.TenantAsync(token);
        var owner = await host.UserAsync(tenant, owner: true, token);
        using var client = host.Client();
        await SmokeScenario.SignInAsync(client, owner, token);
        var bytes = Encoding.UTF8.GetBytes("A permitted download.\n");
        var grant = await ReleasedGrantAsync(host, owner, tenant, bytes, "permitted.txt", token);
        var prefix = "/api/v1/t/" + tenant.ToString("D");
        // Include a valid header as well: a 400 caused solely by a missing header would not test the query guard.
        foreach (var includeHeader in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, prefix + "/download?grant=" + Uri.EscapeDataString(grant.Token.Reveal()));
            if (includeHeader) request.Headers.Add(FileSteps.GrantHeader, grant.Token.Reveal());
            using var response = await client.SendAsync(request, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Check.That(!(await response.Content.ReadAsStringAsync(token)).Contains(grant.Token.Reveal(), StringComparison.Ordinal), "query-grant-not-in-response");
        }
        using (var control = await SmokeScenario.DownloadAsync(client, prefix, grant.Token.Reveal(), token))
        {
            Assert.Equal(HttpStatusCode.OK, control.StatusCode);
            Assert.Equal(bytes, await control.Content.ReadAsByteArrayAsync(token));
        }
        await host.StopApplicationAsync(token);
        // Boolean checks keep the canaries out of assertion diagnostics even when the test catches a leak.
        Check.That(!host.Logs.Lines.Concat(traces).Any(line => line.Contains(grant.Token.Reveal(), StringComparison.Ordinal)
            || line.Contains(owner.Password, StringComparison.Ordinal)), "query-grant-and-password-not-in-logs-or-traces");
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task A_previously_released_file_outside_the_delivery_allowlist_is_hidden_with_404()
    {
        using var timeout = Timeout();
        var token = timeout.Token;
        await using var host = await SmokeHost.StartAsync(token);
        var tenant = await host.TenantAsync(token);
        var owner = await host.UserAsync(tenant, owner: true, token);
        using var client = host.Client();
        await SmokeScenario.SignInAsync(client, owner, token);
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF\n");
        var grant = await ReleasedGrantAsync(host, owner, tenant, pdf, "earlier.pdf", token, earlierPolicy: true);
        var prefix = "/api/v1/t/" + tenant.ToString("D");
        using (var refused = await SmokeScenario.DownloadAsync(client, prefix, grant.Token.Reveal(), token))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.NotEqual("application/pdf", refused.Content.Headers.ContentType?.MediaType);
            Assert.False((await refused.Content.ReadAsByteArrayAsync(token)).SequenceEqual(pdf));
        }
        // The single read slot is also released after the delivery policy refuses a real, readable version.
        var bytes = Encoding.UTF8.GetBytes("The next permitted download.\n");
        var allowed = await ReleasedGrantAsync(host, owner, tenant, bytes, "current.txt", token);
        using var control = await SmokeScenario.DownloadAsync(client, prefix, allowed.Token.Reveal(), token);
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        Assert.Equal(bytes, await control.Content.ReadAsByteArrayAsync(token));
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task The_host_starts_with_the_development_seed_off_and_serves_no_seed_endpoint()
    {
        using var timeout = Timeout();
        var token = timeout.Token;
        await using var host = await SmokeHost.StartAsync(token, developmentSeed: false);
        var tenant = await host.TenantAsync(token);
        var owner = await host.UserAsync(tenant, owner: true, token);
        using var client = host.Client();
        using (var health = await client.GetAsync("/health/ready", token)) Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        await SmokeScenario.SignInAsync(client, owner, token);
        var prefix = "/api/v1/t/" + tenant.ToString("D");
        using (var refused = await SmokeScenario.SendAsync(client, HttpMethod.Post, prefix + "/development-seed", new { }, token))
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ProjectView[]>(prefix + "/projects", token))!);
        using var created = await SmokeScenario.SendAsync(client, HttpMethod.Post, prefix + "/projects", new CreateProject("Explicit project"), token);
        Assert.True(created.IsSuccessStatusCode);
        Assert.Single((await client.GetFromJsonAsync<ProjectView[]>(prefix + "/projects", token))!);
    }

    private static async Task<IssuedFileGrant> ReleasedGrantAsync(SmokeHost host, SmokeUser user, Guid tenant, byte[] bytes,
        string name, CancellationToken token, bool earlierPolicy = false)
    {
        await using var scope = await host.EnterAsync(user, tenant, token);
        var context = scope.ServiceProvider.GetRequiredService<TenantExecution>().Context!;
        var principal = scope.ServiceProvider.GetRequiredService<IExecutionPrincipalAccessor>().Current!;
        var current = host.Services.GetRequiredService<IFiles>();
        using var content = new MemoryStream(bytes, writable: false);
        var version = await current.UploadAsync(context, principal,
            new(null, name, "review-downloads", "review", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))), content, token);
        var release = current;
        if (earlierPolicy)
        {
            // Model a version released under a prior deployment policy, through real Files authorization, audit,
            // PostgreSQL and filesystem storage. The serving host's policy and service registrations stay unchanged.
            release = new FileService(new FileMetadataStore(host.Composition.Files, host.Services.GetRequiredService<IAuditTrail>()),
                new FileSystemBlobStore(host.BlobDirectory, TeamWorkspaceHost.MaximumAttachmentBytes, readSlots: 1),
                host.Services.GetRequiredService<IPermissionEvaluator>(), host.Clock,
                release: new AllowlistFileReleasePolicy(["application/pdf"], TeamWorkspaceHost.MaximumAttachmentBytes));
        }
        var released = await release.ReleaseAsync(context, principal, version.FileId, version.VersionId, token);
        Assert.Equal(FileQuarantineState.Released, released.Quarantine);
        Assert.Equal(earlierPolicy ? "application/pdf" : "text/plain", released.ContentType);
        var observed = Assert.Single(await current.ListAsync(context, principal, version.FileId, token));
        Assert.Equal(FileQuarantineState.Released, observed.Quarantine);
        return await current.IssueGrantAsync(context, principal, version.FileId, version.VersionId, FileGrantPurpose.Download, TimeSpan.FromMinutes(1), token);
    }

    private static string TraceText(Activity activity) => string.Join(" ", new[] { activity.DisplayName, activity.StatusDescription ?? "", activity.TraceStateString ?? "" }
        .Concat(activity.TagObjects.Select(pair => pair.Key + "=" + pair.Value))
        .Concat(activity.Baggage.Select(pair => pair.Key + "=" + pair.Value))
        .Concat(activity.Events.SelectMany(item => new[] { item.Name }.Concat(item.Tags.Select(pair => pair.Key + "=" + pair.Value)))));

    private static CancellationTokenSource Timeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        return timeout;
    }
}
