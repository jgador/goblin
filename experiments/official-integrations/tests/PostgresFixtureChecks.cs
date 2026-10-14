using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Npgsql;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

internal static class PostgresFixtureChecks
{
    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int checks = 0;

        async Task Check(string name, Func<Task> run)
        {
            await run();
            checks++;
            Console.WriteLine("PASS " + name);
        }

        await Check("PostgreSQL default does not even read credentials", () =>
        {
            Require(PostgresFixturePlan.Read(false, _ => throw new Exception("Environment must not be read.")) is null);
            return Task.CompletedTask;
        });
        await Check("explicit PostgreSQL opt-in without test configuration skips", () =>
        {
            Require(PostgresFixturePlan.Read(true, _ => null) is null);
            return Task.CompletedTask;
        });
        foreach (string scenario in new[] { "partial", "malformed", "ssl", "certificate", "password", "role", "endpoint", "multi-host", "unix-socket" })
            await Check("unsafe fixture configuration rejected without raw detail: " + scenario, () =>
            {
                string? Read(string name)
                {
                    if (scenario == "partial" && name == Env.GoblinTestPostgresApp) return null;
                    if (scenario == "malformed") return "PRIVATE not a connection string";
                    var value = new NpgsqlConnectionStringBuilder(Configuration(name));
                    if (scenario == "ssl") value.SslMode = SslMode.Require;
                    if (scenario == "certificate") value.SslKey = "";
                    if (scenario == "password") value.Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    if (scenario == "role") value.Username = "postgres";
                    if (scenario == "endpoint" && name == Env.GoblinTestPostgresApp) value.Port++;
                    if (scenario == "multi-host") value.Host = "customer.example,other.example";
                    if (scenario == "unix-socket") value.Host = "/synthetic/socket";
                    return value.ConnectionString;
                }
                try { _ = PostgresFixturePlan.Read(true, Read); }
                catch (InvalidOperationException error)
                {
                    Require(error.Message == "PostgreSQL test configuration is incomplete or unsafe." && error.InnerException is null);
                    return Task.CompletedTask;
                }
                throw new Exception("Expected unsafe fixture rejection.");
            });
        PostgresFixturePlan plan = PostgresFixturePlan.Read(true, Configuration)!;
        await Check("fixture names are fresh crypto-safe identifiers", () =>
        {
            Require(Regex.IsMatch(plan.Name, "^goblin_mcp_test_[0-9a-f]{32}$") &&
                plan.Name != PostgresFixturePlan.Read(true, Configuration)!.Name);
            return Task.CompletedTask;
        });
        await Check("connections target maintenance or only the owned fixture", () =>
        {
            var admin = new NpgsqlConnectionStringBuilder(plan.Connection(true, true));
            var app = new NpgsqlConnectionStringBuilder(plan.Connection(false));
            Require(admin.Database == "postgres" && app.Database == plan.Name &&
                new NpgsqlConnectionStringBuilder(plan.Connection(true)).Database == plan.Name &&
                !app.Pooling && !app.Enlist && app.Timeout == 5 && app.CommandTimeout == 5 && !app.IncludeErrorDetail && !app.LogParameters);
            return Task.CompletedTask;
        });
        await Check("uncreated fixture never attempts cleanup", async () =>
        {
            var control = new FakeControl();
            await new OwnedPostgresFixture(plan, control).DisposeAsync();
            Require(control.Creates == 0 && control.Checks == 0 && control.Drops == 0);
        });
        await Check("confirmed ownership permits one non-repeated cleanup", async () =>
        {
            var control = new FakeControl();
            var fixture = new OwnedPostgresFixture(plan, control);
            await fixture.CreateAsync(deadline.Token);
            await fixture.DisposeAsync();
            await fixture.DisposeAsync();
            Require(control.Creates == 1 && control.Checks == 1 && control.Drops == 1 && control.Name == plan.Name);
        });
        await Check("uncertain create acknowledgement never authorizes DROP", async () =>
        {
            var control = new FakeControl { CreateFails = true };
            var fixture = new OwnedPostgresFixture(plan, control);
            try { await fixture.CreateAsync(deadline.Token); }
            catch (InvalidOperationException) { await fixture.DisposeAsync(); Require(control.Drops == 0); return; }
            throw new Exception("Expected uncertain CREATE.");
        });
        await Check("catalog identity or owner mismatch refuses cleanup", async () =>
        {
            var control = new FakeControl { Owned = false };
            var fixture = new OwnedPostgresFixture(plan, control);
            await fixture.CreateAsync(deadline.Token);
            try { await fixture.DisposeAsync(); }
            catch (InvalidOperationException) { Require(control.Drops == 0); return; }
            throw new Exception("Expected ownership refusal.");
        });
        await Check("caller cancellation prevents fixture creation", async () =>
        {
            var control = new FakeControl();
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await using var fixture = new OwnedPostgresFixture(plan, control);
            try { await fixture.CreateAsync(canceled.Token); }
            catch (OperationCanceledException) { Require(control.Creates == 0); return; }
            throw new Exception("Expected canceled fixture creation.");
        });
        await Check("cleanup provider failure is sanitized without forced retry", async () =>
        {
            var control = new FakeControl { DropFails = true };
            var fixture = new OwnedPostgresFixture(plan, control);
            await fixture.CreateAsync(deadline.Token);
            try { await fixture.DisposeAsync(); }
            catch (InvalidOperationException error)
            {
                await fixture.DisposeAsync();
                Require(control.Drops == 1 && error.InnerException is null && !error.Message.Contains("PRIVATE", StringComparison.Ordinal));
                return;
            }
            throw new Exception("Expected sanitized cleanup failure.");
        });
        Console.WriteLine($"PASS {checks} PostgreSQL fixture safety contracts; no database connection.");
        return checks;
    }

    public static async Task LiveAsync(bool enabled)
    {
        PostgresFixturePlan? plan = PostgresFixturePlan.Read(enabled, Environment.GetEnvironmentVariable);
        if (plan is null)
        {
            Console.WriteLine("SKIP live PostgreSQL: explicit --postgres and both certificate-authenticated test connections required.");
            return;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await using var control = new NpgsqlFixtureControl(plan);
            await using var fixture = new OwnedPostgresFixture(plan, control);
            await fixture.CreateAsync(deadline.Token);
            foreach (bool admin in new[] { true, false })
            {
                await using NpgsqlDataSource source = NpgsqlDataSource.Create(plan.Connection(admin));
                await using NpgsqlCommand query = source.CreateCommand("SELECT current_database(), current_user");
                await using NpgsqlDataReader result = await query.ExecuteReaderAsync(deadline.Token);
                Require(await result.ReadAsync(deadline.Token) && result.GetString(0) == plan.Name &&
                    result.GetString(1) == (admin ? "goblin_admin" : "goblin_app"));
            }
            await fixture.DisposeAsync();
            Console.WriteLine("PASS live PostgreSQL fixture connectivity/roles and cleanup. No Work/grant/audit coverage.");
        }
        catch (Exception)
        {
            // Only a generated nonsecret identifier may be displayed. Never
            // print raw provider errors, connection strings or certificate data.
            throw new InvalidOperationException("PostgreSQL fixture failed; inspect owned test database " + plan.Name + ". Provider details withheld.");
        }
    }

    private static string Configuration(string name) =>
        "Host=customer.example;Database=existing_customer_data;Username=" + (name == Env.GoblinTestPostgresAdmin ? "goblin_admin" : "goblin_app") +
        ";SSL Mode=VerifyFull;Root Certificate=/synthetic/ca.crt;SSL Certificate=/synthetic/client.crt;SSL Key=/synthetic/client.key";

    private static void Require(bool value)
    {
        if (!value) throw new Exception("PostgreSQL fixture safety assertion failed.");
    }

    private sealed class FakeControl : IPostgresFixtureControl
    {
        public bool CreateFails { get; init; }
        public bool Owned { get; init; } = true;
        public bool DropFails { get; init; }
        public int Creates { get; private set; }
        public int Checks { get; private set; }
        public int Drops { get; private set; }
        public string? Name { get; private set; }

        public Task<uint> CreateAsync(string name, CancellationToken token)
        {
            Creates++;
            Name = name;
            if (CreateFails) throw new InvalidOperationException("Synthetic uncertain CREATE.");
            return Task.FromResult(123U);
        }

        public Task<bool> OwnsAsync(string name, uint oid, CancellationToken token)
        {
            Checks++;
            Require(name == Name && oid == 123U && !token.IsCancellationRequested);
            return Task.FromResult(Owned);
        }

        public Task DropAsync(string name, CancellationToken token)
        {
            Drops++;
            Require(name == Name && !token.IsCancellationRequested);
            if (DropFails) throw new InvalidOperationException("PRIVATE provider connection detail");
            return Task.CompletedTask;
        }
    }
}
