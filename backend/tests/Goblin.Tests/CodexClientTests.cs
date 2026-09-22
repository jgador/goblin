using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Integrations.Codex;
using Goblin.Protocol;
using Goblin.Web;
using Xunit;

namespace Goblin.Tests;

public sealed class CodexClientTests
{
    private static long _nextId = int.MaxValue;
    private static long NextId() => System.Threading.Interlocked.Increment(ref _nextId);

    [Fact]
    public async Task ConcurrentStartsPerformExactlyOneHandshakeAndIsolateConfiguration()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Client.StartAsync()));
        await fixture.ReadAsync();
        string[] calls = await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "requests.jsonl"));
        Assert.Equal(["initialize", "initialized", "account/read"], calls.Select(line => JsonSerializer.Deserialize<JSONRPCNotification>(line, ProtocolJson.Options)!.Method));
        Dictionary<string, string> environment = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(fixture.Workspace.CodexHome, "environment.json")))!;
        Assert.False(environment.ContainsKey("OPENAI_API_KEY"));
        Assert.False(environment.ContainsKey("GOBLIN_SECRET"));
        Assert.Equal(fixture.Workspace.Home, environment["HOME"]);
        Assert.Equal(fixture.Workspace.CodexHome, environment["CODEX_HOME"]);
        string args = await File.ReadAllTextAsync(Path.Combine(fixture.Workspace.CodexHome, "arguments.json"));
        Assert.Contains("app-server", args);
        Assert.Contains("features.multi_agent=false", args);
        Assert.Contains("skills.include_instructions=false", args);
    }

    [Fact]
    public async Task ConcurrentRequestsCorrelateResponsesDeliveredInReverseOrder()
    {
        await using Fixture fixture = await Fixture.CreateAsync("reordered");
        await fixture.Client.StartAsync();
        Task<GetAccountResponse> first = fixture.ReadAsync();
        Task<GetAccountResponse> second = fixture.ReadAsync();
        Assert.Equal("2", Assert.IsType<ChatgptAccount>((await first).Account).Email);
        Assert.Equal("3", Assert.IsType<ChatgptAccount>((await second).Account).Email);
    }

    [Fact]
    public async Task JsonlHandlesSplitUtf8AndMultipleFrames()
    {
        await using Fixture fixture = await Fixture.CreateAsync("fragmented");
        await fixture.Client.StartAsync();
        Assert.Equal("测试🙂@example.test", Assert.IsType<ChatgptAccount>((await fixture.ReadAsync()).Account).Email);
    }

    [Fact]
    public async Task UnknownFutureNotificationsAreIgnored()
    {
        await using Fixture fixture = await Fixture.CreateAsync("unknown-notification");
        await fixture.Client.StartAsync();
        Assert.True((await fixture.ReadAsync()).RequiresOpenaiAuth);
        Assert.True(fixture.Client.Ready);
    }

    [Fact]
    public async Task InteractiveServerRequestsAreRejectedWithTheirOriginalStringId()
    {
        await using Fixture fixture = await Fixture.CreateAsync("server-request");
        await fixture.Client.StartAsync();
        await fixture.ReadAsync();
        string file = Path.Combine(fixture.Workspace.CodexHome, "server-responses.jsonl");
        for (int i = 0; i < 100 && !File.Exists(file); i++) await Task.Delay(10);
        JSONRPCError reply = JsonSerializer.Deserialize<JSONRPCError>((await File.ReadAllLinesAsync(file))[0], ProtocolJson.Options)!;
        Assert.Equal(new RequestId("approval-42"), reply.Id);
        Assert.Equal(-32601, reply.Error.Code);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("missing-required")]
    [InlineData("crash")]
    public async Task BrokenTransportsRejectAllPendingRequestsAndCanRestart(string scenario)
    {
        await using Fixture fixture = await Fixture.CreateAsync(scenario);
        await fixture.Client.StartAsync();
        Task<GetAccountResponse> first = fixture.ReadAsync();
        Task<GetAccountResponse> second = fixture.ReadAsync();
        Assert.Equal("runtime_unavailable", (await Assert.ThrowsAsync<IntegrationFailure>(() => first)).Code);
        Assert.Equal("runtime_unavailable", (await Assert.ThrowsAsync<IntegrationFailure>(() => second)).Code);
        Assert.False(fixture.Client.Ready);
        int oldPid = await fixture.PidAsync();
        await fixture.Client.StartAsync();
        Assert.True(fixture.Client.Ready);
        Assert.False(IsAlive(oldPid));
    }

    [Fact]
    public async Task RequestTimeoutRetiresEvenATerminationResistantProcessBeforeRestart()
    {
        await using Fixture fixture = await Fixture.CreateAsync("stubborn", TimeSpan.FromMilliseconds(500));
        await fixture.Client.StartAsync();
        int pid = await fixture.PidAsync();
        IntegrationFailure error = await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.ReadAsync());
        Assert.Equal("runtime_timeout", error.Code);
        Assert.False(fixture.Client.Ready);
        await fixture.Client.StartAsync();
        Assert.False(IsAlive(pid));
    }

    [Fact]
    public async Task CancellationOfAnInFlightRequestRetiresTheConnection()
    {
        await using Fixture fixture = await Fixture.CreateAsync("hang");
        await fixture.Client.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ReadAsync(cancellation.Token));
        Assert.False(fixture.Client.Ready);
        await fixture.Client.StartAsync();
        Assert.True(fixture.Client.Ready);
    }

    [Fact]
    public async Task InitializationTimeoutAndShutdownRejectWaiters()
    {
        await using Fixture hung = await Fixture.CreateAsync("hang-initialize", TimeSpan.FromMilliseconds(500));
        Assert.Equal("runtime_timeout", (await Assert.ThrowsAsync<IntegrationFailure>(() => hung.Client.StartAsync())).Code);
        await using Fixture fixture = await Fixture.CreateAsync("hang");
        await fixture.Client.StartAsync();
        Task<GetAccountResponse> request = fixture.ReadAsync();
        int pid = await fixture.PidAsync();
        await fixture.Client.DisposeAsync();
        Assert.Equal("runtime_unavailable", (await Assert.ThrowsAsync<IntegrationFailure>(() => request)).Code);
        Assert.False(IsAlive(pid));
        await Assert.ThrowsAsync<IntegrationFailure>(() => fixture.Client.StartAsync());
    }

    [Theory]
    [InlineData("work-result", ObservationKind.Result)]
    [InlineData("work-input", ObservationKind.Paused)]
    public async Task WorkAdapterUsesPersistedContextAndKeepsEarlyResultProvenance(string scenario, ObservationKind expected)
    {
        await using Fixture fixture = await Fixture.CreateAsync(scenario);
        var work = new WorkItem(NextId(), "Answer the durable objective", DateTimeOffset.UtcNow);
        work.Assign(NextId(), DateTimeOffset.UtcNow);
        work.AddContext(NextId(), "Retained context", DateTimeOffset.UtcNow);
        work.QueueExecution(NextId(), new("codex", NextId()), DateTimeOffset.UtcNow);
        var progress = new List<ExecutionObservation>();
        ExecutionObservation result = await new CodexWorkRunner(fixture.Client).RunAsync(work.Snapshot(), false,
            value => { progress.Add(value); return Task.CompletedTask; }, default);
        Assert.Equal(expected, result.Kind);
        Assert.Equal("test-model", result.Session!.Model);
        Assert.Equal("test-thread-1", result.Session.SessionReference);
        Assert.Equal("test-turn-1", result.Session.OperationReference);
        Assert.Contains(progress, x => x.Kind == ObservationKind.Running);
        string requests = await File.ReadAllTextAsync(Path.Combine(fixture.Workspace.CodexHome, "prompt-requests.jsonl"));
        Assert.Contains("Retained context", requests);
        Assert.Contains("\"ephemeral\":false", requests);
        Assert.Contains("\"outputSchema\"", requests);
        using var turnRequest = JsonDocument.Parse(requests.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(line => line.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal)));
        JsonElement schema = turnRequest.RootElement.GetProperty("params").GetProperty("outputSchema");
        foreach (JsonElement required in schema.GetProperty("required").EnumerateArray())
            Assert.True(schema.GetProperty("properties").TryGetProperty(required.GetString()!, out _));
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("releaseWorkspace").GetProperty("type").GetString());
        Assert.Contains("\"networkAccess\":false", requests);
    }

    [Fact]
    public async Task WorkAdapterInterruptsEvenAnUpstreamRetryableFailure()
    {
        await using Fixture fixture = await Fixture.CreateAsync("work-retryable-error");
        var work = new WorkItem(NextId(), "Surface every failure", DateTimeOffset.UtcNow);
        work.Assign(NextId(), DateTimeOffset.UtcNow);
        work.QueueExecution(NextId(), new("codex", NextId()), DateTimeOffset.UtcNow);
        IntegrationFailure failure = await Assert.ThrowsAsync<IntegrationFailure>(() =>
            new CodexWorkRunner(fixture.Client).RunAsync(work.Snapshot(), false, _ => Task.CompletedTask, default));
        Assert.DoesNotContain("THIS-MUST-NOT-LEAK", failure.Message);
        string[] calls = await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "prompt-requests.jsonl"));
        Assert.Single(calls, x => x.Contains("turn/start", StringComparison.Ordinal));
        Assert.Contains(calls, x => x.Contains("turn/interrupt", StringComparison.Ordinal));
    }

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(Workspace workspace, CodexClient client)
        {
            Workspace = workspace;
            Client = client;
        }

        public Workspace Workspace { get; }
        public CodexClient Client { get; }
        public Task<GetAccountResponse> ReadAsync(CancellationToken cancellationToken = default) =>
            Client.RequestAsync<GetAccountParams, GetAccountResponse>("account/read", new() { RefreshToken = false }, cancellationToken);
        public async Task<int> PidAsync() => int.Parse(await File.ReadAllTextAsync(Path.Combine(Workspace.CodexHome, "pid")));

        public static async Task<Fixture> CreateAsync(string scenario = "manual", TimeSpan? timeout = null)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "backend/schemas/codex"))) root = root.Parent;
            string data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"goblin-dotnet-{Guid.NewGuid():N}")).FullName;
            string passwordFile = Path.Combine(data, "owner-password");
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] digest = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("codex-client-test"), salt, 600_000, HashAlgorithmName.SHA256, 32);
            await File.WriteAllTextAsync(passwordFile, $"pbkdf2-sha256$600000${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}\n");
            Workspace workspace = await Workspace.OpenAsync(data, "http://localhost:8787", passwordFile);
            return new(workspace, new(new()
            {
                CodexHome = workspace.CodexHome,
                Home = workspace.Home,
                Workspace = workspace.WorkingDirectory,
                Command = "node",
                Arguments = [Path.Combine(root!.FullName, "tests/fixtures/fake-codex.mts"), scenario],
                RequestTimeout = timeout ?? TimeSpan.FromSeconds(3),
                ShutdownTimeout = TimeSpan.FromMilliseconds(150),
                Environment = new Dictionary<string, string?>
                {
                    ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
                    ["OPENAI_API_KEY"] = "must-not-inherit",
                    ["GOBLIN_SECRET"] = "must-not-inherit"
                }
            }));
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Directory.Delete(Workspace.DataDirectory, recursive: true);
        }
    }
}
