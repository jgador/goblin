using System;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Core.Work;

namespace Goblin.Contracts.Runtime;

public sealed record RuntimeCapabilities(string Runtime, bool TextExecution,
    bool RepositoryExecution, bool Cancellation, bool LiveApprovals, bool ResumeSession);
public enum ObservationKind { Pending, Running, Result, InputRequired, Failed, Uncertain, Stopped }
public sealed record ExecutionObservation(ObservationKind Kind, ExecutionSession? Session = null,
    string? Text = null, FailureKind? Failure = null, string? ArtifactReference = null);

// The host chooses the environment for the requested capability. Work itself
// is not an environment and does not require an agent sandbox to exist.
public interface IExecutionHost
{
    RuntimeCapabilities[] Capabilities { get; }
    string EnvironmentFor(long workId, long attemptId);
    Task StartAsync(WorkSnapshot work, CancellationToken token);
    Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token);
    Task CleanupAsync(WorkSnapshot work, CancellationToken token);
}

public sealed record DispatchFailureEvidence(long WorkId, long AttemptId, FailureKind Failure, bool Cleanup = false);
public interface IDispatchFailureJournal
{
    Task RecordAsync(DispatchFailureEvidence evidence, CancellationToken token);
    Task<DispatchFailureEvidence[]> ReadAsync(CancellationToken token);
    Task RemoveAsync(long attemptId, CancellationToken token);
}
