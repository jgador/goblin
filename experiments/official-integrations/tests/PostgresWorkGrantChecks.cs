using System.Security.Cryptography;
using System.Text.Json;
using Goblin.Mcp.Prototype;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NpgsqlTypes;

internal static class PostgresWorkGrantChecks
{
    private const string Issuer = "https://identity.customer.example/";
    private const string Resource = "https://goblin.customer.example/mcp";
    private const string Subject = "Reviewed-Subject";
    private const long WorkId = 9007199254740993;
    private const long Version = 9007199254740995;
    private static readonly DateTime UpdatedAt = new(2026, 10, 14, 2, 0, 0, DateTimeKind.Utc);

    public static async Task<int> RunContractsAsync()
    {
        string schema = await SchemaAsync(default);
        int checks = 0;

        void Check(string name, bool passed)
        {
            Require(passed);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        Check("embedded Work fixture is marked test-only and contains private-field decoys",
            schema.Contains("TEST ONLY", StringComparison.Ordinal) && schema.Contains("objective text NOT NULL", StringComparison.Ordinal) &&
            schema.Contains("state jsonb NOT NULL", StringComparison.Ordinal));
        Check("grant fixture retains exact identity collation and Work foreign key",
            schema.Split("COLLATE \"C\"", StringSplitOptions.None).Length == 4 &&
            schema.Contains("REFERENCES public.work_item(id)", StringComparison.Ordinal) &&
            schema.Contains("UNIQUE (issuer, subject, resource, work_id)", StringComparison.Ordinal));
        Check("grant fixture retains approval expiry and revocation constraints",
            schema.Contains("expires_at > approved_at", StringComparison.Ordinal) &&
            schema.Contains("revoked_at >= approved_at", StringComparison.Ordinal) &&
            schema.Contains("approved_at timestamptz NOT NULL", StringComparison.Ordinal));
        Check("fixture read role is limited to four Work columns without mutation privileges",
            schema.Contains("REVOKE ALL ON public.work_item, public.connector_work_read_grant FROM PUBLIC, goblin_app", StringComparison.Ordinal) &&
            schema.Contains("GRANT SELECT (id, version, status, updated_at)", StringComparison.Ordinal) &&
            !schema.Contains("GRANT SELECT ON", StringComparison.Ordinal) && !schema.Contains("GRANT ALL", StringComparison.Ordinal) &&
            !schema.Contains("GRANT INSERT", StringComparison.Ordinal) && !schema.Contains("GRANT UPDATE", StringComparison.Ordinal));
        Check("test DDL never creates databases or roles and is not startup migration SQL",
            !schema.Contains("CREATE DATABASE", StringComparison.Ordinal) && !schema.Contains("CREATE ROLE", StringComparison.Ordinal) &&
            !schema.Contains("DROP ", StringComparison.Ordinal));
        const string hostile = "subject'; DELETE FROM public.work_item; --";
        using NpgsqlCommand grant = GrantCommand(hostile);
        Check("fixture approval binds hostile subject instead of interpolating SQL",
            !grant.CommandText.Contains(hostile, StringComparison.Ordinal) && (string)grant.Parameters[1].Value! == hostile &&
            grant.Parameters[1].NpgsqlDbType == NpgsqlDbType.Text);
        Check("fixture approval preserves bigint and UTC timestamptz parameter types",
            (long)grant.Parameters[3].Value! == WorkId && grant.Parameters[3].NpgsqlDbType == NpgsqlDbType.Bigint &&
            grant.Parameters[4].NpgsqlDbType == NpgsqlDbType.TimestampTz && ((DateTime)grant.Parameters[4].Value!).Kind == DateTimeKind.Utc &&
            grant.Parameters[5].NpgsqlDbType == NpgsqlDbType.TimestampTz);
        using NpgsqlCommand work = WorkCommand();
        Check("fixture Work seed has typed exact IDs version timestamp and JSON decoy",
            work.Parameters[0].NpgsqlDbType == NpgsqlDbType.Bigint && (long)work.Parameters[0].Value! == WorkId &&
            (long)work.Parameters[1].Value! == Version && work.Parameters[2].NpgsqlDbType == NpgsqlDbType.TimestampTz &&
            work.Parameters[3].NpgsqlDbType == NpgsqlDbType.Jsonb);
        Console.WriteLine($"PASS {checks} embedded-schema/Npgsql parameter contracts; no SQL execution or durability evidence.");
        return checks;
    }

    // Called only after the live-gated owned fixture is created. A generated
    // target/database-role check precedes DDL; no incoming production DB is used.
    public static async Task RunLiveAsync(PostgresFixturePlan plan, CancellationToken token)
    {
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(plan.Connection(admin: true));
        await using NpgsqlConnection owner = await admin.OpenConnectionAsync(token);
        await using (var target = new NpgsqlCommand("SELECT current_database(), current_user", owner))
        await using (NpgsqlDataReader result = await target.ExecuteReaderAsync(token))
            Require(await result.ReadAsync(token) && result.GetString(0) == plan.Name && result.GetString(1) == "goblin_admin");
        await using (var schema = new NpgsqlCommand(await SchemaAsync(token), owner))
            await schema.ExecuteNonQueryAsync(token);
        await using (NpgsqlCommand seed = WorkCommand())
        {
            seed.Connection = owner;
            await seed.ExecuteNonQueryAsync(token);
        }
        await using NpgsqlDataSource app = NpgsqlDataSource.Create(plan.Connection(admin: false));
        var source = new PostgresGrantedWorkStatusSource(app);
        using RSA signingKey = RSA.Create(2048);
        int checks = 0;

        async Task Check(string name, Func<Task> run)
        {
            await run();
            checks++;
            Console.WriteLine("PASS live PostgreSQL " + name);
        }

        async Task<ConnectorIdentity> Identity(string issuer = Issuer, string subject = Subject, string resource = Resource)
        {
            var boundary = new ConnectorReadBoundary(issuer, resource, new RsaSecurityKey(signingKey.ExportParameters(false)), source);
            string access = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer, Audience = resource, TokenType = "at+jwt",
                Claims = new Dictionary<string, object> { ["sub"] = subject, ["jti"] = Guid.NewGuid().ToString("N"), ["scope"] = ConnectorReadBoundary.ReadScope },
                NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(signingKey), SecurityAlgorithms.RsaSha256)
            });
            return await boundary.AuthenticateAsync(access, token);
        }

        async Task Denied(ConnectorIdentity identity, long workId = WorkId)
        {
            try { await source.ReadAuthorizedAsync(identity, workId, token); }
            catch (WorkReadFailure error)
            {
                Require(error.Message == "Connector Work read access is not authorized." && error.InnerException is null);
                return;
            }
            throw new Exception("Expected live local grant denial.");
        }

        ConnectorIdentity valid = await Identity();
        await Check("no local grant denies existing Work", () => Denied(valid));
        await using (NpgsqlCommand grant = GrantCommand(Subject))
        {
            grant.Connection = owner;
            await grant.ExecuteNonQueryAsync(token);
        }
        await Check("granted source returns exact bigint UTC projection only", async () =>
        {
            WorkStatusResult result = await source.ReadAuthorizedAsync(valid, WorkId, token);
            Require(result == new WorkStatusResult(WorkId.ToString(), Version.ToString(), "Ready", new DateTimeOffset(UpdatedAt)) &&
                !JsonSerializer.Serialize(result).Contains("PRIVATE", StringComparison.Ordinal));
        });
        await Check("ungranted existing Work denies", () => Denied(valid, WorkId + 1));
        await Check("missing Work has the same denial", () => Denied(valid, WorkId + 2));
        await Check("different issuer has no implicit local grant", async () => await Denied(await Identity(issuer: "https://other.customer.example/")));
        await Check("subject matching is exact and case sensitive", async () => await Denied(await Identity(subject: "reviewed-subject")));
        await Check("different resource has no implicit local grant", async () => await Denied(await Identity(resource: "https://other.customer.example/mcp")));
        await Check("hostile subject remains data and cannot widen grant", async () => await Denied(await Identity(subject: "x' OR TRUE --")));
        await Check("fresh connection observes committed grant", async () =>
        {
            await using NpgsqlDataSource fresh = NpgsqlDataSource.Create(plan.Connection(admin: false));
            Require((await new PostgresGrantedWorkStatusSource(fresh).ReadAuthorizedAsync(valid, WorkId, token)).WorkId == WorkId.ToString());
        });
        await Check("read role cannot fetch private objective", async () =>
        {
            await using NpgsqlCommand denied = app.CreateCommand("SELECT objective FROM public.work_item");
            try { await denied.ExecuteScalarAsync(token); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege) { return; }
            throw new Exception("Expected private-column privilege denial.");
        });
        await Check("read role cannot self-approve", async () =>
        {
            await using NpgsqlCommand denied = app.CreateCommand("UPDATE public.connector_work_read_grant SET revoked_at = NULL");
            try { await denied.ExecuteNonQueryAsync(token); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege) { return; }
            throw new Exception("Expected grant-mutation privilege denial.");
        });
        // Explicit fixture administrator writes, not production approval/audit.
        await using (var revoke = new NpgsqlCommand("UPDATE public.connector_work_read_grant SET revoked_at = statement_timestamp()", owner))
            await revoke.ExecuteNonQueryAsync(token);
        await Check("committed revocation denies the same identity", () => Denied(valid));
        await using (var expire = new NpgsqlCommand("UPDATE public.connector_work_read_grant SET revoked_at = NULL, expires_at = approved_at + interval '1 second'", owner))
            await expire.ExecuteNonQueryAsync(token);
        await Check("expired local grant denies without sleeps", () => Denied(valid));
        await using (var future = new NpgsqlCommand("UPDATE public.connector_work_read_grant SET approved_at = statement_timestamp() + interval '1 minute', expires_at = statement_timestamp() + interval '1 hour'", owner))
            await future.ExecuteNonQueryAsync(token);
        await Check("not-yet-effective approval denies", () => Denied(valid));
        Console.WriteLine($"PASS {checks} live minimal-schema Work/grant checks; no production migration, owner-session or audit coverage.");
    }

    private static NpgsqlCommand GrantCommand(string subject)
    {
        var command = new NpgsqlCommand("INSERT INTO public.connector_work_read_grant (issuer, subject, resource, work_id, approved_at, expires_at) VALUES ($1, $2, $3, $4, $5, $6)");
        command.Parameters.AddWithValue(NpgsqlDbType.Text, Issuer);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, subject);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, Resource);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, WorkId);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, DateTime.UtcNow.AddMinutes(-1));
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, DateTime.UtcNow.AddHours(1));
        return command;
    }

    private static NpgsqlCommand WorkCommand()
    {
        var command = new NpgsqlCommand("INSERT INTO public.work_item (id, objective, state, version, status, updated_at) VALUES ($1, 'PRIVATE objective', $4, $2, 'Ready', $3), ($1 + 1, 'PRIVATE other', $4, 1, 'Ready', $3)");
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, WorkId);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, Version);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, UpdatedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, "{\"history\":[\"PRIVATE\"]}");
        return command;
    }

    private static async Task<string> SchemaAsync(CancellationToken token)
    {
        using Stream stream = typeof(PostgresWorkGrantChecks).Assembly.GetManifestResourceStream("Goblin.Mcp.Prototype.Tests.WorkGrantSchema.sql")
            ?? throw new InvalidOperationException("Embedded test schema is unavailable.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token);
    }

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Work/grant fixture assertion failed.");
    }
}
