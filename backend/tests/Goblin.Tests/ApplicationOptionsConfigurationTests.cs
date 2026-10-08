using System;
using System.Collections.Generic;
using Goblin.Contracts.Configuration;
using Goblin.Integrations.Codex;
using Goblin.Web;
using Xunit;

namespace Goblin.Tests;

public sealed class ApplicationOptionsConfigurationTests
{
    [Fact]
    public void DefaultsAreMappedAtTheExecutableBoundary()
    {
        ApplicationOptions options = ApplicationOptionsConfiguration.Read(_ => null);

        Assert.Equal(".goblin-auth", options.DataDirectory);
        Assert.Null(options.PasswordHashFile);
        Assert.Equal("http://localhost:8787", options.PublicOrigin);
        Assert.False(options.AllowInsecureHttp);
        Assert.Equal("http://127.0.0.1:8787", options.ListenUrl);
        Assert.True(options.EnableWork);
        Assert.Null(options.HeadlampUrl);
        Assert.Null(options.VictoriaLogsUrl);
        Assert.Equal("codex", options.ConfigureCodex!(Codex("codex")).Command);
    }

    [Fact]
    public void EnvironmentOverridesAreMappedWithoutChangingTheirMeaning()
    {
        var environment = new Dictionary<string, string?>
        {
            [EnvironmentVariables.GoblinPort] = "9000",
            [EnvironmentVariables.GoblinPublicOrigin] = "https://goblin.example",
            [EnvironmentVariables.GoblinHost] = "0.0.0.0",
            [EnvironmentVariables.GoblinDataDir] = "/data",
            [EnvironmentVariables.GoblinPasswordHashFile] = "/password",
            [EnvironmentVariables.GoblinAllowInsecureHttp] = "TRUE",
            [EnvironmentVariables.GoblinWorkEnabled] = "FALSE",
            [EnvironmentVariables.GoblinHeadlampUrl] = "https://headlamp.example",
            [EnvironmentVariables.GoblinVictorialogsUrl] = "https://logs.example",
            [EnvironmentVariables.GoblinCodexCommand] = "/usr/local/bin/codex"
        };

        ApplicationOptions options = ApplicationOptionsConfiguration.Read(name => environment.GetValueOrDefault(name));

        Assert.Equal("/data", options.DataDirectory);
        Assert.Equal("/password", options.PasswordHashFile);
        Assert.Equal("https://goblin.example", options.PublicOrigin);
        Assert.True(options.AllowInsecureHttp);
        Assert.Equal("http://0.0.0.0:9000", options.ListenUrl);
        Assert.False(options.EnableWork);
        Assert.Equal("https://headlamp.example", options.HeadlampUrl);
        Assert.Equal("https://logs.example", options.VictoriaLogsUrl);
        Assert.Equal("/usr/local/bin/codex", options.ConfigureCodex!(Codex("codex")).Command);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("65536")]
    public void InvalidPortIsRejected(string port)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            ApplicationOptionsConfiguration.Read(name =>
                name == EnvironmentVariables.GoblinPort ? port : null));

        Assert.Equal("GOBLIN_PORT must be a valid port.", exception.Message);
    }

    private static CodexOptions Codex(string command) => new()
    {
        CodexHome = "/codex",
        Home = "/home",
        Workspace = "/workspace",
        Command = command
    };
}
