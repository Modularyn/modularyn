using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

public sealed class PostgreSqlMigrationCoordinator(PostgreSqlModuleRegistry registry)
{
    /// <summary>
    /// Applies the composer's dependency order. Each EF migration is a transaction and a durable checkpoint.
    /// Recovery is forward-only: compare EF history with progress, then resume pending migrations.
    /// </summary>
    public async Task<MigrationRunResult> ApplyAsync(ActivationPlan plan, IMigrationCheckpointObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var preflight = MigrationPreflight.Prepare(registry, plan.Modules, cancellationToken);
        if (preflight.Diagnostics.Count > 0) return new([], preflight.Diagnostics);
        if (preflight.Modules.Count == 0) return new([], []);
        var completed = new List<MigrationCheckpoint>();
        await using var migrationLock = await PostgreSqlMigrationLock.TryAcquireAsync(preflight.Modules[0].Module.Options, cancellationToken).ConfigureAwait(false);
        if (!migrationLock.Acquired)
            return new([], [new("MGP002", preflight.Modules[0].Module.ModuleId, null, "Another migration coordinator holds the database lock. Retry after it completes.")]);

        // Inspect every selected module before the first schema change, including histories ahead of this deployment.
        foreach (var prepared in preflight.Modules)
        {
            try { await ModuleSchemaOwnership.CheckAsync(migrationLock.Connection, prepared.Module, cancellationToken).ConfigureAwait(false); }
            catch (PersistenceContractException exception)
            {
                return new([], [new(exception.Code, prepared.Module.ModuleId, null, exception.Message)]);
            }
            await using var context = prepared.Module.CreateContext();
            var history = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
            var progress = await MigrationProgressStore.ReadAsync(migrationLock.Connection, prepared.Module, cancellationToken).ConfigureAwait(false);
            if (history.Except(prepared.MigrationIds, StringComparer.Ordinal).Any()
                || progress.Keys.Except(prepared.MigrationIds, StringComparer.Ordinal).Any()
                || progress.Values.Any(row => row.State == MigrationState.Complete && !history.Contains(row.MigrationId, StringComparer.Ordinal))
                || !history.SequenceEqual(prepared.MigrationIds.Take(history.Length), StringComparer.Ordinal))
                return new([], [new("MGP003", prepared.Module.ModuleId, null, "The database history differs from this deployment. Reconcile the deployment and its forward recovery plan.")]);
        }

        foreach (var prepared in preflight.Modules)
        {
            var module = prepared.Module;
            await MigrationProgressStore.EnsureAsync(migrationLock.Connection, module, cancellationToken).ConfigureAwait(false);
            await using var context = module.CreateContext();
            var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            var progress = await MigrationProgressStore.ReadAsync(migrationLock.Connection, module, cancellationToken).ConfigureAwait(false);
            foreach (var id in prepared.MigrationIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.TryGetValue(id, out var previous);
                if (applied.Contains(id) && previous?.State == MigrationState.Complete) continue;
                var recovered = previous is not null || applied.Contains(id);
                var attempt = (previous?.Attempt ?? 0) + 1;
                try
                {
                    await MigrationProgressStore.WriteAsync(migrationLock.Connection, module, id, MigrationState.Applying, attempt, cancellationToken).ConfigureAwait(false);
                    if (!applied.Contains(id))
                        await context.GetService<IMigrator>().MigrateAsync(id, cancellationToken).ConfigureAwait(false);
                    var checkpoint = new MigrationCheckpoint(module.ModuleId, id, recovered);
                    if (observer is not null) await observer.ReachedAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                    await MigrationProgressStore.WriteAsync(migrationLock.Connection, module, id, MigrationState.Complete, attempt, cancellationToken).ConfigureAwait(false);
                    completed.Add(checkpoint);
                }
                catch (OperationCanceledException)
                {
                    await MigrationProgressStore.TryRecordFailureAsync(migrationLock.Connection, module, id, MigrationState.Interrupted, attempt).ConfigureAwait(false);
                    throw;
                }
                catch (Exception)
                {
                    await MigrationProgressStore.TryRecordFailureAsync(migrationLock.Connection, module, id, MigrationState.Failed, attempt).ConfigureAwait(false);
                    return new(completed.AsReadOnly(), [new("MGP004", module.ModuleId, id,
                        "The migration did not reach coordinator completion. Inspect the module migration and retry its forward recovery plan.")]);
                }
            }
        }
        return new(completed.AsReadOnly(), []);
    }
}
