using System.Text.Json;
using Goblin.Application.Work;
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
        string inheritedJson = JsonSerializer.Serialize(inherited, WorkStore.Json);
        string clearedJson = JsonSerializer.Serialize(cleared, WorkStore.Json);
        Assert.NotEqual(inheritedJson, clearedJson);
        Assert.False(JsonSerializer.Deserialize<WorkCommand>(inheritedJson, WorkStore.Json)!.ModelSelectionProvided);
        WorkCommand restored = JsonSerializer.Deserialize<WorkCommand>(clearedJson, WorkStore.Json)!;
        Assert.True(restored.ModelSelectionProvided);
        Assert.Null(restored.Model);
        Assert.Null(restored.ReasoningEffort);
        Assert.Equal(action, restored.Action);
        Assert.Equal(33, restored.ExpectedVersion);
        restored = JsonSerializer.Deserialize<WorkCommand>(JsonSerializer.Serialize(selected, WorkStore.Json), WorkStore.Json)!;
        Assert.True(restored.ModelSelectionProvided);
        Assert.Equal("chosen-model", restored.Model);
        Assert.Equal("high", restored.ReasoningEffort);
    }
}
