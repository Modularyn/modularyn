using Xunit;

namespace Modularyn.Framework.Persistence.EfCore.Tests;

// The coordinator lock covers the database, so ordinary fixtures serialize; contention tests opt in explicitly.
[CollectionDefinition(PostgreSqlCollection.Name, DisableParallelization = true)]
public sealed class PostgreSqlCollection
{
    public const string Name = "PostgreSQL persistence";
}
