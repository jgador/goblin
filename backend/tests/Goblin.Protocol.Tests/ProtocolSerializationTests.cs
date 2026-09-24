using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Goblin.Protocol.Tests;

public sealed class ProtocolSerializationTests
{
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, ProtocolJson.Options)!;
    private static string Write<T>(T value) => JsonSerializer.Serialize(value, ProtocolJson.Options);

    [Fact]
    public void InitializationPreservesNamesWhileOmittingUnspecifiedOptions()
    {
        var request = new InitializeParams
        {
            ClientInfo = new ClientInfo { Name = "goblin", Version = "1.0" },
        };

        Assert.Equal("{\"clientInfo\":{\"name\":\"goblin\",\"version\":\"1.0\"}}", Write(request));
        Assert.Equal("goblin", Read<InitializeParams>(Write(request)).ClientInfo.Name);
        Assert.Equal("{\"refreshToken\":false}", Write(new GetAccountParams { RefreshToken = false }));
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
        ChatgptDeviceCodeLoginAccountResponse response = Assert.IsType<ChatgptDeviceCodeLoginAccountResponse>(Read<LoginAccountResponse>(json));
        Assert.Equal("login-1", response.LoginId);
        Assert.Equal("ABCD-EFGH", response.UserCode);
        ChatgptDeviceCodeLoginAccountResponse restored = Assert.IsType<ChatgptDeviceCodeLoginAccountResponse>(
            Read<LoginAccountResponse>(Write<LoginAccountResponse>(response)));
        Assert.Equal(response.LoginId, restored.LoginId);
        Assert.Equal(response.UserCode, restored.UserCode);
        Assert.Equal(response.VerificationUrl, restored.VerificationUrl);
    }

    [Fact]
    public void RequiredNullableAccountEmailRemainsPresentOnTheWire()
    {
        const string json = """{"type":"chatgpt","email":null,"planType":"plus"}""";
        ChatgptAccount account = Assert.IsType<ChatgptAccount>(Read<Account>(json));
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
            Input = [new TextUserInput { Text = "hello" }],
        };
        Assert.Equal("{\"input\":[{\"text\":\"hello\",\"type\":\"text\"}],\"threadId\":\"thread-1\"}", Write(turn));
        var thread = new ThreadStartParams { Ephemeral = true, ApprovalPolicy = AskForApproval.Never, Sandbox = SandboxMode.ReadOnly };
        Assert.Equal("{\"approvalPolicy\":\"never\",\"ephemeral\":true,\"sandbox\":\"read-only\"}", Write(thread));
    }

    [Fact]
    public void CompletedNotificationsDeserializeIntoTypedNestedItems()
    {
        const string json = """{"params":{"completedAtMs":1800000000000,"item":{"text":"done","phase":"final_answer","id":"message-1","type":"agentMessage"},"threadId":"thread-1","turnId":"turn-1"},"method":"item/completed"}""";
        ItemCompletedServerNotification notification = Assert.IsType<ItemCompletedServerNotification>(Read<ServerNotification>(json));
        AgentMessageThreadItem item = Assert.IsType<AgentMessageThreadItem>(notification.Params.Item);
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
        Assert.False(ServerNotification.IsKnownMethod("thread/status/changed"));
        Assert.False(ServerNotification.IsKnownMethod("Item/Completed"));
        Assert.False(ServerNotification.IsKnownMethod("thread/start"));
    }

    [Fact]
    public void FailedTurnsUseStructuredCodexErrorDetails()
    {
        const string json = """{"threadId":"thread-1","turn":{"id":"turn-1","items":[],"status":"failed","error":{"message":"disconnected","codexErrorInfo":{"httpConnectionFailed":{"httpStatusCode":503}}}}}""";
        TurnCompletedNotification notification = Read<TurnCompletedNotification>(json);
        Assert.Equal(TurnStatus.Failed, notification.Turn.Status);
        HttpConnectionFailedCodexErrorInfo error = Assert.IsType<HttpConnectionFailedCodexErrorInfo>(notification.Turn.Error!.CodexErrorInfo);
        Assert.Equal((ushort)503, error.HttpConnectionFailed.HttpStatusCode);
        Assert.Equal("disconnected", notification.Turn.Error.Message);
    }

    [Fact]
    public void OtherStructuredCodexErrorsStillDeserialize()
    {
        const string json = """{"message":"busy","codexErrorInfo":{"activeTurnNotSteerable":{"turnKind":"review"}}}""";
        TurnError error = Read<TurnError>(json);
        ActiveTurnNotSteerableCodexErrorInfo details = Assert.IsType<ActiveTurnNotSteerableCodexErrorInfo>(error.CodexErrorInfo);
        Assert.Equal(NonSteerableTurnKind.Review, details.ActiveTurnNotSteerable.TurnKind);
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
    public void DeserializedRequestIdsMatchDictionaryKeysWithoutConfusingStringsAndNumbers()
    {
        var pending = new Dictionary<RequestId, string>
        {
            [new RequestId(7)] = "numeric request",
            [new RequestId("7")] = "string request",
        };

        Assert.Equal("numeric request", pending[Read<RequestId>("7")]);
        Assert.Equal("string request", pending[Read<RequestId>("\"7\"")]);
    }

    [Fact]
    public void NullRequestIdsAreRejectedInEnvelopes()
    {
        Assert.Throws<JsonException>(() => Read<JSONRPCResponse>("""{"id":null,"result":{}}"""));
        Assert.Throws<JsonException>(() => Write(new JSONRPCResponse
        {
            Id = null!,
            Result = JsonSerializer.SerializeToElement(new { }),
        }));
    }

    [Fact]
    public void JsonRpcEnvelopeKeepsOnlyUnconstrainedPayloadAsJsonElement()
    {
        const string json = """{"id":7,"result":{"requiresOpenaiAuth":true,"account":{"type":"apiKey"}}}""";
        JSONRPCResponse response = Read<JSONRPCResponse>(json);
        GetAccountResponse result = response.Result.Deserialize<GetAccountResponse>(ProtocolJson.Options)!;
        Assert.Equal(new RequestId(7), response.Id);
        Assert.True(result.RequiresOpenaiAuth);
        Assert.IsType<ApiKeyAccount>(result.Account);
        Assert.Equal(json, Write(response));
    }

    [Fact]
    public void MixedPrimitiveAndObjectUnionsSerializeBothConcreteAndAbstractBranches()
    {
        var primitive = new StringAskForApproval(AskForApprovalValue.Never);
        Assert.Equal("\"never\"", Write(primitive));
        Assert.Equal(AskForApprovalValue.Never, Assert.IsType<StringAskForApproval>(Read<AskForApproval>(Write(primitive))).Value);
        const string json = """{"granular":{"mcp_elicitations":false,"rules":true,"sandbox_approval":false}}""";
        GranularAskForApproval granular = Assert.IsType<GranularAskForApproval>(Read<AskForApproval>(json));
        Assert.True(granular.Granular.Rules);
        Assert.False(granular.Granular.SandboxApproval);
        Assert.Equal(json, Write<AskForApproval>(granular));
    }

    [Fact]
    public void SelectedResponsesIgnoreFieldsOutsideTheAdapterContract()
    {
        const string json = """{"approvalPolicy":"never","cwd":"/unused","model":"gpt-test","modelProvider":"openai","sandbox":{"type":"readOnly","networkAccess":false},"thread":{"ephemeral":true,"id":"thread-1","preview":"unused","turns":[]}}""";
        ThreadStartResponse response = Read<ThreadStartResponse>(json);
        Assert.Equal("thread-1", response.Thread.Id);
        Assert.True(response.Thread.Ephemeral);
        using JsonDocument selected = JsonDocument.Parse(Write(response));
        Assert.False(selected.RootElement.TryGetProperty("cwd", out _));
        Assert.False(selected.RootElement.GetProperty("thread").TryGetProperty("preview", out _));
    }

    [Fact]
    public void UnusedTurnItemKindsDoNotBreakACompletedTurn()
    {
        const string json = """{"threadId":"thread-1","turn":{"id":"turn-1","items":[{"type":"commandExecution","id":"command-1","command":"git status"},{"type":"agentMessage","id":"message-1","text":"done"}],"status":"completed"}}""";
        TurnCompletedNotification notification = Read<TurnCompletedNotification>(json);
        Assert.IsType<CommandExecutionThreadItem>(notification.Turn.Items[0]);
        Assert.Equal("done", Assert.IsType<AgentMessageThreadItem>(notification.Turn.Items[1]).Text);
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
    public void UnconstrainedEnvelopePayloadSurvivesSerialization()
    {
        const string json = """{"id":7,"result":{"futureSetting":{"nested":[1,"two",null]}}}""";
        JSONRPCResponse response = Read<JSONRPCResponse>(json);
        Assert.Equal(JsonValueKind.Object, response.Result.ValueKind);
        Assert.Equal(json, Write(response));
    }
}
