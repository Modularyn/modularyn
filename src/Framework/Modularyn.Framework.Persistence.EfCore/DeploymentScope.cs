namespace Modularyn.Framework.Persistence.EfCore;

/// <summary>
/// An explicit, named deployment scope for a unit of work that changes host-scoped data outside a host scope: framework
/// metadata, required system seeds and test fixtures, under deployment authority. A unit begun without a tenant or host
/// context, or this scope, can only read host-scoped data, so host scope is never applied implicitly. It names its
/// operation and reason so that every such write is deliberate and identifiable. It grants no authority over tenant data
/// and is not an authorization decision; product code outside the persistence adapter does not create one.
/// </summary>
public sealed class DeploymentScope
{
    public DeploymentScope(string operationId, string reason)
    {
        if (string.IsNullOrEmpty(operationId) || operationId.Length > 200
            || !operationId.All(character => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_'))
            throw new ArgumentException("A deployment operation identity is 1 to 200 letters, digits, '.', '-' or '_'.", nameof(operationId));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500 || reason != reason.Trim() || reason.Any(char.IsControl))
            throw new ArgumentException("A deployment reason is 1 to 500 characters with no surrounding whitespace or control characters.", nameof(reason));
        OperationId = operationId;
        Reason = reason;
    }

    public string OperationId { get; }
    public string Reason { get; }
}
