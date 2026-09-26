using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;

namespace Goblin.Integrations.GitHub;

// Only Git object bundles cross the boundary. Never execute code, hooks, config,
// credential helpers, or paths supplied by the agent in this trusted process.
public sealed class GitHubRepositoryRemote : IRepositoryRemote
{
    private readonly GitHubConnection _github;
    private readonly Func<RepositoryChange, string> _url;
    public GitHubRepositoryRemote(GitHubConnection github) { _github = github; _url = Remote; }
    internal GitHubRepositoryRemote(GitHubConnection github, string testRemote) { _github = github; _url = _ => testRemote; }
    private static string GitDirectory(string directory) => Path.Combine(directory, "repository.git");
    private static string Remote(RepositoryChange repository) => "https://github.com/" + repository.Repository + ".git";
    private async Task VerifyAsync(RepositoryChange repository, CancellationToken token)
    {
        RepositoryGrant grant = repository.Grant ?? throw new GitHubFailure();
        GitHubState state = await _github.CheckAsync(token);
        if (state.Status != "Connected" || state.Account?.Generation != grant.Generation || state.Account.AccountId != grant.AccountId)
            throw new GitHubFailure();
        RepositoryInfo info = await _github.RepositoryAsync(repository.Repository, token);
        if (info.Id != grant.RepositoryId || ((grant.PolicyVersion == 1 || grant.AllowPush) && !info.CanPush) || info.DefaultBranch == grant.Branch) throw new GitHubFailure();
    }
    public async Task PrepareAsync(RepositoryChange repository, string directory, string? checkpoint, CancellationToken token)
    {
        await VerifyAsync(repository, token);
        GitHubConnection.PrivateDirectory(directory);
        string git = GitDirectory(directory);
        await _github.GitAsync(directory, ["init", "--bare", git], token);
        await _github.GitAsync(git, ["fetch", "--no-tags", "--", _url(repository), "+refs/heads/*:refs/heads/*"], token);
        string start = (await _github.GitAsync(git, ["rev-parse", "--verify", "refs/heads/" + (checkpoint ?? repository.Grant!.BaseBranch) + "^{commit}"], token)).Trim();
        await _github.GitAsync(git, ["update-ref", "refs/heads/" + repository.Grant!.Branch, start], token);
        await _github.GitAsync(git, ["symbolic-ref", "HEAD", "refs/heads/" + repository.Grant.Branch], token);
        await _github.GitAsync(git, ["bundle", "create", Path.Combine(directory, "input.bundle"), "--all"], token);
    }
    public async Task PrepareCheckpointAsync(RepositoryChange repository, string directory, WorkspaceCheckpoint checkpoint, CancellationToken token)
    {
        await PrepareAsync(repository, directory, checkpoint.Branch, token);
        string actual = (await _github.GitAsync(GitDirectory(directory), ["rev-parse", "--verify", "refs/heads/" + checkpoint.Branch + "^{commit}"], token)).Trim();
        if (actual != checkpoint.CommitSha) throw new GitHubFailure();
        if (repository.Grant!.Branch == checkpoint.Branch)
            await File.WriteAllTextAsync(Path.Combine(directory, "published-head"), checkpoint.CommitSha, token);
    }
    public async Task<string> InspectBundleAsync(RepositoryChange repository, string directory, string bundle, CancellationToken token)
    {
        string git = GitDirectory(directory);
        await _github.GitAsync(git, ["bundle", "verify", bundle], token);
        await _github.GitAsync(git, ["fetch", "--no-tags", "--", bundle, "refs/heads/" + repository.Grant!.Branch + ":refs/goblin/incoming"], token);
        string commit = (await _github.GitAsync(git, ["rev-parse", "--verify", "refs/goblin/incoming^{commit}"], token)).Trim();
        string head = Path.Combine(directory, "published-head");
        if (File.Exists(head)) await _github.GitAsync(git, ["merge-base", "--is-ancestor", (await File.ReadAllTextAsync(head, token)).Trim(), commit], token);
        return commit;
    }
    public async Task<RepositoryOperationResult> ExecuteAsync(RepositoryChange repository, string directory, string operation, string commit, CancellationToken token)
    {
        RepositoryGrant grant = repository.Grant ?? throw new GitHubFailure();
        if (grant.PolicyVersion == 2 && (operation == "publish" && !grant.AllowPush || operation == "pull-request" && (!grant.AllowPush || !grant.AllowPullRequest)))
            throw new GitHubFailure();
        await VerifyAsync(repository, token);
        if (operation == "fetch")
        {
            string git = GitDirectory(directory);
            await _github.GitAsync(git, ["fetch", "--no-tags", "--", _url(repository), "+refs/heads/*:refs/heads/*"], token);
            string fresh = Path.Combine(directory, "fresh.bundle");
            await _github.GitAsync(git, ["bundle", "create", fresh, "--all"], token);
            File.Move(fresh, Path.Combine(directory, "input.bundle"), true);
            return new(null, null);
        }
        if (operation == "publish")
        {
            string head = Path.Combine(directory, "published-head");
            string expected = File.Exists(head) ? (await File.ReadAllTextAsync(head, token)).Trim() : "";
            await _github.GitAsync(GitDirectory(directory), ["push", "--force-with-lease=refs/heads/" + repository.Grant!.Branch + ":" + expected,
                "--", _url(repository), commit + ":refs/heads/" + repository.Grant.Branch], token);
            await File.WriteAllTextAsync(head, commit, token);
            return new(commit, "https://github.com/" + repository.Repository + "/tree/" + repository.Grant.Branch);
        }
        if (operation != "pull-request" || !File.Exists(Path.Combine(directory, "published-head"))) throw new GitHubFailure();
        if ((await File.ReadAllTextAsync(Path.Combine(directory, "published-head"), token)).Trim() != commit) throw new GitHubFailure();
        string? existing = await PullRequestAsync(repository, token);
        if (existing is not null) return new(commit, existing);
        using JsonDocument result = JsonDocument.Parse(await _github.CliAsync(["api", "--method", "POST", "repos/" + repository.Repository + "/pulls",
            "-f", "head=" + repository.Grant!.Branch, "-f", "base=" + repository.Grant.BaseBranch,
            "-f", "title=Goblin " + repository.Grant.Branch, "-f", "body=Changes prepared by Goblin. Review this branch before merging.", "-F", "draft=true"], token));
        return new(commit, result.RootElement.GetProperty("html_url").GetString());
    }
    public async Task<RepositoryOperationResult?> ReconcileAsync(RepositoryChange repository, string directory, string operation, string commit, CancellationToken token)
    {
        if (operation == "fetch") return null;
        if (operation == "pull-request")
        {
            string? pr = await PullRequestAsync(repository, token);
            return pr is null ? null : new(commit, pr);
        }
        string result = await _github.GitAsync(GitDirectory(directory), ["ls-remote", "--refs", "--", _url(repository), "refs/heads/" + repository.Grant!.Branch], token);
        if (result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() != commit) return null;
        await File.WriteAllTextAsync(Path.Combine(directory, "published-head"), commit, token);
        return new(commit, "https://github.com/" + repository.Repository + "/tree/" + repository.Grant.Branch);
    }
    private async Task<string?> PullRequestAsync(RepositoryChange repository, CancellationToken token)
    {
        string owner = repository.Repository.Split('/')[0];
        using JsonDocument result = JsonDocument.Parse(await _github.CliAsync(["api", "repos/" + repository.Repository + "/pulls?state=all&head=" +
            Uri.EscapeDataString(owner + ":" + repository.Grant!.Branch) + "&base=" + Uri.EscapeDataString(repository.Grant.BaseBranch)], token));
        return result.RootElement.EnumerateArray().Select(x => x.GetProperty("html_url").GetString()).FirstOrDefault();
    }
}
