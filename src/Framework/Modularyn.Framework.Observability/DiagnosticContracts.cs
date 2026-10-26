namespace Modularyn.Framework.Observability;

public enum DiagnosticOperation { HostStart, HostStop, ModuleConstruction, ModuleInitialization, ModuleStart, ModuleStop, HttpRequest }
public enum DiagnosticOutcome { Began, Succeeded, Failed, Cancelled }
public enum DiagnosticField { ModuleId, ModuleVersion }

/// <summary>
/// Extension point for the separately supplied diagnostics redaction policy.
/// Only structural module metadata reaches this seam; null omits a field.
/// Implementations must be thread safe. No policy is supplied by this package.
/// </summary>
public interface IDiagnosticRedactor
{
    string? Redact(DiagnosticField field, string value);
}
