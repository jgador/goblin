using System.Security.Cryptography;
using Npgsql;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

// Test-only planning. Explicit --postgres and both catalogued test variables
// are mandatory; never consult normal application connection configuration.
internal sealed class PostgresFixturePlan
{
    private readonly NpgsqlConnectionStringBuilder _admin;
    private readonly NpgsqlConnectionStringBuilder _app;

    private PostgresFixturePlan(NpgsqlConnectionStringBuilder admin, NpgsqlConnectionStringBuilder app)
    {
        _admin = admin;
        _app = app;
        Name = "goblin_mcp_test_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public string Name { get; }

    public static PostgresFixturePlan? Read(bool enabled, Func<string, string?> read)
    {
        if (!enabled) return null;
        string? admin = read(Env.GoblinTestPostgresAdmin), app = read(Env.GoblinTestPostgresApp);
        if (string.IsNullOrWhiteSpace(admin) && string.IsNullOrWhiteSpace(app)) return null;
        try
        {
            if (string.IsNullOrWhiteSpace(admin) || string.IsNullOrWhiteSpace(app)) throw Invalid();
            var owner = new NpgsqlConnectionStringBuilder(admin);
            var application = new NpgsqlConnectionStringBuilder(app);
            foreach (NpgsqlConnectionStringBuilder settings in new[] { owner, application })
            {
                if (string.IsNullOrWhiteSpace(settings.Host) || settings.Host.Contains(',') || settings.Host.Contains('/') ||
                    settings.Host.Any(char.IsWhiteSpace) ||
                    settings.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(settings.RootCertificate) ||
                    string.IsNullOrWhiteSpace(settings.SslCertificate) || string.IsNullOrWhiteSpace(settings.SslKey) ||
                    !string.IsNullOrEmpty(settings.Password)) throw Invalid();
                settings.Timeout = 5;
                settings.CommandTimeout = 5;
                settings.Pooling = false;
                settings.Enlist = false;
                settings.IncludeErrorDetail = false;
                settings.LogParameters = false;
            }
            if (owner.Host != application.Host || owner.Port != application.Port ||
                owner.Username != "goblin_admin" || application.Username != "goblin_app") throw Invalid();
            owner.Database = "postgres";
            return new(owner, application);
        }
        catch (Exception) { throw Invalid(); }
    }

    public string Connection(bool admin, bool maintenance = false)
    {
        if (maintenance && !admin) throw Invalid();
        var settings = new NpgsqlConnectionStringBuilder((admin ? _admin : _app).ConnectionString)
        {
            Database = maintenance ? "postgres" : Name
        };
        return settings.ConnectionString;
    }

    private static InvalidOperationException Invalid() => new("PostgreSQL test configuration is incomplete or unsafe.");
}

internal interface IPostgresFixtureControl
{
    Task<uint> CreateAsync(string name, CancellationToken token);

    Task<bool> OwnsAsync(string name, uint oid, CancellationToken token);

    Task DropAsync(string name, CancellationToken token);
}

internal sealed class OwnedPostgresFixture : IAsyncDisposable
{
    private readonly PostgresFixturePlan _plan;
    private readonly IPostgresFixtureControl _control;
    private uint? _oid;
    private bool _createAttempted;
    private bool _cleanupAttempted;

    public OwnedPostgresFixture(PostgresFixturePlan plan, IPostgresFixtureControl control)
    {
        _plan = plan;
        _control = control;
    }

    public async Task CreateAsync(CancellationToken token)
    {
        if (_createAttempted) throw new InvalidOperationException("Fixture creation was already attempted.");
        token.ThrowIfCancellationRequested();
        _createAttempted = true;
        uint oid = await _control.CreateAsync(_plan.Name, token);
        if (oid == 0) throw new InvalidOperationException("Fixture creation ownership could not be confirmed.");
        // No name-only cleanup following an uncertain CREATE acknowledgement.
        _oid = oid;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cleanupAttempted || _oid is not uint oid) return;
        _cleanupAttempted = true;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            if (!await _control.OwnsAsync(_plan.Name, oid, cleanup.Token))
                throw new InvalidOperationException("Fixture ownership changed.");
            await _control.DropAsync(_plan.Name, cleanup.Token);
            _oid = null;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Fixture cleanup not confirmed; preserve database " + _plan.Name + ". Provider details withheld.");
        }
    }
}

internal sealed class NpgsqlFixtureControl : IPostgresFixtureControl, IAsyncDisposable
{
    private readonly PostgresFixturePlan _plan;
    private readonly NpgsqlDataSource _maintenance;

    public NpgsqlFixtureControl(PostgresFixturePlan plan)
    {
        _plan = plan;
        _maintenance = NpgsqlDataSource.Create(plan.Connection(admin: true, maintenance: true));
    }

    public async Task<uint> CreateAsync(string name, CancellationToken token)
    {
        RequireName(name);
        await using (NpgsqlCommand create = _maintenance.CreateCommand("CREATE DATABASE \"" + name + "\" TEMPLATE template0"))
            await create.ExecuteNonQueryAsync(token);
        await using NpgsqlCommand identity = _maintenance.CreateCommand("SELECT oid FROM pg_catalog.pg_database WHERE datname = $1 AND pg_catalog.pg_get_userbyid(datdba) = current_user");
        identity.Parameters.AddWithValue(name);
        return await identity.ExecuteScalarAsync(token) is uint oid ? oid : 0;
    }

    public async Task<bool> OwnsAsync(string name, uint oid, CancellationToken token)
    {
        RequireName(name);
        await using NpgsqlCommand check = _maintenance.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname = $1 AND oid = $2 AND pg_catalog.pg_get_userbyid(datdba) = current_user)");
        check.Parameters.AddWithValue(name);
        check.Parameters.AddWithValue(oid);
        return await check.ExecuteScalarAsync(token) is true;
    }

    public async Task DropAsync(string name, CancellationToken token)
    {
        RequireName(name);
        // Never FORCE or clear global pools. A foreign/active connection causes
        // failure and leaves the exact fixture for explicit investigation.
        await using NpgsqlCommand drop = _maintenance.CreateCommand("DROP DATABASE \"" + name + "\"");
        await drop.ExecuteNonQueryAsync(token);
    }

    private void RequireName(string name)
    {
        if (name != _plan.Name) throw new InvalidOperationException("Not this fixture's database.");
    }

    public ValueTask DisposeAsync() => _maintenance.DisposeAsync();
}
