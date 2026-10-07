using System.Text.Json;
using Goblin.Application.Work;
using Goblin.Contracts;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class WorkCommandConstructionTests
{
    [Theory]
    [InlineData(WorkAction.Execute)]
    [InlineData(WorkAction.Retry)]
    public void ExplicitNullSelectionHasADifferentReplayPayloadFromInheritance(WorkAction action)
    {
        WorkCommand Construct(WorkModelSelection? selection) => action == WorkAction.Execute
            ? WorkCommands.Execute(11, 22, 33, selection)
            : WorkCommands.Retry(11, 22, 33, selection);

        WorkCommand inherited = Construct(null);
        WorkCommand cleared = Construct(new(null, null));
        WorkCommand selected = Construct(new("chosen-model", "high"));
        string inheritedJson = JsonSerializer.Serialize(inherited, ContractJson.Options);
        string clearedJson = JsonSerializer.Serialize(cleared, ContractJson.Options);
        Assert.NotEqual(inheritedJson, clearedJson);
        Assert.False(JsonSerializer.Deserialize<WorkCommand>(inheritedJson, ContractJson.Options)!.ModelSelectionProvided);
        WorkCommand restored = JsonSerializer.Deserialize<WorkCommand>(clearedJson, ContractJson.Options)!;
        Assert.True(restored.ModelSelectionProvided);
        Assert.Null(restored.Model);
        Assert.Null(restored.ReasoningEffort);
        Assert.Equal(action, restored.Action);
        Assert.Equal(33, restored.ExpectedVersion);
        restored = JsonSerializer.Deserialize<WorkCommand>(JsonSerializer.Serialize(selected, ContractJson.Options), ContractJson.Options)!;
        Assert.True(restored.ModelSelectionProvided);
        Assert.Equal("chosen-model", restored.Model);
        Assert.Equal("high", restored.ReasoningEffort);
    }
}
