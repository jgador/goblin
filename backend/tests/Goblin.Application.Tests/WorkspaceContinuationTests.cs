using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Goblin.Core.Work;
using Goblin.Execution;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class WorkspaceContinuationTests
{
    [Fact]
    public async Task FreshAttemptPreservesUnpublishedCommitsIndexDirtyAndIgnoredFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "goblin-continuation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            async Task<string> Git(params string[] args)
            {
                var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in args) info.ArgumentList.Add(arg);
                using Process process = Process.Start(info)!;
                Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); await error; return (await output).Trim();
            }
            await Git("init", "--quiet"); await Git("config", "user.name", "Goblin"); await Git("config", "user.email", "goblin@example.test");
            await Git("remote", "add", "origin", "https://github.com/owner/repo.git");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "original\n");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "output.log\n");
            await Git("add", "."); await Git("commit", "--quiet", "-m", "baseline");
            await Git("checkout", "-b", "goblin/1/1");
            await File.WriteAllTextAsync(Path.Combine(root, "local.txt"), "unpublished commit\n");
            await Git("add", "local.txt"); await Git("commit", "--quiet", "-m", "unpublished");
            string head = await Git("rev-parse", "HEAD");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "staged edit\n"); await Git("add", "tracked.txt");
            await File.AppendAllTextAsync(Path.Combine(root, "tracked.txt"), "unstaged edit\n");
            await File.WriteAllTextAsync(Path.Combine(root, "untracked.txt"), "unfinished\n");
            await File.WriteAllTextAsync(Path.Combine(root, "output.log"), "ignored output\n");
            string status = await Git("status", "--porcelain"), staged = await Git("diff", "--cached"), dirty = await Git("diff");
            var repository = new RepositoryChange("owner/repo", "Goblin", "goblin@example.test",
                new(1, "generation", "account", "owner", 1, "main", "goblin/1/2"));
            await SandboxWorker.PrepareCheckoutAsync(root, new Dictionary<string, string>
            { ["PATH"] = Environment.GetEnvironmentVariable("PATH")!, ["GIT_CONFIG_GLOBAL"] = "/dev/null" }, repository);
            Assert.Equal("goblin/1/2", await Git("branch", "--show-current"));
            Assert.Equal(head, await Git("rev-parse", "HEAD"));
            Assert.Equal(status, await Git("status", "--porcelain"));
            Assert.Equal(staged, await Git("diff", "--cached")); Assert.Equal(dirty, await Git("diff"));
            Assert.Equal("ignored output\n", await File.ReadAllTextAsync(Path.Combine(root, "output.log")));
            string first = SandboxWorker.ClaimPrefix(root, 1, 1), next = SandboxWorker.ClaimPrefix(root, 2, 1);
            using (File.Open(first + ".claimed", FileMode.CreateNew)) { }
            using (File.Open(next + ".claimed", FileMode.CreateNew)) { }
            Assert.Throws<IOException>(() => File.Open(next + ".claimed", FileMode.CreateNew));
        }
        finally { Directory.Delete(root, true); }
    }
}
