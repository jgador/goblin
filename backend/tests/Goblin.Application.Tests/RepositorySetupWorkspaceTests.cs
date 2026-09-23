using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Repositories;
using Goblin.Execution;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class RepositorySetupWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "goblin-setup-test-" + Guid.NewGuid().ToString("N"));
    private readonly RepositorySetupWorkspace _workspace;
    private static RepositorySetup Setup => new("python-tests", "Python is needed for repository tests", ["fixture-python 3.12"],
        ["install-fixture-python"], ["pyproject.toml"], [new("printf '3.12'", "3.12")]);

    public RepositorySetupWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        using Process process = Process.Start(new ProcessStartInfo("git")
        { WorkingDirectory = _root, ArgumentList = { "init", "--quiet" } })!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        File.WriteAllText(Path.Combine(_root, "pyproject.toml"), "[project]\nname = 'fixture'\n");
        _workspace = new(_root, new Dictionary<string, string> { ["PATH"] = Environment.GetEnvironmentVariable("PATH")! });
    }

    private static RepositorySetupMemory Memory(VerifiedRepositorySetup observation, string branch = "old-branch") =>
        new(9007199254740993, 1, 2, 1, branch, new string('a', 40), "image-one", DateTimeOffset.UtcNow.AddMonths(-3), observation);

    [Fact]
    public async Task ReusesOldVerifiedMemoryAcrossBranchesAndSourceEditsButNeverRunsItsRecipe()
    {
        RepositorySetup setup = Setup with { Commands = ["touch must-not-execute"] };
        VerifiedRepositorySetup observation = Assert.Single(await _workspace.VerifyAsync([setup], default));
        await File.WriteAllTextAsync(Path.Combine(_root, "app.py"), "print('new source')");
        RepositorySetupMemory memory = Memory(observation);
        Assert.Equal(memory, Assert.Single(await _workspace.SelectAsync([memory], "image-one", default)));
        Assert.False(File.Exists(Path.Combine(_root, "must-not-execute")));
        Assert.Empty(await _workspace.SelectAsync([memory], "image-two", default));
    }

    [Fact]
    public async Task ChangedAddedAndRemovedRequirementsInvalidateWithoutErasingOlderBranchMemory()
    {
        RepositorySetupMemory old = Memory(Assert.Single(await _workspace.VerifyAsync([Setup], default)));
        string path = Path.Combine(_root, "pyproject.toml"), original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original + "version='2'\n");
        RepositorySetupMemory newer = Memory(Assert.Single(await _workspace.VerifyAsync([Setup], default)), "new-branch")
            with
        { VerifiedAt = DateTimeOffset.UtcNow };
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
        RepositorySetup setup = Setup with { Files = ["pyproject.toml", "prepare.sh"] };
        RepositorySetupMemory old = Memory(Assert.Single(await _workspace.VerifyAsync([setup], default)));
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
        RepositorySetup setup = Setup with { Checks = [new("cat installed-version", "3.12")] };
        RepositorySetupMemory memory = Memory(Assert.Single(await _workspace.VerifyAsync([setup], default)));
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
