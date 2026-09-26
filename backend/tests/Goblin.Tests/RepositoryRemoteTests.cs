using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Integrations.GitHub;
using Xunit;

namespace Goblin.Tests;

public sealed class RepositoryRemoteTests
{
    [Fact]
    public async Task RealBundlesPublishOnlyAssignedBranchAndNeverRunSandboxHooks()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "goblin-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source"), origin = Path.Combine(root, "origin.git"), sandbox = Path.Combine(root, "sandbox"), broker = Path.Combine(root, "broker");
            await Git(root, "init", "-b", "main", source);
            await Git(source, "config", "user.name", "Fixture"); await Git(source, "config", "user.email", "fixture@example.test");
            await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "original");
            await Git(source, "add", "."); await Git(source, "commit", "-m", "Initial");
            string initial = await Git(source, "rev-parse", "HEAD");
            await Git(root, "clone", "--bare", source, origin);
            string profile = Path.Combine(root, "profile"); Directory.CreateDirectory(Path.Combine(profile, "active"));
            var account = new RepositoryAccount("generation", "42", "owner");
            await File.WriteAllTextAsync(Path.Combine(profile, "active", "account.json"), JsonSerializer.Serialize(account, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            string cli = Path.Combine(root, "fake-gh");
            await File.WriteAllTextAsync(cli, "#!/bin/sh\nif [ \"$2\" = user ]; then printf '{\"id\":42,\"login\":\"owner\"}'; else printf '{\"id\":22,\"full_name\":\"owner/repo\",\"default_branch\":\"main\",\"permissions\":{\"push\":true}}'; fi\n");
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var github = new GitHubConnection(profile, cli);
            var remote = new GitHubRepositoryRemote(github, origin);
            var repository = new RepositoryChange("owner/repo", "Goblin", "goblin@example.test", new(1, "generation", "42", "owner", 22, "main", "goblin/1/2", AllowPush: true, AllowPullRequest: true));
            await remote.PrepareAsync(repository, broker, null, default);
            await Git(root, "clone", "--branch", "goblin/1/2", Path.Combine(broker, "input.bundle"), sandbox);
            await Git(sandbox, "config", "user.name", "Goblin"); await Git(sandbox, "config", "user.email", "goblin@example.test");
            await File.WriteAllTextAsync(Path.Combine(sandbox, "hello.txt"), "changed");
            await Git(sandbox, "add", "."); await Git(sandbox, "commit", "-m", "Change");
            string changed = await Git(sandbox, "rev-parse", "HEAD");
            string hook = Path.Combine(sandbox, ".git/hooks/pre-push");
            await File.WriteAllTextAsync(hook, "#!/bin/sh\ntouch '" + Path.Combine(root, "hook-ran") + "'\n");
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string bundle = Path.Combine(root, "changes.bundle");
            await Git(sandbox, "bundle", "create", bundle, "refs/heads/goblin/1/2");
            string commit = await remote.InspectBundleAsync(repository, broker, bundle, default);
            Assert.Equal(changed, commit);
            var local = new RepositoryChange(repository.Repository, repository.GitAuthorName, repository.GitAuthorEmail,
                repository.Grant! with { AllowPush = false, AllowPullRequest = false });
            Assert.Equal(commit, await remote.InspectBundleAsync(local, broker, bundle, default));
            await Assert.ThrowsAsync<GitHubFailure>(() => remote.ExecuteAsync(local, broker, "publish", commit, default));
            await Assert.ThrowsAsync<GitHubFailure>(() => remote.ExecuteAsync(local, broker, "pull-request", commit, default));
            Assert.DoesNotContain("goblin/1/2", await Git(origin, "for-each-ref", "--format=%(refname)"));
            await remote.ExecuteAsync(repository, broker, "publish", commit, default);
            Assert.Equal(initial, await Git(origin, "rev-parse", "main"));
            Assert.Equal(changed, await Git(origin, "rev-parse", "goblin/1/2"));
            Assert.False(File.Exists(Path.Combine(root, "hook-ran")));
            Assert.NotNull(await remote.ReconcileAsync(repository, broker, "publish", commit, default));
            // A resumed published attempt needs its confirmed remote head for the next lease.
            Directory.Delete(broker, true);
            await remote.PrepareCheckpointAsync(repository, broker,
                new(1, 1, 2, 1, 1, repository.Repository, repository.Grant!.Branch, commit, DateTimeOffset.UtcNow), default);
            await remote.InspectBundleAsync(repository, broker, bundle, default);
            await remote.ExecuteAsync(repository, broker, "publish", commit, default);
            await Assert.ThrowsAsync<GitHubFailure>(() => remote.ExecuteAsync(repository, broker, "merge", commit, default));
            await Git(sandbox, "checkout", "-b", "wrong-branch");
            await Git(sandbox, "bundle", "create", Path.Combine(root, "wrong.bundle"), "refs/heads/wrong-branch");
            await Assert.ThrowsAsync<GitHubFailure>(() => remote.InspectBundleAsync(repository, broker, Path.Combine(root, "wrong.bundle"), default));
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (string value in arguments) info.ArgumentList.Add(value);
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await stderr;
        Assert.Equal(0, process.ExitCode);
        return (await stdout).Trim();
    }
}
