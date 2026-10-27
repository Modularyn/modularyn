using System.Collections.Frozen;
using Modularyn.Framework.Security.Abstractions;

namespace Modularyn.Modules.Identity.Domain;

/// <summary>A background service's own identity and the exact permissions it may exercise (ARC-006, TM-G02-018).</summary>
public sealed class ServiceIdentityDeclaration
{
    public ServiceIdentityDeclaration(string name, IEnumerable<string> scope)
    {
        if (!PermissionNames.IsValid(name))
            throw new ArgumentException("A service identity name is a lowercase identifier such as svc-items.", nameof(name));
        Name = name;
        Scope = PermissionNames.ToScope(scope, nameof(scope));
    }

    public string Name { get; }
    public IReadOnlySet<string> Scope { get; }

    public PrincipalReference Principal => new(Name, IdentityKind.Service);
}

/// <summary>The complete set of declared service identities. Names are unique; an undeclared name never resolves.</summary>
public sealed class ServiceIdentityRegistry
{
    private readonly FrozenDictionary<string, ServiceIdentityDeclaration> declarations;

    public ServiceIdentityRegistry(IEnumerable<ServiceIdentityDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var copy = declarations.ToArray();
        if (copy.Any(declaration => declaration is null)) throw new ArgumentException("Declarations cannot contain null entries.", nameof(declarations));
        var duplicate = copy.GroupBy(declaration => declaration.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Service identity '{duplicate.Key}' is declared more than once.", nameof(declarations));
        this.declarations = copy.ToFrozenDictionary(declaration => declaration.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Names => declarations.Keys;

    public ServiceIdentityDeclaration? Find(string? name) => name is not null && declarations.TryGetValue(name, out var declaration) ? declaration : null;
}
