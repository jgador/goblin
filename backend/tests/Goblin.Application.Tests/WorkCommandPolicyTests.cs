using System;
using Goblin.Application.Work;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class WorkCommandPolicyTests
{
    [Fact]
    public void ExternalCommandsEnforceIdentifiersFieldOwnershipAndLimits()
    {
        WorkCommandPolicy.Validate(new(1, 2, WorkAction.Execute)
        {
            GitRepository = new("owner/repo", "Goblin", "goblin@example.test"),
            Model = "gpt-test",
            ReasoningEffort = "high",
            ModelSelectionProvided = true,
            Delivery = new("master", true, false)
        });
        WorkCommandPolicy.Validate(new(1, 2, WorkAction.AuthorizeGitRepository) { AuthorizationId = 3 });

        AssertFailure("invalid_command", new(0, 2, WorkAction.Create));
        AssertFailure("invalid_command", new(1, 0, WorkAction.Create));
        AssertFailure("invalid_command", new(1, 2, (WorkAction)999));
        AssertFailure("text_too_long", new(1, 2, WorkAction.Create) { Text = new string('x', 4001) });
        AssertFailure("invalid_command", new(1, 2, WorkAction.AddContext)
        {
            GitRepository = new("owner/repo", "Goblin", "goblin@example.test")
        });
        AssertFailure("invalid_model_selection", new(1, 2, WorkAction.AddContext) { Model = "gpt-test" });
        AssertFailure("invalid_model_selection", new(1, 2, WorkAction.Execute) { Model = new string('x', 129) });
        AssertFailure("invalid_model_selection", new(1, 2, WorkAction.Execute) { ReasoningEffort = new string('x', 33) });
        AssertFailure("invalid_command", new(1, 2, WorkAction.Cancel) { Delivery = new("master", true, false) });
        AssertFailure("invalid_command", new(1, 2, WorkAction.Execute) { AuthorizationId = 3 });
        AssertFailure("invalid_command", new(1, 2, WorkAction.AuthorizeGitRepository) { AuthorizationId = 3, Text = "approve" });
    }

    [Fact]
    public void ConversationCommandsKeepTheirNarrowTrustedActionSet()
    {
        foreach (WorkAction action in new[] { WorkAction.Create, WorkAction.Execute, WorkAction.Answer, WorkAction.AddContext })
            WorkCommandPolicy.ValidateConversation(new(0, 0, action));

        AssertFailure("invalid_command", new(1, 2, WorkAction.Cancel), conversation: true);
        AssertFailure("invalid_command", new(1, 2, WorkAction.Execute)
        {
            GitRepository = new("owner/repo", "Goblin", "goblin@example.test")
        }, conversation: true);
        AssertFailure("invalid_command", new(1, 2, WorkAction.Answer) { AuthorizationId = 3 }, conversation: true);
        AssertFailure("invalid_command", new(1, 2, WorkAction.AddContext) { Text = new string('x', 4001) }, conversation: true);
    }

    [Fact]
    public void FingerprintIsStableAndCoversReplayRelevantFields()
    {
        var command = new WorkCommand(1, 2, WorkAction.Answer)
        {
            ExpectedVersion = 3,
            DecisionId = 4,
            Text = "answer"
        };

        string fingerprint = WorkCommandPolicy.Fingerprint(command);
        Assert.Equal("3D21205B74F5BC919A700B3A847FCEB22997AB68DF9146F9AEAE5C9279E88038", fingerprint);
        Assert.Equal(fingerprint, WorkCommandPolicy.Fingerprint(command));
        Assert.Equal(64, fingerprint.Length);
        Assert.NotEqual(fingerprint, WorkCommandPolicy.Fingerprint(new(1, 2, WorkAction.Answer)
        {
            ExpectedVersion = 3,
            DecisionId = 4,
            Text = "different"
        }));
    }

    private static void AssertFailure(string code, WorkCommand command, bool conversation = false)
    {
        ApplicationFailure failure = Assert.Throws<ApplicationFailure>(() =>
        {
            if (conversation) WorkCommandPolicy.ValidateConversation(command);
            else WorkCommandPolicy.Validate(command);
        });
        Assert.Equal(code, failure.Code);
    }
}
