using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;
using Goblin.Execution;
using Xunit;
using Env = Goblin.Contracts.Configuration.EnvironmentVariables;

namespace Goblin.Application.Tests;

public sealed class GitRepositorySetupWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "goblin-setup-test-" + Guid.NewGuid().ToString("N"));
    private readonly GitRepositorySetupWorkspace _workspace;

    private static GitRepositorySetup Setup => new("python-tests", "Python is needed for repository tests", ["fixture-python 3.12"],
        ["install-fixture-python"], ["pyproject.toml"], [new("printf '3.12'", "3.12")]);

    public GitRepositorySetupWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        using Process process = Process.Start(new ProcessStartInfo("git")
        { WorkingDirectory = _root, ArgumentList = { "init", "--quiet" } })!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        File.WriteAllText(Path.Combine(_root, "pyproject.toml"), "[project]\nname = 'fixture'\n");
        _workspace = new(_root, new Dictionary<string, string> { [Env.Path] = Environment.GetEnvironmentVariable(Env.Path)! });
    }

    private static GitRepositorySetupMemory Memory(VerifiedGitRepositorySetup observation, string branch = "old-branch",
        DateTimeOffset? verifiedAt = null) =>
        new()
        {
            Id = 9007199254740993,
            WorkId = 1,
            AttemptId = 2,
            TurnNumber = 1,
            Branch = branch,
            Commit = new string('a', 40),
            Environment = "image-one",
            VerifiedAt = verifiedAt ?? DateTimeOffset.UtcNow.AddMonths(-3),
            Observation = observation
        };

    [Fact]
    public async Task ReusesOldVerifiedMemoryAcrossBranchesAndSourceEditsButNeverRunsItsRecipe()
    {
        GitRepositorySetup setup = Setup with { Commands = ["touch must-not-execute"] };
        VerifiedGitRepositorySetup observation = Assert.Single(await _workspace.VerifyAsync([setup], default));
        await File.WriteAllTextAsync(Path.Combine(_root, "app.py"), "print('new source')");
        GitRepositorySetupMemory memory = Memory(observation);
        Assert.Equal(memory, Assert.Single(await _workspace.SelectAsync([memory], "image-one", default)));
        Assert.False(File.Exists(Path.Combine(_root, "must-not-execute")));
        Assert.Empty(await _workspace.SelectAsync([memory], "image-two", default));
    }

    [Fact]
    public async Task ChangedAddedAndRemovedRequirementsInvalidateWithoutErasingOlderBranchMemory()
    {
        GitRepositorySetupMemory old = Memory(Assert.Single(await _workspace.VerifyAsync([Setup], default)));
        string path = Path.Combine(_root, "pyproject.toml"), original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original + "version='2'\n");
        GitRepositorySetupMemory newer = Memory(Assert.Single(await _workspace.VerifyAsync([Setup], default)), "new-branch",
            verifiedAt: DateTimeOffset.UtcNow);
        Assert.Equal(newer, Assert.Single(await _workspace.SelectAsync([old, newer], "image-one", default)));
        await File.WriteAllTextAsync(path, original);
        Assert.Equal(old, Assert.Single(await _workspace.SelectAsync([old, newer], "image-one", default)));
        await File.WriteAllTextAsync(Path.Combine(_root, "global.json"), "{}");
        Assert.Empty(await _workspace.SelectAsync([old], "image-one", default));
        File.Delete(Path.Combine(_root, "global.json"));
        File.Delete(path);
        Assert.Empty(await _workspace.SelectAsync([old], "image-one", default));
    }

    [Fact]
    public async Task CustomSetupScriptAndInstructionsArePartOfApplicability()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "prepare.sh"), "echo prepare-v1");
        GitRepositorySetup setup = Setup with { Files = ["pyproject.toml", "prepare.sh"] };
        GitRepositorySetupMemory old = Memory(Assert.Single(await _workspace.VerifyAsync([setup], default)));
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "Use different tooling.");
        Assert.Empty(await _workspace.SelectAsync([old], "image-one", default));
        File.Delete(Path.Combine(_root, "AGENTS.md"));
        await File.WriteAllTextAsync(Path.Combine(_root, "prepare.sh"), "echo prepare-v2");
        Assert.Empty(await _workspace.SelectAsync([old], "image-one", default));
    }

    [Theory]
    [InlineData("exit 1", "")]
    [InlineData("printf 3.11", "3.12")]
    [InlineData("printf changed > pyproject.toml", "")]
    public async Task FailedIncorrectOrInputChangingChecksProduceNoVerifiedMemory(string command, string expected)
    {
        await Assert.ThrowsAsync<IOException>(() => _workspace.VerifyAsync([Setup with { Checks = [new(command, expected)] }], default));
    }

    [Fact]
    public async Task ReplacedComputeMustVerifyAgainEvenWhenRequirementsMatch()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "installed-version"), "3.12");
        GitRepositorySetup setup = Setup with { Checks = [new("cat installed-version", "3.12")] };
        GitRepositorySetupMemory memory = Memory(Assert.Single(await _workspace.VerifyAsync([setup], default)));
        File.Delete(Path.Combine(_root, "installed-version"));
        Assert.Single(await _workspace.SelectAsync([memory], "image-one", default));
        await Assert.ThrowsAsync<IOException>(() => _workspace.VerifyAsync([setup], default));
    }

    [Fact]
    public async Task RejectsEscapesSymlinksOversizedOutputAndUnverifiedClaims()
    {
        await Assert.ThrowsAsync<IOException>(() => _workspace.VerifyAsync([Setup with { Files = ["../outside"] }], default));
        File.CreateSymbolicLink(Path.Combine(_root, "linked"), "/etc/hostname");
        await Assert.ThrowsAsync<IOException>(() => _workspace.VerifyAsync([Setup with { Files = ["linked"] }], default));
        await Assert.ThrowsAsync<IOException>(() => _workspace.VerifyAsync([Setup with { Checks = [] }], default));
        await Assert.ThrowsAsync<IOException>(() => _workspace.VerifyAsync([Setup with { Checks = [new("head -c 20000 /dev/zero", "")] }], default));
        Assert.Empty(await _workspace.VerifyAsync([], default));
    }

    [Fact]
    public async Task CancellationStopsVerification()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _workspace.VerifyAsync(
            [Setup with { Checks = [new("sleep 60", "")] }], timeout.Token));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
