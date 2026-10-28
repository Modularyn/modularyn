using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Api;
using Modularyn.Framework.Persistence.EfCore;
using Xunit;

namespace Modularyn.Framework.Sdk.Client.Tests;

[Collection(AuthorizationPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class G02AC008Tests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("dotnet")]
    [InlineData("typescript")]
    public Task AC_008_P01_AdvertisedClientsAgreeWithTheEndpointContract_Contract(string client) => RunAsync(client, "positive");

    [Theory]
    [InlineData("dotnet")]
    [InlineData("typescript")]
    public Task AC_008_N01_EveryClientRetainsDistinctTypedFailures_Contract(string client) => RunAsync(client, "negative");

    [Theory]
    [InlineData("dotnet")]
    [InlineData("typescript")]
    public Task AC_008_B01_VersionAndCancellationEdgesPreserveAuthoritativeOutcome_Contract(string client) => RunAsync(client, "boundary");

    [Theory]
    [InlineData("dotnet")]
    [InlineData("typescript")]
    public Task AC_008_R01_InterruptedResponseReconcilesWithoutDuplicateCreation_Contract(string client) => RunAsync(client, "recovery");

    [Theory]
    [InlineData("dotnet")]
    [InlineData("typescript")]
    public Task AC_008_R01_InterruptedResponseReconcilesWithoutDuplicateCreation_Integration(string client) => RunAsync(client, "concurrent");

    [Theory]
    [InlineData("dotnet")]
    [InlineData("typescript")]
    public Task AC_008_R01_InterruptedResponseReconcilesWithoutDuplicateCreation_Recovery(string client) => RunAsync(client, "restart");

    private static async Task RunAsync(string language, string scenario)
    {
        await using var database = await AuthorizationDatabase.CreateAsync();
        var clock = new TestClock();
        await using var host = AuthorizationHost.Create(database, clock);
        var tenant = await host.TenantAsync("client-tenant");
        var user = await host.UserAsync("client-member", tenant);
        await host.OwnerAsync(user, tenant);
        var (session, _) = await host.SignInAsync(user);
        var key = session.Token.Reveal();
        var deniedUser = await host.UserAsync("client-without-grant", tenant);
        var deniedSession = (await host.SignInAsync(deniedUser)).Session.Token.Reveal();
        var before = Guid.NewGuid();
        var after = Guid.NewGuid();
        var recovery = Guid.NewGuid();
        var itemId = Guid.Empty;
        await using (var initial = await ClientServer.StartAsync(database, clock))
        {
            Assert.Contains(8306, initial.Events.Codes);
            using var transport = initial.Transport(key);
            var client = new FixtureClient(transport);
            var item = await client.CreateItemAsync(tenant.ToString("D"), new("initial", null), Guid.NewGuid(), Cancellation);
            itemId = item.Id;
            if (scenario == "restart")
            {
                initial.Pause(recovery, "interrupt");
                await Assert.ThrowsAsync<OutcomeUnknownFailure>(() => client.CreateItemAsync(tenant.ToString("D"), new("recovered", null), recovery, Cancellation));
            }
            else
            {
                await ExerciseAsync(initial, item, language, scenario, tenant, key, deniedSession, before, after, recovery);
                if (scenario is "recovery" or "concurrent")
                    Assert.True(initial.Requests.Where(request => request.Operation == recovery).Select(request => request.Correlation).Distinct().Count() >= 2);
                await CountsAsync(host, user, tenant, scenario, before, after, recovery);
                return;
            }
        }
        // A new host and fresh clients read the same committed receipt; no state from the previous process is reused.
        await using var restarted = await ClientServer.StartAsync(database, clock);
        using var restartedTransport = restarted.Transport(key);
        var original = await new FixtureClient(restartedTransport).GetItemAsync(tenant.ToString("D"), itemId.ToString("D"), Cancellation);
        await ExerciseAsync(restarted, original, language, scenario, tenant, key, deniedSession, before, after, recovery);
        await CountsAsync(host, user, tenant, scenario, before, after, recovery);
    }

    private static async Task CountsAsync(AuthorizationHost host, User user, Guid tenant, string scenario, Guid before, Guid after, Guid recovery)
    {
        await using var scope = await host.EnterAsync(user, tenant);
        await using var unit = await EfCoreUnitOfWork.BeginAsync(host.Database.ItemsModule, scope.Context, cancellationToken: Cancellation);
        var count = await unit.Context.Set<Item>().CountAsync(Cancellation);
        Assert.Equal(scenario is "recovery" or "concurrent" or "restart" ? 2 : 1, count);
        if (scenario == "boundary")
        {
            Assert.False(await unit.Context.Set<ApiOperation>().AnyAsync(row => row.Id == before, Cancellation));
            Assert.Equal(1, await unit.Context.Set<ApiOperation>().CountAsync(row => row.Id == after, Cancellation));
        }
        if (scenario is "recovery" or "concurrent" or "restart")
            Assert.Equal(1, await unit.Context.Set<ApiOperation>().CountAsync(row => row.Id == recovery, Cancellation));
    }

    private static async Task ExerciseAsync(ClientServer server, ItemView item, string language, string scenario,
        Guid tenantId, string session, string deniedSession, Guid before, Guid after, Guid recovery)
    {
        var tenant = tenantId.ToString("D");
        var edge = Guid.NewGuid();
        var shape = Guid.NewGuid();
        Fault? beforeFault = null;
        Fault? afterFault = null;
        if (scenario == "boundary")
        {
            beforeFault = server.Pause(before, "before"); afterFault = server.Pause(after, "after");
            server.Pause(edge, "version"); server.Pause(shape, "protocol");
        }
        if (scenario == "recovery") server.Pause(recovery, "interrupt");
        if (language == "typescript")
        {
            await TypeScriptAsync(server, new { origin = server.Origin.Value, tenant, session, deniedSession, item, before, after, recovery, edge, shape, scenario });
            return;
        }
        using var transport = server.Transport(session);
        var client = new FixtureClient(transport);
        switch (scenario)
        {
            case "positive":
                Assert.Equal(item, await client.GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                var updated = await client.UpdateItemAsync(tenant, item.Id.ToString("D"), new("changed", "", item.Version), Guid.NewGuid(), Cancellation);
                Assert.NotEqual(item.Version, updated.Version);
                Assert.Equal("", updated.Note);
                Assert.Equal(updated, await client.GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                var bootstrap = await client.GetConfigurationAsync(tenant, Cancellation);
                Assert.Single(bootstrap.Settings);
                Assert.Equal("20", bootstrap.Settings[Items.ModuleId + ":" + Items.PageSize.Name].GetString());
                Assert.Single(bootstrap.Features);
                Assert.False(bootstrap.Features[Items.ModuleId + ":" + Items.ClientFeature.Name].GetBoolean());
                break;
            case "negative":
                var validation = await Assert.ThrowsAsync<ValidationFailure>(() => client.CreateItemAsync(tenant, new("", null), Guid.NewGuid(), Cancellation));
                Assert.Equal(ApiCodes.Validation, validation.Problem.Code);
                Assert.NotEmpty(validation.Problem.CorrelationId);
                var conflict = await Assert.ThrowsAsync<ConcurrencyFailure>(() => client.UpdateItemAsync(tenant, item.Id.ToString("D"), new("stale", null, Guid.NewGuid()), Guid.NewGuid(), Cancellation));
                Assert.Equal(ApiCodes.Conflict, conflict.Problem.Code);
                Assert.NotEmpty(conflict.Problem.CorrelationId);
                using (var unsupported = server.Transport(session, 0))
                {
                    var failure = await Assert.ThrowsAsync<CompatibilityFailure>(() => new FixtureClient(unsupported).GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                    Assert.Equal(ApiCodes.Version, failure.Problem.Code);
                    Assert.NotEmpty(failure.Problem.CorrelationId);
                }
                using (var anonymous = server.Transport(null))
                    await Assert.ThrowsAsync<AuthenticationFailure>(() => new FixtureClient(anonymous).GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                using (var forbidden = server.Transport(deniedSession))
                    await Assert.ThrowsAsync<AuthorizationFailure>(() => new FixtureClient(forbidden).GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                Assert.Equal(item, await client.GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                break;
            case "boundary":
                Assert.Equal(2, ApiClient.NegotiateVersion(1, 2, 1, 2));
                Assert.Throws<CompatibilityFailure>(() => ApiClient.NegotiateVersion(1, 2, 3, 4));
                using (var cancelled = new CancellationTokenSource())
                {
                    await cancelled.CancelAsync();
                    var count = server.Requests.Count;
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateItemAsync(tenant, new("not sent", null), Guid.NewGuid(), cancelled.Token));
                    Assert.Equal(count, server.Requests.Count);
                }
                foreach (var version in new[] { 1, 2 })
                {
                    using var supported = server.Transport(session, version);
                    Assert.Equal(item, await new FixtureClient(supported).GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                }
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation))
                {
                    var pending = client.UpdateItemAsync(tenant, item.Id.ToString("D"), new("cancelled", null, item.Version), before, cancellation.Token);
                    await beforeFault!.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
                    await cancellation.CancelAsync();
                    var unknown = await Assert.ThrowsAsync<OutcomeUnknownFailure>(() => pending);
                    Assert.Equal(before, unknown.Problem.OperationId);
                    await beforeFault!.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
                }
                Assert.Equal("notObserved", (await client.GetOperationAsync(tenant, before.ToString("D"), Cancellation)).State);
                Assert.Equal(item, await client.GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation))
                {
                    var pending = client.UpdateItemAsync(tenant, item.Id.ToString("D"), new("committed", null, item.Version), after, cancellation.Token);
                    await afterFault!.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
                    await cancellation.CancelAsync();
                    await Assert.ThrowsAsync<OutcomeUnknownFailure>(() => pending);
                    await afterFault!.Finished.Task.WaitAsync(TimeSpan.FromSeconds(20), Cancellation);
                }
                var committed = await client.GetOperationAsync(tenant, after.ToString("D"), Cancellation);
                Assert.Equal("committed", committed.State);
                Assert.Equal("committed", committed.Item!.Title);
                Assert.Equal(committed.Item, await client.GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                var mismatched = await Assert.ThrowsAsync<CompatibilityFailure>(() => client.UpdateItemAsync(tenant, item.Id.ToString("D"),
                    new("observed", null, committed.Item!.Version), edge, Cancellation));
                Assert.Equal(edge, mismatched.Problem.OperationId);
                Assert.Equal("observed", mismatched.AuthoritativeResponse!.Value.GetProperty("title").GetString());
                Assert.Equal("observed", (await client.GetOperationAsync(tenant, edge.ToString("D"), Cancellation)).Item!.Title);
                var observed = (await client.GetOperationAsync(tenant, edge.ToString("D"), Cancellation)).Item!;
                var malformed = await Assert.ThrowsAsync<ProtocolFailure>(() => client.UpdateItemAsync(tenant, item.Id.ToString("D"),
                    new("shape", null, observed.Version), shape, Cancellation));
                Assert.Equal(shape, malformed.Problem.OperationId);
                Assert.Equal("shape", malformed.AuthoritativeResponse!.Value.GetProperty("title").GetString());
                Assert.Equal("shape", (await client.GetOperationAsync(tenant, shape.ToString("D"), Cancellation)).Item!.Title);
                using (var unsupported = server.Transport(session, 0))
                    await Assert.ThrowsAsync<CompatibilityFailure>(() => new FixtureClient(unsupported).GetItemAsync(tenant, item.Id.ToString("D"), Cancellation));
                break;
            case "recovery":
                await Assert.ThrowsAsync<OutcomeUnknownFailure>(() => client.CreateItemAsync(tenant, new("recovered", null), recovery, Cancellation));
                server.Clear(recovery);
                goto case "restart";
            case "restart":
                var disposition = await client.GetOperationAsync(tenant, recovery.ToString("D"), Cancellation);
                Assert.Equal("committed", disposition.State);
                var retried = await client.CreateItemAsync(tenant, new("recovered", null), recovery, Cancellation);
                Assert.Equal(disposition.Item, retried);
                Assert.Equal(retried, await client.GetItemAsync(tenant, retried.Id.ToString("D"), Cancellation));
                await Assert.ThrowsAsync<OperationConflictFailure>(() => client.CreateItemAsync(tenant, new("different", null), recovery, Cancellation));
                break;
            case "concurrent":
                var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.CreateItemAsync(tenant, new("concurrent", null), recovery, Cancellation)));
                Assert.All(results, result => Assert.Equal(results[0], result));
                break;
            default: Assert.Fail("Unknown client scenario."); break;
        }
    }

    private static async Task TypeScriptAsync(ClientServer server, object fixture)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Modularyn.slnx"))) root = root.Parent ?? throw new InvalidOperationException("The repository is required.");
        var working = Path.Combine(root.FullName, "src", "Framework", "Modularyn.Framework.Sdk.Client", "typescript");
        var start = new ProcessStartInfo("node") { WorkingDirectory = working, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Environment.GetEnvironmentVariable("MODULARYN_CLIENT_TEST_VITEST") == "1")
        {
            start.ArgumentList.Add("node_modules/vitest/vitest.mjs");
            start.ArgumentList.Add("run");
        }
        else start.ArgumentList.Add("tests/run.mjs");
        start.Environment["MODULARYN_CLIENT_FIXTURE"] = JsonSerializer.Serialize(fixture, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        start.Environment["NODE_EXTRA_CA_CERTS"] = server.CertificateFile;
        start.Environment.Remove("MODULARYN_TEST_POSTGRESQL");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The pinned Node.js runtime is required.");
        var output = process.StandardOutput.ReadToEndAsync(Cancellation);
        var error = process.StandardError.ReadToEndAsync(Cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        // Diagnostics never contain the fixture's session or its environment.
        var stdout = (await output).Replace(JsonSerializer.SerializeToElement(fixture).GetProperty("session").GetString()!, "[redacted]", StringComparison.Ordinal);
        var stderr = (await error).Replace(JsonSerializer.SerializeToElement(fixture).GetProperty("session").GetString()!, "[redacted]", StringComparison.Ordinal);
        var denied = JsonSerializer.SerializeToElement(fixture).GetProperty("deniedSession").GetString()!;
        Assert.True(process.ExitCode == 0, "TypeScript contract failed: " + stdout.Replace(denied, "[redacted]", StringComparison.Ordinal)
            + stderr.Replace(denied, "[redacted]", StringComparison.Ordinal));
    }
}
