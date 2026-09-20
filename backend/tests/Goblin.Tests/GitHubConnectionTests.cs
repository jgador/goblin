using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Goblin.Integrations.GitHub;
using Xunit;

namespace Goblin.Tests;

public sealed class GitHubConnectionTests
{
    [Fact]
    public async Task DeviceLoginSurvivesPollingAndRestartWithoutExposingToken()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "goblin-gh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string cli = await FakeAsync(root);
            using var connection = new GitHubConnection(Path.Combine(root, "profile"), cli);
            await connection.StartAsync();
            GitHubState state = await UntilAsync(connection, s => s.UserCode is not null);
            Assert.Equal("ABCD-1234", state.UserCode);
            Assert.Equal("https://github.com/login/device", state.VerificationUrl);
            await Assert.ThrowsAsync<GitHubFailure>(() => connection.StartAsync());
            await File.WriteAllTextAsync(Path.Combine(root, "finish"), "done");
            state = await UntilAsync(connection, s => s.Login is not null);
            Assert.Equal("test-owner", state.Login);
            Assert.DoesNotContain("private-test-token", JsonSerializer.Serialize(state));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(connection.Profile, "hosts.yml")));
            using var restarted = new GitHubConnection(Path.Combine(root, "profile"), cli);
            Assert.Equal(state.Account, (await restarted.StatusAsync()).Account);
            Assert.Equal("Connected", (await restarted.CheckAsync()).Status);
            await File.WriteAllTextAsync(Path.Combine(root, "reject"), "reject");
            Assert.Equal("Unavailable", (await restarted.CheckAsync()).Status);
            Assert.Null((await restarted.DisconnectAsync()).Account);
            Assert.False(Directory.Exists(restarted.Profile));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task CancelPreventsLateCredentialInstallation()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "goblin-gh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var connection = new GitHubConnection(Path.Combine(root, "profile"), await FakeAsync(root));
            await connection.StartAsync();
            await UntilAsync(connection, s => s.UserCode is not null);
            Assert.Null((await connection.DisconnectAsync()).Account);
            await File.WriteAllTextAsync(Path.Combine(root, "finish"), "late");
            Assert.Null((await connection.StatusAsync()).Account);
            Assert.False(Directory.Exists(connection.Profile));
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task<GitHubState> UntilAsync(GitHubConnection connection, Func<GitHubState, bool> predicate)
    {
        for (int i = 0; i < 200; i++) { GitHubState state = await connection.StatusAsync(); if (predicate(state)) return state; await Task.Delay(25); }
        throw new TimeoutException();
    }
    private static async Task<string> FakeAsync(string root)
    {
        string script = Path.Combine(root, "gh-test");
        await File.WriteAllTextAsync(script, """
            #!/bin/sh
            set -eu
            base=$(dirname "$0")
            test -z "${GH_TOKEN:-}"
            test -z "${GITHUB_TOKEN:-}"
            test "$HOME" = "$GH_CONFIG_DIR"
            if [ "$1 $2" = "auth login" ]; then
              printf '! First copy your one-time code: ABCD-1234\nOpen this URL to continue in your web browser: https://github.com/login/device\n' >&2
              while [ ! -f "$base/finish" ]; do sleep 0.05; done
              printf 'private-test-token' > "$GH_CONFIG_DIR/hosts.yml"
            else
              test ! -f "$base/reject"
              printf '{"id":42,"login":"test-owner"}'
            fi
            """);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
