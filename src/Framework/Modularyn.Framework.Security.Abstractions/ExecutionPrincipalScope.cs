namespace Modularyn.Framework.Security.Abstractions;

/// <summary>
/// The principal of one unit of work, such as a request, a job or a message, each of which runs in its own service
/// scope. It is established once, cannot change afterwards, and ends when its unit ends. Nothing ambient carries it: work
/// sees the principal of the service scope it resolves its services from, so work that is not given a unit's services,
/// such as a task started without them or a new scope, has no principal, and a reused worker keeps nothing from its
/// previous unit.
/// </summary>
public sealed class ExecutionPrincipalScope : IExecutionPrincipalAccessor
{
    private readonly Lock gate = new();
    private ExecutionPrincipal? principal;
    private bool established;
    private bool ended;

    /// <summary>The established principal, or null before it is established and after the unit ends.</summary>
    public ExecutionPrincipal? Current
    {
        get { lock (gate) return ended ? null : principal; }
    }

    /// <summary>
    /// Establishes the unit's principal. False, with nothing changed, when a principal was already established or the
    /// unit has ended: a unit's principal is never replaced.
    /// </summary>
    public bool TryEstablish(ExecutionPrincipal value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            if (established || ended) return false;
            principal = value;
            established = true;
            return true;
        }
    }

    /// <summary>Ends the unit's principal for the rest of the unit, as after a sign-out. It cannot be established again.</summary>
    public void End()
    {
        lock (gate)
        {
            ended = true;
            principal = null;
        }
    }
}

/// <summary>
/// Reads the principal of the current unit of work from that unit's own service scope. Null means no principal:
/// consumers deny by default.
/// </summary>
public interface IExecutionPrincipalAccessor
{
    ExecutionPrincipal? Current { get; }
}

/// <summary>
/// Runs background work under a declared service identity. The identity module implements it; jobs and message
/// handlers use it instead of reusing a captured user credential or an administrator's identity (TM-G02-018, TM-G02-041).
/// </summary>
public interface IServiceIdentityScopes
{
    /// <summary>
    /// Begins a unit of work as the named declared service identity, with exactly its declared scope, in a new service
    /// scope of its own. Resolve the work's services from <see cref="ServiceIdentityScope.Services"/>: there the principal
    /// is the service identity, and nothing of the principal of the code that began it is visible. An undeclared name is
    /// refused.
    /// </summary>
    ServiceIdentityScope Begin(string serviceIdentity);
}

/// <summary>
/// A unit of work running as a service identity, with its own service scope. Disposing it ends the principal and the
/// scope; the principal of the code that began it is unaffected throughout.
/// </summary>
public sealed class ServiceIdentityScope : IAsyncDisposable
{
    private readonly IAsyncDisposable lifetime;
    private int disposed;

    public ServiceIdentityScope(ExecutionPrincipal principal, IServiceProvider services, IAsyncDisposable lifetime)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(lifetime);
        if (principal.ServiceScope is null) throw new ArgumentException("A service identity scope runs a service principal.", nameof(principal));
        Principal = principal;
        Services = services;
        this.lifetime = lifetime;
    }

    public ExecutionPrincipal Principal { get; }

    /// <summary>The unit's own services, whose <see cref="IExecutionPrincipalAccessor"/> returns <see cref="Principal"/>.</summary>
    public IServiceProvider Services { get; }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? lifetime.DisposeAsync() : ValueTask.CompletedTask;
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
