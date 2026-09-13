using System.Text.Json;
using Xunit;

namespace Goblin.Protocol.Tests;

public sealed class ProtocolSerializationTests
{
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, ProtocolJson.Options)!;
    private static string Write<T>(T value) => JsonSerializer.Serialize(value, ProtocolJson.Options);

    [Fact]
    public void InitializationPreservesNamesAndExplicitFalseWhileOmittingUnspecifiedOptions()
    {
        var request = new InitializeParams
        {
            ClientInfo = new ClientInfo { Name = "goblin", Version = "1.0" },
            Capabilities = new InitializeCapabilities { ExperimentalApi = false },
        };

        Assert.Equal("{\"capabilities\":{\"experimentalApi\":false},\"clientInfo\":{\"name\":\"goblin\",\"version\":\"1.0\"}}", Write(request));
        Assert.False(Read<InitializeParams>(Write(request)).Capabilities!.ExperimentalApi);
    }

    [Fact]
    public void ConcreteAndAbstractLoginVariantsHaveTheSameWireRepresentation()
    {
        var concrete = new ApiKeyLoginAccountParams { ApiKey = "test-key" };
        LoginAccountParams abstractValue = concrete;

        Assert.Equal("{\"apiKey\":\"test-key\",\"type\":\"apiKey\"}", Write(concrete));
        Assert.Equal(Write(concrete), Write(abstractValue));
        Assert.Equal("test-key", Assert.IsType<ApiKeyLoginAccountParams>(Read<LoginAccountParams>(Write(concrete))).ApiKey);
    }

    [Fact]
    public void DeviceCodeResponseAcceptsDiscriminatorAfterData()
    {
        const string json = """{"loginId":"login-1","userCode":"ABCD-EFGH","verificationUrl":"https://auth.openai.com/codex/device","type":"chatgptDeviceCode"}""";
        var response = Assert.IsType<ChatgptDeviceCodeLoginAccountResponse>(Read<LoginAccountResponse>(json));
        Assert.Equal("login-1", response.LoginId);
        Assert.Equal("ABCD-EFGH", response.UserCode);
        Assert.Equal(response, Read<LoginAccountResponse>(Write<LoginAccountResponse>(response)));
    }

    [Fact]
    public void RequiredNullableAccountEmailRemainsPresentOnTheWire()
    {
        const string json = """{"type":"chatgpt","email":null,"planType":"plus"}""";
        var account = Assert.IsType<ChatgptAccount>(Read<Account>(json));
        Assert.Null(account.Email);
        Assert.Contains("\"email\":null", Write<Account>(account));
        Assert.Throws<JsonException>(() => Read<Account>("""{"type":"chatgpt","planType":"plus"}"""));
    }

    [Fact]
    public void TextInputAndThreadPoliciesKeepTheirSchemaSpelling()
    {
        var turn = new TurnStartParams
        {
            ThreadId = "thread-1",
            Input = [new TextUserInput { Text = "hello", TextElements = [] }],
        };
        Assert.Equal("{\"input\":[{\"text\":\"hello\",\"text_elements\":[],\"type\":\"text\"}],\"threadId\":\"thread-1\"}", Write(turn));
        var thread = new ThreadStartParams { Ephemeral = true, ApprovalPolicy = AskForApproval.Never, Sandbox = SandboxMode.ReadOnly };
        Assert.Equal("{\"approvalPolicy\":\"never\",\"ephemeral\":true,\"sandbox\":\"read-only\"}", Write(thread));
    }

    [Fact]
    public void CompletedNotificationsDeserializeIntoTypedNestedItems()
    {
        const string json = """{"params":{"completedAtMs":1800000000000,"item":{"text":"done","phase":"final_answer","id":"message-1","type":"agentMessage"},"threadId":"thread-1","turnId":"turn-1"},"method":"item/completed"}""";
        var notification = Assert.IsType<ItemCompletedServerNotification>(Read<ServerNotification>(json));
        var item = Assert.IsType<AgentMessageThreadItem>(notification.Params.Item);
        Assert.Equal("item/completed", notification.Method);
        Assert.Equal("done", item.Text);
        Assert.Equal(MessagePhase.FinalAnswer, item.Phase);
        Assert.Equal("thread-1", notification.Params.ThreadId);
        Assert.IsType<ItemCompletedServerNotification>(Read<ServerNotification>(Write<ServerNotification>(notification)));
    }

    [Fact]
    public void KnownNotificationMethodsComeFromTheSchemaAndAreCaseSensitive()
    {
        Assert.True(ServerNotification.IsKnownMethod("item/completed"));
        Assert.True(ServerNotification.IsKnownMethod("account/login/completed"));
        Assert.True(ServerNotification.IsKnownMethod("turn/completed"));
        Assert.False(ServerNotification.IsKnownMethod("future/notification"));
        Assert.False(ServerNotification.IsKnownMethod("Item/Completed"));
        Assert.False(ServerNotification.IsKnownMethod("thread/start"));
    }

    [Fact]
    public void FailedTurnsUseStructuredCodexErrorDetails()
    {
        const string json = """{"threadId":"thread-1","turn":{"id":"turn-1","items":[],"status":"failed","error":{"message":"disconnected","codexErrorInfo":{"httpConnectionFailed":{"httpStatusCode":503}}}}}""";
        var notification = Read<TurnCompletedNotification>(json);
        Assert.Equal(TurnStatus.Failed, notification.Turn.Status);
        var error = Assert.IsType<HttpConnectionFailedCodexErrorInfo>(notification.Turn.Error!.CodexErrorInfo);
        Assert.Equal((ushort)503, error.HttpConnectionFailed.HttpStatusCode);
        Assert.Equal("disconnected", notification.Turn.Error.Message);
    }

    [Theory]
    [InlineData("\"request-1\"")]
    [InlineData("0")]
    [InlineData("-9223372036854775808")]
    [InlineData("9223372036854775807")]
    public void RequestIdsRoundTripWithoutPrecisionLoss(string json)
        => Assert.Equal(json, Write(Read<RequestId>(json)));

    [Theory]
    [InlineData("null")]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    [InlineData("true")]
    [InlineData("{}")]
    public void InvalidRequestIdsAreRejected(string json)
        => Assert.Throws<JsonException>(() => Read<RequestId>(json));

    [Fact]
    public void StringAndNumericRequestIdsAreDistinct()
    {
        Assert.NotEqual(new RequestId("1"), new RequestId(1));
        Assert.Equal(new RequestId(1), Read<RequestId>("1"));
        Assert.Throws<JsonException>(() => Write(default(RequestId)));
    }

    [Fact]
    public void JsonRpcEnvelopeKeepsOnlyUnconstrainedPayloadAsJsonElement()
    {
        const string json = """{"id":7,"result":{"requiresOpenaiAuth":true,"account":{"type":"apiKey"}}}""";
        var response = Read<JSONRPCResponse>(json);
        var result = response.Result.Deserialize<GetAccountResponse>(ProtocolJson.Options)!;
        Assert.Equal(new RequestId(7), response.Id);
        Assert.True(result.RequiresOpenaiAuth);
        Assert.IsType<ApiKeyAccount>(result.Account);
        var message = Read<JSONRPCMessage>(json);
        Assert.IsType<JSONRPCResponseJSONRPCMessage>(message);
        Assert.Equal(json, Write(message));
    }

    [Fact]
    public void MixedPrimitiveAndObjectUnionsSerializeBothConcreteAndAbstractBranches()
    {
        var primitive = new StringAskForApproval(AskForApprovalValue.Never);
        Assert.Equal("\"never\"", Write(primitive));
        Assert.Equal(AskForApproval.Never, Read<AskForApproval>(Write(primitive)));
        const string json = """{"granular":{"mcp_elicitations":false,"rules":true,"sandbox_approval":false}}""";
        var granular = Assert.IsType<GranularAskForApproval>(Read<AskForApproval>(json));
        Assert.True(granular.Granular.Rules);
        Assert.False(granular.Granular.SandboxApproval);
        Assert.Equal(json, Write<AskForApproval>(granular));
    }

    [Theory]
    [InlineData("\"READ-ONLY\"")]
    [InlineData("\"future-mode\"")]
    [InlineData("0")]
    public void EnumsRequireExactSchemaWireValues(string json)
        => Assert.Throws<JsonException>(() => Read<SandboxMode>(json));

    [Theory]
    [InlineData("""{"type":"future-login"}""")]
    [InlineData("""{"type":"apiKey"}""")]
    [InlineData("""{"type":"apiKey","apiKey":null}""")]
    [InlineData("""{"type":"apiKey","type":"chatgptDeviceCode","apiKey":"test"}""")]
    public void InvalidDiscriminatedUnionsFailClearly(string json)
        => Assert.Throws<JsonException>(() => Read<LoginAccountParams>(json));

    [Fact]
    public void DirectVariantDeserializationAlsoValidatesItsDiscriminator()
    {
        Assert.Throws<JsonException>(() => Read<ApiKeyLoginAccountParams>("""{"type":"chatgptDeviceCode","apiKey":"test"}"""));
        Assert.Throws<JsonException>(() => Read<ApiKeyLoginAccountParams>("""{"apiKey":"test"}"""));
    }

    [Fact]
    public void ClosedSchemaObjectsRejectUnknownProperties()
        => Assert.Throws<JsonException>(() => Read<AskForApproval>("""{"granular":{"mcp_elicitations":false,"rules":true,"sandbox_approval":false},"unexpected":true}"""));

    [Fact]
    public void UnconstrainedExtensionValuesSurviveSerialization()
    {
        const string json = """{"enabled":true,"futureSetting":{"nested":[1,"two",null]}}""";
        var config = Read<AnalyticsConfig>(json);
        Assert.True(config.Enabled);
        Assert.Equal(json, Write(config));
    }
}
