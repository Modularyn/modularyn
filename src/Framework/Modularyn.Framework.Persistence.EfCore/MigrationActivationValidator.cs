using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

/// <summary>Read-only readiness gate. Register as ICompositionValidator; migration application is a separate operation.</summary>
public sealed class MigrationActivationValidator(PostgreSqlModuleRegistry registry) : ICompositionMigrationValidator
{
    public const string Incomplete = "MDC201";
    public const string Busy = "MDC202";
    public const string Invalid = "MDC203";
    public int Order => 100;

    public async ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var preflight = MigrationPreflight.Prepare(registry, selection.Modules, cancellationToken);
        if (preflight.Diagnostics.Count > 0)
            return preflight.Diagnostics.Select(diagnostic => new CompositionDiagnostic(Invalid,
                $"Module '{diagnostic.ModuleId}' has an invalid persistence registration. Correct its ownership and migration declarations before activation.", [diagnostic.ModuleId])).ToArray();
        if (preflight.Modules.Count == 0) return [];
        var diagnostics = new List<CompositionDiagnostic>();
        try
        {
            await using var migrationLock = await PostgreSqlMigrationLock.TryAcquireAsync(preflight.Modules[0].Module.Options, cancellationToken).ConfigureAwait(false);
            if (!migrationLock.Acquired)
                return [new(Busy, "The database migration lock is held. Retry activation after migration completion.", preflight.Modules.Select(item => item.Module.ModuleId))];
            foreach (var prepared in preflight.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var owned = await ModuleSchemaOwnership.CheckAsync(migrationLock.Connection, prepared.Module, cancellationToken).ConfigureAwait(false);
                await using var context = prepared.Module.CreateContext();
                var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
                var progress = await MigrationProgressStore.ReadAsync(migrationLock.Connection, prepared.Module, cancellationToken).ConfigureAwait(false);
                if (applied.Except(prepared.MigrationIds, StringComparer.Ordinal).Any()
                    || progress.Keys.Except(prepared.MigrationIds, StringComparer.Ordinal).Any()
                    || !applied.SequenceEqual(prepared.MigrationIds.Take(applied.Length), StringComparer.Ordinal)
                    || progress.Values.Any(row => row.State == MigrationState.Complete && !applied.Contains(row.MigrationId, StringComparer.Ordinal)))
                    diagnostics.Add(new(Invalid,
                        $"Module '{prepared.Module.ModuleId}' has a history inconsistent with this deployment. Reconcile its migration history before activation.", [prepared.Module.ModuleId]));
                else if (!owned || !applied.SequenceEqual(prepared.MigrationIds, StringComparer.Ordinal)
                    || prepared.MigrationIds.Any(id => !progress.TryGetValue(id, out var row) || row.State != MigrationState.Complete))
                    diagnostics.Add(new(Incomplete,
                        $"Module '{prepared.Module.ModuleId}' has pending, interrupted or failed migrations. Run its migration coordinator to completion before activation.", [prepared.Module.ModuleId]));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            diagnostics.Add(new(Invalid, "Migration readiness could not be established. Restore database availability and reconcile migration history before activation.",
                preflight.Modules.Select(item => item.Module.ModuleId)));
        }
        return diagnostics.AsReadOnly();
    }
}
