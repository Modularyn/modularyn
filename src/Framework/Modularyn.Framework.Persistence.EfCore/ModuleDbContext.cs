using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

public abstract class ModuleDbContext : DbContext
{
    private static readonly PropertyInfo TenantFilterProperty =
        typeof(ModuleDbContext).GetProperty(nameof(TenantFilterValue), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly PropertyInfo ScopeHostProperty =
        typeof(ModuleDbContext).GetProperty(nameof(ScopeHostValue), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly PropertyInfo ScopeTenantProperty =
        typeof(ModuleDbContext).GetProperty(nameof(ScopeTenantValue), BindingFlags.NonPublic | BindingFlags.Instance)!;

    protected ModuleDbContext(PostgreSqlModule module) : base(CreateOptions(module)) => Module = module;

    public PostgreSqlModule Module { get; }
    internal EfCoreUnitOfWork? Work { get; set; }

    /// <summary>
    /// The validated tenant context bound by the unit of work, or null. Without an active tenant-scope context every
    /// query and write of tenant-scoped data is refused; a host-scope context reaches tenant data only through
    /// <see cref="AcrossTenants{TEntity}"/>.
    /// </summary>
    public TenantContext? Tenant { get; private set; }

    /// <summary>
    /// The named deployment scope bound by the unit of work, or null. It allows host-scoped writes outside a host scope,
    /// for framework metadata, required seeds and tests, and nothing else.
    /// </summary>
    public DeploymentScope? Deployment { get; private set; }

    /// <summary>
    /// The owner scope bound by an owner-bound unit of work, or null. It allows this module's own host-scoped writes, and
    /// nothing else: no tenant-scoped row and no cross-tenant read or update.
    /// </summary>
    public ModuleOwnerScope? Owner { get; private set; }

    private static DbContextOptions CreateOptions(PostgreSqlModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return new DbContextOptionsBuilder()
            .UseNpgsql(module.Options.ConnectionString, options => options.MigrationsHistoryTable(PostgreSqlModule.HistoryTable, module.Schema))
            .ReplaceService<IModelCacheKeyFactory, ModuleModelCacheKeyFactory>()
            .ReplaceService<IMigrationsAssembly, ModuleMigrationsAssembly>()
            .AddInterceptors(ModuleQueryGuard.Instance, ModuleCommandGuard.Instance)
            .Options;
    }

    protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) { }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Module.Schema);
        ConfigureModule(modelBuilder);
        ModuleOwnership.ValidateModel(modelBuilder.Model, Module);
        TenantModel.Classify(modelBuilder);
        var filters = new ModuleQueryFilterRegistry(modelBuilder);
        var context = Expression.Constant(this, typeof(ModuleDbContext));
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(entity => entity.BaseType is null && !entity.IsOwned()).ToArray())
        {
            if (typeof(IConcurrencyTracked).IsAssignableFrom(entity.ClrType))
                modelBuilder.Entity(entity.ClrType).Property<Guid>(nameof(IConcurrencyTracked.Version)).IsConcurrencyToken().ValueGeneratedNever();
            var parameter = Expression.Parameter(entity.ClrType, "row");
            if (typeof(ITenantScoped).IsAssignableFrom(entity.ClrType))
            {
                // Context members are evaluated for each execution, so a cached query never carries another unit's tenant.
                filters.Add(entity.ClrType, ModuleQueryFilterRegistry.Tenant, Expression.Lambda(Expression.Equal(
                    Expression.Property(parameter, TenantModel.TenantMember), Expression.Property(context, TenantFilterProperty)), parameter));
                // The scope predicate holds a tenant unit to its tenant on its own, so a query that lifts the tenant
                // predicate can reach other tenants only in an active host scope.
                filters.Add(entity.ClrType, ModuleQueryFilterRegistry.Scope, Expression.Lambda(Expression.OrElse(
                    Expression.Property(context, ScopeHostProperty), Expression.Equal(Expression.Property(parameter, TenantModel.TenantMember),
                        Expression.Property(context, ScopeTenantProperty))), parameter));
            }
            if (!typeof(ISoftDeleted).IsAssignableFrom(entity.ClrType)) continue;
            filters.Add(entity.ClrType, ModuleQueryFilterRegistry.SoftDelete,
                Expression.Lambda(Expression.Not(Expression.Property(parameter, nameof(ISoftDeleted.IsDeleted))), parameter));
        }
        ConfigureFilters(filters);
        TenantModel.Validate(modelBuilder.Model);
    }

    protected abstract void ConfigureModule(ModelBuilder modelBuilder);

    /// <summary>
    /// Contribute named predicates here. Context member expressions retain EF's per-context parameterization. Names
    /// beginning with <c>Modularyn.</c> belong to the adapter.
    /// </summary>
    protected virtual void ConfigureFilters(IQueryFilterRegistry filters) { }

    /// <summary>
    /// The entity's rows with the soft-delete predicate lifted, and only that one, for the returned query. The bypass is
    /// privileged and audited: it is recorded, with its reason and operation, through the audit that tenancy attached to the
    /// unit's active tenant or host context, before the query exists, and refused (<c>MPS047</c>) when the unit has no such
    /// context or the record cannot be written. A named deployment scope, which product code never opens, is deployment
    /// authority and records nothing.
    /// </summary>
    public async Task<BypassedQuery<TEntity>> IncludingSoftDeletedAsync<TEntity>(SoftDeleteBypass bypass, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(bypass);
        var query = ModuleQueryFilterRegistry.Bypass(Set<TEntity>(), bypass);
        await RecordBypassAsync(FilterBypass.SoftDeleted, Model.FindEntityType(typeof(TEntity)), bypass.OperationId, bypass.Reason, cancellationToken)
            .ConfigureAwait(false);
        return query;
    }

    /// <summary>
    /// Tenant-scoped rows of every tenant, for a host-scope unit only, and only of an entity its grant's operation reaches.
    /// Only the tenant predicate is lifted: soft delete and module filters remain, each execution still requires the host
    /// scope to be active, and the rows cannot be changed. Each such read is recorded in audit, naming the entity and the
    /// grant, before the query exists; a read that cannot be recorded is refused (<c>MPS047</c>).
    /// </summary>
    public async Task<CrossTenantQuery<TEntity>> AcrossTenantsAsync<TEntity>(CancellationToken cancellationToken = default) where TEntity : class, ITenantScoped
    {
        var entity = Model.FindEntityType(typeof(TEntity));
        var grant = RequireHostGrant(entity);
        var query = ModuleQueryFilterRegistry.BypassTenant(Set<TEntity>(), grant);
        await RecordBypassAsync(FilterBypass.CrossTenant, entity, grant.OperationId, grant.Reason, cancellationToken).ConfigureAwait(false);
        return query;
    }

    /// <summary>
    /// Records a filter bypass through the unit's context before its query is handed out. Only an active tenant or host
    /// context carries the audit that records it; a deployment scope may lift soft delete unrecorded, and every other unit
    /// is refused.
    /// </summary>
    internal async Task RecordBypassAsync(FilterBypass bypass, IReadOnlyEntityType? entity, string operationId, string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entity is null || ReachTable(entity) is not { } table)
            throw new PersistenceContractException("MPS047", "A filter bypass names an entity this module maps to a table.");
        switch (Tenant)
        {
            case { IsActive: false }:
                throw new PersistenceContractException("MPS035", "The unit's tenant context has ended.");
            case { } context:
                try
                {
                    await context.RecordBypassAsync(bypass, Module.ModuleId, table, operationId, reason, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    throw new PersistenceContractException("MPS047", "The filter bypass could not be recorded in audit, so it was refused.");
                }
                return;
            case null when bypass == FilterBypass.SoftDeleted && Deployment is not null:
                return;
            default:
                throw new PersistenceContractException("MPS047",
                    "A filter bypass needs an active tenant or host context, whose audit records it; this unit has neither.");
        }
    }

    /// <summary>
    /// The unit's active host grant, when its operation reaches the entity in this module. The elevated policy binds each
    /// host operation to the entities its owning module declared for it, so every other entity is refused.
    /// </summary>
    internal HostScopeGrant RequireHostGrant(IReadOnlyEntityType? entity)
    {
        var grant = Tenant is { Scope: TenantScope.Host, IsActive: true, HostGrant: { } active } ? active
            : throw new PersistenceContractException("MPS038", "Reading across tenants requires an active, audited host-scope unit of work.");
        RequireReach(grant, entity);
        return grant;
    }

    private void RequireReach(HostScopeGrant grant, IReadOnlyEntityType? entity)
    {
        if (entity is null || ReachTable(entity) is not { } table || !grant.Reaches(Module.ModuleId, table))
            throw new PersistenceContractException("MPS043",
                "The host grant's operation does not reach this entity. A host operation reaches only the entities its module declared for it.");
    }

    /// <summary>The table an entity is reached by: its root owner's, so an owned type follows the entity that owns it.</summary>
    internal static string? ReachTable(IReadOnlyEntityType entity)
    {
        var root = entity;
        while (root.IsOwned() && root.FindOwnership() is { } ownership) root = ownership.PrincipalEntityType;
        return root.GetRootType().GetTableName();
    }

    internal void Bind(TenantContext? tenant, DeploymentScope? deployment, ModuleOwnerScope? owner)
    {
        if (Tenant is not null || Deployment is not null || Owner is not null)
            throw new PersistenceContractException("MPS035", "A module context is bound to one context once.");
        if ((tenant is null ? 0 : 1) + (deployment is null ? 0 : 1) + (owner is null ? 0 : 1) > 1)
            throw new PersistenceContractException("MPS035",
                "A unit of work runs in one of a tenant or host context, a deployment scope or an owner scope, never in more than one.");
        if (tenant is { IsActive: false })
            throw new PersistenceContractException("MPS035", "The tenant context has ended; begin a unit of work only while its context is active.");
        if (owner is not null && owner.ContextType != GetType())
            throw new PersistenceContractException("MPS042", "An owner-bound unit of work changes only the data of the module whose context type it names.");
        Tenant = tenant;
        Deployment = deployment;
        Owner = owner;
    }

    /// <summary>The tenant predicate's value for one execution. It refuses rather than widening or emptying the result.</summary>
    internal Guid TenantFilterValue => Tenant switch
    {
        null => throw new PersistenceContractException("MPS035", "Tenant-scoped data requires a unit of work with a validated tenant context."),
        { IsActive: false } => throw new PersistenceContractException("MPS035", "The unit's tenant context has ended."),
        { Scope: TenantScope.Host } => throw new PersistenceContractException("MPS038",
            "A host-scope unit reaches tenant-scoped data only through AcrossTenants."),
        _ => Tenant.TenantId!.Value
    };

    /// <summary>
    /// Kept on every tenant-scoped query, a cross-tenant one included: the unit's context must still be active, and only an
    /// active host scope is exempt from the scope predicate's own tenant comparison.
    /// </summary>
    internal bool ScopeHostValue => Tenant is { IsActive: true } current
        ? current.Scope == TenantScope.Host
        : throw new PersistenceContractException("MPS035", "Tenant-scoped data requires an active, validated tenant context.");

    /// <summary>The scope predicate's tenant for one execution; it matches no row in host scope, where the predicate is exempt.</summary>
    internal Guid ScopeTenantValue => Tenant is { IsActive: true } current
        ? current.TenantId ?? Guid.Empty
        : throw new PersistenceContractException("MPS035", "Tenant-scoped data requires an active, validated tenant context.");

    public sealed override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try
        {
            PrepareWrites();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            Work?.RecordConflicts(exception);
            throw;
        }
        catch
        {
            Work?.MarkRollbackOnly();
            throw;
        }
    }

    public sealed override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareWrites();
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            Work?.RecordConflicts(exception);
            throw;
        }
        catch
        {
            Work?.MarkRollbackOnly();
            throw;
        }
    }

    private void PrepareWrites()
    {
        RequireWork();
        ChangeTracker.DetectChanges();
        // The tenant is derived from the unit, never taken from the caller; dependents receive it through key fixup.
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added && entry.Entity is ITenantScoped).ToArray())
        {
            var tenant = entry.Property(TenantModel.TenantMember);
            if ((Guid)tenant.CurrentValue! == Guid.Empty) tenant.CurrentValue = RequireTenantWrite();
        }
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray())
        {
            ModuleSql.RequireNotAppendOnly(entry.Metadata);
            CheckScope(entry);
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeleted)
            {
                entry.State = EntityState.Unchanged;
                entry.Property(nameof(ISoftDeleted.IsDeleted)).CurrentValue = true;
                entry.Property(nameof(ISoftDeleted.IsDeleted)).IsModified = true;
            }
            if (entry.Entity is not IConcurrencyTracked) continue;
            var token = entry.Property(nameof(IConcurrencyTracked.Version));
            if (entry.State != EntityState.Added && token.OriginalValue is not Guid)
                throw new PersistenceContractException("MPS010", "A write requires its original concurrency version.");
            if (entry.State != EntityState.Added && (Guid)token.OriginalValue! == Guid.Empty)
                throw new PersistenceContractException("MPS010", "A write requires a nonempty original concurrency version.");
            if (entry.State != EntityState.Deleted) token.CurrentValue = Guid.NewGuid();
        }
    }

    private void CheckScope(EntityEntry entry)
    {
        var tenantProperty = TenantModel.TenantProperty(entry.Metadata);
        if (tenantProperty is null)
        {
            RequireHostWrite(entry.Metadata);
            return;
        }
        var current = RequireTenantWrite();
        var tenant = entry.Property(tenantProperty.Name);
        if ((Guid)tenant.CurrentValue! != current || (entry.State != EntityState.Added && (Guid)tenant.OriginalValue! != current))
            throw new PersistenceContractException("MPS036", "A unit of work writes only its own tenant's rows, and a row's tenant cannot change.");
    }

    internal Guid RequireTenantWrite() => Tenant switch
    {
        null => throw new PersistenceContractException("MPS035", "Writing tenant-scoped data requires a unit of work with a validated tenant context."),
        { IsActive: false } => throw new PersistenceContractException("MPS035", "The unit's tenant context has ended."),
        { Scope: TenantScope.Host } => throw new PersistenceContractException("MPS038", "A host-scope unit reads tenant-scoped data but cannot change it."),
        _ => Tenant.TenantId!.Value
    };

    /// <summary>
    /// Host-scoped data changes only in an active host scope whose operation reaches the entity, a named deployment scope or
    /// the owning module's owner-bound unit, never implicitly. Every entity of this context belongs to its module, so an
    /// owner-bound unit reaches only its own module's tables.
    /// </summary>
    internal void RequireHostWrite(IReadOnlyEntityType entity)
    {
        if (Tenant is { IsActive: false }) throw new PersistenceContractException("MPS035", "The unit's tenant context has ended.");
        if (Tenant is { Scope: TenantScope.Tenant })
            throw new PersistenceContractException("MPS037", "A tenant unit of work can read host-scoped data but cannot change it.");
        if (Tenant is null && Deployment is null && Owner is null)
            throw new PersistenceContractException("MPS040",
                "Changing host-scoped data requires an audited host scope, a named deployment scope or the owning module's owner-bound unit; a unit without one can only read.");
        if (Tenant is { Scope: TenantScope.Host, HostGrant: { } grant }) RequireReach(grant, entity);
    }

    internal void RequireWork()
    {
        if (Work is null || Work.State != UnitOfWorkState.Active)
            throw new PersistenceContractException("MPS011", "Writes require an active module unit of work.");
    }
}

public sealed class ModuleModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is ModuleDbContext module
        ? (context.GetType(), module.Module, designTime)
        : (object)(context.GetType(), designTime);
}

public sealed record BypassedQuery<TEntity>(IQueryable<TEntity> Query, SoftDeleteBypass Bypass) where TEntity : class;

/// <summary>A query over every tenant's rows, valid only while its host scope is active, with the grant that allows it.</summary>
public sealed record CrossTenantQuery<TEntity>(IQueryable<TEntity> Query, HostScopeGrant Grant) where TEntity : class;

internal sealed class ModuleQueryFilterRegistry(ModelBuilder builder) : IQueryFilterRegistry
{
    internal const string SoftDelete = "Modularyn.SoftDelete";
    internal const string Tenant = "Modularyn.Tenant";
    internal const string Scope = "Modularyn.Scope";
    private const string ReservedPrefix = "Modularyn.";
    private readonly HashSet<(Type, string)> names = [];

    public void Add<TEntity>(string name, Expression<Func<TEntity, bool>> predicate) where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith(ReservedPrefix, StringComparison.Ordinal))
            throw new PersistenceContractException("MPS012", "Filter names beginning with 'Modularyn.' belong to the adapter.");
        Add(typeof(TEntity), name, predicate);
    }

    internal void Add(Type type, string name, LambdaExpression predicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(predicate);
        if (builder.Model.FindEntityType(type) is null || !names.Add((type, name)))
            throw new PersistenceContractException("MPS012", "A named filter must have one registration on an owned entity.");
        builder.Entity(type).HasQueryFilter(name, predicate);
    }

    internal static BypassedQuery<TEntity> Bypass<TEntity>(IQueryable<TEntity> query, SoftDeleteBypass bypass) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(bypass);
        return new(QueryMarkers.Apply(QueryMarkers.SoftDeleteBypass, query.IgnoreQueryFilters([SoftDelete])), bypass);
    }

    internal static CrossTenantQuery<TEntity> BypassTenant<TEntity>(IQueryable<TEntity> query, HostScopeGrant grant) where TEntity : class =>
        new(QueryMarkers.Apply(QueryMarkers.TenantBypass, query.IgnoreQueryFilters([Tenant])), grant);
}
