using System;
using Goblin.Contracts;
using Goblin.Integrations.Codex;
using Goblin.Protocol;
using Xunit;

namespace Goblin.Tests;

public sealed class AuthenticationInputPolicyTests
{
    [Fact]
    public void PromptIsTrimmedAndPreservesAllowedMultilineText()
    {
        Assert.Equal("first\nsecond", AuthenticationInputPolicy.Prompt("  first\nsecond  "));
        Assert.Equal(new string('x', 500), AuthenticationInputPolicy.Prompt(new string('x', 500)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("bad\u0001prompt")]
    public void InvalidPromptIsRejected(string? value) => AssertFailure("invalid_prompt", () => AuthenticationInputPolicy.Prompt(value));

    [Fact]
    public void OversizedPromptIsRejected() =>
        AssertFailure("invalid_prompt", () => AuthenticationInputPolicy.Prompt(new string('x', 501)));

    [Fact]
    public void ApiKeyIsTrimmedAndPrintableAsciiIsAccepted()
    {
        string key = "sk-" + new string('a', 20);
        Assert.Equal(key, AuthenticationInputPolicy.ApiKey("  " + key + "  "));
        Assert.Equal(new string('x', 4096), AuthenticationInputPolicy.ApiKey(new string('x', 4096)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("sk-abcdefghijklmnop qr")]
    [InlineData("sk-abcdefghijklmnopé")]
    public void InvalidApiKeyIsRejected(string? value) => AssertFailure("invalid_api_key", () => AuthenticationInputPolicy.ApiKey(value));

    [Fact]
    public void OversizedApiKeyIsRejected() =>
        AssertFailure("invalid_api_key", () => AuthenticationInputPolicy.ApiKey(new string('x', 4097)));

    [Theory]
    [InlineData("https://auth.openai.com/codex/device")]
    [InlineData("https://auth.openai.com/codex/device?user_code=ABCD")]
    public void DeviceLoginRequiresThePinnedOpenAiEndpoint(string verificationUrl)
    {
        DeviceLogin login = Assert.IsType<DeviceLogin>(AuthenticationInputPolicy.DeviceLogin(Device(verificationUrl)));

        Assert.Equal("login-1", login.Id);
        Assert.Equal("ABCD-EFGH", login.UserCode);
        Assert.Equal(verificationUrl, login.VerificationUrl);
    }

    [Theory]
    [InlineData("http://auth.openai.com/codex/device")]
    [InlineData("https://auth.openai.com:444/codex/device")]
    [InlineData("https://auth.openai.com.evil.test/codex/device")]
    [InlineData("https://user@auth.openai.com/codex/device")]
    [InlineData("https://auth.openai.com/other")]
    [InlineData("not-a-url")]
    public void UntrustedDeviceLoginEndpointIsRejected(string verificationUrl) =>
        Assert.Null(AuthenticationInputPolicy.DeviceLogin(Device(verificationUrl)));

    [Fact]
    public void WrongResponseTypeAndInvalidUserCodeAreRejected()
    {
        Assert.Null(AuthenticationInputPolicy.DeviceLogin(new ApiKeyLoginAccountResponse()));
        Assert.Null(AuthenticationInputPolicy.DeviceLogin(Device("https://auth.openai.com/codex/device", "")));
        Assert.Null(AuthenticationInputPolicy.DeviceLogin(Device("https://auth.openai.com/codex/device", new string('x', 65))));
    }

    private static ChatGPTDeviceCodeLoginAccountResponse Device(string verificationUrl, string code = "ABCD-EFGH") => new()
    {
        LoginId = "login-1",
        UserCode = code,
        VerificationUrl = verificationUrl
    };

    private static void AssertFailure(string code, Action action)
    {
        IntegrationFailure failure = Assert.Throws<IntegrationFailure>(action);
        Assert.Equal(code, failure.Code);
    }
}
