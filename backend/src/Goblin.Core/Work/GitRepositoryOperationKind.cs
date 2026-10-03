using System;

namespace Goblin.Core.Work;

// A repository grant can authorize only these operations. Wire names stay explicit.
public enum GitRepositoryOperationKind
{
    Publish,
    PullRequest,
    Fetch,
    Checkpoint
}

public static class GitRepositoryOperationNames
{
    public static string WireValue(this GitRepositoryOperationKind kind) => kind switch
    {
        GitRepositoryOperationKind.Publish => "publish",
        GitRepositoryOperationKind.PullRequest => "pull-request",
        GitRepositoryOperationKind.Fetch => "fetch",
        GitRepositoryOperationKind.Checkpoint => "checkpoint",
        _ => throw new WorkRuleException(WorkRule.InvalidValue)
    };

    public static bool TryParse(string value, out GitRepositoryOperationKind kind)
    {
        kind = value switch
        {
            "publish" => GitRepositoryOperationKind.Publish,
            "pull-request" => GitRepositoryOperationKind.PullRequest,
            "fetch" => GitRepositoryOperationKind.Fetch,
            "checkpoint" => GitRepositoryOperationKind.Checkpoint,
            _ => (GitRepositoryOperationKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    public static GitRepositoryOperationKind Parse(string value) => TryParse(value, out GitRepositoryOperationKind kind)
        ? kind : throw new WorkRuleException(WorkRule.InvalidValue);
}
