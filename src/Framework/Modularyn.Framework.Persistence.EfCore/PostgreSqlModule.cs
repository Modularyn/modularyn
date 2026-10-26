using Modularyn.Framework.Persistence.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

public sealed class PostgreSqlPersistenceOptions
{
    public PostgreSqlPersistenceOptions(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }
}

/// <summary>One context type owns one schema, its EF history and its coordinator progress.</summary>
public sealed class PostgreSqlModule
{
    public const string HistoryTable = "__EFMigrationsHistory";
    public const string ProgressTable = "__ModularynMigrationProgress";
    public const string OwnershipTable = "__ModularynModule";
    private readonly Func<PostgreSqlModule, ModuleDbContext> factory;

    private PostgreSqlModule(string moduleId, string schema, PostgreSqlPersistenceOptions options,
        Type contextType, Func<PostgreSqlModule, ModuleDbContext> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        if (moduleId != moduleId.Trim() || moduleId.Any(char.IsControl))
            throw new ArgumentException("Module identities must be trimmed and contain no control characters.", nameof(moduleId));
        if (string.IsNullOrEmpty(schema) || schema.Length > 63 || schema[0] is < 'a' or > 'z'
            || schema.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            || schema is "public" or "information_schema" || schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("Use a module-owned lowercase PostgreSQL schema of at most 63 ASCII characters.", nameof(schema));
        ModuleId = moduleId;
        Schema = schema;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ContextType = contextType;
        this.factory = factory;
    }

    public string ModuleId { get; }
    public string Schema { get; }
    public Type ContextType { get; }
    internal PostgreSqlPersistenceOptions Options { get; }
    internal string ContextIdentity => ContextType.Assembly.GetName().Name + ":" + ContextType.FullName;

    public static PostgreSqlModule Create<TContext>(string moduleId, string schema, PostgreSqlPersistenceOptions options,
        Func<PostgreSqlModule, TContext> factory) where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(moduleId, schema, options, typeof(TContext), module => factory(module));
    }

    public ModuleDbContext CreateContext()
    {
        var context = factory(this);
        if (context is null || context.GetType() != ContextType || !ReferenceEquals(context.Module, this))
        {
            context?.Dispose();
            throw new PersistenceContractException("MPS001", "The context factory must return its registered module and context type.");
        }
        return context;
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    internal string Table(string name) => Quote(Schema) + "." + Quote(name);
}

public sealed class PostgreSqlModuleRegistry
{
    public PostgreSqlModuleRegistry(IEnumerable<PostgreSqlModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var copy = modules.ToArray();
        if (copy.Any(module => module is null)
            || copy.Select(module => module.ModuleId).Distinct(StringComparer.Ordinal).Count() != copy.Length
            || copy.Select(module => module.Schema).Distinct(StringComparer.Ordinal).Count() != copy.Length
            || copy.Select(module => module.ContextType).Distinct().Count() != copy.Length)
            throw new PersistenceContractException("MPS002", "Register each module, context type and schema with exactly one owner.");
        if (copy.Select(module => module.Options.ConnectionString).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new PersistenceContractException("MPS003", "A migration registry coordinates exactly one configured database.");
        Modules = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<PostgreSqlModule> Modules { get; }
}
