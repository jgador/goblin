using System;
using System.IO;
using System.Threading.Tasks;
using Goblin.Database;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Goblin.Persistence.Tests;

// Opt-in integration tests create and drop their own database. They never run
// merely because the application has a configured database connection.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_APP")))
            Skip = "Set GOBLIN_TEST_POSTGRES_ADMIN and GOBLIN_TEST_POSTGRES_APP to run PostgreSQL integration tests.";
    }
}

public sealed class PostgresTests
{
    [PostgresFact]
    public async Task WorkItemRoundTripUsesTheApplicationRoleAndSeparateContexts()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        var id = Guid.NewGuid();
        const string objective = "Persist café 🧌 and 'quoted' text";
        await using (GoblinDbContext write = database.Context())
        {
            write.WorkItems.Add(new WorkItem { Id = id, Objective = objective });
            await write.SaveChangesAsync();
        }
        await using (GoblinDbContext read = database.Context())
        {
            WorkItem item = await read.WorkItems.SingleAsync(x => x.Id == id);
            Assert.Equal(objective, item.Objective);
            item.Objective = "Updated objective";
            await read.SaveChangesAsync();
        }
        await using (GoblinDbContext delete = database.Context())
        {
            Assert.Equal("Updated objective", (await delete.WorkItems.SingleAsync()).Objective);
            await delete.WorkItems.ExecuteDeleteAsync();
        }
        await using GoblinDbContext empty = database.Context();
        Assert.False(await empty.WorkItems.AnyAsync());
    }

    [PostgresFact]
    public async Task ApplicationRoleCannotChangeTheSchemaOrMigrationJournal()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        await using var connection = new NpgsqlConnection(database.AppConnection);
        await connection.OpenAsync();
        foreach (string sql in new[]
        {
            "CREATE TABLE goblin.forbidden (id integer);",
            "DROP TABLE goblin.work_items;",
            "DELETE FROM goblin_meta.schema_migrations;"
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [PostgresFact]
    public async Task MigrationsAreRepeatableDetectEditsAndRollBackFailedDdl()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        string directory = Path.Combine(Path.GetTempPath(), "goblin-migrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string file in Directory.GetFiles(TestDatabase.Migrations, "*.sql"))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            await SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null);
            string original = Path.Combine(directory, "0001_work_items.sql");
            await File.AppendAllTextAsync(original, "\n-- changed after applying\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null));
            File.Copy(Path.Combine(TestDatabase.Migrations, "0001_work_items.sql"), original, overwrite: true);

            string second = Path.Combine(directory, "9999_transaction_check.sql");
            await File.WriteAllTextAsync(second, "CREATE TABLE goblin.transaction_check (id integer); SELECT 1 / 0;");
            await Assert.ThrowsAsync<PostgresException>(() => SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null));
            // The same CREATE succeeds only if the failed migration rolled back
            // both its table and its journal entry.
            await File.WriteAllTextAsync(second, "CREATE TABLE goblin.transaction_check (id integer);");
            await SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null);
            await SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class TestDatabase(string adminConnection, string appConnection, string name) : IAsyncDisposable
    {
        public static string Migrations => Path.Combine(AppContext.BaseDirectory, "migrations");
        public string AdminConnection { get; } = adminConnection;
        public string AppConnection { get; } = appConnection;

        public GoblinDbContext Context() => new(new DbContextOptionsBuilder<GoblinDbContext>().UseNpgsql(AppConnection).Options);

        public static async Task<TestDatabase> CreateAsync()
        {
            var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN"));
            var app = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_APP"));
            string name = "goblin_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name};", connection);
            await create.ExecuteNonQueryAsync();
            admin.Database = name;
            app.Database = name;
            var database = new TestDatabase(admin.ConnectionString, app.ConnectionString, name);
            try
            {
                await using var owner = new NpgsqlConnection(database.AdminConnection);
                await owner.OpenAsync();
                await using var schema = new NpgsqlCommand("""
                    CREATE SCHEMA goblin;
                    GRANT USAGE ON SCHEMA goblin TO goblin_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA goblin GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO goblin_app;
                    """, owner);
                await schema.ExecuteNonQueryAsync();
                await SqlMigrations.ApplyAsync(database.AdminConnection, Migrations, TextWriter.Null);
                return database;
            }
            catch { await database.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN"));
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE);", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
