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
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Integrations.Codex;
using Goblin.Protocol;
using Goblin.Web;
using Xunit;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Tests;

public sealed class CodexClientTests
{
    private static ChatGptPluginContext PluginContext(string? appId = null) => new(
        "https://goblin.example.test/mcp", "https://goblin.example.test/", "goblin-chatgpt",
        "openid offline_access goblin.work.read", appId);

    private static async Task<Authentication> PluginAuthenticationAsync(Fixture fixture, bool? chatGpt = true,
        TimeSpan? timeout = null)
    {
        if (chatGpt is not null)
            await File.WriteAllTextAsync(Path.Combine(fixture.Workspace.CodexHome, "auth.json"), chatGpt.Value
                ? "{\"type\":\"chatgpt\",\"email\":\"owner@example.test\",\"planType\":\"plus\"}"
                : "{\"type\":\"apiKey\"}");
        return new Authentication(fixture.Client, _ => Task.FromResult(VerificationState.Accepted), timeout ?? TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("plugin-ready", true, true, true)]
    [InlineData("plugin-source-unavailable", true, false, true)]
    [InlineData("plugin-tools-unavailable", true, true, false)]
    [InlineData("plugin-runtime-unsupported", false, false, false)]
    public async Task PluginPreparationUsesASkillWithoutGrantingItsTurnHostedTools(string scenario, bool supported, bool source, bool callable)
    {
        await using Fixture fixture = await Fixture.CreateAsync(scenario);
        using Authentication auth = await PluginAuthenticationAsync(fixture);
        ChatGptPluginDraft draft = await auth.PreparePluginAsync(PluginContext("plugin_asdk_app_1234"), default);
        Assert.Equal(supported, draft.CreatorAccess.RuntimeSupported);
        Assert.Equal(source, draft.CreatorAccess.SkillAvailable);
        Assert.Equal(callable, draft.CreatorAccess.AppCallable);
        Assert.Equal(source ? 64 : 0, draft.CreatorAccess.SkillSha256?.Length ?? 0);
        Assert.Contains("plugin_asdk_app_1234", draft.CreatorPrompt);
        Assert.Contains(PluginContext().ServerUrl, draft.CreatorPrompt);
        Assert.Contains("client_secret_basic", draft.CreatorPrompt);
        Assert.Contains("goblin_ping", draft.CreatorPrompt);
        Assert.Equal("test-model", draft.Model);
        Assert.Contains("name: goblin-plugin-setup", draft.SkillMarkdown);
        string[] requests = await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "requests.jsonl"));
        using JsonDocument generation = JsonDocument.Parse(Assert.Single(requests, x => x.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal)));
        JsonElement turn = generation.RootElement.GetProperty("params");
        JsonElement skill = turn.GetProperty("input")[1];
        Assert.Equal("skill", skill.GetProperty("type").GetString());
        Assert.Equal(PluginSetupSkill.Name, skill.GetProperty("name").GetString());
        string path = skill.GetProperty("path").GetString()!;
        Assert.StartsWith(fixture.Workspace.CodexHome, path, StringComparison.Ordinal);
        Assert.Equal(draft.SkillMarkdown, await File.ReadAllTextAsync(path));
        Assert.False(turn.GetProperty("sandboxPolicy").GetProperty("networkAccess").GetBoolean());
        Assert.Equal("never", turn.GetProperty("approvalPolicy").GetString());
        Assert.Equal(new[] { "description" }, turn.GetProperty("outputSchema").GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        string[] starts = [.. requests.Where(x => x.Contains("\"method\":\"thread/start\"", StringComparison.Ordinal))];
        Assert.Equal(2, starts.Length);
        using JsonDocument metadataThread = JsonDocument.Parse(starts[0]);
        JsonElement apps = metadataThread.RootElement.GetProperty("params").GetProperty("config").GetProperty("apps");
        Assert.False(apps.GetProperty("_default").GetProperty("enabled").GetBoolean());
        Assert.True(apps.GetProperty(PluginCreatorProbe.AppId).GetProperty("enabled").GetBoolean());
        using JsonDocument preparationThread = JsonDocument.Parse(starts[1]);
        JsonElement config = preparationThread.RootElement.GetProperty("params").GetProperty("config");
        Assert.True(config.GetProperty("skills.include_instructions").GetBoolean());
        Assert.False(config.TryGetProperty("features.apps", out _));
        Assert.Equal(2, requests.Count(x => x.Contains("\"method\":\"thread/unsubscribe\"", StringComparison.Ordinal)));
        string arguments = await File.ReadAllTextAsync(Path.Combine(fixture.Workspace.CodexHome, "arguments.json"));
        Assert.Contains("features.apps=false", arguments);
        Assert.Contains("features.shell_tool=false", arguments);
    }

    [Theory]
    [InlineData("plugin-skill-unavailable")]
    [InlineData("plugin-skill-disabled")]
    public async Task PreparationRequiresTheRuntimeToDiscoverTheEnabledBundledSkill(string scenario)
    {
        await using Fixture fixture = await Fixture.CreateAsync(scenario);
        using Authentication auth = await PluginAuthenticationAsync(fixture);
        IntegrationFailure error = await Assert.ThrowsAsync<IntegrationFailure>(() => auth.PreparePluginAsync(PluginContext(), default));
        Assert.Equal("plugin_skill_unavailable", error.Code);
        string[] requests = await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "requests.jsonl"));
        Assert.DoesNotContain(requests, line => line.Contains("turn/start", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task PluginPreparationRequiresTheSavedChatGptAccount(bool? chatGpt)
    {
        await using Fixture fixture = await Fixture.CreateAsync("plugin-ready");
        using Authentication auth = await PluginAuthenticationAsync(fixture, chatGpt);
        IntegrationFailure error = await Assert.ThrowsAsync<IntegrationFailure>(() => auth.PreparePluginAsync(PluginContext(), default));
        Assert.Equal("plugin_chatgpt_login_required", error.Code);
        Assert.DoesNotContain(await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "requests.jsonl")),
            x => x.Contains("thread/start", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("plugin-output-invalid", "plugin_draft_invalid")]
    [InlineData("plugin-timeout", "prompt_timeout")]
    [InlineData("plugin-interactive", "plugin_manual_action_required")]
    public async Task PluginPreparationDoesNotAcceptInvalidOutputOrApproveInteractiveActions(string scenario, string code)
    {
        await using Fixture fixture = await Fixture.CreateAsync(scenario);
        using Authentication auth = await PluginAuthenticationAsync(fixture, timeout: TimeSpan.FromMilliseconds(200));
        IntegrationFailure error = await Assert.ThrowsAsync<IntegrationFailure>(() => auth.PreparePluginAsync(PluginContext(), default));
        Assert.Equal(code, error.Code);
        string[] requests = await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "requests.jsonl"));
        Assert.Single(requests, x => x.Contains("\"method\":\"turn/start\"", StringComparison.Ordinal));
        Assert.Equal(2, requests.Count(x => x.Contains("thread/unsubscribe", StringComparison.Ordinal)));
        if (scenario != "plugin-output-invalid") Assert.Contains(requests, x => x.Contains("turn/interrupt", StringComparison.Ordinal));
        if (scenario == "plugin-interactive")
        {
            using JsonDocument rejection = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "server-responses.jsonl"))));
            Assert.Equal(-32601, rejection.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task PluginPreparationRejectsOverlapsAndSerializesLogoutBehindTheTurn()
    {
        await using Fixture fixture = await Fixture.CreateAsync("plugin-delayed");
        using Authentication auth = await PluginAuthenticationAsync(fixture);
        Task<ChatGptPluginDraft> preparation = auth.PreparePluginAsync(PluginContext(), default);
        IntegrationFailure overlap = await Assert.ThrowsAsync<IntegrationFailure>(() => auth.CheckPluginCreatorAsync(default));
        Assert.Equal("prompt_in_progress", overlap.Code);
        Task<AuthenticationState> logout = auth.LogoutAsync();
        Assert.False(logout.IsCompleted);
        await preparation;
        Assert.Null((await logout).Account);
        IntegrationFailure disconnected = await Assert.ThrowsAsync<IntegrationFailure>(() => auth.CheckPluginCreatorAsync(default));
        Assert.Equal("plugin_chatgpt_login_required", disconnected.Code);
    }

    [Fact]
    public async Task PluginPreparationCancellationInterruptsAndAllowsANewCheck()
    {
        await using Fixture fixture = await Fixture.CreateAsync("plugin-timeout");
        using Authentication auth = await PluginAuthenticationAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        Task<ChatGptPluginDraft> preparation = auth.PreparePluginAsync(PluginContext(), cancellation.Token);
        string file = Path.Combine(fixture.Workspace.CodexHome, "prompt-requests.jsonl");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!File.Exists(file) || !(await File.ReadAllTextAsync(file, deadline.Token)).Contains("turn/start", StringComparison.Ordinal))
            await Task.Delay(10, deadline.Token);
        cancellation.Cancel();
        Assert.Equal("prompt_cancelled", (await Assert.ThrowsAsync<IntegrationFailure>(() => preparation)).Code);
        Assert.True((await auth.CheckPluginCreatorAsync(default)).RuntimeSupported);
        Assert.Contains("turn/interrupt", await File.ReadAllTextAsync(file));
    }

    private static long _nextId = int.MaxValue;

    private static long NextId() => System.Threading.Interlocked.Increment(ref _nextId);

    [Fact]
    public async Task ModelCatalogPaginatesAndFiltersHiddenOptions()
    {
        await using Fixture fixture = await Fixture.CreateAsync("models");
        RuntimeModel[] models = await new CodexModelCatalogSource(fixture.Client).ListAsync(default);
        Assert.Equal(["gpt-test-1", "gpt-test-2"], models.Select(x => x.Model));
        Assert.True(models[0].IsDefault);
        Assert.Equal(["low", "high"], models[0].SupportedReasoningEfforts);
        string[] requests = await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "requests.jsonl"));
        var calls = new List<JsonElement>();
        foreach (string line in requests)
        {
            using JsonDocument request = JsonDocument.Parse(line);
            if (request.RootElement.GetProperty("method").GetString() == "model/list")
                calls.Add(request.RootElement.GetProperty("params").Clone());
        }
        Assert.Equal(2, calls.Count);
        Assert.False(calls[0].GetProperty("includeHidden").GetBoolean());
        Assert.Equal("page-2", calls[1].GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task ModelCatalogDoesNotStampAnOldProcessAsAReplacedExecutable()
    {
        if (OperatingSystem.IsWindows()) return;
        await using Fixture fixture = await Fixture.CreateAsync("models", commandWrapper: true);
        var source = new CodexModelCatalogSource(fixture.Client);
        Assert.Equal(2, (await source.ListAsync(default)).Length);
        string original = source.ExecutableStamp();
        await File.AppendAllTextAsync(fixture.Client.Options.Command, "\n# updated CLI\n");
        Assert.NotEqual(original, source.ExecutableStamp());
        IntegrationFailure failure = await Assert.ThrowsAsync<IntegrationFailure>(() => source.ListAsync(default));
        Assert.Equal("runtime_unavailable", failure.Code);
    }

    [Fact]
    public async Task ConcurrentStartsPerformExactlyOneHandshakeAndIsolateConfiguration()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => fixture.Client.StartAsync()));
        await fixture.ReadAsync();
        string[] calls = await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "requests.jsonl"));
        Assert.Equal(["initialize", "initialized", "account/read"], calls.Select(line => JsonSerializer.Deserialize<JSONRPCNotification>(line, ProtocolJson.Options)!.Method));
        using JsonDocument handshake = JsonDocument.Parse(calls[0]);
        Assert.Equal("goblin-platform", handshake.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
        Dictionary<string, string> environment = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(fixture.Workspace.CodexHome, "environment.json")))!;
        Assert.False(environment.ContainsKey(Env.OpenaiApiKey));
        Assert.False(environment.ContainsKey(Env.GoblinSecret));
        Assert.Equal(fixture.Workspace.Home, environment[Env.Home]);
        Assert.Equal(fixture.Workspace.CodexHome, environment[Env.CodexHome]);
        string args = await File.ReadAllTextAsync(Path.Combine(fixture.Workspace.CodexHome, "arguments.json"));
        Assert.Contains("app-server", args);
        Assert.Contains("features.multi_agent=false", args);
        Assert.Contains("features.browser_use=false", args);
        Assert.Contains("features.computer_use=false", args);
        Assert.Contains("skills.include_instructions=false", args);
    }

    [Fact]
    public async Task ConcurrentRequestsCorrelateResponsesDeliveredInReverseOrder()
    {
        await using Fixture fixture = await Fixture.CreateAsync("reordered");
        await fixture.Client.StartAsync();
        Task<GetAccountResponse> first = fixture.ReadAsync();
        Task<GetAccountResponse> second = fixture.ReadAsync();
        Assert.Equal("2", Assert.IsType<ChatGPTAccount>((await first).Account).Email);
        Assert.Equal("3", Assert.IsType<ChatGPTAccount>((await second).Account).Email);
    }

    [Fact]
    public async Task JsonlHandlesSplitUtf8AndMultipleFrames()
    {
        await using Fixture fixture = await Fixture.CreateAsync("fragmented");
        await fixture.Client.StartAsync();
        Assert.Equal("测试🙂@example.test", Assert.IsType<ChatGPTAccount>((await fixture.ReadAsync()).Account).Email);
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
        string response = Assert.Single(await File.ReadAllLinesAsync(file));
        JSONRPCError reply = JsonSerializer.Deserialize<JSONRPCError>(response, ProtocolJson.Options)!;
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

    [Fact]
    public async Task PromptAdapterKeepsImmediateResultProvenanceAndIgnoresOtherThreads()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Client.StartAsync();
        (string reply, string model, long duration) = await new PromptRunner(fixture.Client, TimeSpan.FromSeconds(3))
            .RunAsync("Explain the connected account.");
        Assert.Equal("Hello from the connected account.", reply);
        Assert.Equal("test-model", model);
        Assert.True(duration >= 0);
        string requests = await File.ReadAllTextAsync(Path.Combine(fixture.Workspace.CodexHome, "prompt-requests.jsonl"));
        Assert.Contains("\"ephemeral\":true", requests);
        Assert.Contains("\"networkAccess\":false", requests);
    }

    [Theory]
    [InlineData("prompt-fail", "prompt_unauthorized")]
    [InlineData("prompt-usage-limit", "prompt_limit_reached")]
    [InlineData("prompt-rate-limit", "prompt_limit_reached")]
    [InlineData("prompt-unknown-string-error", "prompt_failed")]
    [InlineData("prompt-unknown-object-error", "prompt_failed")]
    [InlineData("prompt-large", "prompt_reply_too_large")]
    [InlineData("prompt-empty", "prompt_empty_reply")]
    public async Task PromptAdapterPreservesBoundedAndSanitizedFailurePolicy(string scenario, string expected)
    {
        await using Fixture fixture = await Fixture.CreateAsync(scenario);
        await fixture.Client.StartAsync();
        IntegrationFailure failure = await Assert.ThrowsAsync<IntegrationFailure>(() =>
            new PromptRunner(fixture.Client, TimeSpan.FromSeconds(3)).RunAsync("Verify the account."));
        Assert.Equal(expected, failure.Code);
        Assert.DoesNotContain("THIS-MUST-NOT-LEAK", failure.Message);
        Assert.DoesNotContain("PRIVATE-DETAILS", failure.Message);
    }

    [Theory]
    [InlineData("prompt-unknown-string-error")]
    [InlineData("prompt-unknown-object-error")]
    public async Task UnknownCodexErrorsFailWorkWithoutRetryingOrLeakingDetails(string scenario)
    {
        await using Fixture fixture = await Fixture.CreateAsync(scenario);
        var work = new WorkItem(NextId(), "Surface unknown runtime failures", DateTimeOffset.UtcNow);
        work.Assign(NextId(), DateTimeOffset.UtcNow);
        work.QueueExecution(NextId(), new("codex", NextId()), DateTimeOffset.UtcNow);
        IntegrationFailure failure = await Assert.ThrowsAsync<IntegrationFailure>(() =>
            new CodexWorkRunner(fixture.Client).RunAsync(work.Snapshot(), false, _ => Task.CompletedTask, default));
        Assert.DoesNotContain("THIS-MUST-NOT-LEAK", failure.Message);
        Assert.DoesNotContain("PRIVATE-DETAILS", failure.Message);
        string[] calls = await File.ReadAllLinesAsync(Path.Combine(fixture.Workspace.CodexHome, "prompt-requests.jsonl"));
        Assert.Single(calls, x => x.Contains("turn/start", StringComparison.Ordinal));
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

        public static async Task<Fixture> CreateAsync(string scenario = "manual", TimeSpan? timeout = null,
            bool commandWrapper = false)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "backend/schemas/codex"))) root = root.Parent;
            string data = Directory.CreateDirectory(Path.Combine(root!.FullName, ".artifacts", "codex-client-tests", Guid.NewGuid().ToString("N"))).FullName;
            string passwordFile = Path.Combine(data, "owner-password");
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            byte[] digest = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("codex-client-test"), salt, 600_000, HashAlgorithmName.SHA256, 32);
            await File.WriteAllTextAsync(passwordFile, $"pbkdf2-sha256$600000${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}\n");
            Workspace workspace = await Workspace.OpenAsync(data, "http://localhost:8787", passwordFile);
            string command = "node";
            if (commandWrapper)
            {
                if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                command = Path.Combine(data, "codex-wrapper");
                await File.WriteAllTextAsync(command, "#!/bin/sh\nexec node \"$@\"\n");
                File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return new(workspace, new(new()
            {
                CodexHome = workspace.CodexHome,
                Home = workspace.Home,
                Workspace = workspace.WorkingDirectory,
                Command = command,
                Arguments = [Path.Combine(root!.FullName, "tests/fixtures/fake-codex.mts"), scenario],
                RequestTimeout = timeout ?? TimeSpan.FromSeconds(3),
                ShutdownTimeout = TimeSpan.FromMilliseconds(150),
                Environment = new Dictionary<string, string?>
                {
                    [Env.Path] = Environment.GetEnvironmentVariable(Env.Path),
                    [Env.OpenaiApiKey] = "must-not-inherit",
                    [Env.GoblinSecret] = "must-not-inherit"
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
