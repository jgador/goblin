using System;
using Goblin.Application.Work;
using Goblin.Contracts.Conversations;
using Goblin.Core.Work;
using Xunit;

namespace Goblin.Application.Tests;

public sealed class ExternalConversationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LinkSecretsAreRandomBoundedAndExpireAfterFiveMinutes()
    {
        (string first, string firstHash, DateTime expiresAt) = ExternalConversationPolicy.NewLink(Now.UtcDateTime);
        (string second, string secondHash, _) = ExternalConversationPolicy.NewLink(Now.UtcDateTime);

        Assert.Equal(32, first.Length);
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Matches("^[0-9A-F]{64}$", firstHash);
        Assert.NotEqual(first, second);
        Assert.NotEqual(firstHash, secondHash);
        Assert.Equal(Now.AddMinutes(5).UtcDateTime, expiresAt);
    }

    [Theory]
    [InlineData(true, "link  secret  ", true)]
    [InlineData(false, "link secret", false)]
    [InlineData(true, "Link secret", false)]
    [InlineData(true, "please link secret", false)]
    public void OnlyExplicitDirectLinkCommandsBecomeProofAttempts(bool direct, string text, bool expected)
    {
        ExternalLinkAttempt attempt = ExternalConversationPolicy.LinkAttempt(Message(direct, text));

        Assert.Equal(expected, attempt.IsLinking);
        Assert.Equal(expected, attempt.CodeHash is not null);
        if (expected) Assert.Matches("^[0-9A-F]{64}$", attempt.CodeHash!);
    }

    [Theory]
    [InlineData(false, false, true, "request", "Pending")]
    [InlineData(false, false, false, null, "Pending")]
    [InlineData(false, true, true, "request", "Pending")]
    [InlineData(true, true, true, null, "PendingLink")]
    [InlineData(true, false, true, null, "Pending")]
    public void AcceptanceRetainsTextOnlyForAuthorizedNonLinkMessages(bool linking, bool validProof,
        bool authorized, string? expectedBody, string expectedState)
    {
        ExternalMessageAcceptance accepted = ExternalConversationPolicy.Accept("request", linking, validProof, authorized);

        Assert.Equal(expectedBody, accepted.Body);
        Assert.Equal(expectedState, accepted.State.ToString());
    }

    [Fact]
    public void ExternalReplyAnswersOnlyTheCurrentExplicitInputDecision()
    {
        WorkItem work = RunningWork();
        work.RequestInput(3, 5, 6, "Which environment?", Now);

        WorkCommand answer = ExternalConversationPolicy.Continue(work.Snapshot(), 7, 1, 8, "Production");
        Assert.Equal(WorkAction.Answer, answer.Action);
        Assert.Equal(6, answer.DecisionId);
        Assert.Equal("Production", answer.Text);

        WorkCommand context = ExternalConversationPolicy.Continue(new WorkItem(1, "Inspect", Now).Snapshot(),
            9, 1, 10, "Additional detail");
        Assert.Equal(WorkAction.AddContext, context.Action);
        Assert.Null(context.DecisionId);
        Assert.Equal("Additional detail", context.Text);
    }

    private static ExternalMessage Message(bool direct, string text) => new()
    {
        Installation = new("installation", "workspace", "app", "bot"),
        EventId = "event",
        UserId = "user",
        ChannelId = "channel",
        ThreadId = "thread",
        MessageId = "message",
        Text = text,
        Direct = direct
    };

    [Fact]
    public void QuestionsRemainPendingUntilDeliveredAndDisappearAfterAnswering()
    {
        WorkItem work = RunningWork();
        work.RequestInput(3, 5, 6, "Which environment?", Now);
        Assert.Equal(6, ExternalConversationPolicy.PendingQuestion(work.Snapshot(), null)!.Id);
        Assert.Null(ExternalConversationPolicy.PendingQuestion(work.Snapshot(), 6));
        work.AnswerDecision(6, "Development", Now);
        Assert.Null(ExternalConversationPolicy.PendingQuestion(work.Snapshot(), null));
    }

    private static WorkItem RunningWork()
    {
        var work = new WorkItem(1, "Inspect", Now);
        work.Assign(2, Now);
        work.QueueExecution(3, new("codex", 4), Now);
        Assert.True(work.TryClaimExecution(3, 5, "sandbox/3", Now));
        work.ExecutionStarted(3, 5, new("model", "session"), Now);
        return work;
    }
}
