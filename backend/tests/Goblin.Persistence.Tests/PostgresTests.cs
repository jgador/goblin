using System;
using System.IO;
using System.Threading.Tasks;
using Goblin.Database;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        await using (GoblinDbContext write = await database.ContextFactory.CreateDbContextAsync())
        {
            write.WorkItems.Add(new WorkItem { Id = id, Objective = objective });
            await write.SaveChangesAsync();
        }
        await using (GoblinDbContext read = await database.ContextFactory.CreateDbContextAsync())
        {
            WorkItem item = await read.WorkItems.SingleAsync(x => x.Id == id);
            Assert.Equal(objective, item.Objective);
            item.Objective = "Updated objective";
            await read.SaveChangesAsync();
        }
        await using (GoblinDbContext delete = await database.ContextFactory.CreateDbContextAsync())
        {
            Assert.Equal("Updated objective", (await delete.WorkItems.SingleAsync()).Objective);
            await delete.WorkItems.ExecuteDeleteAsync();
        }
        await using GoblinDbContext empty = await database.ContextFactory.CreateDbContextAsync();
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
            "CREATE TABLE public.forbidden (id integer);",
            "DROP TABLE public.work_items;",
            "DROP TABLE public.wolverine_incoming_envelopes;",
            "SELECT * FROM public.schema_migrations;",
            "DELETE FROM public.schema_migrations;"
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
            string original = Path.Combine(directory, "0001_initial.sql");
            await File.AppendAllTextAsync(original, "\n-- changed after applying\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null));
            File.Copy(Path.Combine(TestDatabase.Migrations, "0001_initial.sql"), original, overwrite: true);

            string second = Path.Combine(directory, "9999_transaction_check.sql");
            await File.WriteAllTextAsync(second, "CREATE TABLE public.transaction_check (id integer); SELECT 1 / 0;");
            await Assert.ThrowsAsync<PostgresException>(() => SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null));
            // The same CREATE succeeds only if the failed migration rolled back
            // both its table and its journal entry.
            await File.WriteAllTextAsync(second, "CREATE TABLE public.transaction_check (id integer);");
            await SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null);
            await SqlMigrations.ApplyAsync(database.AdminConnection, directory, TextWriter.Null);
            // Future tables in public retain application DML privileges.
            await using var app = new NpgsqlConnection(database.AppConnection);
            await app.OpenAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO public.transaction_check VALUES (42) RETURNING id;", app);
            Assert.Equal(42, await insert.ExecuteScalarAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [PostgresFact]
    public async Task InitialSchemaSupportsMessagingAndEnforcesCleanupOwnership()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        await using var admin = new NpgsqlConnection(database.AdminConnection);
        await admin.OpenAsync();
        await using (var schemas = new NpgsqlCommand("SELECT array_agg(DISTINCT schemaname::text) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema');", admin))
            Assert.Equal(new[] { "public" }, (string[])(await schemas.ExecuteScalarAsync())!);

        await using var app = new NpgsqlConnection(database.AppConnection);
        await app.OpenAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO public.work_items (id, objective)
                VALUES ('10000000-0000-0000-0000-000000000001', 'Finish cleanup');
            INSERT INTO public.execution_attempts
                (id, work_id, agent_id, connection_id, runtime, status, queued_at, updated_at, cleanup_pending)
                VALUES ('20000000-0000-0000-0000-000000000001',
                    '10000000-0000-0000-0000-000000000001',
                    '00000000-0000-0000-0000-000000000001',
                    '00000000-0000-0000-0000-000000000001', 'codex', 'Succeeded', now(), now(), true);
            INSERT INTO public.wolverine_incoming_envelopes (id, status, owner_id, body, message_type)
                VALUES ('30000000-0000-0000-0000-000000000001', 'Incoming', 0, decode('010203', 'hex'), 'DispatchWork');
            """, app))
            await seed.ExecuteNonQueryAsync();

        await using GoblinDbContext db = await database.ContextFactory.CreateDbContextAsync();
        Assert.True((await db.ExecutionAttempts.SingleAsync()).CleanupPending);
        await using (var message = new NpgsqlCommand("SELECT body FROM public.wolverine_incoming_envelopes WHERE id = '30000000-0000-0000-0000-000000000001';", app))
            Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])(await message.ExecuteScalarAsync())!);
        await using (var sequence = new NpgsqlCommand("INSERT INTO public.wolverine_node_records (node_number, event_name) VALUES (1, 'Test event') RETURNING id;", app))
        {
            Assert.Equal(1, await sequence.ExecuteScalarAsync());
            Assert.Equal(2, await sequence.ExecuteScalarAsync());
        }
        await using (var duplicate = new NpgsqlCommand("""
            INSERT INTO public.execution_attempts
                (id, work_id, agent_id, connection_id, runtime, status, queued_at, updated_at)
            SELECT '20000000-0000-0000-0000-000000000002', work_id, agent_id,
                connection_id, runtime, 'Starting', queued_at, updated_at FROM public.execution_attempts;
            """, app))
        {
            PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, conflict.SqlState);
            Assert.Equal("one_execution_per_connection", conflict.ConstraintName);
        }
        await using (var orphan = new NpgsqlCommand("UPDATE public.execution_attempts SET work_id = '10000000-0000-0000-0000-000000000002';", app))
        {
            PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(() => orphan.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, conflict.SqlState);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _name;
        private readonly ServiceProvider _services;

        public TestDatabase(string adminConnection, string appConnection, string name)
        {
            _name = name;
            AdminConnection = adminConnection;
            AppConnection = appConnection;
            _services = new ServiceCollection().AddGoblinPersistence(appConnection).BuildServiceProvider(validateScopes: true);
        }

        public static string Migrations => Path.Combine(AppContext.BaseDirectory, "migrations");
        public string AdminConnection { get; }
        public string AppConnection { get; }

        public IDbContextFactory<GoblinDbContext> ContextFactory => _services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();

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
                    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
                    GRANT USAGE ON SCHEMA public TO goblin_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO goblin_app;
                    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO goblin_app;
                    """, owner);
                await schema.ExecuteNonQueryAsync();
                await SqlMigrations.ApplyAsync(database.AdminConnection, Migrations, TextWriter.Null);
                return database;
            }
            catch { await database.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            // Close this process's idle application sessions before dropping a
            // test database; the schema owner need not terminate other roles.
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("GOBLIN_TEST_POSTGRES_ADMIN"));
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {_name} WITH (FORCE);", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
