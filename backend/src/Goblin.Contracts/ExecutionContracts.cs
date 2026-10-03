using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.GitRepositories;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed class RuntimeCapabilities
{
    public RuntimeCapabilities(string runtime, bool textExecution, bool gitRepositoryExecution, bool cancellation,
        bool liveApprovals, bool resumeSession)
    {
        Runtime = runtime;
        TextExecution = textExecution;
        GitRepositoryExecution = gitRepositoryExecution;
        Cancellation = cancellation;
        LiveApprovals = liveApprovals;
        ResumeSession = resumeSession;
    }

    public string Runtime { get; init; }

    public bool TextExecution { get; init; }

    public bool GitRepositoryExecution { get; init; }

    public bool Cancellation { get; init; }

    public bool LiveApprovals { get; init; }

    public bool ResumeSession { get; init; }
}

public enum ObservationKind
{
    Pending,
    Running,
    Result,
    InputRequired,
    Failed,
    Uncertain,
    Stopped,
    Paused,
    WorkspaceRequired
}

public sealed record ExecutionObservation(ObservationKind Kind, ExecutionSession? Session = null,
    string? Text = null, FailureKind? Failure = null, string? ArtifactReference = null)
{
    public int TurnNumber { get; init; } = 1;
    public bool ReleaseWorkspace { get; init; } = true;
    public long? CheckpointId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GitRepositorySetup[]? Setup { get; init; }
}

// The host chooses the environment for the requested capability. Work itself
// is not an environment and does not require an agent sandbox to exist.
public interface IExecutionHost
{
    RuntimeCapabilities[] Capabilities { get; }

    string EnvironmentFor(long workId, long attemptId);

    string EnvironmentFor(WorkSnapshot work) => EnvironmentFor(work.Id, work.Attempts[^1].Id);

    Task StartAsync(WorkSnapshot work, CancellationToken token);

    Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token);

    Task CleanupAsync(WorkSnapshot work, CancellationToken token);
}

public sealed class DispatchFailureEvidence
{
    public DispatchFailureEvidence(long workId, long attemptId, FailureKind failure, bool cleanup = false,
        int turnNumber = 1)
    {
        WorkId = workId;
        AttemptId = attemptId;
        Failure = failure;
        Cleanup = cleanup;
        TurnNumber = turnNumber;
    }

    public long WorkId { get; init; }

    public long AttemptId { get; init; }

    public FailureKind Failure { get; init; }

    public bool Cleanup { get; init; }

    public int TurnNumber { get; init; }
}

public interface IDispatchFailureJournal
{
    Task RecordAsync(DispatchFailureEvidence evidence, CancellationToken token);

    Task<DispatchFailureEvidence[]> ReadAsync(CancellationToken token);

    Task RemoveAsync(long attemptId, CancellationToken token);
}
