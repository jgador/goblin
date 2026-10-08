using System.Text.Json;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Web;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class IdentityJsonTests
{
    [Theory]
    [InlineData(9007199254740993L)]
    [InlineData(long.MaxValue)]
    public void HttpIdsAreDecimalStringsAndCommandsReadThemWithoutRounding(long id)
    {
        var json = new JsonSerializerOptions(ContractJson.Options);
        json.Converters.Add(new LongJsonConverter());
        var command = new WorkCommand(id, id - 1, WorkAction.Assign)
        {
            ExpectedVersion = long.MaxValue,
            AgentId = 1
        };
        string body = JsonSerializer.Serialize(command, json);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("commandId").ValueKind);
        Assert.Equal(id, long.Parse(document.RootElement.GetProperty("commandId").GetString()!));
        Assert.Equivalent(command, JsonSerializer.Deserialize<WorkCommand>(body, ContractJson.Options), strict: true);
        Assert.Equivalent(command, JsonSerializer.Deserialize<WorkCommand>(body, json), strict: true);
    }

    [Theory]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    [InlineData("\"not-an-id\"")]
    public void InvalidInt64JsonIsRejected(string value)
    {
        var json = new JsonSerializerOptions();
        json.Converters.Add(new LongJsonConverter());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<long>(value, json));
    }
}
