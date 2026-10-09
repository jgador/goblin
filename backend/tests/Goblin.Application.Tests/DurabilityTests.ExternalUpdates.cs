using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts.Conversations;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goblin.Application.Tests;

public sealed partial class DurabilityTests
{
    private static readonly ExternalInstallation UpdateInstallation = new("update-installation", "T123", "A123", "U123");

    [DatabaseFact]
    public async Task SlackResultsSurviveRestartAndCompletionDeliveryCannotMoveBackwards()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        long workId;
        long attemptId;
        ExternalWorkUpdate result;
        using (IServiceScope scope = fixture.Host.Services.CreateScope())
        {
            ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
            WorkView started = await StartSlackNotificationWork(fixture, store);
            workId = started.Work.Id;
            attemptId = started.Work.Attempts[^1].Id;
            Assert.Empty(await store.PendingUpdatesAsync(UpdateInstallation, default));
            fixture.Runtime.Observations[attemptId] = new(ObservationKind.Result, Text: "All checks passed.");
            await fixture.Reconcile(workId, attemptId);
            result = Assert.Single(await store.PendingUpdatesAsync(UpdateInstallation, default));
            Assert.Equal(ExternalWorkUpdateKind.ResultReady, result.Content.Kind);
            Assert.Equal("All checks passed.", result.Content.Text);
            Assert.Equal("D123", result.ChannelId);
            Assert.Equal("10.000001", result.ThreadId);
            Assert.Empty(await store.PendingUpdatesAsync(new("other", "T123", "A123", "U123"), default));
            Assert.Empty(await store.PendingUpdatesAsync(new(UpdateInstallation.Id, "T999", "A123", "U123"), default));
        }
        await fixture.RestartAsync();
        using (IServiceScope scope = fixture.Host.Services.CreateScope())
        {
            ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
            Assert.Equal(result.Content, Assert.Single(await store.PendingUpdatesAsync(UpdateInstallation, default)).Content);
            Assert.True(await store.UpdateIsCurrentAsync(UpdateInstallation, result, default));
            await store.UpdateSentAsync(UpdateInstallation,
                new(result.ConversationId, workId, result.ChannelId, "wrong-thread", result.Content), default);
            Assert.Single(await store.PendingUpdatesAsync(UpdateInstallation, default));
            await store.UpdateSentAsync(UpdateInstallation, result, default);
            Assert.Empty(await store.PendingUpdatesAsync(UpdateInstallation, default));
            WorkView review = await fixture.Get(workId);
            await fixture.Apply(WorkCommands.Approve(NextId(), workId, review.Version, attemptId));
            Assert.False(await store.UpdateIsCurrentAsync(UpdateInstallation, result, default));
            ExternalWorkUpdate completed = Assert.Single(await store.PendingUpdatesAsync(UpdateInstallation, default));
            Assert.Equal(ExternalWorkUpdateKind.Completed, completed.Content.Kind);
            Assert.True(completed.Content.Sequence > result.Content.Sequence);
            await store.UpdateSentAsync(UpdateInstallation, completed, default);
            await store.UpdateSentAsync(UpdateInstallation, result, default);
            Assert.Empty(await store.PendingUpdatesAsync(UpdateInstallation, default));
            Assert.Single((await fixture.Get(workId)).Work.Attempts);
        }
        await fixture.RestartAsync();
        using IServiceScope restarted = fixture.Host.Services.CreateScope();
        Assert.Empty(await restarted.ServiceProvider.GetRequiredService<ExternalConversationStore>().PendingUpdatesAsync(UpdateInstallation, default));
    }

    [DatabaseFact]
    public async Task SlackErrorsStayPendingWithoutRetryAndRevocationPreventsSharing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
        WorkView started = await StartSlackNotificationWork(fixture, store);
        long workId = started.Work.Id;
        long attemptId = started.Work.Attempts[^1].Id;
        fixture.Runtime.Observations[attemptId] = new(ObservationKind.Failed, Failure: FailureKind.ExecutionFailed);
        await fixture.Reconcile(workId, attemptId);
        ExternalWorkUpdate failure = Assert.Single(await store.PendingUpdatesAsync(UpdateInstallation, default));
        Assert.Equal(ExternalWorkUpdateKind.Failed, failure.Content.Kind);
        Assert.Equal(failure.Content, Assert.Single(await store.PendingUpdatesAsync(UpdateInstallation, default)).Content);
        Assert.Single((await fixture.Get(workId)).Work.Attempts);
        Assert.Equal(1, fixture.Runtime.Starts[attemptId]);
        ExternalIdentityView identity = Assert.Single(await store.IdentitiesAsync(UpdateInstallation, default));
        await store.RevokeAsync(UpdateInstallation, identity.Id, default);
        Assert.False(await store.UpdateIsCurrentAsync(UpdateInstallation, failure, default));
        Assert.Empty(await store.PendingUpdatesAsync(UpdateInstallation, default));
    }

    private static async Task<WorkView> StartSlackNotificationWork(Fixture fixture, ExternalConversationStore store)
    {
        ExternalLinkCode code = await store.StartLinkAsync(UpdateInstallation, "owner-session", default);
        static ExternalMessage Message(string eventId, string timestamp, string text) => new()
        {
            Installation = UpdateInstallation,
            EventId = eventId,
            UserId = "U456",
            ChannelId = "D123",
            ThreadId = "10.000001",
            MessageId = timestamp,
            Text = text,
            Direct = true
        };
        await store.AcceptAsync(Message("link-event", "9.000001", "link " + code.Code), default);
        await store.ProcessNextAsync(UpdateInstallation, default);
        await store.ConfirmLinkAsync(UpdateInstallation, "owner-session", code.Id, default);
        await store.AcceptAsync(Message("request-event", "10.000001", "Explain the design"), default);
        long workId = (await store.ProcessNextAsync(UpdateInstallation, default))!.WorkId!.Value;
        return await fixture.Until(workId, view => view.Work.Attempts[^1].Status == AttemptStatus.Starting);
    }
}
