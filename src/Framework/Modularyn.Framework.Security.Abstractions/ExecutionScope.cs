namespace Modularyn.Framework.Security.Abstractions;

/// <summary>
/// Establishes the principal for one unit of work outside an HTTP request, such as a job or a message handler. The
/// principal flows with the asynchronous execution context and ends when the returned handle is disposed, so a reused
/// worker carries nothing into its next unit of work. Dispose the handle in the execution context that created it.
/// </summary>
public static class ExecutionScope
{
    private static readonly AsyncLocal<Frame?> Ambient = new();

    public static IDisposable Begin(ExecutionPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var frame = new Frame(principal, Ambient.Value);
        Ambient.Value = frame;
        return frame;
    }

    /// <summary>The innermost undisposed principal. A disposed frame is skipped even when disposal was out of order.</summary>
    internal static ExecutionPrincipal? Current
    {
        get
        {
            var frame = Ambient.Value;
            while (frame is { Disposed: true }) frame = frame.Parent;
            return frame?.Principal;
        }
    }

    private sealed class Frame(ExecutionPrincipal principal, Frame? parent) : IDisposable
    {
        internal ExecutionPrincipal Principal { get; } = principal;
        internal Frame? Parent { get; } = parent;
        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (!ReferenceEquals(Ambient.Value, this)) return;
            var restored = Parent;
            while (restored is { Disposed: true }) restored = restored.Parent;
            Ambient.Value = restored;
        }
    }
}

/// <summary>Reads the principal of the current unit of work. Null means no principal: consumers deny by default.</summary>
public interface IExecutionPrincipalAccessor
{
    ExecutionPrincipal? Current { get; }
}

/// <summary>The principal established through <see cref="ExecutionScope"/>. HTTP hosts register an accessor that also reads the request's session.</summary>
public sealed class AmbientExecutionPrincipalAccessor : IExecutionPrincipalAccessor
{
    public ExecutionPrincipal? Current => ExecutionScope.Current;
}

/// <summary>
/// Runs background work under a declared service identity. The identity module implements it; jobs and message
/// handlers use it instead of reusing a captured user credential or an administrator's identity (TM-G02-018, TM-G02-041).
/// </summary>
public interface IServiceIdentityScopes
{
    /// <summary>
    /// Begins work as the named declared service identity, with exactly its declared scope. The scope replaces any ambient
    /// principal for its duration and carries none of that principal's authority. An undeclared name is refused.
    /// </summary>
    ServiceIdentityScope Begin(string serviceIdentity);
}

/// <summary>A unit of work running as a service identity. Disposing it restores the previous principal.</summary>
public sealed class ServiceIdentityScope : IDisposable
{
    private readonly IDisposable frame;

    public ServiceIdentityScope(ExecutionPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.ServiceScope is null) throw new ArgumentException("A service identity scope runs a service principal.", nameof(principal));
        Principal = principal;
        frame = ExecutionScope.Begin(principal);
    }

    public ExecutionPrincipal Principal { get; }

    public void Dispose() => frame.Dispose();
}

public enum FreshnessStatus
{
    /// <summary>The session, delegation and subject behind the principal are still current.</summary>
    Current = 1,

    /// <summary>The session has ended, expired or no longer matches its account's security state.</summary>
    SessionEnded = 2,

    /// <summary>The delegation was revoked, expired or no longer resolves to its actor and subject.</summary>
    DelegationEnded = 3,

    /// <summary>The effective subject or service identity no longer exists or is disabled.</summary>
    SubjectUnavailable = 4
}

/// <summary>
/// Rechecks a principal against the authoritative store for a security-sensitive operation, instead of trusting state
/// captured when the request began (TM-G02-017, FND-017). Anything other than <see cref="FreshnessStatus.Current"/> refuses the operation.
/// </summary>
public interface IPrincipalFreshness
{
    ValueTask<FreshnessStatus> RecheckAsync(ExecutionPrincipal principal, CancellationToken cancellationToken);
}
