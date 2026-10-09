using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Execution;

// Carries only an attempt capability, never an upstream GitHub credential.
public static class GitRepositoryClient
{
    private static async Task<HttpClient> ClientAsync()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(3),
            BaseAddress = new Uri((await File.ReadAllTextAsync("/run/credentials/repository-url")).Trim())
        };
        client.DefaultRequestHeaders.Add("X-Goblin-Repository", (await File.ReadAllTextAsync("/run/credentials/repository-capability")).Trim());
        return client;
    }

    public static async Task DownloadAsync(long attemptId, string path)
    {
        using HttpClient client = await ClientAsync();
        using HttpResponseMessage response = await client.GetAsync($"/internal/repository/{attemptId}/input", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using FileStream file = File.Create(path);
        await response.Content.CopyToAsync(file);
    }

    public static async Task<string?> SubmitAsync(long attemptId, string branch, string checkout, GitRepositoryOperationKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new IOException("Unsupported repository operation.");
        using HttpClient client = await ClientAsync();
        using HttpResponseMessage reservation = await client.PostAsync($"/internal/repository/{attemptId}/operation-id", null);
        reservation.EnsureSuccessStatusCode();
        long id = await reservation.Content.ReadFromJsonAsync<long>();
        string bundle = Path.Combine("/workspace", id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bundle");
        await GitRepositoryProcess.RunAsync(checkout, GitRepositoryProcess.CreateEnvironment(), CancellationToken.None,
            "bundle", "create", bundle, "refs/heads/" + branch);
        try
        {
            await using FileStream stream = File.OpenRead(bundle);
            using var content = new StreamContent(stream);
            using HttpResponseMessage accepted = await client.PostAsync($"/internal/repository/{attemptId}/{id}/{kind.WireValue()}", content);
            accepted.EnsureSuccessStatusCode();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            while (true)
            {
                GitRepositoryOperationResponse state = await client.GetFromJsonAsync<GitRepositoryOperationResponse>($"/internal/repository/{attemptId}/operations/{id}", ExecutionFiles.Json, deadline.Token) ?? throw new IOException();
                GitRepositoryOperationState status = state.Status;
                if (status == GitRepositoryOperationState.Succeeded) return state.Url;
                if (status is GitRepositoryOperationState.Failed or GitRepositoryOperationState.Uncertain) throw new IOException("Publishing needs attention. Inspect this Work before trying again.");
                await Task.Delay(1000, deadline.Token);
            }
        }
        finally { File.Delete(bundle); }
    }

    public static async Task<WorkSnapshot?> CurrentAsync(long attemptId)
    {
        using HttpClient client = await ClientAsync();
        return await client.GetFromJsonAsync<WorkSnapshot>($"/internal/repository/{attemptId}/current", ExecutionFiles.Json);
    }

    public static async Task<GitRepositorySetupMemory[]> SetupMemoryAsync(long attemptId)
    {
        using HttpClient client = await ClientAsync();
        return await client.GetFromJsonAsync<GitRepositorySetupMemory[]>($"/internal/repository/{attemptId}/setup-memory", ExecutionFiles.Json) ?? [];
    }

    public static async Task SaveSetupMemoryAsync(long attemptId, SetupMemoryWrite request)
    {
        using HttpClient client = await ClientAsync();
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/internal/repository/{attemptId}/setup-memory", request, ExecutionFiles.Json);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<WorkspaceCheckpoint> SaveCheckpointAsync(WorkSnapshot work, string commit)
    {
        using HttpClient client = await ClientAsync();
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/internal/repository/{work.Attempts[^1].Id}/checkpoint",
            new WorkspaceCheckpointWrite(work.Attempts[^1].TurnNumber, commit), ExecutionFiles.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkspaceCheckpoint>(ExecutionFiles.Json))!;
    }

    public static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            WorkerInput input = (await ExecutionFiles.ReadAsync<WorkerInput>("/run/input/input.json"))!;
            AttemptSnapshot attempt = input.Work.Attempts[^1];
            string? url = await SubmitAsync(attempt.Id, attempt.Target.GitRepository!.Grant!.Branch, "/workspace/repository", GitRepositoryOperationNames.Parse(arguments.Length == 1 ? arguments[0] : ""));
            if (arguments[0] == "fetch")
            {
                await DownloadAsync(attempt.Id, "/workspace/fetched.bundle");
                await GitRepositoryProcess.RunAsync("/workspace/repository", GitRepositoryProcess.CreateEnvironment(), CancellationToken.None,
                    "fetch", "--no-tags", "--", "/workspace/fetched.bundle", "+refs/heads/*:refs/remotes/origin/*");
            }
            Console.WriteLine(url); return 0;
        }
        catch { Console.Error.WriteLine("Repository operation needs attention. Check this Work in Goblin."); return 1; }
    }
}
