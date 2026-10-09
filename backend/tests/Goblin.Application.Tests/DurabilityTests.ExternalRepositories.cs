using System.Linq;
using System.Threading.Tasks;
using Goblin.Application.Work;
using Goblin.Contracts;
using Goblin.Contracts.Conversations;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goblin.Application.Tests;

public sealed partial class DurabilityTests
{
    private static readonly ExternalInstallation RepositorySlack = new("repository-slack", "T123", "A123", "U123");

    private static ExternalMessage RepositoryMessage(string id, string text, string thread = "20.000001") => new()
    {
        Installation = RepositorySlack,
        EventId = "Ev" + id,
        UserId = "U456",
        ChannelId = "D123",
        ThreadId = thread,
        MessageId = id,
        Text = text,
        Direct = true
    };

    private static async Task LinkRepositoryRequester(ExternalConversationStore store)
    {
        ExternalLinkCode code = await store.StartLinkAsync(RepositorySlack, "owner-session", default);
        await store.AcceptAsync(RepositoryMessage("19.000001", "link " + code.Code), default);
        Assert.Equal(ExternalReplyKind.LinkConfirmationRequired, (await store.ProcessNextAsync(RepositorySlack, default))!.Kind);
        await store.ConfirmLinkAsync(RepositorySlack, "owner-session", code.Id, default);
    }

    [DatabaseFact]
    public async Task EnabledSlackRepositoryRequestUsesAccountDefaultsAndDispatchesOnce()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
        await LinkRepositoryRequester(store);
        ExternalMessage request = RepositoryMessage("20.000001", "Fix owner/repo and open a draft PR using base branch develop");
        await Task.WhenAll(store.AcceptAsync(request, default), store.AcceptAsync(request, default));
        ExternalReply?[] deliveries = await Task.WhenAll(store.ProcessNextAsync(RepositorySlack, default), store.ProcessNextAsync(RepositorySlack, default));
        ExternalReply reply = Assert.Single(deliveries.OfType<ExternalReply>());
        Assert.Equal(ExternalReplyKind.WorkSaved, reply.Kind);
        Assert.Null(reply.Notice);
        WorkView work = await fixture.Until(reply.WorkId!.Value, value => value.Work.Attempts.LastOrDefault()?.Status == AttemptStatus.Starting);
        AttemptSnapshot attempt = Assert.Single(work.Work.Attempts);
        GitRepositoryChange repository = attempt.Target.GitRepository!;
        Assert.Equal("owner/repo", repository.GitRepository);
        Assert.Equal("owner", repository.GitAuthorName);
        Assert.Equal("42+owner@users.noreply.github.com", repository.GitAuthorEmail);
        Assert.Equal("slack:T123/U456", repository.RequestedBy);
        Assert.Equal("develop", repository.Grant!.BaseBranch);
        Assert.Equal($"goblin/{work.Work.Id}/{attempt.Id}", repository.Grant.Branch);
        Assert.True(repository.Grant.AllowPush);
        Assert.True(repository.Grant.AllowPullRequest);
        Assert.Equal(1, fixture.Runtime.Starts[attempt.Id]);
        await fixture.RestartAsync();
        Assert.Equal(repository, (await fixture.Get(work.Work.Id)).Work.Attempts[0].Target.GitRepository);
    }

    [DatabaseFact]
    public async Task SlackRepositoryAnswerContinuesSameWorkWithDefaultBranchAndNoPublication()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
        await LinkRepositoryRequester(store);
        await store.AcceptAsync(RepositoryMessage("20.000001", "Help fix a test"), default);
        long id = (await store.ProcessNextAsync(RepositorySlack, default))!.WorkId!.Value;
        WorkView work = await fixture.Until(id, value => value.Work.Attempts.LastOrDefault()?.Status == AttemptStatus.Starting);
        long original = work.Work.Attempts[0].Id;
        fixture.Runtime.Observations[original] = new(ObservationKind.Paused, Text: "Which repository?");
        await fixture.Reconcile(id, original);
        await fixture.Until(id, value => value.Work.Attention?.Reason == AttentionReason.InputRequired && !value.Work.Attempts[^1].CleanupPending);
        await store.AcceptAsync(RepositoryMessage("20.000002", "Use owner/repo"), default);
        Assert.Equal(id, (await store.ProcessNextAsync(RepositorySlack, default))!.WorkId);
        work = await fixture.Until(id, value => value.Work.Attempts.Length == 2 && value.Work.Attempts[^1].Status == AttemptStatus.Starting);
        Assert.Equal(original, work.Work.Attempts[0].Id);
        Assert.Null(work.Work.Attempts[0].Target.GitRepository);
        Assert.Equal("Use owner/repo", work.Work.Decisions[^1].Answer);
        GitRepositoryChange repository = work.Work.Attempts[^1].Target.GitRepository!;
        Assert.Equal("main", repository.Grant!.BaseBranch);
        Assert.False(repository.Grant.AllowPush);
        Assert.False(repository.Grant.AllowPullRequest);
        Assert.Equal("slack:T123/U456", repository.RequestedBy);
    }

    [DatabaseFact]
    public async Task SlackCannotEnableDisabledRepositoriesOrResolveAmbiguityWithYesOrUseRevokedIdentity()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await EnableHandoffGitRepository(fixture);
        using IServiceScope scope = fixture.Host.Services.CreateScope();
        GitHubStore github = scope.ServiceProvider.GetRequiredService<GitHubStore>();
        await github.SetGitRepositoryAsync(new(23, "another/repo", "main", true), false, "handoff");
        ExternalConversationStore store = scope.ServiceProvider.GetRequiredService<ExternalConversationStore>();
        await LinkRepositoryRequester(store);
        await store.AcceptAsync(RepositoryMessage("20.000001", "Fix repo"), default);
        ExternalReply reply = (await store.ProcessNextAsync(RepositorySlack, default))!;
        long id = reply.WorkId!.Value;
        Assert.Contains("full owner/repository", reply.Notice);
        await store.AcceptAsync(RepositoryMessage("20.000002", "yes"), default);
        await store.ProcessNextAsync(RepositorySlack, default);
        Assert.Empty((await fixture.Get(id)).Work.Attempts);
        await store.AcceptAsync(RepositoryMessage("20.000003", "Use another/repo"), default);
        await store.ProcessNextAsync(RepositorySlack, default);
        Assert.Empty((await fixture.Get(id)).Work.Attempts);
        Assert.False((await github.GitRepositoriesAsync()).Single(value => value.Name == "another/repo").Enabled);
        await store.AcceptAsync(RepositoryMessage("20.000004", "Use owner/repo"), default);
        await store.RevokeAsync(RepositorySlack, Assert.Single(await store.IdentitiesAsync(RepositorySlack, default)).Id, default);
        Assert.Equal(ExternalReplyKind.AccessRequired, (await store.ProcessNextAsync(RepositorySlack, default))!.Kind);
        Assert.Empty((await fixture.Get(id)).Work.Attempts);
        Assert.Empty(fixture.Runtime.Starts);
    }
}
