using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Execution;
using Xunit;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Application.Tests;

public sealed class GitRepositoryProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "goblin-git-process-" + Guid.NewGuid().ToString("N"));

    public GitRepositoryProcessTests()
    {
        Directory.CreateDirectory(_root);
        using Process process = Process.Start(new ProcessStartInfo("git")
        { WorkingDirectory = _root, ArgumentList = { "init", "--quiet" } })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task UsesOnlyTheSuppliedEnvironmentAndDisablesRepositoryHooks()
    {
        if (!OperatingSystem.IsLinux()) return;
        string hook = Path.Combine(_root, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 99\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string secret = "GOBLIN_GIT_PROCESS_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(secret, "ambient-secret");
        try
        {
            var environment = new Dictionary<string, string>
            {
                [Env.Path] = Environment.GetEnvironmentVariable(Env.Path)!,
                ["GOBLIN_ALLOWED"] = "allowed"
            };
            string output = await GitRepositoryProcess.RunAsync(_root, environment, CancellationToken.None,
                "-c", $"alias.environment=!f() {{ printf '%s|%s' \"$GOBLIN_ALLOWED\" \"${secret}\"; }}; f", "environment");
            Assert.Equal("allowed|", output);

            await GitRepositoryProcess.RunAsync(_root, environment, CancellationToken.None,
                "-c", "user.name=Goblin", "-c", "user.email=goblin@example.test",
                "commit", "--allow-empty", "--quiet", "-m", "isolated");
        }
        finally { Environment.SetEnvironmentVariable(secret, null); }
    }

    [Fact]
    public async Task DeadlineStopsTheEntireGitProcessTree()
    {
        if (!OperatingSystem.IsLinux()) return;
        string marker = Path.Combine(_root, "child-survived");
        var environment = new Dictionary<string, string>
        {
            [Env.Path] = Environment.GetEnvironmentVariable(Env.Path)!
        };

        await Assert.ThrowsAsync<TimeoutException>(() => GitRepositoryProcess.RunAsync(_root, environment,
            TimeSpan.FromMilliseconds(100), CancellationToken.None,
            "-c", $"alias.wait=!f() {{ (sleep 1; touch '{marker}') & wait; }}; f", "wait"));
        await Task.Delay(1500);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task ExcessiveCapturedOutputRetiresTheProcessWithoutExposingItsOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        string marker = Path.Combine(_root, "output-child-survived");
        string release = Path.Combine(_root, "release-child");
        IOException error = await Assert.ThrowsAsync<IOException>(() => GitRepositoryProcess.RunAsync(_root,
            GitRepositoryProcess.CreateEnvironment(_root), CancellationToken.None, "-c",
            $"alias.output=!f() {{ (while [ ! -f '{release}' ]; do sleep 0.01; done; touch '{marker}') & head -c 8388608 /dev/zero; wait; }}; f", "output"));
        Assert.Equal("Repository operation output is too large.", error.Message);
        await File.WriteAllTextAsync(release, "release");
        await Task.Delay(500);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task StreamingPreservesEveryByteBeyondTheCaptureLimitAndDrainsLargeStderr()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = Path.Combine(_root, "changes.patch");
        await GitRepositoryProcess.WriteOutputAsync(_root, GitRepositoryProcess.CreateEnvironment(_root), path,
            CancellationToken.None, "-c",
            "alias.output=!head -c 8388608 /dev/zero; head -c 8388608 /dev/zero >&2; printf 'tail'", "output");
        Assert.Equal(8388612, new FileInfo(path).Length);
        await using FileStream file = File.OpenRead(path);
        byte[] buffer = new byte[65536];
        for (int chunk = 0; chunk < 128; chunk++)
        {
            await file.ReadExactlyAsync(buffer);
            Assert.All(buffer, value => Assert.Equal(0, value));
        }
        byte[] tail = new byte[4];
        await file.ReadExactlyAsync(tail);
        Assert.Equal("tail", System.Text.Encoding.UTF8.GetString(tail));
    }

    [Fact]
    public async Task FailedStreamingPreservesTheExistingPatchAndRemovesThePartialFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = Path.Combine(_root, "changes.patch");
        await File.WriteAllTextAsync(path, "saved-patch");
        IOException error = await Assert.ThrowsAsync<IOException>(() => GitRepositoryProcess.WriteOutputAsync(_root,
            GitRepositoryProcess.CreateEnvironment(_root), path, CancellationToken.None,
            "-c", "alias.output=!printf 'partial'; printf 'private-upstream-error' >&2; exit 1", "output"));
        Assert.Equal("Repository operation failed.", error.Message);
        Assert.Equal("saved-patch", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_root, "changes.patch.*.tmp"));
    }

    [Fact]
    public async Task CancellationRetiresTheProcessTreeAndRemovesThePartialStream()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = Path.Combine(_root, "changes.patch");
        string marker = Path.Combine(_root, "cancel-child-survived");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitRepositoryProcess.WriteOutputAsync(_root,
            GitRepositoryProcess.CreateEnvironment(_root), path, cancellation.Token, "-c",
            $"alias.output=!f() {{ (sleep 1; touch '{marker}') & printf 'partial'; wait; }}; f", "output"));
        await Task.Delay(1500);
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_root, "changes.patch.*.tmp"));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
