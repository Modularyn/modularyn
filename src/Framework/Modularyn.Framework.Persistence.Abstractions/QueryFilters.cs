using System.Linq.Expressions;

namespace Modularyn.Framework.Persistence.Abstractions;

public interface ISoftDeleted
{
    bool IsDeleted { get; set; }
}

/// <summary>All supported query paths compose the named predicates registered through this seam.</summary>
public interface IQueryFilterRegistry
{
    void Add<TEntity>(string name, Expression<Func<TEntity, bool>> predicate) where TEntity : class;
}

/// <summary>
/// Carries an explicit reason and operation identity for a query-local soft-delete bypass.
/// This is an audit integration value, not an authorization grant or an audit implementation.
/// </summary>
public sealed record SoftDeleteBypass
{
    public SoftDeleteBypass(string reason, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        Reason = reason;
        OperationId = operationId;
    }

    public string Reason { get; }
    public string OperationId { get; }
}
