namespace Goblin.Application.Work;

// Application-originated commands use only the fields belonging to their action.
// HTTP requests map every submitted field directly for validation and replay.
public static class WorkCommands
{
    public static WorkCommand Create(long commandId, long workId, string objective) =>
        new(commandId, workId, WorkAction.Create) { Text = objective };

    public static WorkCommand Answer(long commandId, long workId, long? expectedVersion, long decisionId, string answer) =>
        new(commandId, workId, WorkAction.Answer)
        {
            ExpectedVersion = expectedVersion,
            DecisionId = decisionId,
            Text = answer
        };

    public static WorkCommand AddContext(long commandId, long workId, long? expectedVersion, string text) =>
        new(commandId, workId, WorkAction.AddContext) { ExpectedVersion = expectedVersion, Text = text };

    public static WorkCommand Approve(long commandId, long workId, long? expectedVersion, long attemptId) =>
        new(commandId, workId, WorkAction.Approve) { ExpectedVersion = expectedVersion, AttemptId = attemptId };

    public static WorkCommand RequestChanges(long commandId, long workId, long? expectedVersion, long attemptId, string feedback) =>
        new(commandId, workId, WorkAction.RequestChanges)
        {
            ExpectedVersion = expectedVersion,
            AttemptId = attemptId,
            Text = feedback
        };

    public static WorkCommand Execute(long commandId, long workId, long? expectedVersion, WorkModelSelection? selection = null) =>
        Execution(commandId, workId, WorkAction.Execute, expectedVersion, selection);

    public static WorkCommand Retry(long commandId, long workId, long? expectedVersion, WorkModelSelection? selection = null) =>
        Execution(commandId, workId, WorkAction.Retry, expectedVersion, selection);

    private static WorkCommand Execution(long commandId, long workId, WorkAction action, long? expectedVersion, WorkModelSelection? selection) =>
        new(commandId, workId, action)
        {
            ExpectedVersion = expectedVersion,
            Model = selection?.Model,
            ReasoningEffort = selection?.ReasoningEffort,
            ModelSelectionProvided = selection is not null
        };
}

// A supplied selection, even with null values, overrides inherited preferences.
// Passing no selection to an execution factory preserves inheritance.
public sealed class WorkModelSelection
{
    public WorkModelSelection(string? model, string? reasoningEffort)
    {
        Model = model;
        ReasoningEffort = reasoningEffort;
    }

    public string? Model { get; }
    public string? ReasoningEffort { get; }
}
