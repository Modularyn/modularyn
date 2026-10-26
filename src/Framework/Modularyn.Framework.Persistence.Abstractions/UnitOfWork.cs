namespace Modularyn.Framework.Persistence.Abstractions;

public enum UnitOfWorkMode { Join, New, Suppress }

public enum UnitOfWorkState { Active, Joined, Committed, RolledBack, Failed, CompletedWithoutTransaction }

/// <summary>Original tokens and proposed values survive a conflict; database values are not loaded.</summary>
public sealed record ConcurrencyConflict(string EntityType, Guid ExpectedVersion,
    IReadOnlyDictionary<string, object?> ProposedValues);

public sealed record UnitOfWorkResult(UnitOfWorkState State, IReadOnlyList<ConcurrencyConflict> Conflicts);

/// <summary>One module and one database. Joining defers completion callbacks to the owning transaction.</summary>
public interface IUnitOfWork : IAsyncDisposable
{
    UnitOfWorkMode Mode { get; }
    UnitOfWorkState State { get; }
    Guid? TransactionId { get; }
    void OnCompleted(Action<UnitOfWorkResult> callback);
    Task<UnitOfWorkResult> CompleteAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public interface IConcurrencyTracked
{
    Guid Version { get; set; }
}
