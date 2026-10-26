using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

public abstract class ModuleDbContext : DbContext
{
    protected ModuleDbContext(PostgreSqlModule module) : base(CreateOptions(module)) => Module = module;

    public PostgreSqlModule Module { get; }
    internal EfCoreUnitOfWork? Work { get; set; }

    private static DbContextOptions CreateOptions(PostgreSqlModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return new DbContextOptionsBuilder()
            .UseNpgsql(module.Options.ConnectionString, options => options.MigrationsHistoryTable(PostgreSqlModule.HistoryTable, module.Schema))
            .ReplaceService<IModelCacheKeyFactory, ModuleModelCacheKeyFactory>()
            .ReplaceService<IMigrationsAssembly, ModuleMigrationsAssembly>()
            .Options;
    }

    protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) { }

    protected sealed override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Module.Schema);
        ConfigureModule(modelBuilder);
        ModuleOwnership.ValidateModel(modelBuilder.Model, Module);
        var filters = new ModuleQueryFilterRegistry(modelBuilder);
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(entity => entity.BaseType is null))
        {
            if (typeof(IConcurrencyTracked).IsAssignableFrom(entity.ClrType))
                modelBuilder.Entity(entity.ClrType).Property<Guid>(nameof(IConcurrencyTracked.Version)).IsConcurrencyToken().ValueGeneratedNever();
            if (!typeof(ISoftDeleted).IsAssignableFrom(entity.ClrType)) continue;
            var parameter = Expression.Parameter(entity.ClrType, "row");
            filters.Add(entity.ClrType, ModuleQueryFilterRegistry.SoftDelete,
                Expression.Lambda(Expression.Not(Expression.Property(parameter, nameof(ISoftDeleted.IsDeleted))), parameter));
        }
        ConfigureFilters(filters);
    }

    protected abstract void ConfigureModule(ModelBuilder modelBuilder);

    /// <summary>Contribute named predicates here. Context member expressions retain EF's per-context parameterization.</summary>
    protected virtual void ConfigureFilters(IQueryFilterRegistry filters) { }

    public BypassedQuery<TEntity> IncludingSoftDeleted<TEntity>(SoftDeleteBypass bypass) where TEntity : class =>
        ModuleQueryFilterRegistry.Bypass(Set<TEntity>(), bypass);

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
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray())
        {
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

internal sealed class ModuleQueryFilterRegistry(ModelBuilder builder) : IQueryFilterRegistry
{
    internal const string SoftDelete = "Modularyn.SoftDelete";
    private readonly HashSet<(Type, string)> names = [];

    public void Add<TEntity>(string name, Expression<Func<TEntity, bool>> predicate) where TEntity : class =>
        Add(typeof(TEntity), name, predicate);

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
        return new(query.IgnoreQueryFilters([SoftDelete]), bypass);
    }
}
