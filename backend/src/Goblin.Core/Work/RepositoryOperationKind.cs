using System;

namespace Goblin.Core.Work;

// A repository grant can authorize only these operations. Wire names stay explicit.
public enum RepositoryOperationKind { Publish, PullRequest, Fetch, Checkpoint }

public static class RepositoryOperationNames
{
    public static string WireValue(this RepositoryOperationKind kind) => kind switch
    {
        RepositoryOperationKind.Publish => "publish",
        RepositoryOperationKind.PullRequest => "pull-request",
        RepositoryOperationKind.Fetch => "fetch",
        RepositoryOperationKind.Checkpoint => "checkpoint",
        _ => throw new WorkRuleException(WorkRule.InvalidValue)
    };

    public static bool TryParse(string value, out RepositoryOperationKind kind)
    {
        kind = value switch
        {
            "publish" => RepositoryOperationKind.Publish,
            "pull-request" => RepositoryOperationKind.PullRequest,
            "fetch" => RepositoryOperationKind.Fetch,
            "checkpoint" => RepositoryOperationKind.Checkpoint,
            _ => (RepositoryOperationKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    public static RepositoryOperationKind Parse(string value) => TryParse(value, out var kind)
        ? kind : throw new WorkRuleException(WorkRule.InvalidValue);
}
