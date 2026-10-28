using System.Collections.Frozen;

namespace Modularyn.Framework.Tenancy.Abstractions;

/// <summary>
/// One entity a host operation may reach: a table of one module's own schema, named by the module's identity and the
/// table's name as that module's model maps it. Names compare ordinally.
/// </summary>
public sealed record HostReach
{
    public const int MaxNameLength = 200;

    public HostReach(string moduleId, string table)
    {
        ModuleId = Name(moduleId, nameof(moduleId));
        Table = Name(table, nameof(table));
    }

    public string ModuleId { get; }
    public string Table { get; }

    private static string Name(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > MaxNameLength || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException("A reach names a module and a table in at most 200 characters, with no surrounding whitespace or control characters.", parameter);
        return value;
    }
}

/// <summary>
/// A host operation as its owning module declares it: its identity and every entity it may reach. A host scope granted
/// for the operation reads tenant data across tenants, changes host-scoped rows and records a named tenant's disposition
/// only in those entities; the persistence adapter refuses every other entity. An operation that only authorizes a use
/// case, without reaching data in host scope, declares no entity.
/// </summary>
public sealed class HostOperation
{
    public HostOperation(string id, IEnumerable<HostReach> reach)
    {
        if (!PropagatedTenantContext.ValidOperation(id))
            throw new ArgumentException("A host operation identity is 1 to 200 letters, digits, '.', '-' or '_'.", nameof(id));
        ArgumentNullException.ThrowIfNull(reach);
        var copy = reach.ToArray();
        if (copy.Any(entity => entity is null)) throw new ArgumentException("A reach lists entities, never null.", nameof(reach));
        Id = id;
        Reach = copy.ToFrozenSet();
    }

    public string Id { get; }

    /// <summary>The entities the operation may reach in host scope, and nothing else.</summary>
    public IReadOnlySet<HostReach> Reach { get; }

    public bool Reaches(string moduleId, string table) =>
        moduleId is not null && table is not null && Reach.Any(entity => entity.ModuleId == moduleId && entity.Table == table);

    public override string ToString() => "Host operation " + Id;
}
