using System.Collections.ObjectModel;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Modularyn.Framework.Persistence.Abstractions;
using Npgsql;

namespace Modularyn.Framework.Persistence.EfCore;

public sealed record SqlWriteResult(bool Succeeded, Guid? Version, ConcurrencyConflict? Conflict);

/// <summary>
/// Direct SQL for one owned table at a time. Callers supply typed members and parameter values,
/// never SQL fragments. EF composes all named filters over the raw SELECT and over updates.
/// </summary>
public sealed class ModuleSql
{
    private readonly ModuleDbContext context;
    internal ModuleSql(ModuleDbContext context) => this.context = context;

    public IQueryable<TEntity> Query<TEntity>() where TEntity : class
    {
        context.RequireWork();
        var entity = Entity<TEntity>();
        // Only identifiers from the EF model, quoted by PostgreSqlModule.Quote, are concatenated;
        // every value is a provider parameter.
#pragma warning disable EF1003
        return context.Set<TEntity>().FromSqlRaw("SELECT * FROM " + Table(entity)).AsNoTracking();
#pragma warning restore EF1003
    }

    public IQueryable<TEntity> Query<TEntity, TValue>(Expression<Func<TEntity, TValue>> column, TValue value) where TEntity : class
    {
        context.RequireWork();
        var entity = Entity<TEntity>();
        var property = Property(entity, column);
        using var command = new NpgsqlCommand();
        var parameter = property.GetRelationalTypeMapping().CreateParameter(command, "value", value);
        var sql = "SELECT * FROM " + Table(entity) + " WHERE " + Column(entity, property) + " IS NOT DISTINCT FROM @value";
        return context.Set<TEntity>().FromSqlRaw(sql, parameter).AsNoTracking();
    }

    public BypassedQuery<TEntity> IncludingSoftDeleted<TEntity>(SoftDeleteBypass bypass) where TEntity : class =>
        ModuleQueryFilterRegistry.Bypass(Query<TEntity>(), bypass);

    /// <summary>Inserts scalar values with provider type mappings on this unit's connection and transaction.</summary>
    public async Task InsertAsync<TEntity>(TEntity row, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(row);
        cancellationToken.ThrowIfCancellationRequested();
        context.RequireWork();
        var entity = Entity<TEntity>();
        var properties = entity.GetProperties().ToArray();
        if (properties.Any(property => property.IsShadowProperty() || property.ValueGenerated != ValueGenerated.Never)
            || entity.GetNavigations().Any() || entity.GetComplexProperties().Any())
            throw new PersistenceContractException("MPS016", "Direct inserts support scalar rows with explicit keys and values.");
        if (row is IConcurrencyTracked tracked) tracked.Version = Guid.NewGuid();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandTimeout = context.Database.GetCommandTimeout() ?? 30;
        var entry = context.Entry(row);
        for (var index = 0; index < properties.Length; index++)
            command.Parameters.Add(properties[index].GetRelationalTypeMapping().CreateParameter(command, "p" + index,
                entry.Property(properties[index].Name).CurrentValue));
        command.CommandText = "INSERT INTO " + Table(entity) + " (" + string.Join(", ", properties.Select(property => Column(entity, property)))
            + ") VALUES (" + string.Join(", ", Enumerable.Range(0, properties.Length).Select(index => "@p" + index)) + ")";
        try { await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            context.Work!.MarkRollbackOnly();
            throw;
        }
    }

    /// <summary>A stale token or a filtered-out row reports a conflict and prevents this transaction from committing.</summary>
    public async Task<SqlWriteResult> UpdateAsync<TEntity, TKey, TValue>(Expression<Func<TEntity, TKey>> key, TKey keyValue, Guid expectedVersion,
        Expression<Func<TEntity, TValue>> column, TValue value, CancellationToken cancellationToken = default)
        where TEntity : class, IConcurrencyTracked
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.RequireWork();
        var entity = Entity<TEntity>();
        var keyProperty = Property(entity, key);
        var property = Property(entity, column);
        var primaryKey = entity.FindPrimaryKey();
        if (primaryKey is null || primaryKey.Properties.Count != 1 || primaryKey.Properties[0] != keyProperty)
            throw new PersistenceContractException("MPS016", "Direct updates require the entity's single primary-key member.");
        if (expectedVersion == Guid.Empty || property.IsPrimaryKey() || property.Name == nameof(IConcurrencyTracked.Version))
            throw new PersistenceContractException("MPS010", "Direct updates require an original version and a non-key value member.");
        // A captured value becomes a provider parameter; it is never concatenated into command text.
        Expression<Func<TKey>> parameter = () => keyValue;
        var match = Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(key.Body, parameter.Body), key.Parameters);
        var version = Guid.NewGuid();
        try
        {
            var count = await context.Set<TEntity>().Where(match).Where(row => EF.Property<Guid>(row, nameof(IConcurrencyTracked.Version)) == expectedVersion)
                .ExecuteUpdateAsync(setters => setters.SetProperty(column, value)
                    .SetProperty(row => EF.Property<Guid>(row, nameof(IConcurrencyTracked.Version)), version), cancellationToken)
                .ConfigureAwait(false);
            if (count == 1) return new(true, version, null);
            var conflict = new ConcurrencyConflict(typeof(TEntity).Name, expectedVersion,
                new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { [property.Name] = value }));
            context.Work!.MarkRollbackOnly(conflict);
            return new(false, null, conflict);
        }
        catch
        {
            context.Work!.MarkRollbackOnly();
            throw;
        }
    }

    private IEntityType Entity<TEntity>() where TEntity : class
    {
        var entity = context.Model.FindEntityType(typeof(TEntity));
        if (entity is null || entity.BaseType is not null || entity.GetDerivedTypes().Any() || entity.IsOwned()
            || entity.GetMappingFragments(StoreObjectType.Table).Any())
            throw new PersistenceContractException("MPS017", "Direct SQL requires one owned entity mapped to one table without inheritance or splitting.");
        return entity;
    }

    private static IProperty Property<TEntity, TValue>(IEntityType entity, Expression<Func<TEntity, TValue>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (expression.Body is not MemberExpression member || member.Expression != expression.Parameters[0]
            || entity.FindProperty(member.Member.Name) is not { } property)
            throw new PersistenceContractException("MPS018", "Select one mapped scalar member; SQL text and computed expressions are not accepted.");
        return property;
    }

    private string Table(IEntityType entity) => context.Module.Table(entity.GetTableName()!);

    private static string Column(IEntityType entity, IProperty property) => PostgreSqlModule.Quote(
        property.GetColumnName(StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema()))
        ?? throw new PersistenceContractException("MPS018", "The selected member has no column mapping."));
}
