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

    public void Dispose() => Directory.Delete(_root, true);
}
