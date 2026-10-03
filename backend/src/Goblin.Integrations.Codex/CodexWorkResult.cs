using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Goblin.Contracts.Runtime;
using Goblin.Core.GitRepositories;

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

internal sealed class CodexGitRepositoryWorkResult : CodexWorkResult
{
    [JsonPropertyName("releaseWorkspace")]
    public bool ReleaseWorkspace { get; init; } = true;

    [JsonPropertyName("setup")]
    public GitRepositorySetupOutput[]? Setup { get; init { field = value; SetupProvided = true; } }

    [JsonIgnore]
    public bool SetupProvided { get; private init; }
}

internal sealed class GitRepositorySetupOutput
{
    public GitRepositorySetupOutput(string topic, string reason, string[] tools, string[] commands,
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

    public GitRepositorySetup ToCore() => new(Topic, Reason, Tools, Commands, Files,
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
    public static CodexWorkResult Read(string json, bool gitRepositoryChanges)
    {
        CodexWorkResult result = gitRepositoryChanges
            ? JsonSerializer.Deserialize<CodexGitRepositoryWorkResult>(json) ?? throw new JsonException()
            : JsonSerializer.Deserialize<CodexWorkResult>(json) ?? throw new JsonException();
        if (string.IsNullOrWhiteSpace(result.Text) ||
            (result.Kind is not ("result" or "input") && !(result.Kind == "workspace" && !gitRepositoryChanges)))
            throw new IntegrationFailure("invalid_work_result", "The runtime returned an invalid result.");
        return result;
    }

    public static GitRepositorySetup[]? Setup(CodexWorkResult result)
    {
        if (result is not CodexGitRepositoryWorkResult { SetupProvided: true } gitRepository) return null;
        GitRepositorySetup[]? setups = gitRepository.Setup is null ? null : Array.ConvertAll(gitRepository.Setup,
            setup => setup is null ? null! : setup.ToCore());
        if (setups is null || setups.Length > GitRepositorySetupRules.MaxObservations || !setups.All(GitRepositorySetupRules.Valid))
            throw new IntegrationFailure("invalid_work_result", "The runtime returned invalid setup observations.");
        return setups;
    }
}
