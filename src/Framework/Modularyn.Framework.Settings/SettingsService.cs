using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modularyn.Framework.Caching;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Settings.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Settings;

/// <summary>
/// Validated scoped overrides. Every write owns a tracked transaction; null expectedVersion creates an override,
/// and replacement or reset requires the current version. The caller's context supplies the tenant and user.
/// Authorization of management operations belongs to the calling application boundary.
/// </summary>
public sealed class SettingsService
{
    public const string GlobalOperationId = "modularyn.settings.global";
    private readonly PostgreSqlModule module;
    private readonly ValueDefinitionRegistry definitions;
    private readonly ScopedValueCache cache;
    private readonly TenantWorkRunner runner;
    private readonly TimeProvider time;
    private readonly ISecretStore? secrets;

    public SettingsService(PostgreSqlModule module, ValueDefinitionRegistry definitions, ScopedValueCache cache,
        TenantWorkRunner runner, TimeProvider time, ISecretStore? secrets = null)
    {
        SettingsModule.Require(module);
        this.module = module;
        this.definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.secrets = secrets;
    }

    public Task<ResolvedValue> ResolveAsync(TenantContext tenant, ScopedValueDefinition definition, CancellationToken cancellationToken = default)
    {
        definitions.Require(definition);
        ScopedValueCache.RequireTenant(tenant);
        var propagated = PropagatedTenantContext.Capture(tenant, time);
        return cache.GetOrCreateAsync(tenant, definition.ModuleId, definition.Version.Value, definition.AssetName,
            definition.MaxStaleness, async _ =>
            {
                // A shared cache miss owns its own revalidated context, even if its first waiter ends or cancels.
                using var timeout = new CancellationTokenSource(definition.MaxStaleness, time);
                ResolvedValue? result = null;
                var outcome = await runner.RunAsync(propagated, async (unit, token) =>
                {
                    result = await LoadAsync(unit.Context, definition, token).ConfigureAwait(false);
                }, timeout.Token).ConfigureAwait(false);
                if (outcome.Disposition != TenantWorkDisposition.Completed || result is null) throw new ValueUnavailableException();
                return result;
            }, cancellationToken);
    }

    /// <summary>Resolves the reference on every call. SecretValue is caller-owned and is never cached.</summary>
    public async ValueTask<SecretValue> ResolveSecretAsync(TenantContext tenant, SettingDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsSecret) throw new InvalidOperationException("Only a secret definition can resolve a secret reference.");
        var resolved = await ResolveAsync(tenant, definition, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsSet || secrets is null) throw new ValueUnavailableException();
        try
        {
            ScopedValueCache.RequireTenant(tenant);
            var value = await secrets.GetAsync(resolved.Get<SecretReference>(), cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScopedValueCache.RequireTenant(tenant);
                return value;
            }
            catch
            {
                value.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ValueUnavailableException(); }
    }

    /// <summary>Reads one override afresh for management and its version, without resolving a secret's contents.</summary>
    public async Task<ResolvedValue?> ReadOverrideAsync(TenantContext context, ScopedValueDefinition definition, ValueSource scope,
        CancellationToken cancellationToken = default)
    {
        RequireScope(context, definition, scope);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var db = (SettingsContext)work.Context;
        if (scope == ValueSource.Global)
        {
            var global = await db.GlobalValues.AsNoTracking().SingleOrDefaultAsync(row => row.ModuleId == definition.ModuleId
                && row.Name == definition.AssetName, cancellationToken).ConfigureAwait(false);
            RequireScope(context, definition, scope);
            return global is null ? null : Materialize(definition, global.Value, global.Kind, scope, global.Version, global.UpdatedAt);
        }
        var user = User(context, scope);
        var local = await db.TenantValues.AsNoTracking().SingleOrDefaultAsync(row => row.ModuleId == definition.ModuleId
            && row.Name == definition.AssetName && row.User == user, cancellationToken).ConfigureAwait(false);
        RequireScope(context, definition, scope);
        return local is null ? null : Materialize(definition, local.Value, local.Kind, scope, local.Version, local.UpdatedAt);
    }

    public Task<Guid> SetSecretReferenceAsync(TenantContext context, SettingDefinition definition, ValueSource scope,
        SecretReference reference, Guid? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsSecret || !reference.IsValid) throw new ValueValidationException();
        return SetAsync(context, definition, scope, reference.ToString(), expectedVersion, cancellationToken);
    }

    public async Task<Guid> SetAsync(TenantContext context, ScopedValueDefinition definition, ValueSource scope, string value,
        Guid? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        RequireScope(context, definition, scope);
        Validate(definition, value);
        // A reference names a deployment-wide secret, so only a host-granted global override may choose one.
        if (definition.IsSecret && scope != ValueSource.Global)
            throw new InvalidOperationException("Only a global override may set a secret reference.");
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var db = (SettingsContext)work.Context;
        IConcurrencyTracked changed;
        var at = SettingsTimestamp.Normalize(time.GetUtcNow());
        if (scope == ValueSource.Global)
        {
            var row = await db.GlobalValues.SingleOrDefaultAsync(row => row.ModuleId == definition.ModuleId
                && row.Name == definition.AssetName, cancellationToken).ConfigureAwait(false);
            RequireVersion(row, expectedVersion);
            if (row is null)
            {
                row = new GlobalValue { ModuleId = definition.ModuleId, Name = definition.AssetName };
                db.Add(row);
            }
            row.Value = value;
            row.Kind = definition.Kind;
            row.UpdatedAt = at;
            changed = row;
        }
        else
        {
            var user = User(context, scope);
            var row = await db.TenantValues.SingleOrDefaultAsync(row => row.ModuleId == definition.ModuleId
                && row.Name == definition.AssetName && row.User == user, cancellationToken).ConfigureAwait(false);
            RequireVersion(row, expectedVersion);
            if (row is null)
            {
                row = new TenantValue { ModuleId = definition.ModuleId, Name = definition.AssetName, User = user };
                db.Add(row);
            }
            row.Value = value;
            row.Kind = definition.Kind;
            row.UpdatedAt = at;
            changed = row;
        }
        InvalidateAfterCommit(work, context, definition, scope);
        await CommitAsync(work, cancellationToken).ConfigureAwait(false);
        return changed.Version;
    }

    public async Task ResetAsync(TenantContext context, ScopedValueDefinition definition, ValueSource scope, Guid expectedVersion,
        CancellationToken cancellationToken = default)
    {
        RequireScope(context, definition, scope);
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, context, cancellationToken: cancellationToken).ConfigureAwait(false);
        var db = (SettingsContext)work.Context;
        IConcurrencyTracked? row;
        if (scope == ValueSource.Global)
            row = await db.GlobalValues.SingleOrDefaultAsync(row => row.ModuleId == definition.ModuleId
                && row.Name == definition.AssetName, cancellationToken).ConfigureAwait(false);
        else
        {
            var user = User(context, scope);
            row = await db.TenantValues.SingleOrDefaultAsync(row => row.ModuleId == definition.ModuleId
                && row.Name == definition.AssetName && row.User == user, cancellationToken).ConfigureAwait(false);
        }
        RequireVersion(row, expectedVersion);
        db.Remove((object)row!);
        InvalidateAfterCommit(work, context, definition, scope);
        await CommitAsync(work, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedValue> LoadAsync(TenantContext tenant, ScopedValueDefinition definition, CancellationToken token)
    {
        try
        {
            await using var work = await EfCoreUnitOfWork.BeginAsync(module, tenant, cancellationToken: token).ConfigureAwait(false);
            var db = (SettingsContext)work.Context;
            var user = tenant.Actor.Kind == ActorKind.User ? tenant.Actor.Subject : "";
            // One statement takes a consistent PostgreSQL snapshot of every participating scope.
            var global = db.GlobalValues.AsNoTracking().Where(row => row.ModuleId == definition.ModuleId && row.Name == definition.AssetName)
                .Select(row => new { row.Value, row.Kind, Source = ValueSource.Global, row.Version, row.UpdatedAt });
            var local = db.TenantValues.AsNoTracking().Where(row => row.ModuleId == definition.ModuleId
                && row.Name == definition.AssetName && (row.User == "" || row.User == user))
                .Select(row => new { row.Value, row.Kind, Source = row.User == "" ? ValueSource.Tenant : ValueSource.User, row.Version, row.UpdatedAt });
            var candidates = await global.Concat(local).ToListAsync(token).ConfigureAwait(false);
            var choice = ValuePrecedence.Resolve(candidates.SingleOrDefault(candidate => candidate.Source == ValueSource.User)?.Value,
                candidates.SingleOrDefault(candidate => candidate.Source == ValueSource.Tenant)?.Value,
                candidates.SingleOrDefault(candidate => candidate.Source == ValueSource.Global)?.Value, definition.DefaultValue);
            var winner = candidates.SingleOrDefault(candidate => candidate.Source == choice.Source);
            return winner is null ? new(definition, definition.DefaultValue, ValueSource.Default, null, null)
                : Materialize(definition, winner.Value, winner.Kind, winner.Source, winner.Version, winner.UpdatedAt);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ValueUnavailableException(); }
    }

    private static ResolvedValue Materialize(ScopedValueDefinition definition, string value, ValueKind kind, ValueSource source,
        Guid version, DateTimeOffset at)
    {
        if (kind != definition.Kind || (definition.IsSecret && source != ValueSource.Global)) throw new ValueValidationException();
        Validate(definition, value);
        return new(definition, value, source, version, SettingsTimestamp.Normalize(at));
    }

    private static void Validate(ScopedValueDefinition definition, string value)
    {
        if (value is null || (definition.IsSecret ? !SecretReference.TryParse(value, out _) : !definition.Validation.Accepts(definition.Kind, value)))
            throw new ValueValidationException();
    }

    private void RequireScope(TenantContext context, ScopedValueDefinition definition, ValueSource scope)
    {
        definitions.Require(definition);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsActive || scope is not (ValueSource.Global or ValueSource.Tenant or ValueSource.User))
            throw new InvalidOperationException("An override requires an active context and a writable scope.");
        if (scope == ValueSource.Global)
        {
            if (context.Scope != TenantScope.Host || context.HostGrant?.OperationId != GlobalOperationId)
                throw new InvalidOperationException("Global overrides require the explicitly granted settings host operation.");
        }
        else
        {
            ScopedValueCache.RequireTenant(context);
            if (scope == ValueSource.User && context.Actor.Kind != ActorKind.User)
                throw new InvalidOperationException("A user override requires a user actor in the current tenant.");
        }
    }

    private static string User(TenantContext context, ValueSource scope) => scope == ValueSource.User ? context.Actor.Subject : "";

    private static void RequireVersion(IConcurrencyTracked? row, Guid? expected)
    {
        if (expected == Guid.Empty || (row is null ? expected is not null : expected != row.Version)) throw new ValueConflictException();
    }

    private void InvalidateAfterCommit(IUnitOfWork work, TenantContext context, ScopedValueDefinition definition, ValueSource scope) =>
        work.OnCompleted(result =>
        {
            if (result.State != UnitOfWorkState.Committed) return;
            if (scope == ValueSource.Global) cache.InvalidateGlobal(definition.ModuleId);
            else if (scope == ValueSource.Tenant) cache.InvalidateTenant(context.TenantId!.Value);
            else cache.InvalidateUser(context.TenantId!.Value, context.Actor);
        });

    private static async Task CommitAsync(EfCoreUnitOfWork work, CancellationToken token)
    {
        try
        {
            if ((await work.CompleteAsync(token).ConfigureAwait(false)).State != UnitOfWorkState.Committed) throw new ValueConflictException();
        }
        catch (DbUpdateConcurrencyException) { throw new ValueConflictException(); }
        catch (DbUpdateException) { throw new ValueConflictException(); }
    }

}

public static class SettingsServiceCollectionExtensions
{
    /// <summary>Register after tenancy, and include the settings module in the host's migration registry.</summary>
    public static IServiceCollection AddModularynSettings(this IServiceCollection services, PostgreSqlModule module, ValueDefinitionRegistry definitions)
    {
        SettingsModule.Require(module);
        if (services.Any(service => service.ServiceType == typeof(SettingsService))) throw new InvalidOperationException("Register settings once.");
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TenantCache>();
        services.TryAddSingleton<ScopedValueCache>();
        services.AddSingleton(definitions);
        services.AddSingleton<ICompositionValidator>(definitions);
        services.AddSingleton(provider => new SettingsService(module, definitions, provider.GetRequiredService<ScopedValueCache>(),
            provider.GetRequiredService<TenantWorkRunner>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<ISecretStore>()));
        return services;
    }
}
