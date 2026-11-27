using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Goblin.Contracts.Runtime;
using Goblin.Core.Work;
using Goblin.Execution;

// Durable browser fixture; never compiled into the application image.
internal sealed class FakeWorkHost : IExecutionHost, IWorkspaceVolumeHost
{
    private readonly string _root;

    public FakeWorkHost(string root) => _root = root;

    public RuntimeCapabilities[] Capabilities => [new("codex", true, true, true, false, false)];

    public string EnvironmentFor(long workId, long attemptId) => "fixture/" + attemptId;

    private string PathFor(WorkSnapshot work) => Path.Combine(_root, work.Attempts[^1].Id + ".work-result");

    public Task StartAsync(WorkSnapshot work, CancellationToken token)
    {
        bool question = work.Description.Contains("decision", StringComparison.OrdinalIgnoreCase) && work.Decisions.Length == 0;
        if (work.Description.Contains("repository handoff", StringComparison.OrdinalIgnoreCase) && work.Attempts[^1].Target.GitRepository is null)
            return ExecutionFiles.WriteAsync(PathFor(work), new ExecutionObservation(ObservationKind.WorkspaceRequired,
                new("fixture-model", "conversation-session", "fixture-operation"), "Inspect the repository files."), token);
        return ExecutionFiles.WriteAsync(PathFor(work), new ExecutionObservation(
            work.Description.Contains("failure", StringComparison.OrdinalIgnoreCase) ? ObservationKind.Failed : question ? ObservationKind.InputRequired : ObservationKind.Result,
            new("fixture-model", "fixture-session", "fixture-operation"), question ? "Which outcome should I prioritize?" : "Proposed result for: " + work.Description,
            FailureKind.ExecutionFailed), token);
    }

    public async Task<ExecutionObservation> ObserveAsync(WorkSnapshot work, bool stop, CancellationToken token) =>
        await ExecutionFiles.ReadAsync<ExecutionObservation>(PathFor(work), token) ?? new(stop ? ObservationKind.Stopped : ObservationKind.Pending);

    public Task CleanupAsync(WorkSnapshot work, CancellationToken token) => Task.CompletedTask;

    public Task DeleteClosedWorkspaceAsync(WorkSnapshot work, CancellationToken token) => Task.CompletedTask;
}
