using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Modularyn.Framework.Security.Abstractions;

/// <summary>The kind of identity an execution runs as. Interactive users and background services are never interchangeable (ARC-006, FND-017).</summary>
public enum IdentityKind
{
    User = 1,
    Service = 2
}

/// <summary>A principal's stable identifier and kind. Identifiers compare ordinally; they identify, they never authorize.</summary>
public sealed record PrincipalReference
{
    public const int MaximumIdLength = 200;

    public PrincipalReference(string id, IdentityKind kind)
    {
        if (!IsValidId(id))
            throw new ArgumentException($"A principal identifier is 1 to {MaximumIdLength} printable ASCII characters without spaces.", nameof(id));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), "The identity kind is not defined.");
        Id = id;
        Kind = kind;
    }

    public string Id { get; }
    public IdentityKind Kind { get; }

    public static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= MaximumIdLength } && id.All(character => character is >= '!' and <= '~');
}

/// <summary>
/// Stable permission identifiers as delegations and service identities name them: lowercase ASCII segments such as
/// <c>item.read</c>. Wildcards and patterns are never permission names, so a scope can only list exact permissions.
/// </summary>
public static partial class PermissionNames
{
    public const int MaximumLength = 128;

    public static bool IsValid(string? name) => name is { Length: > 0 and <= MaximumLength } && Grammar().IsMatch(name);

    /// <summary>Copies a scope into an immutable ordinal set, refusing an empty scope and any name that is not a permission identifier.</summary>
    public static IReadOnlySet<string> ToScope(IEnumerable<string> names, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(names);
        var copy = names.ToArray();
        if (copy.Length == 0) throw new ArgumentException("A scope lists at least one permission.", parameterName);
        if (copy.Any(name => !IsValid(name)))
            throw new ArgumentException("A scope lists only exact permission identifiers, such as item.read, never wildcards or patterns.", parameterName);
        return copy.ToFrozenSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Grammar();
}

/// <summary>
/// The authority a delegation grant carries into an execution: what it covers, why, in which tenant and until when.
/// A delegation never widens the subject's own authority; it only narrows what the actor may do as that subject.
/// </summary>
public sealed class DelegatedAuthority
{
    public const int MaximumPurposeLength = 500;

    public DelegatedAuthority(Guid grantId, IEnumerable<string> scope, string purpose, Guid? tenant, DateTimeOffset notBefore, DateTimeOffset expiresAt)
    {
        if (grantId == Guid.Empty) throw new ArgumentException("A delegation grant has an identifier.", nameof(grantId));
        Scope = PermissionNames.ToScope(scope, nameof(scope));
        if (!IsValidPurpose(purpose))
            throw new ArgumentException($"A delegation records its purpose in 1 to {MaximumPurposeLength} characters without control characters.", nameof(purpose));
        if (tenant == Guid.Empty) throw new ArgumentException("A delegation's tenant is a tenant identifier, never the empty one.", nameof(tenant));
        if (expiresAt <= notBefore) throw new ArgumentException("A delegation expires after it begins.", nameof(expiresAt));
        GrantId = grantId;
        Purpose = purpose;
        Tenant = tenant;
        NotBefore = notBefore;
        ExpiresAt = expiresAt;
    }

    public Guid GrantId { get; }
    public IReadOnlySet<string> Scope { get; }
    public string Purpose { get; }

    /// <summary>The only tenant the delegation applies in, or null for an execution outside any tenant.</summary>
    public Guid? Tenant { get; }

    public DateTimeOffset NotBefore { get; }
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>True only inside the validity window, for a listed permission and the delegation's own tenant.</summary>
    public bool Covers(string permission, Guid? tenant, DateTimeOffset now) =>
        now >= NotBefore && now < ExpiresAt && Scope.Contains(permission) && Tenant == tenant;

    public static bool IsValidPurpose(string? purpose) =>
        !string.IsNullOrWhiteSpace(purpose) && purpose.Length <= MaximumPurposeLength && !purpose.Any(char.IsControl);
}

/// <summary>
/// Who an execution acts as. The actor is the principal performing the work; the effective subject is the principal
/// whose authority applies. They differ only under delegation. Each carries its identity kind, so an interactive user,
/// a delegated actor and a background service are always distinguishable (TM-G02-018).
/// </summary>
/// <remarks>
/// Principals are established by the identity module from a validated session, a resolved delegation grant or a declared
/// service identity. Module code is trusted, as the threat model assumes, not to construct principals itself.
/// </remarks>
public sealed class ExecutionPrincipal
{
    private ExecutionPrincipal(PrincipalReference actor, PrincipalReference subject, Guid? sessionId, DateTimeOffset? authenticatedAt,
        DelegatedAuthority? delegation, IReadOnlySet<string>? serviceScope)
    {
        Actor = actor;
        EffectiveSubject = subject;
        SessionId = sessionId;
        AuthenticatedAt = authenticatedAt;
        Delegation = delegation;
        ServiceScope = serviceScope;
    }

    public PrincipalReference Actor { get; }
    public PrincipalReference EffectiveSubject { get; }

    /// <summary>The server-side session an interactive execution belongs to. This identifies the session record; it is never the session secret.</summary>
    public Guid? SessionId { get; }

    public DateTimeOffset? AuthenticatedAt { get; }
    public DelegatedAuthority? Delegation { get; }

    /// <summary>A service identity's declared permissions, and the most it may ever exercise. Null for users.</summary>
    public IReadOnlySet<string>? ServiceScope { get; }

    public bool IsDelegated => Delegation is not null;

    /// <summary>An interactive user acting as themselves through a validated session.</summary>
    public static ExecutionPrincipal ForUser(PrincipalReference user, Guid sessionId, DateTimeOffset authenticatedAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Kind != IdentityKind.User) throw new ArgumentException("An interactive principal is a user.", nameof(user));
        if (sessionId == Guid.Empty) throw new ArgumentException("An interactive principal belongs to a session.", nameof(sessionId));
        return new(user, user, sessionId, authenticatedAt, null, null);
    }

    /// <summary>A user acting for another user under a resolved delegation grant, through the actor's own session.</summary>
    public static ExecutionPrincipal ForDelegation(PrincipalReference actor, Guid actorSessionId, DateTimeOffset authenticatedAt,
        PrincipalReference subject, DelegatedAuthority delegation)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(delegation);
        if (actor.Kind != IdentityKind.User || subject.Kind != IdentityKind.User)
            throw new ArgumentException("Delegation is between users; a service identity runs under its own declared scope.", nameof(actor));
        if (actor == subject) throw new ArgumentException("A delegation's actor and subject are different principals.", nameof(subject));
        if (actorSessionId == Guid.Empty) throw new ArgumentException("A delegated execution belongs to the actor's session.", nameof(actorSessionId));
        return new(actor, subject, actorSessionId, authenticatedAt, delegation, null);
    }

    /// <summary>A background service acting as itself under its declared scope, never as a user or an administrator.</summary>
    public static ExecutionPrincipal ForService(PrincipalReference service, IEnumerable<string> scope)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (service.Kind != IdentityKind.Service) throw new ArgumentException("A service principal is a service identity.", nameof(service));
        return new(service, service, null, null, null, PermissionNames.ToScope(scope, nameof(scope)));
    }

    /// <summary>
    /// Whether the permission lies within the most this execution may exercise. This is a ceiling, never a grant:
    /// authorization still checks the effective subject's own grants. An undelegated user has no ceiling beyond those
    /// grants; a delegation covers only its scope, tenant and validity window; a service identity only its declared scope.
    /// </summary>
    public bool IsWithinCeiling(string permission, Guid? tenant, DateTimeOffset now)
    {
        if (!PermissionNames.IsValid(permission)) return false;
        if (Delegation is not null) return Delegation.Covers(permission, tenant, now);
        if (ServiceScope is not null) return ServiceScope.Contains(permission);
        return Actor.Kind == IdentityKind.User;
    }

    public override string ToString() => IsDelegated
        ? $"{Actor.Kind} {Actor.Id} acting for {EffectiveSubject.Kind} {EffectiveSubject.Id}"
        : $"{Actor.Kind} {Actor.Id}";
}
