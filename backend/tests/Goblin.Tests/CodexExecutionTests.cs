using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Repositories;
using Goblin.Core.Work;
using Goblin.Execution;
using Goblin.Integrations.Codex;
using Goblin.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Goblin.Tests;

public sealed class CodexExecutionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PinnedRuntimeExecutesModelCommandsOnlyForRepositoryWork(bool repositoryExecution)
    {
        string root = Path.Combine(Path.GetTempPath(), "goblin-command-test-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home"), codexHome = Path.Combine(root, "codex"), workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(codexHome);
        Directory.CreateDirectory(workspace);
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && !File.Exists(Path.Combine(source.FullName, "package.json"))) source = source.Parent;
        string cli = Path.Combine(source!.FullName, "node_modules/@openai/codex/bin/codex.js");
        Assert.True(File.Exists(cli), "Install the pinned Codex runtime with npm ci before running this test.");
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        await using WebApplication model = builder.Build();
        model.Urls.Add("http://127.0.0.1:0");
        string? toolOutput = null;
        string requests = "";
        var setup = new RepositorySetup("fixture-tools", "The repository needs this fixture tool", ["fixture 1.0"],
            ["install-fixture"], ["package.json"], [new("test -f command-marker.txt", "")]);
        var memory = new RepositorySetupMemory(9007199254740993, 11, 12, 1, "older-work", new string('a', 40), "fixture-image",
            DateTimeOffset.UtcNow, new(setup, [new("package.json", new string('b', 64))], new string('c', 64)));
        int calls = 0;
        // A local model fixture drives the real runtime's nested command tool.
        // No user credentials or paid model requests are needed. command/exec
        // alone bypasses this path and cannot detect a disabled code-mode host.
        model.MapGet("/responses", () => Results.StatusCode(StatusCodes.Status426UpgradeRequired));
        model.MapPost("/responses", async context =>
        {
            using JsonDocument request = await JsonDocument.ParseAsync(context.Request.Body);
            requests += request.RootElement.GetRawText();
            JsonElement output = request.RootElement.GetProperty("input").EnumerateArray()
                .LastOrDefault(item => item.GetProperty("type").GetString() == "custom_tool_call_output");
            if (output.ValueKind != JsonValueKind.Undefined) toolOutput = output.GetProperty("output").ToString();
            object item = ++calls == 1
                ? new
                {
                    type = "custom_tool_call",
                    id = "tool-probe",
                    call_id = "command-probe",
                    name = "exec",
                    input = "text(await tools.exec_command({cmd: 'echo command-host-ready > command-marker.txt', yield_time_ms: 1000, max_output_tokens: 100}));"
                }
                : new
                {
                    type = "message",
                    id = "message-probe",
                    role = "assistant",
                    status = "completed",
                    content = new[] { new { type = "output_text", text = repositoryExecution
                        ? JsonSerializer.Serialize(new { kind = "result", text = "Command probe finished.", releaseWorkspace = true, setup = new[] { setup } }, ExecutionFiles.Json)
                        : "{\"kind\":\"result\",\"text\":\"Command probe finished.\",\"releaseWorkspace\":true}" } }
                };
            object[] events = [
                new { type = "response.created", response = new { id = "response-probe", status = "in_progress", output = Array.Empty<object>() } },
                new { type = "response.output_item.added", output_index = 0, item },
                new { type = "response.output_item.done", output_index = 0, item },
                new { type = "response.completed", response = new { id = "response-probe", status = "completed", output = new[] { item },
                    usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } } }
            ];
            context.Response.ContentType = "text/event-stream";
            foreach (object value in events)
            {
                JsonElement frame = JsonSerializer.SerializeToElement(value);
                await context.Response.WriteAsync("event: " + frame.GetProperty("type").GetString() + "\ndata: " + frame.GetRawText() + "\n\n");
            }
        });
        try
        {
            await model.StartAsync();
            await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"),
                "openai_base_url=" + JsonSerializer.Serialize(model.Urls.Single()) + "\n[features]\nenable_request_compression=false\n");
            await using var client = new CodexClient(new()
            {
                Home = home,
                CodexHome = codexHome,
                Workspace = workspace,
                Command = "node",
                Arguments = [cli],
                Environment = new Dictionary<string, string?> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") },
                RepositoryExecution = repositoryExecution
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? runtimeFailure = null;
            client.Notification += notification =>
            {
                if (notification is ErrorServerNotification error)
                    runtimeFailure = JsonSerializer.Serialize(error.Params, ProtocolJson.Options);
                if (notification is TurnCompletedServerNotification done && done.Params.Turn.Status != TurnStatus.Completed)
                    runtimeFailure = JsonSerializer.Serialize(done.Params, ProtocolJson.Options);
            };
            await client.StartAsync(timeout.Token);
            await client.RequestAsync<LoginAccountParams, LoginAccountResponse>("account/login/start",
                new ApiKeyLoginAccountParams { ApiKey = "sk-goblin-command-test-not-a-real-key" }, timeout.Token);
            var work = new WorkItem(1, "Exercise the command host", DateTimeOffset.UtcNow);
            work.Assign(1, DateTimeOffset.UtcNow);
            work.QueueExecution(1, new("codex", 1, "gpt-5.6-sol"), DateTimeOffset.UtcNow);
            ExecutionObservation result;
            try
            {
                result = await new CodexWorkRunner(client).RunAsync(work.Snapshot(), repositoryExecution,
                    _ => Task.CompletedTask, timeout.Token, [memory]);
            }
            catch (IntegrationFailure)
            {
                Assert.Fail($"Local model fixture failed after {calls} requests: {runtimeFailure}");
                throw;
            }
            Assert.Equal(ObservationKind.Result, result.Kind);
            Assert.Equal(2, calls);
            Assert.NotNull(toolOutput);
            string marker = Path.Combine(workspace, "command-marker.txt");
            if (repositoryExecution)
            {
                Assert.DoesNotContain("code-mode host is disabled", toolOutput);
                Assert.Equal("command-host-ready", (await File.ReadAllTextAsync(marker)).Trim());
                Assert.Contains("RepositorySetupMemory", requests);
                Assert.Contains("fixture-tools", requests);
                Assert.Contains("prior observations", requests);
                Assert.Equal(setup.Topic, Assert.Single(result.Setup!).Topic);
                await File.WriteAllTextAsync(Path.Combine(workspace, "package.json"), "{}");
                using Process git = Process.Start(new ProcessStartInfo("git") { WorkingDirectory = workspace, ArgumentList = { "init", "--quiet" } })!;
                await git.WaitForExitAsync();
                var verifier = new RepositorySetupWorkspace(workspace, new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH")! });
                Assert.Single(await verifier.VerifyAsync(result.Setup!, timeout.Token));
            }
            else
            {
                Assert.Contains("code-mode host is disabled", toolOutput);
                Assert.False(File.Exists(marker));
                Assert.DoesNotContain("RepositorySetupMemory", requests);
                Assert.Null(result.Setup);
            }
        }
        finally
        {
            await model.StopAsync();
            Directory.Delete(root, true);
        }
    }
}
