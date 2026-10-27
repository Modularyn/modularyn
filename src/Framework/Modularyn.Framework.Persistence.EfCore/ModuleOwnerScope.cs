namespace Modularyn.Framework.Persistence.EfCore;

/// <summary>
/// The explicit binding under which a module changes its own host-scoped data through its own context, such as the
/// records identity keeps about global accounts and their sessions. It names the module's context type and an operation,
/// so every such unit is deliberate and identifiable, and <see cref="EfCoreUnitOfWork.BeginOwnedAsync"/> accepts it only
/// for the module whose context type it names. Ownership already confines that context to the module's own schema and
/// tables.
/// </summary>
/// <remarks>
/// It is not host scope. It grants no tenant access: a unit bound to it refuses every tenant-scoped read and write, and
/// cross-tenant reads and named-tenant updates still require an active, audited host scope. It is not an authorization
/// decision either: who may invoke the use case that opens it belongs to authorization. Only the owning module's
/// application and persistence projects open one; the repository's boundary test enforces that.
/// </remarks>
public sealed class ModuleOwnerScope
{
    private ModuleOwnerScope(Type contextType, string operationId)
    {
        if (string.IsNullOrEmpty(operationId) || operationId.Length > 200
            || !operationId.All(character => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_'))
            throw new ArgumentException("An owner operation identity is 1 to 200 letters, digits, '.', '-' or '_'.", nameof(operationId));
        ContextType = contextType;
        OperationId = operationId;
    }

    /// <summary>The scope for writes through <typeparamref name="TContext"/>, the owning module's context type.</summary>
    public static ModuleOwnerScope For<TContext>(string operationId) where TContext : ModuleDbContext => new(typeof(TContext), operationId);

    public Type ContextType { get; }
    public string OperationId { get; }
}
