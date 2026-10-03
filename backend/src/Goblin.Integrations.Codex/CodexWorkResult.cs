using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Goblin.Contracts.Runtime;
using Goblin.Core.Repositories;

namespace Goblin.Integrations.Codex;

internal class CodexWorkResult
{
    [JsonPropertyName("kind")]
    [JsonRequired]
    public string? Kind { get; init; }

    [JsonPropertyName("text")]
    [JsonRequired]
    public string? Text { get; init; }
}

internal sealed class CodexRepositoryWorkResult : CodexWorkResult
{
    [JsonPropertyName("releaseWorkspace")]
    public bool ReleaseWorkspace { get; init; } = true;

    [JsonPropertyName("setup")]
    public RepositorySetupOutput[]? Setup { get; init { field = value; SetupProvided = true; } }

    [JsonIgnore]
    public bool SetupProvided { get; private init; }
}

internal sealed class RepositorySetupOutput
{
    public RepositorySetupOutput(string topic, string reason, string[] tools, string[] commands,
        string[] files, SetupCheckOutput[] checks)
    {
        Topic = topic;
        Reason = reason;
        Tools = tools;
        Commands = commands;
        Files = files;
        Checks = checks;
    }

    [JsonPropertyName("topic")]
    public string Topic { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; }

    [JsonPropertyName("tools")]
    public string[] Tools { get; init; }

    [JsonPropertyName("commands")]
    public string[] Commands { get; init; }

    [JsonPropertyName("files")]
    public string[] Files { get; init; }

    [JsonPropertyName("checks")]
    public SetupCheckOutput[] Checks { get; init; }

    public RepositorySetup ToCore() => new(Topic, Reason, Tools, Commands, Files,
        Checks is null ? null! : Array.ConvertAll(Checks, check => check is null ? null! : new SetupCheck(check.Command, check.ExpectedOutput)));
}

internal sealed class SetupCheckOutput
{
    public SetupCheckOutput(string command, string expectedOutput)
    {
        Command = command;
        ExpectedOutput = expectedOutput;
    }

    [JsonPropertyName("command")]
    public string Command { get; init; }

    [JsonPropertyName("expectedOutput")]
    public string ExpectedOutput { get; init; }
}

internal static class CodexWorkResults
{
    public static CodexWorkResult Read(string json, bool repositoryChanges)
    {
        CodexWorkResult result = repositoryChanges
            ? JsonSerializer.Deserialize<CodexRepositoryWorkResult>(json) ?? throw new JsonException()
            : JsonSerializer.Deserialize<CodexWorkResult>(json) ?? throw new JsonException();
        if (string.IsNullOrWhiteSpace(result.Text) ||
            (result.Kind is not ("result" or "input") && !(result.Kind == "workspace" && !repositoryChanges)))
            throw new IntegrationFailure("invalid_work_result", "The runtime returned an invalid result.");
        return result;
    }

    public static RepositorySetup[]? Setup(CodexWorkResult result)
    {
        if (result is not CodexRepositoryWorkResult { SetupProvided: true } repository) return null;
        RepositorySetup[]? setups = repository.Setup is null ? null : Array.ConvertAll(repository.Setup,
            setup => setup is null ? null! : setup.ToCore());
        if (setups is null || setups.Length > RepositorySetupRules.MaxObservations || !setups.All(RepositorySetupRules.Valid))
            throw new IntegrationFailure("invalid_work_result", "The runtime returned invalid setup observations.");
        return setups;
    }
}
