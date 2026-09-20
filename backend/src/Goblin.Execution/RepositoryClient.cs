using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Execution;

// Carries only an attempt capability, never an upstream GitHub credential.
public static class RepositoryClient
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
    public static async Task<string?> SubmitAsync(long attemptId, string branch, string checkout, string kind)
    {
        if (kind is not ("publish" or "pull-request" or "fetch")) throw new IOException("Unsupported repository operation.");
        Guid id = Guid.NewGuid();
        string bundle = Path.Combine("/workspace", id.ToString("N") + ".bundle");
        var start = new ProcessStartInfo("git") { UseShellExecute = false, WorkingDirectory = checkout, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-c", "core.hooksPath=/dev/null", "bundle", "create", bundle, "refs/heads/" + branch }) start.ArgumentList.Add(argument);
        using Process git = Process.Start(start)!;
        Task output = git.StandardOutput.ReadToEndAsync(), error = git.StandardError.ReadToEndAsync();
        await git.WaitForExitAsync(); await output; await error;
        if (git.ExitCode != 0) throw new IOException("Could not prepare repository changes.");
        try
        {
            using HttpClient client = await ClientAsync();
            await using FileStream stream = File.OpenRead(bundle);
            using var content = new StreamContent(stream);
            using HttpResponseMessage accepted = await client.PostAsync($"/internal/repository/{attemptId}/{id}/{kind}", content);
            accepted.EnsureSuccessStatusCode();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            while (true)
            {
                using JsonDocument state = await client.GetFromJsonAsync<JsonDocument>($"/internal/repository/{attemptId}/operations/{id}", deadline.Token) ?? throw new IOException();
                string? status = state.RootElement.GetProperty("state").GetString();
                if (status == "Succeeded") return state.RootElement.GetProperty("url").GetString();
                if (status is "Failed" or "Uncertain") throw new IOException("Publishing needs attention. Inspect this Work before trying again.");
                await Task.Delay(1000, deadline.Token);
            }
        }
        finally { File.Delete(bundle); }
    }
    public static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            WorkerInput input = (await ExecutionFiles.ReadAsync<WorkerInput>("/run/input/input.json"))!;
            AttemptSnapshot attempt = input.Work.Attempts[^1];
            string? url = await SubmitAsync(attempt.Id, attempt.Target.Repository!.Grant!.Branch, "/workspace/repository", arguments.Length == 1 ? arguments[0] : "");
            if (arguments[0] == "fetch")
            {
                await DownloadAsync(attempt.Id, "/workspace/fetched.bundle");
                var start = new ProcessStartInfo("git") { WorkingDirectory = "/workspace/repository", UseShellExecute = false };
                foreach (string value in new[] { "-c", "core.hooksPath=/dev/null", "fetch", "--no-tags", "--", "/workspace/fetched.bundle", "+refs/heads/*:refs/remotes/origin/*" }) start.ArgumentList.Add(value);
                using Process process = Process.Start(start)!;
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new IOException();
            }
            Console.WriteLine(url); return 0;
        }
        catch { Console.Error.WriteLine("Repository operation needs attention. Check this Work in Goblin."); return 1; }
    }
}
