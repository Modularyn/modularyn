using System.Collections.ObjectModel;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

/// <summary>
/// Explicit ambient participation avoids sharing a context across independent workers.
/// New and suppressed scopes always own a fresh context and connection.
/// </summary>
public sealed class EfCoreUnitOfWork : IUnitOfWork
{
    private readonly EfCoreUnitOfWork root;
    private readonly IDbContextTransaction? transaction;
    private readonly List<Action<UnitOfWorkResult>> callbacks = [];
    private readonly List<ConcurrencyConflict> conflicts = [];
    private int openJoins;
    private bool joinedComplete;
    private bool disposed;
    private bool rollbackOnly;
    private UnitOfWorkResult? result;

    private EfCoreUnitOfWork(ModuleDbContext context, UnitOfWorkMode mode, IDbContextTransaction? transaction)
    {
        Context = context;
        Mode = mode;
        this.transaction = transaction;
        root = this;
        context.Work = this;
    }

    private EfCoreUnitOfWork(EfCoreUnitOfWork ambient)
    {
        root = ambient.root;
        Context = root.Context;
        Mode = UnitOfWorkMode.Join;
        root.openJoins++;
    }

    public ModuleDbContext Context { get; }
    public ModuleSql Sql
    {
        get { EnsureActive(); return new(Context); }
    }
    public UnitOfWorkMode Mode { get; }
    public UnitOfWorkState State => root.result?.State ?? (joinedComplete ? UnitOfWorkState.Joined : UnitOfWorkState.Active);
    public Guid? TransactionId => root.transaction?.TransactionId;

    public static async Task<EfCoreUnitOfWork> BeginAsync(PostgreSqlModule module, UnitOfWorkMode mode = UnitOfWorkMode.Join,
        EfCoreUnitOfWork? ambient = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (System.Transactions.Transaction.Current is not null)
            throw new PersistenceContractException("MPS023", "External ambient transactions are unsupported. Pass the module unit of work explicitly.");
        if (mode == UnitOfWorkMode.Join && ambient is not null)
        {
            ambient.EnsureActive();
            if (!ReferenceEquals(module, ambient.Context.Module))
                throw new PersistenceContractException("MPS013", "A joined scope must use the same registered module and database.");
            return new(ambient);
        }

        var context = module.CreateContext();
        try
        {
            // Materialize and validate ownership before opening a connection.
            _ = context.Model;
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ModuleSchemaOwnership.RequireAsync((Npgsql.NpgsqlConnection)context.Database.GetDbConnection(), module, cancellationToken).ConfigureAwait(false);
            var transaction = mode == UnitOfWorkMode.Suppress ? null
                : await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            return new(context, mode, transaction);
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void OnCompleted(Action<UnitOfWorkResult> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        EnsureActive();
        root.callbacks.Add(callback);
    }

    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        try { return await Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            root.rollbackOnly = true;
            throw;
        }
    }

    public async Task<UnitOfWorkResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (root.result is not null) return root.result;
        if (joinedComplete) return new(UnitOfWorkState.Joined, []);
        if (!ReferenceEquals(root, this))
        {
            joinedComplete = true;
            root.openJoins--;
            return new(UnitOfWorkState.Joined, []);
        }
        if (openJoins != 0)
            throw new PersistenceContractException("MPS014", "Complete or dispose all joined scopes before completing their transaction.");
        if (rollbackOnly) return await FinishRollbackAsync(UnitOfWorkState.Failed).ConfigureAwait(false);

        try
        {
            await Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await FinishRollbackAsync(UnitOfWorkState.Failed).ConfigureAwait(false);
        }
        catch
        {
            await FinishRollbackAsync(UnitOfWorkState.Failed).ConfigureAwait(false);
            throw;
        }
        // Callback exceptions cannot reclassify a database commit as a rollback.
        return Finish(transaction is null ? UnitOfWorkState.CompletedWithoutTransaction : UnitOfWorkState.Committed);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (root.result is null)
            await root.FinishRollbackAsync(root.transaction is null ? UnitOfWorkState.Failed : UnitOfWorkState.RolledBack).ConfigureAwait(false);
    }

    internal void MarkRollbackOnly(ConcurrencyConflict? conflict = null)
    {
        root.rollbackOnly = true;
        if (conflict is not null) root.conflicts.Add(conflict);
    }

    internal void RecordConflicts(DbUpdateConcurrencyException exception)
    {
        foreach (var entry in exception.Entries)
        {
            var expected = entry.Metadata.FindProperty(nameof(IConcurrencyTracked.Version)) is null ? Guid.Empty
                : entry.Property(nameof(IConcurrencyTracked.Version)).OriginalValue as Guid? ?? Guid.Empty;
            MarkRollbackOnly(new(entry.Metadata.ClrType.Name, expected,
                new ReadOnlyDictionary<string, object?>(entry.Properties.ToDictionary(property => property.Metadata.Name,
                    property => property.CurrentValue, StringComparer.Ordinal))));
        }
    }

    private async Task<UnitOfWorkResult> FinishRollbackAsync(UnitOfWorkState state)
    {
        if (transaction is not null)
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch
            {
                Finish(UnitOfWorkState.Failed);
                throw;
            }
        }
        return Finish(state);
    }

    private UnitOfWorkResult Finish(UnitOfWorkState state)
    {
        if (result is not null) return result;
        result = new(state, Array.AsReadOnly(conflicts.ToArray()));
        var failures = new List<Exception>();
        foreach (var callback in callbacks)
        {
            try { callback(result); }
            catch (Exception exception) { failures.Add(exception); }
        }
        callbacks.Clear();
        if (failures.Count > 0) throw new AggregateException("Unit-of-work completion callbacks failed after the recorded outcome.", failures);
        return result;
    }

    private void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (State != UnitOfWorkState.Active)
            throw new PersistenceContractException("MPS015", "The unit of work has already completed.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (!ReferenceEquals(root, this))
        {
            if (!joinedComplete)
            {
                root.openJoins--;
                root.rollbackOnly = true;
            }
            return;
        }
        try
        {
            if (result is null)
                await FinishRollbackAsync(transaction is null ? UnitOfWorkState.Failed : UnitOfWorkState.RolledBack).ConfigureAwait(false);
        }
        finally
        {
            try { if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false); }
            finally { await Context.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
