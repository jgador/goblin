using System.Data;
using System.Data.Common;
using System.Globalization;
using Goblin.Core.Work;

namespace Goblin.Mcp.Prototype;

// Undeployed SQL prototype: requires the reviewed schema proposal in
// PERSISTENCE.md. Supply a customer-owned NpgsqlDataSource; no connection string,
// migration, approval writer, owner-session fallback or automatic grant exists.
public sealed class PostgresGrantedWorkStatusSource : ICustomerWorkStatusSource
{
    private readonly DbDataSource _database;

    public PostgresGrantedWorkStatusSource(DbDataSource database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    private const string Sql = """
        SELECT w.id, w.version, w.status, w.updated_at
        FROM public.work_item AS w
        WHERE w.id = @work_id
          AND @token_expires_at > statement_timestamp()
          AND EXISTS (
            SELECT 1 FROM public.connector_work_read_grant AS g
            WHERE g.work_id = w.id
              AND g.issuer = @issuer AND g.subject = @subject AND g.resource = @resource
              AND g.approved_at <= statement_timestamp()
              AND g.expires_at > statement_timestamp()
              AND g.revoked_at IS NULL
          )
        """;

    public async Task<WorkStatusResult> ReadAuthorizedAsync(ConnectorIdentity identity, long workId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (workId <= 0 || identity.ExpiresAt <= DateTimeOffset.UtcNow)
            throw Denied();
        try
        {
            await using DbCommand command = _database.CreateCommand(Sql);
            command.CommandTimeout = 5;
            Add(command, "work_id", DbType.Int64, workId);
            Add(command, "issuer", DbType.String, identity.Issuer);
            Add(command, "subject", DbType.String, identity.Subject);
            Add(command, "resource", DbType.String, identity.Resource);
            Add(command, "token_expires_at", DbType.DateTime, identity.ExpiresAt.UtcDateTime);
            await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult | CommandBehavior.SingleRow, token);
            if (!await reader.ReadAsync(token)) throw Denied();
            long id = reader.GetInt64(0), version = reader.GetInt64(1);
            string status = reader.GetString(2);
            DateTime updatedAt = reader.GetDateTime(3);
            if (id != workId || version <= 0 ||
                !Enum.TryParse(status, out WorkStatus parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != status ||
                updatedAt.Kind != DateTimeKind.Utc || updatedAt == default)
                throw Denied();
            return new(id.ToString(CultureInfo.InvariantCulture), version.ToString(CultureInfo.InvariantCulture),
                status, new DateTimeOffset(updatedAt));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { throw Denied(); }
    }

    private static void Add(DbCommand command, string name, DbType type, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static WorkReadFailure Denied() => new("Connector Work read access is not authorized.");
}
