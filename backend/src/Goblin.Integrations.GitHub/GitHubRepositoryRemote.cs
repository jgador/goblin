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
public sealed class GitHubRepositoryRemote : IGitRepositoryRemote
{
    private readonly GitHubConnection _github;
    private readonly GitHubCommandRunner _commands;
    private readonly Func<GitRepositoryChange, string> _url;

    public GitHubRepositoryRemote(GitHubConnection github) { _github = github; _commands = github.Commands; _url = Remote; }

    internal GitHubRepositoryRemote(GitHubConnection github, string testRemote) { _github = github; _commands = github.Commands; _url = _ => testRemote; }

    private static string GitDirectory(string directory) => Path.Combine(directory, "repository.git");

    private static string Remote(GitRepositoryChange gitRepository) => "https://github.com/" + gitRepository.GitRepository + ".git";

    private async Task VerifyAsync(GitRepositoryChange gitRepository, CancellationToken token)
    {
        GitRepositoryGrant grant = gitRepository.Grant ?? throw new GitHubFailure();
        GitHubState state = await _github.CheckAsync(token);
        if (state.Status != Goblin.Contracts.GitHubConnectionStatus.Connected || state.Account?.Generation != grant.Generation || state.Account.AccountId != grant.AccountId)
            throw new GitHubFailure();
        GitRepositoryInfo info = await _github.GitRepositoryAsync(gitRepository.GitRepository, token);
        if (info.Id != grant.GitRepositoryId || (grant.PublishesChanges() && !info.CanPush) || info.DefaultBranch == grant.Branch) throw new GitHubFailure();
    }

    public async Task PrepareAsync(GitRepositoryChange gitRepository, string directory, string? publishedCommit, CancellationToken token)
    {
        await VerifyAsync(gitRepository, token);
        GitHubConnection.PrivateDirectory(directory);
        string git = GitDirectory(directory);
        await _commands.RunGitAsync(directory, ["init", "--bare", git], token);
        await _commands.RunGitAsync(git, ["fetch", "--no-tags", "--", _url(gitRepository), "+refs/heads/*:refs/heads/*"], token);
        GitRepositoryGrant grant = gitRepository.Grant!;
        string start = (await _commands.RunGitAsync(git, ["rev-parse", "--verify",
            "refs/heads/" + (publishedCommit is null ? grant.BaseBranch : grant.Branch) + "^{commit}"], token)).Trim();
        if (publishedCommit is not null)
        {
            if (start != publishedCommit) throw new GitHubFailure();
            await File.WriteAllTextAsync(Path.Combine(directory, "published-head"), publishedCommit, token);
        }
        await _commands.RunGitAsync(git, ["update-ref", "refs/heads/" + gitRepository.Grant!.Branch, start], token);
        await _commands.RunGitAsync(git, ["symbolic-ref", "HEAD", "refs/heads/" + gitRepository.Grant.Branch], token);
        await _commands.RunGitAsync(git, ["bundle", "create", Path.Combine(directory, "input.bundle"), "--all"], token);
    }

    public async Task<string> InspectBundleAsync(GitRepositoryChange gitRepository, string directory, string bundle, CancellationToken token)
    {
        string git = GitDirectory(directory);
        await _commands.RunGitAsync(git, ["bundle", "verify", bundle], token);
        await _commands.RunGitAsync(git, ["fetch", "--no-tags", "--", bundle, "refs/heads/" + gitRepository.Grant!.Branch + ":refs/goblin/incoming"], token);
        string commit = (await _commands.RunGitAsync(git, ["rev-parse", "--verify", "refs/goblin/incoming^{commit}"], token)).Trim();
        string head = Path.Combine(directory, "published-head");
        if (File.Exists(head)) await _commands.RunGitAsync(git, ["merge-base", "--is-ancestor", (await File.ReadAllTextAsync(head, token)).Trim(), commit], token);
        return commit;
    }

    public async Task<string> ResolveBaseAsync(GitRepositoryChange gitRepository, string directory, CancellationToken token) =>
        (await _commands.RunGitAsync(GitDirectory(directory),
            ["rev-parse", "--verify", "refs/heads/" + gitRepository.Grant!.BaseBranch + "^{commit}"], token)).Trim();

    public async Task<byte[]> FormatPatchAsync(string directory, string baseCommit, string checkpoint, CancellationToken token)
    {
        string git = GitDirectory(directory);
        string baseTree = (await _commands.RunGitAsync(git, ["rev-parse", "--verify", baseCommit + "^{tree}"], token)).Trim();
        string headTree = (await _commands.RunGitAsync(git, ["rev-parse", "--verify", checkpoint + "^{tree}"], token)).Trim();
        if (baseTree == headTree) return [];

        string parents = (await _commands.RunGitAsync(git, ["rev-list", "--parents", "-n", "1", checkpoint], token)).Trim();
        string reviewCommit = checkpoint;
        if (parents != checkpoint + " " + baseCommit)
        {
            // Use the actual trees so repeated edits, reversions and merges all
            // produce one patch with the same result as the checkpoint checkout.
            reviewCommit = (await _commands.RunGitAsync(git, ["-c", "user.name=Goblin", "-c", "user.email=goblin@localhost",
                "commit-tree", headTree, "-p", baseCommit, "-m", "Goblin Work review"], token)).Trim();
        }
        return await _commands.ReadGitPatchAsync(git, ["format-patch", "-1", "--stdout", "--binary", "--no-ext-diff", "--no-textconv",
            "--no-signature", reviewCommit], token);
    }

    public async Task<GitRepositoryOperationResult> ExecuteAsync(GitRepositoryChange gitRepository, string directory, GitRepositoryOperationKind operation, string commit, CancellationToken token)
    {
        GitRepositoryGrant grant = gitRepository.Grant ?? throw new GitHubFailure();
        if (!grant.AllowsOperation(operation)) throw new GitHubFailure();
        await VerifyAsync(gitRepository, token);
        if (operation == GitRepositoryOperationKind.Fetch)
        {
            string git = GitDirectory(directory);
            await _commands.RunGitAsync(git, ["fetch", "--no-tags", "--", _url(gitRepository), "+refs/heads/*:refs/heads/*"], token);
            string fresh = Path.Combine(directory, "fresh.bundle");
            await _commands.RunGitAsync(git, ["bundle", "create", fresh, "--all"], token);
            File.Move(fresh, Path.Combine(directory, "input.bundle"), true);
            return new(null, null);
        }
        if (operation == GitRepositoryOperationKind.Publish)
        {
            string head = Path.Combine(directory, "published-head");
            string expected = File.Exists(head) ? (await File.ReadAllTextAsync(head, token)).Trim() : "";
            await _commands.RunGitAsync(GitDirectory(directory), ["push", "--force-with-lease=refs/heads/" + gitRepository.Grant!.Branch + ":" + expected,
                "--", _url(gitRepository), commit + ":refs/heads/" + gitRepository.Grant.Branch], token);
            await File.WriteAllTextAsync(head, commit, token);
            return new(commit, "https://github.com/" + gitRepository.GitRepository + "/tree/" + gitRepository.Grant.Branch);
        }
        if (operation != GitRepositoryOperationKind.PullRequest || !File.Exists(Path.Combine(directory, "published-head"))) throw new GitHubFailure();
        if ((await File.ReadAllTextAsync(Path.Combine(directory, "published-head"), token)).Trim() != commit) throw new GitHubFailure();
        string? existing = await PullRequestAsync(gitRepository, token);
        if (existing is not null) return new(commit, existing);
        GitHubPullRequestResponse result = JsonSerializer.Deserialize<GitHubPullRequestResponse>(await _commands.RunCliAsync(["api", "--method", "POST", "repos/" + gitRepository.GitRepository + "/pulls",
            "-f", "head=" + gitRepository.Grant!.Branch, "-f", "base=" + gitRepository.Grant.BaseBranch,
            "-f", "title=Goblin " + gitRepository.Grant.Branch, "-f", "body=Changes prepared by Goblin. Review this branch before merging.", "-F", "draft=true"], token)) ?? throw new GitHubFailure();
        return new(commit, result.HtmlUrl);
    }

    public async Task<GitRepositoryOperationResult?> ReconcileAsync(GitRepositoryChange gitRepository, string directory, GitRepositoryOperationKind operation, string commit, CancellationToken token)
    {
        if (!Enum.IsDefined(operation)) throw new GitHubFailure();
        if (operation is GitRepositoryOperationKind.Fetch or GitRepositoryOperationKind.Checkpoint) return null;
        if (operation == GitRepositoryOperationKind.PullRequest)
        {
            string? pr = await PullRequestAsync(gitRepository, token);
            return pr is null ? null : new(commit, pr);
        }
        string result = await _commands.RunGitAsync(GitDirectory(directory), ["ls-remote", "--refs", "--", _url(gitRepository), "refs/heads/" + gitRepository.Grant!.Branch], token);
        if (result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() != commit) return null;
        await File.WriteAllTextAsync(Path.Combine(directory, "published-head"), commit, token);
        return new(commit, "https://github.com/" + gitRepository.GitRepository + "/tree/" + gitRepository.Grant.Branch);
    }

    private async Task<string?> PullRequestAsync(GitRepositoryChange gitRepository, CancellationToken token)
    {
        string owner = gitRepository.GitRepository.Split('/')[0];
        GitHubPullRequestResponse[] result = JsonSerializer.Deserialize<GitHubPullRequestResponse[]>(await _commands.RunCliAsync(["api", "repos/" + gitRepository.GitRepository + "/pulls?state=all&head=" +
            Uri.EscapeDataString(owner + ":" + gitRepository.Grant!.Branch) + "&base=" + Uri.EscapeDataString(gitRepository.Grant.BaseBranch)], token)) ?? throw new GitHubFailure();
        return result.Select(x => x.HtmlUrl).FirstOrDefault();
    }
}
