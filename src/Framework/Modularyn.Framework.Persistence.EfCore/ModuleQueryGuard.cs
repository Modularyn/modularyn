using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

/// <summary>
/// Adapter-internal marker calls that identify the adapter's own bypass forms, raw SQL reads and direct updates; the
/// guard removes them before EF translates the query. They are private methods, but code in the same process could
/// still build such a call by reflection, so isolation never rests on a marker alone. A raw SQL read must also carry
/// exactly the text that ModuleSql builds for its entity, and the scope filter holds a tenant unit to its tenant even
/// where a marker lifts the tenant predicate.
/// </summary>
internal static class QueryMarkers
{
    internal static readonly MethodInfo SoftDeleteBypass = Find(nameof(SoftDeleteBypassMarker));
    internal static readonly MethodInfo TenantBypass = Find(nameof(TenantBypassMarker));
    internal static readonly MethodInfo AdapterSql = Find(nameof(AdapterSqlMarker));
    internal static readonly MethodInfo AdapterUpdate = Find(nameof(AdapterUpdateMarker));

    internal static IQueryable<T> Apply<T>(MethodInfo marker, IQueryable<T> source) =>
        source.Provider.CreateQuery<T>(Expression.Call(marker.MakeGenericMethod(typeof(T)), source.Expression));

    private static IQueryable<T> SoftDeleteBypassMarker<T>(IQueryable<T> source) => throw Unsupported();
    private static IQueryable<T> TenantBypassMarker<T>(IQueryable<T> source) => throw Unsupported();
    private static IQueryable<T> AdapterSqlMarker<T>(IQueryable<T> source) => throw Unsupported();
    private static IQueryable<T> AdapterUpdateMarker<T>(IQueryable<T> source) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("Adapter query markers exist only inside module query expressions.");

    private static MethodInfo Find(string name) =>
        typeof(QueryMarkers).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("A query marker is missing.");
}

/// <summary>
/// Inspects every LINQ query a module context compiles, on the EF and direct-SQL paths alike, before translation.
/// It refuses EF Core's general <c>IgnoreQueryFilters</c> and any named form the adapter did not issue; raw SQL and other
/// query roots other than ModuleSql's own reads, whose text must match its template; bulk writes that would change a
/// tenant, change host-scoped data outside <see cref="ModuleSql"/>, run over a cross-tenant bypass, physically delete
/// soft-deleted rows or skip a version token. It is stateless: a compiled query is cached by its shape, and every
/// decision here depends only on that shape, the module and its model.
/// </summary>
internal sealed class ModuleQueryGuard : IQueryExpressionInterceptor
{
    internal static readonly ModuleQueryGuard Instance = new();

    public Expression QueryCompilationStarting(Expression queryExpression, QueryExpressionEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(queryExpression);
        var context = eventData.Context as ModuleDbContext
            ?? throw new PersistenceContractException("MPS033", "A module query must compile within its module context.");
        return new Visitor(context.Module, context.Model).Visit(queryExpression);
    }

    private sealed class Visitor(PostgreSqlModule module, IModel model) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var method = node.Method;
            if (method.IsGenericMethod && method.DeclaringType == typeof(QueryMarkers))
            {
                var marker = method.GetGenericMethodDefinition();
                var source = node.Arguments[0];
                if (marker == QueryMarkers.SoftDeleteBypass) return Bypass(source, ModuleQueryFilterRegistry.SoftDelete);
                if (marker == QueryMarkers.TenantBypass) return Bypass(source, ModuleQueryFilterRegistry.Tenant);
                // The adapter's FromSql root is the only raw SQL a module query may contain, and only with the adapter's text.
                if (marker == QueryMarkers.AdapterSql) return AdapterRead(source);
                if (marker == QueryMarkers.AdapterUpdate && source.GetType() == typeof(EntityQueryRootExpression)) return source;
                throw new PersistenceContractException("MPS033", "A module query contains an adapter marker in an unsupported position.");
            }
            if (IsQueryable(method, nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters)))
                throw new PersistenceContractException("MPS033",
                    "IgnoreQueryFilters is refused on module queries. Use IncludingSoftDeleted with a bypass reason, or AcrossTenants in an audited host scope.");
            if (IsQueryable(method, "ExecuteUpdate") || IsQueryable(method, "ExecuteDelete")) CheckBulkWrite(node);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitExtension(Expression node)
        {
            // A plain entity root composes every named filter. FromSql, SqlQuery and other roots would not, or would
            // compose them over arbitrary text, so only the adapter's own FromSql root, unwrapped above, is accepted.
            if (node is QueryRootExpression && node.GetType() != typeof(EntityQueryRootExpression))
                throw new PersistenceContractException("MPS034",
                    "Raw SQL and other query roots are refused on module queries. Use ModuleSql, whose reads compose every filter.");
            return node;
        }

        private Expression AdapterRead(Expression source)
        {
            // EF exposes the FromSql root only as an internal type. The SDK pin and locked packages fix its shape, and this
            // file's tests fail if it changes. Its text is compared with the adapter's own template for its entity.
#pragma warning disable EF1001
            if (source is FromSqlQueryRootExpression root && root.GetType() == typeof(FromSqlQueryRootExpression)
                && ModuleSqlText.IsAdapterRead(module, root.EntityType, root.Sql))
                return source;
#pragma warning restore EF1001
            throw new PersistenceContractException("MPS034",
                "A raw SQL read must be ModuleSql's own, with the text it builds for the entity. Use ModuleSql, whose reads compose every filter.");
        }

        private Expression Bypass(Expression source, string filter)
        {
            if (source is not MethodCallExpression ignore || !IsQueryable(ignore.Method, nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters))
                || ignore.Arguments.Count != 2 || ignore.Arguments[1] is not ConstantExpression { Value: IEnumerable<string> names }
                || names.Count() != 1 || names.Single() != filter)
                throw new PersistenceContractException("MPS033", "An adapter bypass must ignore exactly its own named filter.");
            return ignore.Update(null, [Visit(ignore.Arguments[0]), ignore.Arguments[1]]);
        }

        private void CheckBulkWrite(MethodCallExpression node)
        {
            var entity = model.FindEntityType(node.Method.GetGenericArguments()[0])
                ?? throw new PersistenceContractException("MPS037", "A bulk write must target a mapped module entity.");
            var markers = new MarkerCollector();
            markers.Visit(node.Arguments[0]);
            // Over a cross-tenant bypass, only the adapter's single-row, named-tenant update may write.
            if (markers.Found.Contains(QueryMarkers.TenantBypass) && !markers.Found.Contains(QueryMarkers.AdapterUpdate))
                throw new PersistenceContractException("MPS038", "A bulk write cannot run over a cross-tenant bypass; host scope reads tenant data only.");
            var tenant = TenantModel.TenantProperty(entity);
            if (tenant is null && !markers.Found.Contains(QueryMarkers.AdapterUpdate))
                throw new PersistenceContractException("MPS037",
                    "Bulk LINQ writes to host-scoped data are refused. Use tracked writes or ModuleSql.UpdateAsync, which check the unit's scope.");
            var update = node.Method.Name.StartsWith("ExecuteUpdate", StringComparison.Ordinal);
            if (tenant is not null && update)
            {
                if (node.Arguments.Count != 2 || node.Arguments[1] is not NewArrayExpression setters)
                    throw new PersistenceContractException("MPS036", "A bulk update of tenant-scoped data must use recognizable property setters.");
                foreach (var setter in setters.Expressions)
                {
                    if (setter is not NewExpression { Arguments.Count: 2 } pair || StripQuote(pair.Arguments[0]) is not LambdaExpression selector
                        || Target(entity, selector) is not { } property)
                        throw new PersistenceContractException("MPS036", "A bulk update of tenant-scoped data must use recognizable property setters.");
                    if (property.DeclaringType is IReadOnlyEntityType declaring && ReferenceEquals(TenantModel.TenantProperty(declaring), property))
                        throw new PersistenceContractException("MPS036", "A tenant-scoped row's tenant cannot be changed.");
                }
            }
            // A bulk delete would remove soft-deleted rows outright, and a bulk write would skip the version token.
            if (!update && typeof(ISoftDeleted).IsAssignableFrom(entity.ClrType))
                throw new PersistenceContractException("MPS041",
                    "ExecuteDelete would physically delete soft-deleted rows. Remove tracked entities, which the adapter marks as deleted, or set IsDeleted with ModuleSql.UpdateAsync.");
            if (typeof(IConcurrencyTracked).IsAssignableFrom(entity.ClrType) && !markers.Found.Contains(QueryMarkers.AdapterUpdate))
                throw new PersistenceContractException("MPS010",
                    "A bulk write to a version-tracked type would skip its version token. Use ModuleSql.UpdateAsync with the expected version, or tracked writes.");
        }

        private static IReadOnlyProperty? Target(IReadOnlyEntityType entity, LambdaExpression selector)
        {
            var path = new List<string>();
            var body = selector.Body;
            while (true)
            {
                while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert) body = convert.Operand;
                if (body == selector.Parameters[0]) break;
                if (body is MemberExpression { Expression: not null } member)
                {
                    path.Insert(0, member.Member.Name);
                    body = member.Expression;
                    continue;
                }
                if (body is MethodCallExpression { Method.Name: nameof(EF.Property) } call && call.Method.DeclaringType == typeof(EF)
                    && call.Arguments[1] is ConstantExpression { Value: string name })
                {
                    path.Insert(0, name);
                    body = call.Arguments[0];
                    continue;
                }
                return null;
            }
            if (path.Count == 0) return null;
            IReadOnlyTypeBase type = entity;
            foreach (var segment in path.Take(path.Count - 1))
            {
                if (type is IReadOnlyEntityType owner && owner.FindNavigation(segment) is { } navigation) type = navigation.TargetEntityType;
                else if (type.FindComplexProperty(segment) is { } complex) type = complex.ComplexType;
                else return null;
            }
            return type.FindProperty(path[^1]);
        }

        private static Expression StripQuote(Expression expression) =>
            expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;
    }

    private static bool IsQueryable(MethodInfo method, string name) =>
        method.DeclaringType == typeof(EntityFrameworkQueryableExtensions) && method.Name == name;

    private sealed class MarkerCollector : ExpressionVisitor
    {
        internal HashSet<MethodInfo> Found { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod && node.Method.DeclaringType == typeof(QueryMarkers)) Found.Add(node.Method.GetGenericMethodDefinition());
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitExtension(Expression node) => node;
    }
}

/// <summary>
/// Refuses <c>Database.ExecuteSql</c> and its raw and interpolated forms on module contexts. Those commands compose no
/// filter. Migrations, LINQ queries, the adapter's direct SQL and bulk writes use other command sources.
/// </summary>
internal sealed class ModuleCommandGuard : DbCommandInterceptor
{
    internal static readonly ModuleCommandGuard Instance = new();

    public override InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventData.CommandSource == CommandSource.ExecuteSqlRaw)
            throw new PersistenceContractException("MPS034", "Database.ExecuteSql is refused on module contexts. Use ModuleSql, whose commands keep every filter.");
        return result;
    }
}
