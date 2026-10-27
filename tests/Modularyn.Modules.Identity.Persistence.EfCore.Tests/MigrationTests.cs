using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Modules.Identity.Testing;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.Persistence.EfCore.Tests;

[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class MigrationTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "TM-G02-004 the hand-written migration creates exactly the mapped tables and columns, in the module's own schema")]
    public async Task TM_G02_004_The_migration_creates_exactly_the_mapped_columns_in_the_module_schema()
    {
        await using var database = await IdentityDatabase.CreateAsync(migrate: false);
        Assert.Equal(MigrationActivationValidator.Incomplete, Assert.Single(await database.ValidateActivationAsync()).Code);

        await database.MigrateAsync();
        Assert.Empty(await database.ValidateActivationAsync());
        var again = await database.ApplyAsync();
        Assert.Empty(again.Completed);
        Assert.Empty(again.Diagnostics);

        var expected = new SortedSet<string>(StringComparer.Ordinal);
        await using (var context = database.Module.CreateContext())
        {
            foreach (var entity in context.Model.GetEntityTypes())
            {
                var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
                Assert.Equal(database.Schema, entity.GetSchema());
                foreach (var property in entity.GetProperties())
                    expected.Add($"{table.Name}.{property.GetColumnName(table)} {property.GetColumnType()} {(property.IsColumnNullable(table) ? "null" : "not null")}");
            }
        }
        var actual = new SortedSet<string>(StringComparer.Ordinal);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(Cancellation);
            await using var command = new NpgsqlCommand("SELECT table_name, column_name, data_type, character_maximum_length, is_nullable FROM information_schema.columns"
                + " WHERE table_schema = @schema AND table_name NOT LIKE '\\_\\_%'", connection);
            command.Parameters.AddWithValue("schema", database.Schema);
            await using var reader = await command.ExecuteReaderAsync(Cancellation);
            while (await reader.ReadAsync(Cancellation))
            {
                var type = reader.GetString(2) == "character varying" ? $"character varying({reader.GetInt32(3)})" : reader.GetString(2);
                actual.Add($"{reader.GetString(0)}.{reader.GetString(1)} {type} {(reader.GetString(4) == "YES" ? "null" : "not null")}");
            }
        }
        Assert.Equal(expected, actual);
        output.WriteLine($"{actual.Count} columns in {actual.Select(column => column[..column.IndexOf('.')]).Distinct().Count()} tables match the context's mapping.");

        // The account table has no column for e-mail, telephone, two-factor, lockout, tokens, authenticator keys or recovery codes.
        Assert.DoesNotContain(actual, column => column.StartsWith("Accounts.", StringComparison.Ordinal)
            && new[] { "Email", "Phone", "TwoFactor", "Lockout", "AccessFailed" }.Any(name => column.Contains(name, StringComparison.Ordinal)));
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name LIKE 'AspNet%'",
            new NpgsqlParameter("schema", database.Schema)));
    }
}
