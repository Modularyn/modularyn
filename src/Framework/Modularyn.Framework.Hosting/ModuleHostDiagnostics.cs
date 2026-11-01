using Microsoft.Extensions.Diagnostics.HealthChecks;
using Modularyn.Framework.Audit;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Hosting;

/// <summary>One module as the host's detailed diagnostics show it: its identity, version and lifecycle state.</summary>
public sealed record ModuleDiagnostic(string ModuleId, string Version, ModuleState State);

/// <summary>One registered health check by name and status, without its description, exception or data.</summary>
public sealed record HealthCheckDiagnostic(string Name, HealthStatus Status);

/// <summary>
/// The host's detailed diagnostics: its state, each module's version and state, the host's diagnostic codes without their
/// messages, each health check's status, the runtime version and the work still outstanding.
/// </summary>
public sealed record HostDiagnosticsReport(ModuleHostState State, IReadOnlyList<ModuleDiagnostic> Modules, IReadOnlyList<string> DiagnosticCodes,
    IReadOnlyList<HealthCheckDiagnostic> HealthChecks, string RuntimeVersion, int PendingWork, int RetainedInstances);

/// <summary>
/// The dependency, version and topology detail that anonymous probes never see (TM-G02-054). Liveness and readiness answer
/// with their status text alone; this report needs an active host scope granted for
/// <see cref="AuditModule.DiagnosticsHostReadOperation"/>, the diagnostics' own host operation, separate from audit
/// reading and from every administration operation. It is not mapped as an endpoint; a host that exposes it maps it
/// behind that scope.
/// </summary>
public sealed class ModuleHostDiagnostics
{
    private readonly ModuleHost host;
    private readonly HealthCheckService health;

    public ModuleHostDiagnostics(ModuleHost host, HealthCheckService health)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.health = health ?? throw new ArgumentNullException(nameof(health));
    }

    /// <summary>The report, or <see cref="DiagnosticsAccessException"/> before anything is read when the scope is not granted for it.</summary>
    public async Task<HostDiagnosticsReport> ReadAsync(TenantContext context, CancellationToken cancellationToken = default)
    {
        DiagnosticsAccess.RequireHost(context);
        var report = host.Report;
        var versions = (report.Plan?.Modules ?? []).ToDictionary(module => module.Id, module => module.Version.ToString(), StringComparer.Ordinal);
        var checks = await health.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return new(report.State,
            report.Modules.Select(module => new ModuleDiagnostic(module.ModuleId, versions.GetValueOrDefault(module.ModuleId, ""), module.State)).ToArray(),
            report.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray(),
            checks.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new HealthCheckDiagnostic(entry.Key, entry.Value.Status)).ToArray(),
            Environment.Version.ToString(), report.PendingWork, report.RetainedInstances.Count);
    }
}
