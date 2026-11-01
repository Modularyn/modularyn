using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Hosting.Tests;

// TM-G02-054: the host's dependency, version and topology detail is read only in a host scope granted for the
// diagnostics' own host operation, never in a tenant context or a scope granted for another operation such as the audit
// read, and the report holds statuses and codes only, never a check's description, exception or data.
[Collection("Diagnostic console")]
public sealed class HostDiagnosticsTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TM_G02_054_Host_diagnostics_need_their_own_host_scope_and_show_statuses_and_codes_only()
    {
        using var fixture = new LifecycleFixture();
        var secret = "Password=" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var manifest = new ModuleManifest("A", LifecycleFixture.One, healthChecks: [new("Store", LifecycleFixture.One)]);
        var factory = Assert.Single(fixture.Registrations([manifest])).Factory;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddModularynModules(LifecycleFixture.Environment,
            [new(manifest, factory, [new ModuleHealthCheckRegistration("Store", _ => new DescribedCheck(secret))])], LifecycleFixture.Unverified);
        using var application = builder.Build();
        await application.StartAsync(Cancellation);
        var diagnostics = application.Services.GetRequiredService<ModuleHostDiagnostics>();

        var directory = new MemoryDirectory();
        var seams = new TestSeams();
        var tenant = directory.Add("Tenant");
        seams.Membership.Add("operator", tenant);
        seams.Authority.Grant("operator", AuditModule.HostReadOperation);
        seams.Authority.Grant("operator", AuditModule.DiagnosticsHostReadOperation);
        await using var tenancy = UnitServices.Build(directory, seams);

        // Neither a tenant context nor a host scope granted for another operation reads the host's detail.
        var (memberScope, member) = await Units.EnterAsync(tenancy, "operator", tenant);
        await using (memberScope)
            Assert.Equal(AuditCodes.HostReadRequired, (await Assert.ThrowsAsync<DiagnosticsAccessException>(() => diagnostics.ReadAsync(member, Cancellation))).Code);
        var (auditScope, auditor) = await Units.EnterHostAsync(tenancy, "operator", AuditModule.HostReadOperation.Id);
        await using (auditScope)
            Assert.Equal(AuditCodes.HostReadRequired, (await Assert.ThrowsAsync<DiagnosticsAccessException>(() => diagnostics.ReadAsync(auditor, Cancellation))).Code);

        var (scope, host) = await Units.EnterHostAsync(tenancy, "operator", AuditModule.DiagnosticsHostReadOperation.Id);
        await using (scope)
        {
            var report = await diagnostics.ReadAsync(host, Cancellation);
            Assert.Equal(ModuleHostState.Ready, report.State);
            Assert.Equal([new ModuleDiagnostic("A", "1.0.0", ModuleState.Ready)], report.Modules);
            Assert.Contains(report.HealthChecks, check => check.Status == HealthStatus.Degraded && check.Name.Contains("Store", StringComparison.Ordinal));
            Assert.Empty(report.DiagnosticCodes);
            Assert.Equal(Environment.Version.ToString(), report.RuntimeVersion);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(report), StringComparison.Ordinal);
        }
        // An ended scope reads nothing.
        Assert.Equal(AuditCodes.HostReadRequired, (await Assert.ThrowsAsync<DiagnosticsAccessException>(() => diagnostics.ReadAsync(host, Cancellation))).Code);
        await application.StopAsync(Cancellation);
    }

    /// <summary>A check that describes itself with a secret, as a careless provider might.</summary>
    private sealed class DescribedCheck(string secret) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Degraded("Store at " + secret, new InvalidOperationException(secret),
                new Dictionary<string, object> { ["connection"] = secret }));
    }
}
