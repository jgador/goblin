using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Goblin.Mcp.Prototype;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

internal static class PostgresSourceChecks
{
    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using RSA key = RSA.Create(2048);
        var database = new ContractDatabase();
        var source = new PostgresGrantedWorkStatusSource(database);
        var boundary = new ConnectorReadBoundary("https://identity.customer.example/", "https://goblin.customer.example/mcp",
            new RsaSecurityKey(key.ExportParameters(false)), source);
        const string subject = "approved'; DROP TABLE public.work_item; --";
        string credential = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = boundary.Issuer, Audience = boundary.Resource, TokenType = "at+jwt",
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["jti"] = "local-id", ["scope"] = ConnectorReadBoundary.ReadScope },
            NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)
        });
        ConnectorIdentity identity = await boundary.AuthenticateAsync(credential, deadline.Token);
        int checks = 0;

        async Task Check(string name, Func<Task> run)
        {
            await run();
            Console.WriteLine("PASS " + name);
            checks++;
        }

        async Task Denied()
        {
            try { await source.ReadAuthorizedAsync(identity, 42, deadline.Token); }
            catch (WorkReadFailure error)
            {
                Require(error.Message == "Connector Work read access is not authorized." && error.InnerException is null);
                return;
            }
            throw new Exception("Expected guarded source denial.");
        }

        await Check("guarded status projection preserves bigint and UTC", async () =>
        {
            WorkStatusResult result = await boundary.ReadAsync(identity, 42, deadline.Token);
            Require(result == new WorkStatusResult("42", "9007199254740993", "Ready", new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero)));
        });
        await Check("one bounded SELECT joins grant policy and minimal projection", () =>
        {
            ContractCommand command = database.Last!;
            Require(database.Calls == 1 && command.CommandTimeout == 5 && command.WasDisposed &&
                command.Behavior == (CommandBehavior.SingleResult | CommandBehavior.SingleRow));
            string sql = command.CommandText;
            foreach (string clause in new[] { "SELECT w.id, w.version, w.status, w.updated_at", "w.id = @work_id",
                "g.work_id = w.id", "g.issuer = @issuer", "g.subject = @subject", "g.resource = @resource",
                "g.approved_at <= statement_timestamp()", "g.expires_at > statement_timestamp()",
                "g.revoked_at IS NULL", "@token_expires_at > statement_timestamp()" })
                Require(sql.Contains(clause, StringComparison.Ordinal));
            Require(!sql.Contains('*') && !sql.Contains("objective", StringComparison.Ordinal) &&
                !sql.Contains("w.state", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await Check("identity and Work parameters are typed and never interpolated", () =>
        {
            ContractCommand command = database.Last!;
            Require(command.Parameters.Count == 5 && !command.CommandText.Contains(subject, StringComparison.Ordinal) &&
                (string)command.Parameters["subject"].Value! == subject && command.Parameters["subject"].DbType == DbType.String &&
                (long)command.Parameters["work_id"].Value! == 42 && command.Parameters["work_id"].DbType == DbType.Int64 &&
                (string)command.Parameters["issuer"].Value! == identity.Issuer &&
                (string)command.Parameters["resource"].Value! == identity.Resource &&
                command.Parameters["token_expires_at"].DbType == DbType.DateTime &&
                (DateTime)command.Parameters["token_expires_at"].Value! == identity.ExpiresAt.UtcDateTime);
            return Task.CompletedTask;
        });
        await Check("ungranted and nonexistent Work share the same empty-result denial", async () =>
        {
            database.Row = null;
            await Denied();
        });
        foreach (object[] row in new[]
        {
            new object[] { 43L, 1L, "Ready", DateTime.UtcNow },
            new object[] { 42L, 0L, "Ready", DateTime.UtcNow },
            new object[] { 42L, -1L, "Ready", DateTime.UtcNow },
            new object[] { 42L, 1L, "Unknown", DateTime.UtcNow },
            new object[] { 42L, 1L, "0", DateTime.UtcNow },
            new object[] { 42L, 1L, "ready", DateTime.UtcNow },
            new object[] { 42L, 1L, "Ready", DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Unspecified) },
            new object[] { 42L, 1L, "Ready", DBNull.Value }
        })
            await Check("invalid database projection fails closed", async () => { database.Row = row; await Denied(); });
        await Check("database/provider errors are sanitized without credential detail", async () =>
        {
            database.Unavailable = true;
            await Denied();
        });
        await Check("invalid Work ID does not open a database command", async () =>
        {
            int before = database.Calls;
            try { await source.ReadAuthorizedAsync(identity, 0, deadline.Token); }
            catch (WorkReadFailure) { Require(database.Calls == before); return; }
            throw new Exception("Expected invalid ID denial.");
        });
        await Check("caller cancellation is preserved before any database command", async () =>
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            int before = database.Calls;
            try { await source.ReadAuthorizedAsync(identity, 42, canceled.Token); }
            catch (OperationCanceledException) { Require(database.Calls == before); return; }
            throw new Exception("Expected cancellation.");
        });
        await database.DisposeAsync();
        Console.WriteLine($"PASS {checks} PostgreSQL source contract checks; fake ADO.NET execution, no live SQL evaluation or durability coverage.");
        return checks;
    }

    private static void Require(bool value)
    {
        if (!value) throw new Exception("Guarded database contract assertion failed.");
    }

    private sealed class ContractDatabase : DbDataSource
    {
        public override string ConnectionString => "";
        public int Calls { get; private set; }
        public ContractCommand? Last { get; private set; }
        public bool Unavailable { get; set; }
        public object[]? Row { get; set; } = [42L, 9007199254740993L, "Ready", new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)];

        protected override DbConnection CreateDbConnection() => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand(string? commandText = null)
        {
            Calls++;
            Last = new ContractCommand(this) { CommandText = commandText ?? "" };
            return Last;
        }
    }

    private sealed class ContractCommand : DbCommand
    {
        private readonly ContractDatabase _database;
        private readonly Parameters _parameters = new();

        public ContractCommand(ContractDatabase database) { _database = database; }

        [AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        public bool WasDisposed { get; private set; }
        public CommandBehavior Behavior { get; private set; }

        public override void Cancel() => throw new NotSupportedException();

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar() => throw new NotSupportedException();

        public override void Prepare() => throw new NotSupportedException();

        protected override DbParameter CreateDbParameter() => new Parameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Behavior = behavior;
            if (_database.Unavailable) throw new InvalidOperationException("PRIVATE connection and credential detail");
            var table = new DataTable();
            table.Columns.Add("id", typeof(long));
            table.Columns.Add("version", typeof(long));
            table.Columns.Add("status", typeof(string));
            DataColumn updated = table.Columns.Add("updated_at", typeof(DateTime));
            updated.DateTimeMode = _database.Row?[3] is DateTime time && time.Kind == DateTimeKind.Utc
                ? DataSetDateTime.Utc : DataSetDateTime.Unspecified;
            if (_database.Row is not null) table.Rows.Add(_database.Row);
            return table.CreateDataReader();
        }

        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class Parameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        [AllowNull]
        public override string ParameterName { get; set; } = "";
        [AllowNull]
        public override string SourceColumn { get; set; } = "";
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }

        public override void ResetDbType() { }
    }

    private sealed class Parameters : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => this;

        public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }

        public override void AddRange(Array values) { foreach (object value in values) Add(value); }

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains((DbParameter)value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);

        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);

        public override int IndexOf(string name) => _items.FindIndex(p => p.ParameterName == name);

        public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => _items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string name) => RemoveAt(IndexOf(name));

        protected override DbParameter GetParameter(int index) => _items[index];

        protected override DbParameter GetParameter(string name) => _items[IndexOf(name)];

        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;

        protected override void SetParameter(string name, DbParameter value) => SetParameter(IndexOf(name), value);
    }
}
