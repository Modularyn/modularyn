using System.Collections.ObjectModel;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;
using Npgsql;

namespace Modularyn.Framework.Persistence.EfCore;

public sealed record SqlWriteResult(bool Succeeded, Guid? Version, ConcurrencyConflict? Conflict);

/// <summary>
/// Direct SQL for one owned table at a time. Callers supply typed members and parameter values,
/// never SQL fragments. EF composes all named filters, the tenant filter included, over the raw
/// SELECT and over updates; inserts take the tenant from the unit's validated context.
/// </summary>
public sealed class ModuleSql
{
    private readonly ModuleDbContext context;
    internal ModuleSql(ModuleDbContext context) => this.context = context;

    public IQueryable<TEntity> Query<TEntity>() where TEntity : class
    {
        context.RequireWork();
        var entity = Entity<TEntity>();
        return Read<TEntity>(ModuleSqlText.SelectAll(context.Module, entity));
    }

    public IQueryable<TEntity> Query<TEntity, TValue>(Expression<Func<TEntity, TValue>> column, TValue value) where TEntity : class
    {
        context.RequireWork();
        var entity = Entity<TEntity>();
        var property = Property(entity, column);
        using var command = new NpgsqlCommand();
        var parameter = property.GetRelationalTypeMapping().CreateParameter(command, "value", value);
        return Read<TEntity>(ModuleSqlText.SelectWhere(context.Module, entity, property), parameter);
    }

    public BypassedQuery<TEntity> IncludingSoftDeleted<TEntity>(SoftDeleteBypass bypass) where TEntity : class =>
        ModuleQueryFilterRegistry.Bypass(Query<TEntity>(), bypass);

    /// <summary>The direct-SQL read of every tenant's rows, for an active, audited host-scope unit whose operation reaches the entity.</summary>
    public CrossTenantQuery<TEntity> AcrossTenants<TEntity>() where TEntity : class, ITenantScoped
    {
        var grant = context.RequireHostGrant(context.Model.FindEntityType(typeof(TEntity)));
        return ModuleQueryFilterRegistry.BypassTenant(Query<TEntity>(), grant);
    }

    /// <summary>
    /// The adapter's only raw SQL read. The text comes from <see cref="ModuleSqlText"/>, built from owned EF
    /// identifiers quoted by PostgreSqlModule.Quote, and every value is a provider parameter. The query guard
    /// accepts this FromSql root only under the adapter's marker and only when its text equals that template for
    /// the root's entity; EF composes every named filter over it.
    /// </summary>
    private IQueryable<TEntity> Read<TEntity>(string sql, params object[] parameters) where TEntity : class
    {
#pragma warning disable EF1003
        var query = context.Set<TEntity>().FromSqlRaw(sql, parameters);
#pragma warning restore EF1003
        return QueryMarkers.Apply(QueryMarkers.AdapterSql, query).AsNoTracking();
    }

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
        // A direct insert composes no filter, so it applies the same scope rules as a tracked write.
        if (row is ITenantScoped scoped)
        {
            var tenant = context.RequireTenantWrite();
            if (scoped.TenantId == Guid.Empty) scoped.TenantId = tenant;
            else if (scoped.TenantId != tenant)
                throw new PersistenceContractException("MPS036", "A unit of work writes only its own tenant's rows.");
        }
        else context.RequireHostWrite(entity);
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
    public Task<SqlWriteResult> UpdateAsync<TEntity, TKey, TValue>(Expression<Func<TEntity, TKey>> key, TKey keyValue, Guid expectedVersion,
        Expression<Func<TEntity, TValue>> column, TValue value, CancellationToken cancellationToken = default)
        where TEntity : class, IConcurrencyTracked =>
        UpdateCoreAsync(null, key, keyValue, expectedVersion, column, value, cancellationToken);

    /// <summary>
    /// Changes one named tenant's row from an active, audited host-scope unit, for records the host keeps about tenant
    /// work, such as a denied disposition, in an entity the unit's host operation reaches. The tenant is explicit, the
    /// version must match, the tenant cannot change, and every other filter still applies. It is the only write a host
    /// scope can make to tenant-scoped data.
    /// </summary>
    public Task<SqlWriteResult> UpdateForTenantAsync<TEntity, TKey, TValue>(Guid tenantId, Expression<Func<TEntity, TKey>> key, TKey keyValue,
        Guid expectedVersion, Expression<Func<TEntity, TValue>> column, TValue value, CancellationToken cancellationToken = default)
        where TEntity : class, ITenantScoped, IConcurrencyTracked
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Name the tenant whose row changes.", nameof(tenantId));
        return UpdateCoreAsync(tenantId, key, keyValue, expectedVersion, column, value, cancellationToken);
    }

    private async Task<SqlWriteResult> UpdateCoreAsync<TEntity, TKey, TValue>(Guid? hostTenant, Expression<Func<TEntity, TKey>> key, TKey keyValue,
        Guid expectedVersion, Expression<Func<TEntity, TValue>> column, TValue value, CancellationToken cancellationToken)
        where TEntity : class, IConcurrencyTracked
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.RequireWork();
        var entity = Entity<TEntity>();
        var keyProperty = Property(entity, key);
        var property = Property(entity, column);
        var primaryKey = entity.FindPrimaryKey();
        var tenantProperty = TenantModel.TenantProperty(entity);
        // A tenant-scoped key is the tenant plus one member; the tenant filter, or the named tenant, supplies the tenant part.
        var keyMembers = primaryKey?.Properties.Where(member => !ReferenceEquals(member, tenantProperty)).ToArray();
        if (primaryKey is null || keyMembers!.Length != 1 || keyMembers[0] != keyProperty
            || primaryKey.Properties.Count != (tenantProperty is null ? 1 : 2))
            throw new PersistenceContractException("MPS016", "Direct updates require the entity's single primary-key member.");
        if (ReferenceEquals(property, tenantProperty))
            throw new PersistenceContractException("MPS036", "A tenant-scoped row's tenant cannot be changed.");
        if (expectedVersion == Guid.Empty || property.IsPrimaryKey() || property.Name == nameof(IConcurrencyTracked.Version))
            throw new PersistenceContractException("MPS010", "Direct updates require an original version and a non-key value member.");
        IQueryable<TEntity> source = QueryMarkers.Apply(QueryMarkers.AdapterUpdate, context.Set<TEntity>());
        if (hostTenant is { } named)
        {
            context.RequireHostGrant(entity);
            Expression<Func<TEntity, bool>> ofTenant = row => EF.Property<Guid>(row, TenantModel.TenantMember) == named;
            source = QueryMarkers.Apply(QueryMarkers.TenantBypass, source.IgnoreQueryFilters([ModuleQueryFilterRegistry.Tenant])).Where(ofTenant);
        }
        else if (tenantProperty is null) context.RequireHostWrite(entity);
        else context.RequireTenantWrite();
        // A captured value becomes a provider parameter; it is never concatenated into command text.
        Expression<Func<TKey>> parameter = () => keyValue;
        var match = Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(key.Body, parameter.Body), key.Parameters);
        var version = Guid.NewGuid();
        try
        {
            var count = await source.Where(match)
                .Where(row => EF.Property<Guid>(row, nameof(IConcurrencyTracked.Version)) == expectedVersion)
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
        if (entity is null || !ModuleSqlText.IsDirectSqlEntity(entity))
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

    private static string Column(IEntityType entity, IProperty property) => ModuleSqlText.Column(entity, property)
        ?? throw new PersistenceContractException("MPS018", "The selected member has no column mapping.");
}

/// <summary>
/// The text of the adapter's raw SQL reads. <see cref="ModuleSql"/> builds its reads from these templates, and the query
/// guard accepts a FromSql root only when its text equals one of them for the root's entity, so no other SQL text can
/// stand behind an adapter read, whatever expression it arrives in.
/// </summary>
internal static class ModuleSqlText
{
    internal static bool IsDirectSqlEntity(IReadOnlyEntityType entity) => entity.BaseType is null && !entity.GetDerivedTypes().Any()
        && !entity.IsOwned() && !entity.GetMappingFragments(StoreObjectType.Table).Any() && entity.GetTableName() is not null;

    internal static string SelectAll(PostgreSqlModule module, IReadOnlyEntityType entity) => "SELECT * FROM " + module.Table(entity.GetTableName()!);

    internal static string SelectWhere(PostgreSqlModule module, IReadOnlyEntityType entity, IReadOnlyProperty property) =>
        SelectAll(module, entity) + " WHERE " + (Column(entity, property)
            ?? throw new PersistenceContractException("MPS018", "The selected member has no column mapping.")) + " IS NOT DISTINCT FROM @value";

    internal static string? Column(IReadOnlyEntityType entity, IReadOnlyProperty property) =>
        property.GetColumnName(StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema())) is { } column
            ? PostgreSqlModule.Quote(column)
            : null;

    /// <summary>True only for the exact text ModuleSql builds for this entity in this module.</summary>
    internal static bool IsAdapterRead(PostgreSqlModule module, IReadOnlyEntityType entity, string sql) =>
        IsDirectSqlEntity(entity) && (string.Equals(sql, SelectAll(module, entity), StringComparison.Ordinal)
            || entity.GetProperties().Any(property => Column(entity, property) is not null
                && string.Equals(sql, SelectWhere(module, entity, property), StringComparison.Ordinal)));
}
