namespace Modularyn.Framework.Modularity.Abstractions;

public sealed record ModuleDependency
{
    public ModuleDependency(string moduleId, VersionRange versions, bool optional = false)
    {
        ModuleId = DeclarationGuard.Name(moduleId);
        Versions = versions ?? throw new ArgumentNullException(nameof(versions));
        Optional = optional;
    }
    public string ModuleId { get; }
    public VersionRange Versions { get; }
    public bool Optional { get; }
}

/// <summary>A versioned contract, capability or provider offered by a module.</summary>
public sealed record ModuleProvision
{
    public ModuleProvision(string name, SemanticVersion version, bool exclusive = false)
    {
        Name = DeclarationGuard.Name(name);
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Exclusive = exclusive;
    }
    public string Name { get; }
    public SemanticVersion Version { get; }
    public bool Exclusive { get; }
}

public sealed record ModuleRequirement
{
    public ModuleRequirement(string name, VersionRange versions, bool optional = false)
    {
        Name = DeclarationGuard.Name(name);
        Versions = versions ?? throw new ArgumentNullException(nameof(versions));
        Optional = optional;
    }
    public string Name { get; }
    public VersionRange Versions { get; }
    public bool Optional { get; }
}

/// <summary>Ownership metadata only; composition never executes the declared asset.</summary>
public sealed record ModuleAsset
{
    public ModuleAsset(string name, SemanticVersion version)
    {
        Name = DeclarationGuard.Name(name);
        Version = version ?? throw new ArgumentNullException(nameof(version));
    }
    public string Name { get; }
    public SemanticVersion Version { get; }
}

/// <summary>A route declaration. The method '*' reserves the route for all methods.</summary>
public sealed record ModuleRoute
{
    public ModuleRoute(string method, string template)
    {
        Method = DeclarationGuard.Name(method).ToUpperInvariant();
        if (Method != "*" && !Method.All(char.IsAsciiLetter))
            throw new ArgumentException("Use an HTTP method or '*'.", nameof(method));
        Template = DeclarationGuard.Name(template);
        if (!Template.StartsWith('/') || Template.Contains('#'))
            throw new ArgumentException("Use a route template beginning with '/'.", nameof(template));
    }
    public string Method { get; }
    public string Template { get; }
}

internal static class DeclarationGuard
{
    internal static string Name(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("Names must have no surrounding whitespace or control characters.", nameof(value));
        return value;
    }

    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values) where T : class
    {
        var copy = values?.ToArray() ?? [];
        if (copy.Any(value => value is null)) throw new ArgumentException("Declarations cannot contain null entries.", nameof(values));
        return Array.AsReadOnly(copy);
    }
}
