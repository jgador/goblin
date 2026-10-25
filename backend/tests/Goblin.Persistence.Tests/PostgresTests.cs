using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Goblin.Contracts;
using Goblin.Database;
using Goblin.Persistence;
using Goblin.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Persistence.Tests;

// Opt-in integration tests create and drop their own database. They never run
// merely because the application has a configured database connection.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresApp)))
            Skip = "Set GOBLIN_TEST_POSTGRES_ADMIN and GOBLIN_TEST_POSTGRES_APP to run PostgreSQL integration tests.";
    }
}

public sealed class PostgresTests
{
    internal static string ArtifactDirectory => TestDatabase.Artifacts;

    [PostgresFact]
    public async Task WorkItemRoundTripUsesTheApplicationRoleAndSeparateContexts()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        const long id = 2147483648L;
        const string objective = "Persist café 🧌 and 'quoted' text";
        await using (GoblinDbContext write = await database.ContextFactory.CreateDbContextAsync())
        {
            write.WorkItems.Add(new WorkItem { Id = id, Objective = objective, State = WorkState(id, objective) });
            await write.SaveChangesAsync();
        }
        await using (GoblinDbContext read = await database.ContextFactory.CreateDbContextAsync())
        {
            WorkItem item = await read.WorkItems.SingleAsync(x => x.Id == id);
            Assert.Equal(objective, item.Objective);
            Assert.Equal(id, JsonSerializer.Deserialize<Core.Work.WorkSnapshot>(item.State, ContractJson.Options)!.Id);
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
    public async Task WorkItemsRequirePersistedAggregateState()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        await using var connection = new NpgsqlConnection(database.AppConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("INSERT INTO public.work_item (objective) VALUES ('Missing state');", connection);
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
        Assert.Equal("state", error.ColumnName);
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
            "DROP TABLE public.work_item;",
            "DROP TABLE public.wolverine_incoming_envelopes;",
            "SELECT * FROM public.schema_migration;",
            "DELETE FROM public.schema_migration;",
            "SELECT nextval('public.schema_migration_id_seq');",
            "SELECT last_value FROM public.schema_migration_id_seq;"
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
        string directory = Path.Combine(TestDatabase.Artifacts, "migrations-" + Guid.NewGuid().ToString("N"));
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
        await using (var keys = new NpgsqlCommand("""
            SELECT count(*) FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_index i ON i.indrelid = c.oid AND i.indisprimary
            WHERE n.nspname = 'public' AND c.relname NOT LIKE 'wolverine_%'
              AND (i.indnatts <> 1 OR NOT EXISTS (
                SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid
                  AND a.attnum = ANY(i.indkey) AND a.attname = CASE WHEN c.relname LIKE 'OpenIddict%' THEN 'Id' ELSE 'id' END AND a.atttypid = 'int8'::regtype))
            """, admin))
            Assert.Equal(0L, await keys.ExecuteScalarAsync());
        await using (var schemas = new NpgsqlCommand("SELECT array_agg(DISTINCT schemaname::text) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema');", admin))
            Assert.Equal(new[] { "public" }, (string[])(await schemas.ExecuteScalarAsync())!);

        await using var app = new NpgsqlConnection(database.AppConnection);
        await app.OpenAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO public.work_item (id, objective, state)
                VALUES (2147483648, 'Finish cleanup', @state);
            INSERT INTO public.execution_attempt
                (id, work_id, agent_id, connection_id, runtime, status, queued_at, updated_at, cleanup_pending)
                VALUES (2147483648,
                    2147483648,
                    1,
                    1, 'codex', 'Succeeded', now(), now(), true);
            INSERT INTO public.wolverine_incoming_envelopes (id, status, owner_id, body, message_type)
                VALUES ('30000000-0000-0000-0000-000000000001', 'Incoming', 0, decode('010203', 'hex'), 'DispatchWork');
            """, app))
        {
            seed.Parameters.AddWithValue("state", NpgsqlTypes.NpgsqlDbType.Jsonb, WorkState(2147483648, "Finish cleanup"));
            await seed.ExecuteNonQueryAsync();
        }

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
            INSERT INTO public.execution_attempt
                (id, work_id, agent_id, connection_id, runtime, status, queued_at, updated_at)
            SELECT 2147483649, work_id, agent_id,
                connection_id, runtime, 'Starting', queued_at, updated_at FROM public.execution_attempt;
            """, app))
        {
            PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, conflict.SqlState);
            Assert.Equal("one_execution_per_connection", conflict.ConstraintName);
        }
        await using (var orphan = new NpgsqlCommand("UPDATE public.execution_attempt SET work_id = 2147483649;", app))
        {
            PostgresException conflict = await Assert.ThrowsAsync<PostgresException>(() => orphan.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, conflict.SqlState);
        }
    }

    [PostgresFact]
    public async Task FreshCatalogContainsExactlySingularGoblinAndNativeLibraryObjects()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        await using var admin = new NpgsqlConnection(database.AdminConnection);
        await admin.OpenAsync();
        string[] expected =
        [
            "OpenIddictApplications", "OpenIddictAuthorizations", "OpenIddictScopes", "OpenIddictTokens",
            "agent", "chatgpt_grant_event", "chatgpt_work_grant", "connection", "connection_model_catalog", "conversation", "conversation_message",
            "execution_attempt", "external_conversation", "external_identity", "external_link_request",
            "external_message", "github_connection", "github_repository", "repository_operation",
            "repository_setup_memory", "schema_migration", "wolverine_agent_restrictions",
            "wolverine_control_queue", "wolverine_dead_letters", "wolverine_incoming_envelopes",
            "wolverine_node_assignments", "wolverine_node_records", "wolverine_nodes",
            "wolverine_outgoing_envelopes", "work_command", "work_item", "workspace_checkpoint", "workspace_session"
        ];
        await using (var tables = new NpgsqlCommand("SELECT array_agg(tablename::text ORDER BY tablename) FROM pg_tables WHERE schemaname = 'public'", admin))
            Assert.Equal(expected, (string[])(await tables.ExecuteScalarAsync())!);
        // An exact catalog excludes every old table name and any added compatibility table.
        await using (var views = new NpgsqlCommand("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind IN ('v', 'm')", admin))
            Assert.Equal(0L, await views.ExecuteScalarAsync());
        foreach (string table in expected)
        {
            await using var keys = new NpgsqlCommand("""
                SELECT a.atttypid::regtype::text, con.conname
                FROM pg_constraint con JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY(con.conkey)
                WHERE con.conrelid = ('public.' || quote_ident(@table))::regclass AND con.contype = 'p'
                """, admin);
            keys.Parameters.AddWithValue("table", table);
            await using NpgsqlDataReader row = await keys.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            string type = table == "wolverine_node_records" ? "integer" : table == "wolverine_node_assignments" ? "character varying" : table.StartsWith("wolverine_", StringComparison.Ordinal) ? "uuid" : "bigint";
            Assert.Equal(type, row.GetString(0));
            Assert.Equal(table.StartsWith("OpenIddict", StringComparison.Ordinal) ? "PK_" + table : table.StartsWith("wolverine_", StringComparison.Ordinal) ? "pkey_" + table + "_id" : table + "_pkey", row.GetString(1));
            Assert.False(await row.ReadAsync());
        }
        foreach (string table in expected)
        {
            if (table == "github_repository" || table.StartsWith("wolverine_", StringComparison.Ordinal)) continue;
            await using var sequence = new NpgsqlCommand("SELECT pg_get_serial_sequence('public.' || quote_ident(@table), @column)", admin);
            sequence.Parameters.AddWithValue("table", table);
            sequence.Parameters.AddWithValue("column", table.StartsWith("OpenIddict", StringComparison.Ordinal) ? "Id" : "id");
            Assert.Equal(table.StartsWith("OpenIddict", StringComparison.Ordinal) ? "public.\"" + table + "_Id_seq\"" : "public." + table + "_id_seq", await sequence.ExecuteScalarAsync());
        }
        await using (var identities = new NpgsqlCommand("""
            SELECT count(*) FROM pg_attribute a JOIN pg_class t ON t.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = 'public' AND t.relkind = 'r' AND t.relname NOT LIKE 'wolverine_%'
              AND a.attname = 'id' AND a.attidentity <> CASE WHEN t.relname = 'github_repository' THEN '' ELSE 'd' END
            """, admin))
            Assert.Equal(0L, await identities.ExecuteScalarAsync());
        foreach ((string table, string column, string sequence) in new[]
        {
            ("wolverine_node_records", "id", "wolverine_node_records_id_seq"),
            ("wolverine_nodes", "node_number", "wolverine_nodes_node_number_seq")
        })
        {
            await using var owned = new NpgsqlCommand("SELECT pg_get_serial_sequence('public.' || @table, @column), pg_get_expr(adbin, adrelid), (SELECT seqtypid::regtype::text FROM pg_sequence WHERE seqrelid = pg_get_serial_sequence('public.' || @table, @column)::regclass) FROM pg_attrdef WHERE adrelid = ('public.' || quote_ident(@table))::regclass AND adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = ('public.' || quote_ident(@table))::regclass AND attname = @column)", admin);
            owned.Parameters.AddWithValue("table", table);
            owned.Parameters.AddWithValue("column", column);
            await using NpgsqlDataReader row = await owned.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal("public." + sequence, row.GetString(0));
            Assert.Contains(sequence, row.GetString(1), StringComparison.Ordinal);
            Assert.Equal("integer", row.GetString(2));
        }
        string[] expectedSequences = [.. expected
            .Where(table => table != "github_repository" && !table.StartsWith("wolverine_", StringComparison.Ordinal))
            .Select(table => table + (table.StartsWith("OpenIddict", StringComparison.Ordinal) ? "_Id_seq" : "_id_seq"))
            .Concat(["work_event_id_seq", "wolverine_node_records_id_seq", "wolverine_nodes_node_number_seq"])
            .Order(StringComparer.Ordinal)];
        await using (var sequences = new NpgsqlCommand("SELECT array_agg(sequencename::text ORDER BY sequencename) FROM pg_sequences WHERE schemaname = 'public'", admin))
            Assert.Equal(expectedSequences, (string[])(await sequences.ExecuteScalarAsync())!);
        await using (var independent = new NpgsqlCommand("""
            SELECT seqtypid::regtype::text, EXISTS (SELECT 1 FROM pg_depend WHERE objid = seqrelid AND deptype IN ('a', 'i'))
            FROM pg_sequence WHERE seqrelid = 'public.work_event_id_seq'::regclass
            """, admin))
        await using (NpgsqlDataReader row = await independent.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal("bigint", row.GetString(0));
            Assert.False(row.GetBoolean(1));
        }
        await using (var constraints = new NpgsqlCommand("SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace JOIN pg_class t ON t.oid = c.conrelid WHERE n.nspname = 'public' AND c.contype = 'f' AND c.conname NOT LIKE t.relname || '_%' AND c.conname NOT LIKE 'fkey_wolverine_node_assignments_%' AND (t.relname NOT LIKE 'OpenIddict%' OR c.conname NOT LIKE 'FK_' || t.relname || '_%')", admin))
            Assert.Equal(0L, await constraints.ExecuteScalarAsync());
        await using (var assignment = new NpgsqlCommand("""
            SELECT conname, confrelid::regclass::text, confdeltype::text
            FROM pg_constraint WHERE conrelid = 'public.wolverine_node_assignments'::regclass AND contype = 'f'
            """, admin))
        await using (NpgsqlDataReader row = await assignment.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal("fkey_wolverine_node_assignments_node_id", row.GetString(0));
            Assert.Equal("wolverine_nodes", row.GetString(1));
            Assert.Equal("c", row.GetString(2));
            Assert.False(await row.ReadAsync());
        }
        await using (var indexes = new NpgsqlCommand("SELECT array_agg(indexname::text ORDER BY indexname) FROM pg_indexes WHERE schemaname = 'public' AND indexname LIKE 'idx_wolverine_%'", admin))
            Assert.Equal(new[]
            {
                "idx_wolverine_dead_letters_replayable", "idx_wolverine_incoming_envelopes_keep_until",
                "idx_wolverine_incoming_envelopes_owner", "idx_wolverine_incoming_envelopes_recover",
                "idx_wolverine_outgoing_envelopes_owner", "idx_wolverine_outgoing_envelopes_recover"
            }, (string[])(await indexes.ExecuteScalarAsync())!);
        await using (var seeds = new NpgsqlCommand("SELECT nextval('public.connection_id_seq'), nextval('public.agent_id_seq'), nextval('public.github_connection_id_seq')", admin))
        await using (NpgsqlDataReader row = await seeds.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.Equal(2L, row.GetInt64(0));
            Assert.Equal(2L, row.GetInt64(1));
            Assert.Equal(2L, row.GetInt64(2));
        }
    }

    private static string WorkState(long id, string objective) =>
        JsonSerializer.Serialize(new Core.Work.WorkItem(id, objective, DateTimeOffset.UtcNow).Snapshot(), ContractJson.Options);

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

        public static string Artifacts
        {
            get
            {
                string? root = Environment.CurrentDirectory;
                while (root is not null && !File.Exists(Path.Combine(root, "AGENTS.md"))) root = Path.GetDirectoryName(root);
                return Path.Combine(root ?? throw new InvalidOperationException("Repository root not found."), ".artifacts", "postgres-singular", "persistence-tests");
            }
        }

        public static string Migrations => Path.Combine(AppContext.BaseDirectory, "migrations");
        public string AdminConnection { get; }
        public string AppConnection { get; }

        public IDbContextFactory<GoblinDbContext> ContextFactory => _services.GetRequiredService<IDbContextFactory<GoblinDbContext>>();

        public static async Task<TestDatabase> CreateAsync()
        {
            var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin));
            var app = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresApp));
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
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable(Env.GoblinTestPostgresAdmin));
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {_name} WITH (FORCE);", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
