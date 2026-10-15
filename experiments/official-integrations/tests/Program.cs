using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Goblin.Mcp.Prototype;
using ModelContextProtocol;
using ModelContextProtocol.Client;

if (args.Length == 3 && args[2] == "--oauth-metadata-only")
{
    int metadataChecks = OAuthDiscoveryChecks.Run();
    metadataChecks += await ProtectedResourceMetadataChecks.RunAsync();
    metadataChecks += await OfflineOAuthCommandChecks.RunAsync(args[0], args[1]);
    Console.WriteLine($"PASS {metadataChecks} focused offline metadata checks; no OAuth or Work authorization performed.");
    return;
}

bool postgres = args.Length == 3 && args[2] == "--postgres";
if (args.Length is not (0 or 2) && !postgres) throw new ArgumentException("Supply dotnet, prototype DLL and optional --postgres.");
int passed = 0;
string session = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
var options = new PrototypeOptions("https://customer.example/", session, [42]);
var handler = new StubHandler(session);
using var http = new HttpClient(handler);
var reader = new WorkStatusReader(http, options);
var tools = new WorkTools(reader);

await CheckAsync("explicit grant returns only status fields and exact bigint", async () =>
{
    WorkStatusResult result = await reader.ReadAsync(42, default);
    Ensure(result.WorkId == "42" && result.Version == "9007199254740993" && result.Status == "InProgress");
    Ensure(!JsonSerializer.Serialize(result).Contains("PRIVATE", StringComparison.Ordinal));
});
await CheckAsync("unauthorized ID denied before HTTP", async () =>
{
    int before = handler.Calls;
    await ExpectAsync<WorkReadFailure>(() => reader.ReadAsync(43, default));
    Ensure(before == handler.Calls);
});
foreach (string id in new[] { "0", "-1", "42/../43", "https://evil.example", "9223372036854775808" })
    await CheckAsync("invalid ID rejected", () => ExpectAsync<McpException>(() => tools.GetStatusAsync(id, default)));
foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError, HttpStatusCode.Redirect })
    await CheckAsync("upstream failure sanitized", async () =>
    {
        handler.Status = status;
        try { await tools.GetStatusAsync("42", default); throw new Exception("Expected denial."); }
        catch (McpException error) { Ensure(!error.ToString().Contains(session, StringComparison.Ordinal) && !error.Message.Contains("PRIVATE", StringComparison.Ordinal)); }
    });
handler.Status = HttpStatusCode.OK;
handler.Body = "{\"version\":\"1\",\"updatedAt\":\"2026-10-13T21:00:00Z\",\"work\":{\"id\":\"43\",\"status\":\"InProgress\"}}";
await CheckAsync("mismatched upstream ID rejected", () => ExpectAsync<WorkReadFailure>(() => reader.ReadAsync(42, default)));
handler.Body = "not-json PRIVATE";
await CheckAsync("malformed upstream body sanitized", () => ExpectAsync<WorkReadFailure>(() => reader.ReadAsync(42, default)));
handler.Body = "{\"version\":\"1\",\"updatedAt\":\"2026-10-13T21:00:00Z\",\"work\":{\"id\":\"42\",\"status\":\"PRIVATE INSTRUCTIONS\"}}";
await CheckAsync("unknown status cannot inject text into tool result", () => ExpectAsync<WorkReadFailure>(() => reader.ReadAsync(42, default)));
handler.Body = "{\"version\":\"1\",\"updatedAt\":\"2026-10-13T21:00:00Z\",\"work\":{\"id\":\"42\"}}";
await CheckAsync("missing status fails closed", () => ExpectAsync<WorkReadFailure>(() => reader.ReadAsync(42, default)));
await CheckAsync("malformed configured grant fails startup", () =>
{
    try { _ = PrototypeOptions.Read(name => name == PrototypeOptions.WorkIdsVariable ? "42,*" : ""); throw new Exception("Expected rejection."); }
    catch (ArgumentException) { return Task.CompletedTask; }
});
foreach (string origin in new[] { "http://customer.example/", "https://user:password@customer.example/", "https://customer.example/path", "https://customer.example/?token=value" })
    await CheckAsync("unsafe origin rejected", () =>
    {
        try { _ = new PrototypeOptions(origin, session, [42]); throw new Exception("Expected rejection."); }
        catch (ArgumentException) { return Task.CompletedTask; }
    });
await CheckAsync("empty grants fail closed", () =>
{
    try { _ = new PrototypeOptions("https://customer.example/", session, []); throw new Exception("Expected rejection."); }
    catch (ArgumentException) { return Task.CompletedTask; }
});

// Actual SDK stdio transport with a child host. No live customer or ChatGPT connection.
if (args.Length >= 2)
{
    await CheckAsync("real stdio initialization discovery and permission denial", async () =>
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Goblin prototype contract",
            Command = args[0],
            Arguments = [args[1]],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [PrototypeOptions.OriginVariable] = "https://customer.example/",
                [PrototypeOptions.SessionVariable] = session,
                [PrototypeOptions.WorkIdsVariable] = "42"
            }
        });
        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        var available = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Ensure(available.Count == 1 && available[0].Name == "goblin_get_work_status");
        var protocol = available[0].ProtocolTool;
        Ensure(protocol.Annotations?.ReadOnlyHint == true && protocol.Annotations.DestructiveHint == false);
        var result = await client.CallToolAsync("goblin_get_work_status",
            new Dictionary<string, object?> { ["workId"] = "43" }, cancellationToken: deadline.Token);
        Ensure(result.IsError == true && !JsonSerializer.Serialize(result).Contains(session, StringComparison.Ordinal));
    });
}
else Console.WriteLine("SKIP stdio: supply dotnet executable and built prototype DLL.");

passed += await ConnectorBoundaryChecks.RunAsync();
passed += await PrivateHttpChecks.RunAsync();
passed += await RotatingHttpChecks.RunAsync();
passed += await HttpCompositionChecks.RunAsync();
passed += await SdkHttpClientChecks.RunAsync();
passed += await PostgresSourceChecks.RunAsync();
passed += await GrantAdministrationChecks.RunAsync();
passed += await PostgresFixtureChecks.RunAsync();
passed += await PostgresWorkGrantChecks.RunContractsAsync();
passed += OAuthDiscoveryChecks.Run();
passed += await ProtectedResourceMetadataChecks.RunAsync();
passed += await OfflineOAuthCommandChecks.RunAsync(args.Length >= 2 ? args[0] : null, args.Length >= 2 ? args[1] : null);
passed += await SigningKeyRotationChecks.RunAsync();
passed += await HttpsJwksSourceChecks.RunAsync();
passed += await HttpsKeyCancellationChecks.RunAsync();
passed += await SigningKeyInvalidationChecks.RunAsync();
passed += await VerificationLeaseChecks.RunAsync();
Console.WriteLine($"PASS {passed} focused checks. Backend responses mocked; no live ChatGPT, Slack or PostgreSQL coverage.");
await PostgresFixtureChecks.LiveAsync(postgres);
return;

async Task CheckAsync(string name, Func<Task> run)
{
    await run();
    passed++;
    Console.WriteLine("PASS " + name);
}

static void Ensure(bool value)
{
    if (!value) throw new Exception("Contract assertion failed.");
}

static async Task ExpectAsync<T>(Func<Task> run) where T : Exception
{
    try { await run(); }
    catch (T) { return; }
    throw new Exception("Expected contract rejection.");
}

sealed class StubHandler : HttpMessageHandler
{
    private readonly string _session;
    public int Calls { get; private set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Body { get; set; } = "{\"version\":\"9007199254740993\",\"updatedAt\":\"2026-10-13T21:00:00Z\",\"work\":{\"id\":\"42\",\"status\":\"InProgress\",\"objective\":\"PRIVATE\",\"history\":[\"PRIVATE\"]}}";

    public StubHandler(string session) { _session = session; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://customer.example/api/work/42" ||
            request.Headers.GetValues("Cookie").Single() != "goblin_auth_session=" + _session)
            throw new Exception("Fixed upstream request contract failed.");
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}
