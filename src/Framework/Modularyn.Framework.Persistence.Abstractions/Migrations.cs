namespace Modularyn.Framework.Persistence.Abstractions;

public enum MigrationState { Pending, Applying, Complete, Failed, Interrupted }

public sealed record MigrationProgress(string ModuleId, string MigrationId, MigrationState State, int Attempt);

public sealed record MigrationCheckpoint(string ModuleId, string MigrationId, bool Recovered);

/// <summary>Called after the EF migration commits and before coordinator completion is recorded.</summary>
public interface IMigrationCheckpointObserver
{
    ValueTask ReachedAsync(MigrationCheckpoint checkpoint, CancellationToken cancellationToken);
}

public sealed record MigrationDiagnostic(string Code, string ModuleId, string? MigrationId, string Message);

public sealed record MigrationRunResult(IReadOnlyList<MigrationCheckpoint> Completed,
    IReadOnlyList<MigrationDiagnostic> Diagnostics)
{
    public bool Succeeded => Diagnostics.Count == 0;
}

public sealed class PersistenceContractException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
