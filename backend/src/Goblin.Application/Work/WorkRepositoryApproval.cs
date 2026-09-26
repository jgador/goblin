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
    private sealed record RepositoryProposal(RepositoryInfo Info, RepositoryAccount Account,
        RepositoryChange Requested, GitDeliveryIntent Delivery);

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
    private async Task<RepositoryProposal?> ProposalAsync(WorkCommand command, CancellationToken token)
    {
        if (command.Action is not (WorkAction.Execute or WorkAction.Retry or WorkAction.PrepareRepository)) return null;
        WorkView view = await GetAsync(command.WorkId, token);
        if (view.Version != command.ExpectedVersion) throw new ApplicationFailure("work_changed");
        await using GoblinDbContext db = await _dbFactory.CreateDbContextAsync(token);
        Persistence.Entities.GithubRepository[] known = await db.GithubRepositories.AsNoTracking().ToArrayAsync(token);
        RepositoryChange? selected = command.Repository;
        string[] references = RepositoryReferences.Find(view.Work, [.. known.Select(x => x.Name)]);
        string? name = selected?.Repository ?? (command.Action == WorkAction.PrepareRepository ? command.Text?.Trim() : null)
            ?? (references.Length == 1 ? references[0] : null)
            ?? view.Work.Attempts.LastOrDefault()?.Target.Repository?.Repository;
        if (name is null) return null;
        if (!name.Contains('/') && _repositoryCatalog is not null)
        {
            if (!Regex.IsMatch(name, @"^[a-zA-Z0-9_.-]+$")) throw new ApplicationFailure("repository_unavailable");
            string[] matches = [];
            for (int page = 1; page <= 1000; page++)
            {
                RepositoryInfo[] batch = await _repositoryCatalog.RepositoriesAsync(page, token);
                matches = [.. matches, .. batch.Where(x => x.Name.Split('/')[1].Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name)];
                if (matches.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) throw new ApplicationFailure("repository_ambiguous");
                if (batch.Length < 100) break;
                if (page == 1000) throw new ApplicationFailure("repository_ambiguous");
            }
            name = matches.Distinct(StringComparer.OrdinalIgnoreCase).SingleOrDefault() ?? throw new ApplicationFailure("repository_unavailable");
        }
        var requested = new RepositoryChange(name, selected?.GitAuthorName ?? "Goblin", selected?.GitAuthorEmail ?? "goblin@localhost");
        GitDeliveryIntent delivery = command.Delivery ?? RepositoryIntent.Delivery(view.Work);
        delivery.Validate();
        RepositoryAccount account;
        RepositoryInfo info;
        if (_repositoryCatalog is not null)
        {
            account = await _repositoryCatalog.GetAccountAsync(token) ?? throw new ApplicationFailure("repository_unavailable");
            info = await _repositoryCatalog.RepositoryAsync(name, token);
            if (await _repositoryCatalog.GetAccountAsync(token) != account) throw new ApplicationFailure("repository_authorization_changed");
        }
        else
        {
            // Hosts without discovery may propose only already enabled catalog entries.
            Persistence.Entities.GithubRepository repository = known.SingleOrDefault(x => x.Enabled && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApplicationFailure("repository_unavailable");
            Persistence.Entities.GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == repository.ConnectionId, token);
            account = new(connection.Generation!, connection.AccountId!, connection.Login!);
            info = new(repository.Id, repository.Name, repository.DefaultBranch, true);
        }
        if ((delivery.Push || delivery.OpenPullRequest) && !info.CanPush) throw new ApplicationFailure("repository_unavailable");
        return new(info, account, requested, delivery);
    }

    private static async Task SaveProposalAsync(GoblinDbContext db, WorkItem work, long id, ExecutionTarget target,
        RepositoryProposal proposal, bool retry, DateTimeOffset now, CancellationToken token)
    {
        Persistence.Entities.GithubConnection connection = await db.GithubConnections.SingleAsync(x => x.Id == 1, token);
        if (connection.Availability != "Connected" || connection.Generation != proposal.Account.Generation ||
            connection.AccountId != proposal.Account.AccountId || connection.Login != proposal.Account.Login)
            throw new ApplicationFailure("repository_authorization_changed");
        bool enabled = await db.GithubRepositories.AnyAsync(x => x.Id == proposal.Info.Id && x.Enabled &&
            x.Name == proposal.Info.Name && x.ConnectionId == connection.Id, token);
        var repository = new RepositoryChange(proposal.Info.Name, proposal.Requested.GitAuthorName, proposal.Requested.GitAuthorEmail,
            new(connection.Id, proposal.Account.Generation, proposal.Account.AccountId, proposal.Account.Login, proposal.Info.Id,
                proposal.Delivery.BaseBranch ?? proposal.Info.DefaultBranch, $"goblin/{work.Id}/{id}", 2,
                proposal.Delivery.Push, proposal.Delivery.OpenPullRequest));
        work.PrepareRepositoryAuthorization(id, new(target.Runtime, target.ConnectionId, target.RequestedModel, repository, target.RequestedEffort),
            !enabled, retry, now, proposal.Info.DefaultBranch);
    }
}
