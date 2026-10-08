using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Goblin.Contracts;

namespace Goblin.Application.Work;

internal static class WorkCommandPolicy
{
    internal static void Validate(WorkCommand command)
    {
        if (command.CommandId <= 0 || command.WorkId <= 0 || !Enum.IsDefined(command.Action))
            throw new ApplicationFailure("invalid_command");
        if (command.Text?.Length > 4000) throw new ApplicationFailure("text_too_long");
        if (command.GitRepository is not null && command.Action is not (WorkAction.Execute or WorkAction.Retry or WorkAction.PrepareGitRepository))
            throw new ApplicationFailure("invalid_command");
        if (command.Model?.Length > 128 || command.ReasoningEffort?.Length > 32 ||
            command.Action is not (WorkAction.Execute or WorkAction.Retry) &&
            (command.Model is not null || command.ReasoningEffort is not null || command.ModelSelectionProvided))
            throw new ApplicationFailure("invalid_model_selection");
        if (command.Delivery is not null && command.Action is not (WorkAction.Execute or WorkAction.Retry or WorkAction.PrepareGitRepository) ||
            command.AuthorizationId is not null && command.Action is not (WorkAction.AuthorizeGitRepository or WorkAction.DenyGitRepository))
            throw new ApplicationFailure("invalid_command");
        if (command.Action is WorkAction.AuthorizeGitRepository or WorkAction.DenyGitRepository &&
            (command.Text is not null || command.AgentId is not null || command.AttemptId is not null || command.DecisionId is not null))
            throw new ApplicationFailure("invalid_command");
    }

    internal static void ValidateConversation(WorkCommand command)
    {
        if (command.Action is not (WorkAction.Create or WorkAction.Execute or WorkAction.Answer or WorkAction.AddContext) ||
            command.GitRepository is not null || command.AuthorizationId is not null || command.Text?.Length > 4000)
            throw new ApplicationFailure("invalid_command");
    }

    internal static string Fingerprint(WorkCommand command) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, ContractJson.Options))));
}
