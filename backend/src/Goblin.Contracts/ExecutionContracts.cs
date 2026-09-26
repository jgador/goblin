using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Repositories;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed record RuntimeCapabilities(string Runtime, bool TextExecution,
    bool RepositoryExecution, bool Cancellation, bool LiveApprovals, bool ResumeSession);
public enum ObservationKind { Pending, Running, Result, InputRequired, Failed, Uncertain, Stopped, Paused, WorkspaceRequired }
public sealed record ExecutionObservation(ObservationKind Kind, ExecutionSession? Session = null,
    string? Text = null, FailureKind? Failure = null, string? ArtifactReference = null)
{
    public int TurnNumber { get; init; } = 1;
    public bool ReleaseWorkspace { get; init; } = true;
    public long? CheckpointId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositorySetup[]? Setup { get; init; }
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

public sealed record DispatchFailureEvidence(long WorkId, long AttemptId, FailureKind Failure, bool Cleanup = false, int TurnNumber = 1);
public interface IDispatchFailureJournal
{
    Task RecordAsync(DispatchFailureEvidence evidence, CancellationToken token);
    Task<DispatchFailureEvidence[]> ReadAsync(CancellationToken token);
    Task RemoveAsync(long attemptId, CancellationToken token);
}
