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
        if (receipt.Fingerprint != Hash(command)) throw new ApplicationFailure("command_id_reused");
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
        GitRepositoryChange? selected = command.GitRepository;
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
        var requested = new GitRepositoryChange(name, selected?.GitAuthorName ?? "Goblin", selected?.GitAuthorEmail ?? "goblin@localhost");
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
        return new(info, account, requested, delivery);
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
            proposal.CreateGrant(connection.Id, work.Id, id));
        work.PrepareGitRepositoryAuthorization(id, new(target.Runtime, target.ConnectionId, target.RequestedModel, gitRepository, target.RequestedEffort),
            !enabled, retry, now, proposal.Info.DefaultBranch);
    }
}
