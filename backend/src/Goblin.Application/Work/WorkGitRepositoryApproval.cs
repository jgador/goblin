using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goblin.Application.Work;

public sealed partial class WorkStore
{
    private sealed class GitRepositoryProposal
    {
        public GitRepositoryProposal(GitRepositoryInfo info, GitRepositoryAccount account, GitRepositoryChange requested,
            GitDeliveryIntent delivery)
        {
            Info = info;
            Account = account;
            Requested = requested;
            Delivery = delivery;
        }

        public GitRepositoryInfo Info { get; init; }

        public GitRepositoryAccount Account { get; init; }

        public GitRepositoryChange Requested { get; init; }

        public GitDeliveryIntent Delivery { get; init; }

        public GitRepositoryGrant CreateGrant(long connectionId, long workId, long attemptId) => new()
        {
            ConnectionId = connectionId,
            Generation = Account.Generation,
            AccountId = Account.AccountId,
            Login = Account.Login,
            GitRepositoryId = Info.Id,
            BaseBranch = Delivery.BaseBranch ?? Info.DefaultBranch,
            Branch = $"goblin/{workId}/{attemptId}",
            PolicyVersion = 2,
            AllowPush = Delivery.Push,
            AllowPullRequest = Delivery.OpenPullRequest
        };
    }

    private async Task<WorkView?> ReplayAsync(WorkCommand command, CancellationToken token)
    {
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        Persistence.Entities.WorkCommand? receipt = await db.WorkCommands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.CommandId, token);
        if (receipt is null) return null;
        if (receipt.Fingerprint != WorkCommandPolicy.Fingerprint(command)) throw new ApplicationFailure("command_id_reused");
        return JsonSerializer.Deserialize<WorkView>(receipt.Response, Json)!;
    }

    // Discovery may call GitHub. Do it before acquiring the product transaction;
    // expected version and connection generation are checked again when saving.
    private async Task<GitRepositoryProposal?> ProposalAsync(WorkCommand command, CancellationToken token)
    {
        if (command.Action is not (WorkAction.Execute or WorkAction.Retry or WorkAction.PrepareGitRepository)) return null;
        WorkView view = await GetAsync(command.WorkId, token);
        if (view.Version != command.ExpectedVersion) throw new ApplicationFailure("work_changed");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        Persistence.Entities.GithubRepository[] known = await db.GithubRepositories.AsNoTracking().ToArrayAsync(token);
        GitRepositorySelection? selected = command.GitRepository;
        string[] references = GitRepositoryReferences.Find(view.Work, [.. known.Select(x => x.Name)]);
        string? name = selected?.GitRepository ?? (command.Action == WorkAction.PrepareGitRepository ? command.Text?.Trim() : null)
            ?? (references.Length == 1 ? references[0] : null)
            ?? view.Work.Attempts.LastOrDefault()?.Target.GitRepository?.GitRepository;
        if (name is null) return null;
        if (!name.Contains('/') && _gitRepositoryCatalog is not null)
        {
            if (!Regex.IsMatch(name, @"^[a-zA-Z0-9_.-]+$")) throw new ApplicationFailure("repository_unavailable");
            string[] matches = [];
            for (int page = 1; page <= 1000; page++)
            {
                GitRepositoryInfo[] batch = await _gitRepositoryCatalog.GitRepositoriesAsync(page, token);
                matches = [.. matches, .. batch.Where(x => x.Name.Split('/')[1].Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name)];
                if (matches.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) throw new ApplicationFailure("repository_ambiguous");
                if (batch.Length < 100) break;
                if (page == 1000) throw new ApplicationFailure("repository_ambiguous");
            }
            name = matches.Distinct(StringComparer.OrdinalIgnoreCase).SingleOrDefault() ?? throw new ApplicationFailure("repository_unavailable");
        }
        GitDeliveryIntent delivery = command.Delivery ?? GitRepositoryIntent.Delivery(view.Work);
        delivery.Validate();
        GitRepositoryAccount account;
        GitRepositoryInfo info;
        if (_gitRepositoryCatalog is not null)
        {
            account = await _gitRepositoryCatalog.GetAccountAsync(token) ?? throw new ApplicationFailure("repository_unavailable");
            info = await _gitRepositoryCatalog.GitRepositoryAsync(name, token);
            if (await _gitRepositoryCatalog.GetAccountAsync(token) != account) throw new ApplicationFailure("repository_authorization_changed");
        }
        else
        {
            // Hosts without discovery may propose only already enabled catalog entries.
            Persistence.Entities.GithubRepository gitRepository = known.SingleOrDefault(x => x.Enabled && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApplicationFailure("repository_unavailable");
            Persistence.Entities.GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == gitRepository.ConnectionId, token);
            account = new(connection.Generation!, connection.AccountId!, connection.Login!);
            info = new(gitRepository.Id, gitRepository.Name, gitRepository.DefaultBranch, true);
        }
        if ((delivery.Push || delivery.OpenPullRequest) && !info.CanPush) throw new ApplicationFailure("repository_unavailable");
        return new(info, account, RequestedGitRepository(info.Name, account, selected), delivery);
    }

    private static async Task SaveProposalAsync(GoblinDbContext db, WorkItem work, long id, ExecutionTarget target,
        GitRepositoryProposal proposal, bool retry, DateTimeOffset now, CancellationToken token)
    {
        Persistence.Entities.GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == 1, token);
        if (connection.Availability != "Connected" || connection.Generation != proposal.Account.Generation ||
            connection.AccountId != proposal.Account.AccountId || connection.Login != proposal.Account.Login)
            throw new ApplicationFailure("repository_authorization_changed");
        bool enabled = await db.GithubRepositories.AnyAsync(x => x.Id == proposal.Info.Id && x.Enabled &&
            x.Name == proposal.Info.Name && x.ConnectionId == connection.Id, token);
        var gitRepository = new GitRepositoryChange(proposal.Info.Name, proposal.Requested.GitAuthorName, proposal.Requested.GitAuthorEmail,
            proposal.CreateGrant(connection.Id, work.Id, id))
        {
            RequestedBy = await GitRequesterAsync(db, work.Id, token)
        };
        work.PrepareGitRepositoryAuthorization(id, new(target.Runtime, target.ConnectionId, target.RequestedModel, gitRepository, target.RequestedEffort),
            !enabled, retry, now, proposal.Info.DefaultBranch);
    }

    internal static GitRepositoryChange RequestedGitRepository(string name, GitRepositoryAccount account, GitRepositorySelection? selected = null) =>
        new(name, string.IsNullOrWhiteSpace(selected?.GitAuthorName) ? account.Login : selected.GitAuthorName,
            string.IsNullOrWhiteSpace(selected?.GitAuthorEmail) ? $"{account.AccountId}+{account.Login}@users.noreply.github.com" : selected.GitAuthorEmail);

    private static async Task<string?> GitRequesterAsync(GoblinDbContext db, long workId, CancellationToken token)
    {
        var source = await db.ExternalMessages.Where(x => x.WorkId == workId && x.State != nameof(ExternalMessageState.Rejected))
            .OrderBy(x => x.Id).Select(x => new { x.WorkspaceId, x.UserId }).FirstOrDefaultAsync(token);
        return source is null ? null : $"slack:{source.WorkspaceId}/{source.UserId}";
    }

    // Enabling a repository grants local work on it. A trusted user request binds
    // that standing permission to a fresh, immutable attempt. Discovery is not
    // needed here: the stored catalog was verified when the repository was enabled.
    // Actual repository access is rechecked by the claim and GitHub broker.
    private static async Task<GitRepositoryProposal?> EnabledProposalAsync(GoblinDbContext db, WorkSnapshot work,
        WorkCommand command, CancellationToken token)
    {
        Persistence.Entities.GithubRepository[] known = await db.GithubRepositories.ToArrayAsync(token);
        string[] names = command.GitRepository is { } selected ? [selected.GitRepository] : GitRepositoryReferences.Find(work, [.. known.Select(x => x.Name)]);
        if (names.Length != 1) return null;
        Persistence.Entities.GithubRepository? repository = known.SingleOrDefault(x => x.Enabled && x.Name.Equals(names[0], StringComparison.OrdinalIgnoreCase));
        if (repository is null) return null;
        Persistence.Entities.GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == repository.ConnectionId, token);
        if (connection.Availability != "Connected" || connection.Generation is null || connection.AccountId is null || connection.Login is null) return null;
        var account = new GitRepositoryAccount(connection.Generation, connection.AccountId, connection.Login);
        GitDeliveryIntent delivery = command.Delivery ?? GitRepositoryIntent.Delivery(work);
        delivery.Validate();
        return new(new(repository.Id, repository.Name, repository.DefaultBranch, true), account,
            RequestedGitRepository(repository.Name, account, command.GitRepository), delivery);
    }

    private static async Task AuthorizeEnabledProposalAsync(GoblinDbContext db, Wolverine.EntityFrameworkCore.IDbContextOutbox outbox,
        WorkItem work, DateTimeOffset now, CancellationToken token)
    {
        if (work.GitRepositoryAuthorization is not { EnableGitRepository: false, Status: GitRepositoryAuthorizationStatus.Pending } approval ||
            work.CurrentAttempt?.CleanupPending == true) return;
        await GitHubStore.AcceptAsync(db, approval, token);
        work.AuthorizeGitRepository(approval.Id, approval.Target, now);
        await outbox.PublishAsync(new DispatchWork(work.Id, approval.Id));
    }

    private static async Task ContinueEnabledGitRepositoryAsync(GoblinDbContext db, Wolverine.EntityFrameworkCore.IDbContextOutbox outbox,
        WorkItem work, WorkCommand command, DateTimeOffset now, CancellationToken token)
    {
        if (work.Attention?.Reason != AttentionReason.GitRepositoryRequired || work.GitRepositoryRequest is not { } request ||
            work.CurrentAttempt?.CleanupPending == true || work.CurrentAttempt?.Status is AttemptStatus.Failed or AttemptStatus.Uncertain) return;
        // A plain "yes", runtime output, or unrelated context cannot approve a
        // pending request. Require the user's current message to name the repository.
        string[] known = await db.GithubRepositories.Select(x => x.Name).ToArrayAsync(token);
        string[] references = GitRepositoryReferences.FindText(command.Text ?? "", known);
        if (references.Length != 1) return;
        GitRepositoryProposal? proposal = await EnabledProposalAsync(db, work.Snapshot(),
            new(command.CommandId, command.WorkId, command.Action) { GitRepository = new(references[0]) }, token);
        if (proposal is null) return;
        await SaveProposalAsync(db, work, await IdentitySequence.NextAsync(db, IdentityKind.Attempt, token), request.Target, proposal, false, now, token);
        await AuthorizeEnabledProposalAsync(db, outbox, work, now, token);
    }
}
